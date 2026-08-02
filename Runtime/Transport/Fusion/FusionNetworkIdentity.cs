using System;
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
        [Networked] public PlayerRef LogicalOwner { get; set; }
        [Networked] public NetworkBool AuthorityAdmitted { get; private set; }

        private PlayerRef m_LastLogicalOwner = PlayerRef.Invalid;
        private bool m_LastAuthorityAdmission;
        private uint m_LastAuthorityEpoch;
        private bool m_TransportAdmitted;
        private bool m_SuppressChangedObservation;

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
        public uint NetworkId => Object != null && Object.IsValid ? Object.Id.Raw : 0;
        public bool TransportAdmitted => m_TransportAdmitted;
        public bool HasAuthorityAdmission => AuthorityAdmitted;
        public bool IsLocalLogicalOwner =>
            Runner != null && Runner.IsRunning && IsOwnedBy(Runner.LocalPlayer);

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
            return player.IsRealPlayer && LogicalOwner.IsRealPlayer && player == LogicalOwner;
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

        internal void RefreshAuthorityRole()
        {
            m_LastLogicalOwner = LogicalOwner;
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
                AuthorityAdmitted,
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
