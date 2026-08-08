using System;
using Fusion;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// KCC-independent GC2 prediction backend. The optional Advanced KCC package supplies a
    /// sibling <see cref="IFusionKccRuntimeAdapter"/> component; without that component this
    /// proxy remains safely loadable and the NetworkCharacter falls back to its built-in driver.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion KCC Character Backend")]
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkCharacter))]
    [RequireComponent(typeof(FusionNetworkIdentity))]
    public sealed class FusionKccCharacterBackend : NetworkBehaviour,
        INetworkCharacterPredictionBackend,
        INetworkAuthoritativePoseProvider,
        IFusionCharacterInputEndpoint,
        IFusionSharedCharacterEndpoint,
        IFusionSharedCharacterRunnerPump,
        IStateAuthorityChanged
    {
        private const int ServerMotionAuthorizationCapacity = 8;

        [Header("Advanced KCC")]
        [Tooltip(
            "Optional component supplied by the Fusion Advanced KCC integration. " +
            "Leave empty to resolve the adapter automatically in this Character hierarchy.")]
        [SerializeField] private MonoBehaviour m_RuntimeAdapter;

        [Tooltip(
            "Owner Movement Authority follows Fusion Shared Mode's native per-object " +
            "authority model. Shared Master Movement Authority routes owner intent through " +
            "the centralized Shared master.")]
        [SerializeField] private FusionKccSharedAuthorityMode m_SharedAuthorityMode =
            FusionKccSharedAuthorityMode.OwnerMovementAuthority;

        [Header("Diagnostics")]
        [SerializeField] private bool m_LogDiagnostics;

        // Gameplay-authoritative commands deliberately live on the master/server-owned GC2
        // root rather than the optionally owner-owned KCC motor. This keeps Shared owner KCC
        // prediction responsive without granting that child authority over teleports or scale.
        [Networked] public Vector3 ReplicatedKccRootScale { get; private set; }
        [Networked] public Vector3 KccTeleportFootPosition { get; private set; }
        [Networked] public Quaternion KccTeleportRotation { get; private set; }
        [Networked] public int KccTeleportSequence { get; private set; }
        [Networked] public NetworkBool KccTeleportIsHard { get; private set; }
        [Networked] public int KccMotorCommandSequence { get; private set; }
        [Networked] public int KccMotorCommandFlags { get; private set; }
        [Networked] public int KccOwnerMotionUntilTick { get; private set; }
        [Networked] public int KccServerMotionFromTick { get; private set; }
        [Networked] public int KccServerMotionUntilTick { get; private set; }
        [Networked] public uint KccServerMotionOperationId { get; private set; }

        private IFusionKccRuntimeAdapter m_Adapter;
        private NetworkCharacter m_NetworkCharacter;
        private FusionNetworkIdentity m_Identity;
        private NetworkCharacter.NetworkRole m_Role;
        private NetworkSessionProfile m_Profile;
        private bool m_IsServer;
        private bool m_IsOwner;
        private bool m_IsHost;
        private bool m_AdapterInitialized;
        private bool m_MissingAdapterReported;
        private bool m_InvalidAdapterReported;
        private bool m_NetworkSpawned;

        private bool m_HasPendingAuthoritativeTeleport;
        private Vector3 m_PendingTeleportFootPosition;
        private Quaternion m_PendingTeleportRotation = Quaternion.identity;
        private bool m_PendingTeleportIsHard;
        private bool m_HasPendingAuthoritativeScale;
        private Vector3 m_PendingAuthoritativeScale = Vector3.one;
        private Vector3 m_FallbackRootScale = Vector3.one;
        private int m_PendingMotorCommandFlags;
        private bool m_HasPendingOwnerMotionReplication;
        private int m_PendingOwnerMotionUntilTick = int.MinValue;
        private bool m_HasPendingServerMotionReplication;
        private ServerMotionAuthorization m_PendingServerMotionAuthorization;
        private bool m_RehydrateMotionWindowsOnAuthority;

        private int m_OwnerMotionUntilTick = int.MinValue;
        private readonly ServerMotionAuthorization[] m_ServerMotionAuthorizations =
            new ServerMotionAuthorization[ServerMotionAuthorizationCapacity];
        private int m_ServerMotionAuthorizationCount;

        public NetworkPredictionBackend Backend => NetworkPredictionBackend.FusionKCC;
        public FusionKccSharedAuthorityMode SharedAuthorityMode => m_SharedAuthorityMode;
        public MonoBehaviour RuntimeAdapterComponent => m_RuntimeAdapter;
        public IFusionKccRuntimeAdapter RuntimeAdapter => ResolveAdapter(false);
        public FusionNetworkIdentity Identity => m_Identity;
        public NetworkCharacter Character => m_NetworkCharacter;
        public NetworkCharacter.NetworkRole Role => m_Role;
        public NetworkSessionProfile SessionProfile => m_Profile;
        public bool IsServerRole => m_IsServer;
        public bool IsOwnerRole => m_IsOwner;
        public bool IsHostRole => m_IsHost;
        public bool IsRuntimeAvailable => ResolveAdapter(false) != null;
        public bool CanApplyAuthoritativeKccCommands =>
            m_NetworkSpawned && Object != null && Object.IsValid &&
            Object.HasStateAuthority;

        public int LastAppliedSharedTransientSourceTick =>
            ResolveAdapter(false)?.LastAppliedSharedTransientSourceTick ?? int.MinValue;

        public bool RequiresSharedLogicalOwnerProxyPump =>
            m_SharedAuthorityMode == FusionKccSharedAuthorityMode.SharedMasterMovementAuthority &&
            ResolveAdapter(false)?.RequiresSharedLogicalOwnerProxyPump == true;

        private void Awake()
        {
            CacheComponents();
            CacheFallbackScale();
            ResolveAdapter(false);
        }

        public override void Spawned()
        {
            m_NetworkSpawned = true;
            CacheComponents();
            CacheFallbackScale();
            if (Object.HasStateAuthority)
            {
                ReplicatedKccRootScale = m_FallbackRootScale;
                if (!IsFinite(KccTeleportRotation))
                {
                    KccTeleportRotation = Quaternion.identity;
                }
                KccOwnerMotionUntilTick = int.MinValue;
                KccServerMotionFromTick = int.MinValue;
                KccServerMotionUntilTick = int.MinValue;
                KccServerMotionOperationId = 0;
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!CanApplyAuthoritativeKccCommands) return;

            if (m_RehydrateMotionWindowsOnAuthority)
            {
                m_RehydrateMotionWindowsOnAuthority = false;
                RestoreReplicatedMotionWindows();
            }

            if (m_HasPendingAuthoritativeScale)
            {
                ReplicatedKccRootScale = m_PendingAuthoritativeScale;
                m_FallbackRootScale = m_PendingAuthoritativeScale;
                transform.localScale = m_PendingAuthoritativeScale;
                m_HasPendingAuthoritativeScale = false;
            }

            if (m_PendingMotorCommandFlags != 0)
            {
                KccMotorCommandFlags = m_PendingMotorCommandFlags;
                KccMotorCommandSequence = NextSequence(KccMotorCommandSequence);
                m_PendingMotorCommandFlags = 0;
            }

            if (m_HasPendingOwnerMotionReplication)
            {
                KccOwnerMotionUntilTick = m_PendingOwnerMotionUntilTick;
                m_HasPendingOwnerMotionReplication = false;
            }

            if (m_HasPendingServerMotionReplication)
            {
                KccServerMotionFromTick =
                    m_PendingServerMotionAuthorization.FromTick;
                KccServerMotionUntilTick =
                    m_PendingServerMotionAuthorization.UntilTick;
                KccServerMotionOperationId =
                    m_PendingServerMotionAuthorization.OperationId;
                m_HasPendingServerMotionReplication = false;
            }

            if (!m_HasPendingAuthoritativeTeleport) return;

            KccTeleportFootPosition = m_PendingTeleportFootPosition;
            KccTeleportRotation = m_PendingTeleportRotation;
            KccTeleportIsHard = m_PendingTeleportIsHard;
            KccTeleportSequence = NextSequence(KccTeleportSequence);
            m_HasPendingAuthoritativeTeleport = false;
            m_PendingTeleportFootPosition = Vector3.zero;
            m_PendingTeleportRotation = Quaternion.identity;
            m_PendingTeleportIsHard = false;
        }

        public void StateAuthorityChanged()
        {
            m_RehydrateMotionWindowsOnAuthority =
                Object != null && Object.IsValid && Object.HasStateAuthority;
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            m_NetworkSpawned = false;
            ResetAuthoritativeCommandState();
            ResetMotionWindows();
        }

        public IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            CacheComponents(networkCharacter);
            m_Role = role;

            IFusionKccRuntimeAdapter adapter = ResolveAdapter(true);
            IUnitDriver driver = adapter?.CreateDriver(this, networkCharacter, role);
            if (driver == null)
            {
                RestoreBuiltInControllerForFallback();
            }

            return driver;
        }

        public void Initialize(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        {
            CacheComponents(networkCharacter);
            m_Role = role;
            m_IsServer = isServer;
            m_IsOwner = isOwner;
            m_IsHost = isHost;

            IFusionKccRuntimeAdapter adapter = ResolveAdapter(true);
            if (adapter == null) return;

            adapter.Initialize(this, networkCharacter, role);
            m_AdapterInitialized = true;
            if (m_Profile != null) adapter.ApplySessionProfile(m_Profile);
        }

        public void ApplySessionProfile(NetworkSessionProfile profile)
        {
            m_Profile = profile;
            ResolveAdapter(false)?.ApplySessionProfile(profile);
        }

        public void ResetBackend(NetworkCharacter networkCharacter)
        {
            if (m_AdapterInitialized && m_RuntimeAdapter != null && m_Adapter != null)
            {
                m_Adapter?.Shutdown();
            }

            m_AdapterInitialized = false;
            m_Role = NetworkCharacter.NetworkRole.None;
            m_IsServer = false;
            m_IsOwner = false;
            m_IsHost = false;
            ResetAuthoritativeCommandState();
            ResetMotionWindows();
        }

        /// <summary>
        /// Queues a GC2 foot-space teleport on the master/server-owned root. Calls made on a
        /// non-authoritative peer are intentionally ignored; the validated Networking Layer
        /// teleport flow will execute the same command on State Authority.
        /// </summary>
        public bool QueueAuthoritativeTeleport(
            Vector3 footPosition,
            Quaternion rotation,
            bool hardTeleport = true)
        {
            if (!CanApplyAuthoritativeKccCommands || !IsFinite(footPosition) ||
                !IsFinite(rotation))
            {
                return false;
            }

            m_HasPendingAuthoritativeTeleport = true;
            m_PendingTeleportFootPosition = footPosition;
            m_PendingTeleportRotation = rotation;
            m_PendingTeleportIsHard = hardTeleport;
            QueueAuthoritativeVerticalVelocityReset();
            return true;
        }

        public void UpdateQueuedTeleportRotation(Quaternion rotation)
        {
            if (!m_HasPendingAuthoritativeTeleport || !IsFinite(rotation)) return;
            m_PendingTeleportRotation = rotation;
        }

        public bool TryGetAuthoritativeTeleport(
            out int sequence,
            out Vector3 footPosition,
            out Quaternion rotation,
            out bool hardTeleport)
        {
            sequence = 0;
            footPosition = default;
            rotation = Quaternion.identity;
            hardTeleport = false;
            if (!m_NetworkSpawned || Object == null || !Object.IsValid) return false;

            sequence = KccTeleportSequence;
            footPosition = KccTeleportFootPosition;
            rotation = KccTeleportRotation;
            hardTeleport = KccTeleportIsHard;
            return sequence != 0 && IsFinite(footPosition) && IsFinite(rotation);
        }

        public bool QueueAuthoritativeVerticalVelocityReset()
        {
            if (!CanApplyAuthoritativeKccCommands) return false;
            m_PendingMotorCommandFlags |=
                FusionNativeCharacterInput.FlagResetVerticalVelocity;
            return true;
        }

        public bool QueueAuthoritativeCollision(bool enabled)
        {
            if (!CanApplyAuthoritativeKccCommands) return false;
            m_PendingMotorCommandFlags |=
                FusionNativeCharacterInput.FlagCollisionChanged;
            if (enabled)
            {
                m_PendingMotorCommandFlags |=
                    FusionNativeCharacterInput.FlagCollisionEnabled;
            }
            else
            {
                m_PendingMotorCommandFlags &=
                    ~FusionNativeCharacterInput.FlagCollisionEnabled;
            }
            return true;
        }

        public bool TryGetAuthoritativeMotorCommand(
            out int sequence,
            out bool resetVerticalVelocity,
            out bool collisionChanged,
            out bool collisionEnabled)
        {
            sequence = 0;
            resetVerticalVelocity = false;
            collisionChanged = false;
            collisionEnabled = false;
            if (!m_NetworkSpawned || Object == null || !Object.IsValid) return false;

            sequence = KccMotorCommandSequence;
            int flags = KccMotorCommandFlags;
            resetVerticalVelocity =
                (flags & FusionNativeCharacterInput.FlagResetVerticalVelocity) != 0;
            collisionChanged =
                (flags & FusionNativeCharacterInput.FlagCollisionChanged) != 0;
            collisionEnabled =
                (flags & FusionNativeCharacterInput.FlagCollisionEnabled) != 0;
            return sequence != 0 && (resetVerticalVelocity || collisionChanged);
        }

        public bool RequestAuthoritativeScale(Vector3 scale)
        {
            if (!CanApplyAuthoritativeKccCommands || !IsValidScale(scale)) return false;
            m_PendingAuthoritativeScale = scale;
            m_HasPendingAuthoritativeScale = true;
            return true;
        }

        public Vector3 GetRequestedOrReplicatedRootScale(Vector3 fallback)
        {
            if (m_HasPendingAuthoritativeScale &&
                IsValidScale(m_PendingAuthoritativeScale))
            {
                return m_PendingAuthoritativeScale;
            }

            if (m_NetworkSpawned && Object != null && Object.IsValid)
            {
                Vector3 replicated = ReplicatedKccRootScale;
                if (IsValidScale(replicated)) return replicated;
            }

            if (IsValidScale(m_FallbackRootScale)) return m_FallbackRootScale;
            return IsValidScale(fallback) ? fallback : Vector3.one;
        }

        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            if (durationSeconds <= 0f) return;
            int untilTick = CurrentTick + SecondsToTicks(durationSeconds);
            m_OwnerMotionUntilTick = Math.Max(m_OwnerMotionUntilTick, untilTick);
            if (CanApplyAuthoritativeKccCommands)
            {
                m_PendingOwnerMotionUntilTick = Math.Max(
                    m_PendingOwnerMotionUntilTick,
                    m_OwnerMotionUntilTick);
                m_HasPendingOwnerMotionReplication = true;
            }
            LogMotionWindow("owner-opened", 0, CurrentTick, untilTick);
        }

        public bool IsOwnerMotionActive(int tick)
        {
            if (m_OwnerMotionUntilTick != int.MinValue &&
                tick <= m_OwnerMotionUntilTick)
            {
                return true;
            }
            return m_NetworkSpawned && Object != null && Object.IsValid &&
                   KccOwnerMotionUntilTick != int.MinValue &&
                   tick <= KccOwnerMotionUntilTick;
        }

        public void OpenServerOwnerMotionWindow(
            float durationSeconds,
            uint operationId = 0)
        {
            if (durationSeconds <= 0f) return;

            int fromTick = CurrentTick;
            int untilTick = fromTick + SecondsToTicks(durationSeconds);
            int lastIndex = m_ServerMotionAuthorizationCount - 1;
            if (lastIndex >= 0)
            {
                ServerMotionAuthorization latest =
                    m_ServerMotionAuthorizations[lastIndex];
                bool sameOperation = operationId == 0 || latest.OperationId == 0 ||
                                     latest.OperationId == operationId;
                if (sameOperation && fromTick <= latest.UntilTick + 1)
                {
                    latest.UntilTick = Math.Max(latest.UntilTick, untilTick);
                    if (operationId != 0) latest.OperationId = operationId;
                    m_ServerMotionAuthorizations[lastIndex] = latest;
                    QueueServerMotionReplication(latest);
                    LogMotionWindow(
                        "server-refreshed",
                        latest.OperationId,
                        latest.FromTick,
                        latest.UntilTick);
                    return;
                }
            }

            if (m_ServerMotionAuthorizationCount ==
                ServerMotionAuthorizationCapacity)
            {
                Array.Copy(
                    m_ServerMotionAuthorizations,
                    1,
                    m_ServerMotionAuthorizations,
                    0,
                    ServerMotionAuthorizationCapacity - 1);
                m_ServerMotionAuthorizationCount--;
            }

            m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount++] =
                new ServerMotionAuthorization
                {
                    OperationId = operationId,
                    FromTick = fromTick,
                    UntilTick = untilTick
                };
            QueueServerMotionReplication(
                m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount - 1]);
            LogMotionWindow("server-opened", operationId, fromTick, untilTick);
        }

        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            int lastIndex = m_ServerMotionAuthorizationCount - 1;
            if (lastIndex < 0) return;

            ServerMotionAuthorization latest =
                m_ServerMotionAuthorizations[lastIndex];
            uint operationId = latest.OperationId;
            int closeTick = CurrentTick + SecondsToTicks(Mathf.Max(0f, graceSeconds));
            latest.UntilTick = Math.Min(latest.UntilTick, closeTick);
            if (graceSeconds <= 0f) latest.OperationId = 0;
            m_ServerMotionAuthorizations[lastIndex] = latest;
            QueueServerMotionReplication(latest);
            LogMotionWindow(
                "server-closed",
                operationId,
                latest.FromTick,
                latest.UntilTick);
        }

        public bool IsServerMotionTickAuthorized(int tick)
        {
            for (int i = m_ServerMotionAuthorizationCount - 1; i >= 0; i--)
            {
                ServerMotionAuthorization authorization =
                    m_ServerMotionAuthorizations[i];
                if (tick >= authorization.FromTick && tick <= authorization.UntilTick)
                {
                    return true;
                }
            }
            return m_NetworkSpawned && Object != null && Object.IsValid &&
                   KccServerMotionFromTick != int.MinValue &&
                   KccServerMotionUntilTick != int.MinValue &&
                   tick >= KccServerMotionFromTick &&
                   tick <= KccServerMotionUntilTick;
        }

        public bool TryGetAuthoritativePose(
            out Vector3 position,
            out Quaternion rotation)
        {
            IFusionKccRuntimeAdapter adapter = ResolveAdapter(false);
            if (adapter != null)
            {
                return adapter.TryGetAuthoritativePose(out position, out rotation);
            }

            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        public bool TryConsumeNetworkInput(NetworkRunner runner, NetworkInput input)
        {
            return ResolveAdapter(false)?.TryConsumeNetworkInput(runner, input) == true;
        }

        public bool TryGetNetworkInput(
            NetworkRunner runner,
            out FusionNativeCharacterInput characterInput)
        {
            IFusionKccRuntimeAdapter adapter = ResolveAdapter(false);
            if (adapter != null)
            {
                return adapter.TryGetNetworkInput(runner, out characterInput);
            }

            characterInput = default;
            return false;
        }

        public void AcceptSharedCharacterInput(
            PlayerRef source,
            int trustedSourceTick,
            Vector2 move,
            float yaw,
            int sourceTick,
            int flags,
            Vector3 ownerPosition)
        {
            ResolveAdapter(false)?.AcceptSharedCharacterInput(
                source,
                trustedSourceTick,
                move,
                yaw,
                sourceTick,
                flags,
                ownerPosition);
        }

        public void AcceptSharedCharacterTransient(
            PlayerRef source,
            int trustedSourceTick,
            Vector2 move,
            float yaw,
            int sourceTick,
            int flags,
            Vector3 ownerPosition,
            Vector3 rootMotionDelta,
            float rootMotionWeight,
            float jumpForce)
        {
            ResolveAdapter(false)?.AcceptSharedCharacterTransient(
                source,
                trustedSourceTick,
                move,
                yaw,
                sourceTick,
                flags,
                ownerPosition,
                rootMotionDelta,
                rootMotionWeight,
                jumpForce);
        }

        public void SimulateSharedLogicalOwnerProxyTick(
            int tick,
            bool restorePredictedPose)
        {
            if (!RequiresSharedLogicalOwnerProxyPump) return;
            ResolveAdapter(false)?.SimulateSharedLogicalOwnerProxyTick(
                tick,
                restorePredictedPose);
        }

        public void RenderSharedLogicalOwnerProxy()
        {
            if (!RequiresSharedLogicalOwnerProxyPump) return;
            ResolveAdapter(false)?.RenderSharedLogicalOwnerProxy();
        }

        private void OnDestroy()
        {
            if (!m_AdapterInitialized || m_RuntimeAdapter == null || m_Adapter == null) return;
            m_Adapter?.Shutdown();
            m_AdapterInitialized = false;
        }

        private void RestoreBuiltInControllerForFallback()
        {
            CharacterController controller = GetComponent<CharacterController>();
            if (controller == null || controller.enabled) return;

            controller.enabled = true;
            if (m_LogDiagnostics)
            {
                Debug.Log(
                    $"[FusionKCC] Re-enabled the root CharacterController on '{name}' " +
                    "because the optional KCC adapter did not provide a driver.",
                    this);
            }
        }

        private void CacheComponents(NetworkCharacter networkCharacter = null)
        {
            m_NetworkCharacter = networkCharacter != null
                ? networkCharacter
                : GetComponent<NetworkCharacter>();
            m_Identity = GetComponent<FusionNetworkIdentity>();
        }

        private void CacheFallbackScale()
        {
            Vector3 scale = transform != null ? transform.localScale : Vector3.one;
            if (IsValidScale(scale)) m_FallbackRootScale = scale;
        }

        private IFusionKccRuntimeAdapter ResolveAdapter(bool reportMissing)
        {
            if (m_Adapter != null &&
                m_RuntimeAdapter != null &&
                ReferenceEquals(m_Adapter, m_RuntimeAdapter) &&
                m_RuntimeAdapter.isActiveAndEnabled)
            {
                return m_Adapter;
            }

            m_Adapter = null;
            if (m_RuntimeAdapter != null)
            {
                if (m_RuntimeAdapter is IFusionKccRuntimeAdapter assignedAdapter)
                {
                    if (m_RuntimeAdapter.isActiveAndEnabled)
                    {
                        m_Adapter = assignedAdapter;
                        m_InvalidAdapterReported = false;
                        m_MissingAdapterReported = false;
                        return m_Adapter;
                    }

                    if (reportMissing && !m_InvalidAdapterReported)
                    {
                        m_InvalidAdapterReported = true;
                        Debug.LogError(
                            $"[FusionKCC] Assigned runtime adapter " +
                            $"'{m_RuntimeAdapter.GetType().FullName}' on '{name}' is disabled. " +
                            "Enable the nested motor and rerun Fusion prefab validation.",
                            this);
                    }
                }
                else if (!m_InvalidAdapterReported)
                {
                    m_InvalidAdapterReported = true;
                    Debug.LogError(
                        $"[FusionKCC] Assigned runtime adapter '{m_RuntimeAdapter.GetType().FullName}' " +
                        $"on '{name}' does not implement {nameof(IFusionKccRuntimeAdapter)}.",
                        this);
                }
            }

            MonoBehaviour[] behaviours = GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null || !behaviours[i].isActiveAndEnabled ||
                    ReferenceEquals(behaviours[i], this) ||
                    behaviours[i] is not IFusionKccRuntimeAdapter candidate)
                {
                    continue;
                }

                m_RuntimeAdapter = behaviours[i];
                m_Adapter = candidate;
                m_InvalidAdapterReported = false;
                m_MissingAdapterReported = false;
                if (m_LogDiagnostics)
                {
                    Debug.Log(
                        $"[FusionKCC] Resolved runtime adapter " +
                        $"'{behaviours[i].GetType().FullName}' on '{name}'.",
                        this);
                }
                return m_Adapter;
            }

            if (reportMissing && !m_MissingAdapterReported)
            {
                m_MissingAdapterReported = true;
                Debug.LogWarning(
                    $"[FusionKCC] '{name}' requests the FusionKCC prediction backend, but no " +
                    $"{nameof(IFusionKccRuntimeAdapter)} is available. Install/enable the " +
                    "optional Fusion Advanced KCC integration or choose another prediction backend.",
                    this);
            }

            return null;
        }

        private void ResetAuthoritativeCommandState()
        {
            m_HasPendingAuthoritativeTeleport = false;
            m_PendingTeleportFootPosition = Vector3.zero;
            m_PendingTeleportRotation = Quaternion.identity;
            m_PendingTeleportIsHard = false;
            m_HasPendingAuthoritativeScale = false;
            m_PendingAuthoritativeScale = Vector3.one;
            m_PendingMotorCommandFlags = 0;
        }

        private void ResetMotionWindows()
        {
            m_OwnerMotionUntilTick = int.MinValue;
            m_ServerMotionAuthorizationCount = 0;
            Array.Clear(
                m_ServerMotionAuthorizations,
                0,
                m_ServerMotionAuthorizations.Length);
            m_HasPendingOwnerMotionReplication = false;
            m_PendingOwnerMotionUntilTick = int.MinValue;
            m_HasPendingServerMotionReplication = false;
            m_PendingServerMotionAuthorization = default;
            m_RehydrateMotionWindowsOnAuthority = false;
        }

        private void QueueServerMotionReplication(
            ServerMotionAuthorization authorization)
        {
            if (!CanApplyAuthoritativeKccCommands) return;
            m_PendingServerMotionAuthorization = authorization;
            m_HasPendingServerMotionReplication = true;
        }

        private void RestoreReplicatedMotionWindows()
        {
            m_OwnerMotionUntilTick = Math.Max(
                m_OwnerMotionUntilTick,
                KccOwnerMotionUntilTick);

            m_ServerMotionAuthorizationCount = 0;
            Array.Clear(
                m_ServerMotionAuthorizations,
                0,
                m_ServerMotionAuthorizations.Length);
            if (KccServerMotionFromTick == int.MinValue ||
                KccServerMotionUntilTick == int.MinValue)
            {
                return;
            }

            m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount++] =
                new ServerMotionAuthorization
                {
                    OperationId = KccServerMotionOperationId,
                    FromTick = KccServerMotionFromTick,
                    UntilTick = KccServerMotionUntilTick
                };
        }

        private int CurrentTick => Runner != null ? Runner.Tick.Raw : 0;

        private int SecondsToTicks(float seconds)
        {
            float delta = Runner != null && Runner.DeltaTime > 0f
                ? Runner.DeltaTime
                : Mathf.Max(Time.fixedDeltaTime, 0.001f);
            return Mathf.Max(0, Mathf.CeilToInt(seconds / delta));
        }

        private void LogMotionWindow(
            string phase,
            uint operationId,
            int fromTick,
            int untilTick)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log(
                $"[FusionKCC] phase={phase} object='{name}' operation={operationId} " +
                $"fromTick={fromTick} untilTick={untilTick} currentTick={CurrentTick}",
                this);
        }

        private static int NextSequence(int current) =>
            current == int.MaxValue || current < 0 ? 1 : Math.Max(1, current + 1);

        private static bool IsValidScale(Vector3 value) =>
            IsFinite(value) && Mathf.Abs(value.x) > 0.000001f &&
            Mathf.Abs(value.y) > 0.000001f && Mathf.Abs(value.z) > 0.000001f;

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) &&
            IsFinite(value.w);

        private struct ServerMotionAuthorization
        {
            public uint OperationId;
            public int FromTick;
            public int UntilTick;
        }
    }
}
