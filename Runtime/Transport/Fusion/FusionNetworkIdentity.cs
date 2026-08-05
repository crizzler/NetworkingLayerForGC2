using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Fusion identity with a replicated gameplay owner that is independent from Fusion
    /// State Authority. In Shared mode the master retains State Authority for centralized
    /// simulation while <see cref="LogicalOwner"/> identifies the client allowed to send intent.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Network Identity")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class FusionNetworkIdentity : NetworkBehaviour
    {
        private const int SharedTransientSendBacklogCapacity = 128;

        [Networked] public PlayerRef LogicalOwner { get; set; }
        [Networked] public NetworkBool AuthorityAdmitted { get; private set; }

        private PlayerRef m_LastLogicalOwner = PlayerRef.Invalid;
        private bool m_LastAuthorityAdmission;
        private uint m_LastAuthorityEpoch;
        private bool m_TransportAdmitted;
        private bool m_SuppressChangedObservation;
        private readonly Queue<FusionNativeCharacterInput> m_SharedTransientSendBacklog =
            new Queue<FusionNativeCharacterInput>(16);
        private bool m_SharedTransientSendOverflowLatched;
        private float m_NextSharedTransientSendWarningTime;

        public event Action<FusionNetworkIdentity> IdentityChanged;
        public event Action<FusionIdentityObservation> IdentityObservedChanged;

        public static event Action<FusionIdentityObservation> AnyIdentityObservedSpawned;
        public static event Action<FusionIdentityObservation> AnyIdentityObservedChanged;
        public static event Action<FusionIdentityObservation> AnyIdentityObservedDespawned;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetObservedEvents()
        {
            AnyIdentityObservedSpawned = null;
            AnyIdentityObservedChanged = null;
            AnyIdentityObservedDespawned = null;
        }

        public NetworkObject NetworkObject => Object;
        /// <summary>
        /// True only while Fusion permits access to this behaviour's generated
        /// <c>[Networked]</c> properties. Scene objects are discoverable by Unity before
        /// Fusion invokes <see cref="Spawned"/>, so an existing component is not by itself
        /// proof that its replicated state can be read.
        /// </summary>
        public bool IsSpawned => Object != null && Object.IsValid;
        public uint NetworkId => IsSpawned ? Object.Id.Raw : 0;
        public bool TransportAdmitted => m_TransportAdmitted;
        public bool HasAuthorityAdmission => IsSpawned && AuthorityAdmitted;
        public bool IsLocalLogicalOwner =>
            IsSpawned && Runner != null && Runner.IsRunning && IsOwnedBy(Runner.LocalPlayer);

        public uint LogicalOwnerClientId =>
            TryGetLogicalOwnerClientId(out uint clientId)
                ? clientId
                : NetworkTransportBridge.InvalidClientId;

        public bool IsLogicalAuthority
        {
            get
            {
                if (Runner == null || !Runner.IsRunning) return false;
                if (FusionTransportBridge.TryGetBoundBridge(Runner, out var bridge))
                {
                    return bridge.IsServer;
                }

                return Runner.IsServer ||
                       (Runner.GameMode == GameMode.Shared && Runner.IsSharedModeMasterClient);
            }
        }

        public override void Spawned()
        {
            ResetSharedTransientSendBacklog();
            m_SuppressChangedObservation = true;
            try
            {
                if (Runner.GameMode != GameMode.Shared &&
                    !LogicalOwner.IsRealPlayer &&
                    Object.InputAuthority.IsRealPlayer &&
                    Object.HasStateAuthority)
                {
                    LogicalOwner = Object.InputAuthority;
                }

                ValidateSharedAuthority();
                if (FusionAuthoritySpawnRegistry.TryGet(Runner, out var registry))
                {
                    registry.ObserveSpawned(this);
                }
                else
                {
                    // The replicated marker is necessary but not sufficient: without the
                    // authority registry there is no local admission boundary or periodic
                    // validation. Keep the object quarantined until a registry binds.
                    m_TransportAdmitted = false;
                    if (IsLogicalAuthority)
                    {
                        Debug.LogWarning(
                            $"[FusionTransport] No FusionAuthoritySpawnRegistry is bound for '{name}'. " +
                            "Add one to enforce authority-issued object admission.",
                            this);
                    }
                }
                RefreshAuthorityRole();
            }
            finally
            {
                m_SuppressChangedObservation = false;
                PublishIdentityObservation(FusionIdentityLifecyclePhase.Spawned);
            }
        }

        public override void Render()
        {
            uint epoch = 0;
            if (FusionTransportBridge.TryGetBoundBridge(Runner, out var bridge))
            {
                epoch = bridge.AuthorityEpoch;
            }

            bool authorityAdmission = AuthorityAdmitted;
            if (authorityAdmission != m_LastAuthorityAdmission &&
                FusionAuthoritySpawnRegistry.TryGet(Runner, out var registry))
            {
                // Admission is issued by the logical authority and replicated as part of
                // the object. This lets a promoted Shared master rebuild the registry
                // without trusting every object that happens to be master-owned.
                registry.ObserveSpawned(this);
            }

            if (LogicalOwner != m_LastLogicalOwner ||
                authorityAdmission != m_LastAuthorityAdmission ||
                epoch != m_LastAuthorityEpoch)
            {
                RefreshAuthorityRole();
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            NetworkId previousId = Object != null ? Object.Id : default;
            NetworkCharacter character = GetComponentInChildren<NetworkCharacter>(true);
            if (character != null &&
                FusionTransportBridge.TryGetBoundBridge(runner, out var bridge))
            {
                bridge.UnregisterCharacter(character);
            }

            if (FusionAuthoritySpawnRegistry.TryGet(runner, out var registry))
            {
                registry.ObserveDespawned(this, previousId);
            }

            m_LastLogicalOwner = PlayerRef.Invalid;
            m_LastAuthorityAdmission = false;
            m_LastAuthorityEpoch = 0;
            m_TransportAdmitted = false;
            ResetSharedTransientSendBacklog();
            try
            {
                IdentityChanged?.Invoke(this);
            }
            finally
            {
                PublishIdentityObservation(
                    FusionIdentityLifecyclePhase.Despawned,
                    runner,
                    previousId.IsValid ? previousId.Raw : 0);
            }
        }

        public bool TryGetLogicalOwnerClientId(out uint clientId)
        {
            clientId = NetworkTransportBridge.InvalidClientId;
            if (!IsSpawned) return false;

            PlayerRef owner = LogicalOwner;
            if (!owner.IsRealPlayer &&
                Runner != null &&
                Runner.GameMode != GameMode.Shared &&
                Object != null)
            {
                owner = Object.InputAuthority;
            }

            return FusionTransportBridge.TryPlayerToClientId(owner, out clientId);
        }

        public bool IsOwnedBy(PlayerRef player)
        {
            return IsSpawned &&
                   player.IsRealPlayer &&
                   LogicalOwner.IsRealPlayer &&
                   player == LogicalOwner;
        }

        public bool TryAssignLogicalOwner(PlayerRef owner)
        {
            if (!owner.IsRealPlayer || Object == null || !Object.IsValid || !IsLogicalAuthority)
            {
                return false;
            }

            LogicalOwner = owner;
            RefreshAuthorityRole();
            return true;
        }

        internal void AssignLogicalOwnerBeforeSpawn(PlayerRef owner)
        {
            if (!owner.IsRealPlayer) return;
            LogicalOwner = owner;
        }

        /// <summary>
        /// Sends one Shared-mode owner input sample to the centralized State Authority.
        /// The native character motor is manually state-woven and therefore cannot own RPCs:
        /// Fusion skips RPC weaving on behaviours carrying NetworkBehaviourWeavedAttribute.
        /// </summary>
        internal bool TrySubmitSharedCharacterInput(
            FusionNativeCharacterInput input,
            out RpcInvokeInfo invokeInfo)
        {
            invokeInfo = default;
            if (!IsSpawned || Runner == null || !Runner.IsRunning ||
                Runner.GameMode != GameMode.Shared ||
                !IsOwnedBy(Runner.LocalPlayer))
            {
                return false;
            }

            // Continuous steering is latency-sensitive and safe to hold through an occasional
            // packet gap. Continuous MotionInteractive poses are absolute and replaceable, so
            // only their newest endpoint belongs on this same latest-state stream. One-shot
            // Vault/Jump/PullUp/root-motion displacement remains reliably ordered below.
            int continuousFlags = input.HasContinuousOwnerPose
                ? FusionNativeCharacterInput.FlagOwnerPose |
                  FusionNativeCharacterInput.FlagContinuousOwnerPose
                : 0;
            invokeInfo = RPC_SubmitSharedCharacterInput(
                input.Move,
                input.Yaw,
                input.SourceTick,
                continuousFlags,
                input.HasContinuousOwnerPose ? input.OwnerPosition : Vector3.zero);

            if (FusionNativeNetworkCharacterMotor.HasSharedTransientInput(input))
            {
                EnqueueSharedCharacterTransient(input);
            }
            TrySendQueuedSharedCharacterTransient();
            return true;
        }

        private void EnqueueSharedCharacterTransient(FusionNativeCharacterInput input)
        {
            if (m_SharedTransientSendOverflowLatched) return;
            if (m_SharedTransientSendBacklog.Count >= SharedTransientSendBacklogCapacity)
            {
                // Dropping an entry and later sending a higher sequence would let the master's
                // cumulative ACK falsely cover the missing Vault/Jump sample. Fail closed for
                // this object until its ownership/session boundary resets the queue.
                m_SharedTransientSendOverflowLatched = true;
                Debug.LogError(
                    $"[FusionNativeCharacter] Reliable Shared transient send backlog exceeded " +
                    $"{SharedTransientSendBacklogCapacity} samples for '{name}'. " +
                    "Further traversal input is blocked to preserve acknowledgement integrity; " +
                    "reconnect the player and inspect network health.",
                    this);
                return;
            }

            m_SharedTransientSendBacklog.Enqueue(input);
        }

        private void TrySendQueuedSharedCharacterTransient()
        {
            if (m_SharedTransientSendBacklog.Count == 0) return;

            FusionNativeCharacterInput pending = m_SharedTransientSendBacklog.Peek();
            RpcInvokeInfo transientInvokeInfo = RPC_SubmitSharedCharacterTransient(
                pending.Move,
                pending.Yaw,
                pending.SourceTick,
                pending.Flags,
                pending.OwnerPosition,
                pending.RootMotionDelta,
                pending.RootMotionWeight,
                pending.JumpForce);
            if (transientInvokeInfo.SendMessageResult == RpcSendMessageResult.Sent)
            {
                m_SharedTransientSendBacklog.Dequeue();
                return;
            }

            float now = Time.unscaledTime;
            if (now < m_NextSharedTransientSendWarningTime) return;
            m_NextSharedTransientSendWarningTime = now + 1f;
            Debug.LogWarning(
                $"[FusionNativeCharacter] Retaining reliable Shared transient " +
                $"payloadTick={pending.SourceTick} for retry on '{name}': " +
                $"{transientInvokeInfo}",
                this);
        }

        private void ResetSharedTransientSendBacklog()
        {
            m_SharedTransientSendBacklog.Clear();
            m_SharedTransientSendOverflowLatched = false;
            m_NextSharedTransientSendWarningTime = 0f;
        }

        [Rpc(
            RpcSources.All,
            RpcTargets.StateAuthority,
            Channel = RpcChannel.Unreliable,
            InvokeLocal = false,
            TickAligned = true)]
        private RpcInvokeInfo RPC_SubmitSharedCharacterInput(
            Vector2 move,
            float yaw,
            int sourceTick,
            int flags,
            Vector3 ownerPosition,
            RpcInfo info = default)
        {
            if (!IsSpawned || Runner == null ||
                Runner.GameMode != GameMode.Shared || !Object.HasStateAuthority)
            {
                return default;
            }

            FusionNativeNetworkCharacterMotor motor =
                GetComponent<FusionNativeNetworkCharacterMotor>();
            motor?.AcceptSharedCharacterInput(
                info.Source,
                info.Tick.Raw,
                move,
                yaw,
                sourceTick,
                flags,
                ownerPosition);
            return default;
        }

        [Rpc(
            RpcSources.All,
            RpcTargets.StateAuthority,
            Channel = RpcChannel.Reliable,
            InvokeLocal = false,
            TickAligned = true)]
        private RpcInvokeInfo RPC_SubmitSharedCharacterTransient(
            Vector2 move,
            float yaw,
            int sourceTick,
            int flags,
            Vector3 ownerPosition,
            Vector3 rootMotionDelta,
            float rootMotionWeight,
            float jumpForce,
            RpcInfo info = default)
        {
            if (!IsSpawned || Runner == null ||
                Runner.GameMode != GameMode.Shared || !Object.HasStateAuthority)
            {
                return default;
            }

            FusionNativeNetworkCharacterMotor motor =
                GetComponent<FusionNativeNetworkCharacterMotor>();
            motor?.AcceptSharedCharacterTransient(
                info.Source,
                info.Tick.Raw,
                move,
                yaw,
                sourceTick,
                flags,
                ownerPosition,
                rootMotionDelta,
                rootMotionWeight,
                jumpForce);
            return default;
        }

        internal bool TryIssueAuthorityAdmission(bool admitted)
        {
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority ||
                !IsLogicalAuthority)
            {
                return false;
            }

            AuthorityAdmitted = admitted;
            return true;
        }

        internal void SetTransportAdmission(bool admitted)
        {
            if (m_TransportAdmitted == admitted) return;
            m_TransportAdmitted = admitted;

            if (!admitted &&
                NetworkId != 0 &&
                FusionTransportBridge.TryGetBoundBridge(Runner, out var bridge))
            {
                bridge.ClearCharacterOwner(NetworkId);
                NetworkCharacter character = GetComponentInChildren<NetworkCharacter>(true);
                if (character != null) bridge.UnregisterCharacter(character);
            }

            RaiseIdentityChanged();
        }

        internal bool RefreshAuthorityRole()
        {
            // Unity scene objects and freshly-instantiated prefabs can be found before
            // NetworkRunner.InvokeSpawnedCallback. Reading a generated Networked property
            // in that window throws and, when invoked from an authority-announcement RPC,
            // also prevents the reliable stream from recording that packet's sequence.
            if (!IsSpawned) return false;

            PlayerRef logicalOwner = LogicalOwner;
            if (logicalOwner != m_LastLogicalOwner)
            {
                ResetSharedTransientSendBacklog();
            }
            m_LastLogicalOwner = logicalOwner;
            m_LastAuthorityAdmission = AuthorityAdmitted;
            m_LastAuthorityEpoch =
                FusionTransportBridge.TryGetBoundBridge(Runner, out var bridge)
                    ? bridge.AuthorityEpoch
                    : 0;

            NetworkCharacter character = GetComponentInChildren<NetworkCharacter>(true);
            if (character != null && NetworkId != 0 && m_TransportAdmitted)
            {
                character.SetManualNetworkId(NetworkId);
                if (TryGetLogicalOwnerClientId(out uint ownerClientId))
                {
                    bridge?.SetCharacterOwner(NetworkId, ownerClientId);
                }
            }

            RaiseIdentityChanged();
            return true;
        }

        private void RaiseIdentityChanged()
        {
            try
            {
                IdentityChanged?.Invoke(this);
            }
            finally
            {
                if (!m_SuppressChangedObservation)
                {
                    PublishIdentityObservation(FusionIdentityLifecyclePhase.Changed);
                }
            }
        }

        private void PublishIdentityObservation(
            FusionIdentityLifecyclePhase phase,
            NetworkRunner runnerOverride = null,
            uint networkIdOverride = 0)
        {
            // Observation is diagnostics/presentation only. In particular, Fusion may invoke
            // Despawned without retained state; reading a generated [Networked] property in
            // that case must never escape back into Fusion's critical despawn path.
            try
            {
                PublishIdentityObservationCore(phase, runnerOverride, networkIdOverride);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[FusionTransport] Could not publish optional identity observation '{phase}'.",
                    this);
                Debug.LogException(exception, this);
            }
        }

        private void PublishIdentityObservationCore(
            FusionIdentityLifecyclePhase phase,
            NetworkRunner runnerOverride,
            uint networkIdOverride)
        {
            NetworkRunner runner = runnerOverride != null ? runnerOverride : Runner;
            uint logicalOwnerClientId = NetworkTransportBridge.InvalidClientId;
            TryGetLogicalOwnerClientId(out logicalOwnerClientId);
            uint networkId = networkIdOverride != 0 ? networkIdOverride : NetworkId;
            bool localLogicalOwner =
                runner != null && runner.IsRunning && IsOwnedBy(runner.LocalPlayer);
            var observation = new FusionIdentityObservation(
                phase,
                this,
                runner,
                networkId,
                logicalOwnerClientId,
                m_TransportAdmitted,
                HasAuthorityAdmission,
                IsLogicalAuthority,
                localLogicalOwner);

            if (phase == FusionIdentityLifecyclePhase.Changed)
            {
                FusionLifecycleEventUtility.InvokeBestEffort(
                    IdentityObservedChanged,
                    observation,
                    this,
                    nameof(IdentityObservedChanged));
            }

            Action<FusionIdentityObservation> handlers = phase switch
            {
                FusionIdentityLifecyclePhase.Spawned => AnyIdentityObservedSpawned,
                FusionIdentityLifecyclePhase.Despawned => AnyIdentityObservedDespawned,
                _ => AnyIdentityObservedChanged
            };
            FusionLifecycleEventUtility.InvokeBestEffort(
                handlers,
                observation,
                this,
                phase switch
                {
                    FusionIdentityLifecyclePhase.Spawned => nameof(AnyIdentityObservedSpawned),
                    FusionIdentityLifecyclePhase.Despawned => nameof(AnyIdentityObservedDespawned),
                    _ => nameof(AnyIdentityObservedChanged)
                });
        }

        private void ValidateSharedAuthority()
        {
            if (Runner == null || Runner.GameMode != GameMode.Shared || Object == null) return;

            bool masterOwned =
                Object.StateAuthority.IsInternalMasterClientIdentifier ||
                Object.StateAuthority == Runner.GetMasterClient() ||
                (Runner.IsSharedModeMasterClient && Object.HasStateAuthority);
            bool flagConfigured =
                (Object.Flags & NetworkObjectFlags.MasterClientObject) != 0;

            if (!masterOwned && !flagConfigured)
            {
                Debug.LogError(
                    $"[FusionTransport] Shared object '{name}' is not master-owned. " +
                    "Spawn it with SharedModeStateAuthMasterClient or enable Master Client Object.",
                    this);
            }

            if ((Object.Flags & NetworkObjectFlags.AllowStateAuthorityOverride) != 0)
            {
                Debug.LogError(
                    $"[FusionTransport] Shared object '{name}' allows State Authority override. " +
                    "Disable it for server-authoritative GC2 gameplay.",
                    this);
            }

            if ((Object.Flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) != 0)
            {
                Debug.LogError(
                    $"[FusionTransport] Shared object '{name}' is destroyed when State Authority " +
                    "leaves. Disable that flag so master reassignment preserves the object.",
                    this);
            }
        }
    }
}
