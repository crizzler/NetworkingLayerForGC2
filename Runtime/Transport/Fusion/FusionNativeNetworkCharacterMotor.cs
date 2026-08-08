using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Fusion;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Per-player intent sampled by Fusion's input collector. Input Authority and State
    /// Authority both consume the same tick value, which lets Fusion prediction replay the
    /// GC2 CharacterController deterministically after an authoritative correction.
    /// </summary>
    public struct FusionNativeCharacterInput : INetworkInput
    {
        public const int FlagJump = 1;
        public const int FlagOwnerPose = 2;
        public const int FlagContinuousOwnerPose = 4;
        public const int FlagResetVerticalVelocity = 8;
        public const int FlagCollisionChanged = 16;
        public const int FlagCollisionEnabled = 32;

        public Vector2 Move;
        public float Yaw;
        public int SourceTick;
        public int Flags;
        public Vector3 OwnerPosition;
        public Vector3 RootMotionDelta;
        public float RootMotionWeight;
        public float JumpForce;

        public bool HasJump => (Flags & FlagJump) != 0;
        public bool HasOwnerPose => (Flags & FlagOwnerPose) != 0;
        public bool HasContinuousOwnerPose =>
            HasOwnerPose && (Flags & FlagContinuousOwnerPose) != 0;
        public bool HasResetVerticalVelocity =>
            (Flags & FlagResetVerticalVelocity) != 0;
        public bool HasCollisionChange => (Flags & FlagCollisionChanged) != 0;
        public bool CollisionEnabled => (Flags & FlagCollisionEnabled) != 0;
    }

    /// <summary>
    /// NetworkTRSP state plus the motion data needed to restore a GC2 movement tick.
    /// NetworkTRSPData must remain the first field so Fusion's spatial interest and native
    /// render interpolation can consume this behaviour exactly like NetworkTransform.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    [NetworkStructWeaved(WORDS)]
    public struct FusionNativeCharacterState : INetworkStruct
    {
        public const int EXTRA_WORDS = 17;
        public const int WORDS = NetworkTRSPData.WORDS + EXTRA_WORDS;

        private const int ExtraOffset = NetworkTRSPData.WORDS * Allocator.REPLICATE_WORD_SIZE;

        [FieldOffset(0)]
        public NetworkTRSPData TRSPData;

        [FieldOffset(ExtraOffset)]
        public Vector3 Velocity;

        [FieldOffset(ExtraOffset + 3 * Allocator.REPLICATE_WORD_SIZE)]
        public float VerticalSpeed;

        [FieldOffset(ExtraOffset + 4 * Allocator.REPLICATE_WORD_SIZE)]
        public int LastProcessedInputTick;

        [FieldOffset(ExtraOffset + 5 * Allocator.REPLICATE_WORD_SIZE)]
        public int MotionFlags;

        [FieldOffset(ExtraOffset + 6 * Allocator.REPLICATE_WORD_SIZE)]
        public int LastJumpTick;

        [FieldOffset(ExtraOffset + 7 * Allocator.REPLICATE_WORD_SIZE)]
        public int LastGroundedTick;

        [FieldOffset(ExtraOffset + 8 * Allocator.REPLICATE_WORD_SIZE)]
        public int LastAcceptedOwnerPoseTick;

        [FieldOffset(ExtraOffset + 9 * Allocator.REPLICATE_WORD_SIZE)]
        public int LastAppliedSharedSourceTick;

        [FieldOffset(ExtraOffset + 10 * Allocator.REPLICATE_WORD_SIZE)]
        public Vector2 LastContinuousMove;

        [FieldOffset(ExtraOffset + 12 * Allocator.REPLICATE_WORD_SIZE)]
        public float LastContinuousYaw;

        [FieldOffset(ExtraOffset + 13 * Allocator.REPLICATE_WORD_SIZE)]
        public int InputStateOwnerRaw;

        // GC2 interactive traversal drives its blend tree from semantic MoveToDirection
        // commands. That direction must not be inferred from per-tick displacement: the
        // Update-authored climb pose can legitimately be unchanged on an intervening Fusion
        // tick, which would otherwise replicate a false zero and flicker the remote blend tree.
        [FieldOffset(ExtraOffset + 14 * Allocator.REPLICATE_WORD_SIZE)]
        public Vector3 TraversalPresentationVelocity;
    }

    /// <summary>
    /// Fusion-native movement backend for a GC2 <see cref="NetworkCharacter"/>.
    ///
    /// Host/Server mode uses Fusion OnInput/GetInput, simulation ticks, prediction restore,
    /// resimulation and NetworkTRSP render interpolation. Shared mode has no Fusion Input
    /// Authority stream, so logical owners submit tick-stamped intent to the centralized
    /// Shared master while predicting the same intent locally. Transform snapshots are never
    /// sent through the Networking Layer RPC packet bridge.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Native Character Motor")]
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(NetworkCharacter))]
    [RequireComponent(typeof(FusionNetworkIdentity))]
    [NetworkBehaviourWeaved(FusionNativeCharacterState.WORDS)]
    public sealed unsafe class FusionNativeNetworkCharacterMotor : NetworkTRSP,
        INetworkTRSPTeleport,
        IBeforeAllTicks,
        IAfterAllTicks,
        IBeforeCopyPreviousState,
        IStateAuthorityChanged,
        INetworkCharacterPredictionBackend,
        INetworkAuthoritativePoseProvider,
        IFusionCharacterInputEndpoint,
        IFusionSharedCharacterEndpoint,
        IFusionSharedCharacterRunnerPump
    {
        private const int MotionFlagGrounded = 1;
        private const int MotionFlagJumping = 2;
        private const int MotionFlagTraversalPresentation = 4;
        private const float LiveOwnerPresentationHandoffSeconds = 0.75f;
        private const float LiveOwnerPresentationPositionTolerance = 0.025f;
        private const float LiveOwnerPresentationRotationTolerance = 1f;
        private const float LiveOwnerSimulationAdvancePositionTolerance = 0.005f;
        private const float LiveOwnerSimulationAdvanceRotationTolerance = 0.25f;
        private const float SharedPresentationPositionEpsilon = 0.0005f;

        public int LastAppliedSharedTransientSourceTick =>
            Object != null && Object.IsValid
                ? NativeState.LastAppliedSharedSourceTick
                : int.MinValue;
        private const float SharedPresentationRotationEpsilon = 0.05f;
        private const int SharedInputTickOffsetDiagnosticThreshold = 8;
        private const int SharedTransientReceiveBacklogCapacity = 128;

        [Header("Listen Host Presentation")]
        [Tooltip(
            "Optional direct child of the Character root that contains visuals only. " +
            "The listen host interpolates this child for remote players while the " +
            "authoritative CharacterController root remains on the current Fusion tick. " +
            "The GC2 Mannequin is used automatically only when it is a safe direct child.")]
        [SerializeField] private Transform m_ListenHostPresentationVisualRoot;

        [Header("Diagnostics")]
        [SerializeField] private bool m_LogDiagnostics;

        private ref FusionNativeCharacterState NativeState =>
            ref ReinterpretState<FusionNativeCharacterState>();

        private NetworkCharacter m_NetworkCharacter;
        private FusionNetworkIdentity m_Identity;
        private CharacterController m_Controller;
        private FusionNativeCharacterDriver m_Driver;
        private NetworkCharacter.NetworkRole m_Role;
        private NetworkSessionProfile m_Profile;

        private Tick m_InitialRenderTick;
        private bool m_BackendInitialized;
        private bool m_SpawnedObserved;
        private bool m_HasInitialState;
        private bool m_IsServer;
        private bool m_IsOwner;
        private bool m_IsHost;

        private Transform m_PresentationRoot;
        private Transform m_PresentationVisualRoot;
        private int m_PresentationOriginalSiblingIndex = -1;
        private Vector3 m_PresentationWorldPosition;
        private Quaternion m_PresentationWorldRotation;
        private Vector3 m_PresentationWorldScale = Vector3.one;
        private bool m_HasPresentationPose;
        private bool m_PresentationBeforeRenderSubscribed;
        private bool m_PresentationRootWarningIssued;
        private bool m_RootHasRenderPose;
        private int m_ExternalRootWritePresentationUntilTick = int.MinValue;
        private Vector3 m_LiveExternalPresentationPosition;
        private Quaternion m_LiveExternalPresentationRotation = Quaternion.identity;
        private Vector3 m_LiveExternalPresentationWorldScale = Vector3.one;
        private Vector3 m_LiveExternalPresentationLocalScale = Vector3.one;
        private float m_LiveExternalPresentationHoldUntil;
        private bool m_HasLiveExternalPresentationPose;

        // Shared logical owners without State Authority cannot use Fusion's replicated TRSP
        // buffers as their local predicted presentation. Retain two local simulation poses and
        // interpolate them with Fusion's local render alpha instead.
        private Vector3 m_SharedPreviousPredictedPosition;
        private Quaternion m_SharedPreviousPredictedRotation = Quaternion.identity;
        private Vector3 m_SharedPreviousPredictedScale = Vector3.one;
        private Vector3 m_SharedCurrentPredictedPosition;
        private Quaternion m_SharedCurrentPredictedRotation = Quaternion.identity;
        private Vector3 m_SharedCurrentPredictedScale = Vector3.one;
        private int m_SharedPredictedPoseTick = int.MinValue;
        private bool m_HasSharedPredictedPose;

        // Reconciliation must correct the CharacterController immediately, but presenting that
        // same correction immediately makes a Shared joiner visibly pulse whenever a master
        // snapshot arrives. Keep prediction error outside replicated/simulation state and decay
        // it only from the local render pose. This is the same separation Fusion's native
        // prediction lifecycle provides in Host mode while preserving centralized Shared
        // authority for projects that rely on the master validating player movement.
        private Vector3 m_SharedPresentationPositionError;
        private Quaternion m_SharedPresentationRotationError = Quaternion.identity;
        private Vector3 m_SharedPresentationFallbackPositionError;
        private Quaternion m_SharedPresentationFallbackRotationError = Quaternion.identity;
        private Vector3 m_LastSharedPresentedPosition;
        private Quaternion m_LastSharedPresentedRotation = Quaternion.identity;
        private bool m_HasSharedPresentationError;
        private bool m_SharedPresentationContinuityPending;
        private bool m_HasSharedPresentationFallback;
        private bool m_HasLastSharedPresentedPose;
        private int m_LastSharedPresentationDecayFrame = int.MinValue;

        private bool m_HasPendingExternalPosition;
        private bool m_HasPendingExternalRotation;
        private bool m_HasPendingExternalScale;
        private bool m_PendingExternalTeleport;
        private bool m_PendingExternalPositionIsAbsolute;
        private Vector3 m_PendingExternalPosition;
        private Vector3 m_PendingExternalPositionDelta;
        private Quaternion m_PendingExternalRotation;
        private Vector3 m_PendingExternalScale;
        private bool m_PendingExternalPositionCapturedByInput;
        private bool m_PendingExternalPositionApplied;
        private bool m_ForwardExternalChangesHandled;

        // External GC2 systems such as Traversal run from Unity's render frame. Keep a finite
        // recovery pose outside Fusion's replicated buffer so a malformed animation sample can
        // never make the Character root (and therefore its follow camera) unrecoverable.
        private Vector3 m_LastValidRootPosition;
        private Quaternion m_LastValidRootRotation = Quaternion.identity;
        private Vector3 m_LastValidRootScale = Vector3.one;
        private bool m_HasLastValidRootPose;
        private int m_LastInvalidPoseDiagnosticFrame = int.MinValue;
        private float m_NextOwnerPoseDiagnosticTime;
        private float m_NextOwnerPoseCollisionDiagnosticTime;
        private float m_NextOwnerMotionCaptureDiagnosticTime;
        private float m_NextOwnerMotionRejectionDiagnosticTime;
        private float m_NextOwnerMotionWindowDiagnosticTime;
        private float m_NextOwnerPredictionDiagnosticTime;
        private int m_OwnerPredictionResimulationTicks;

        // Shared mode intentionally keeps centralized authority. Fusion does not invoke the
        // regular input callback in Shared topology, so the owner submits intent to the master.
        private FusionNativeCharacterInput m_LatestSharedInput;
        private bool m_HasSharedInput;
        private int m_LatestSharedTrustedTick = int.MinValue;
        private int m_LastSharedPayloadTick = int.MinValue;
        private int m_LastQueuedSharedTransientTick = int.MinValue;
        private readonly Queue<SharedCharacterTransient> m_SharedTransientQueue =
            new Queue<SharedCharacterTransient>(16);
        private bool m_SharedTransientReceiveOverflowLatched;
        private readonly FusionNativeCharacterInput[] m_SharedPredictionHistory =
            new FusionNativeCharacterInput[128];
        private int m_SharedPredictionStart;
        private int m_SharedPredictionCount;
        private int m_LastSharedReconciledStateTick = int.MinValue;
        private int m_ObservedSharedTeleportKey;
        private bool m_HasObservedSharedTeleportKey;
        private PlayerRef m_ObservedLogicalOwner = PlayerRef.Invalid;
        private int m_LastSharedOwnerSimulationTick = int.MinValue;
        private bool m_SharedProxyPumpDiagnosticIssued;
        private bool m_SharedSubmitDiagnosticIssued;
        private bool m_ResetReplicatedOwnerInputStatePending;
        private float m_NextSharedInputDiagnosticTime;
        private float m_NextSharedTransientSubmitDiagnosticTime;
        private float m_NextSharedTransientReceiveDiagnosticTime;
        private float m_NextSharedTransientApplyDiagnosticTime;
        private float m_NextSharedTransientRejectionDiagnosticTime;
        private float m_NextSharedReconcileDiagnosticTime;

        public NetworkPredictionBackend Backend => NetworkPredictionBackend.FusionNative;
        public FusionNativeCharacterDriver Driver => m_Driver;
        public Transform ListenHostPresentationVisualRoot =>
            m_ListenHostPresentationVisualRoot;
        public bool UsesFusionInput => Runner != null && Runner.GameMode != GameMode.Shared;
        public bool UsesSharedIntentFallback => Runner != null && Runner.GameMode == GameMode.Shared;
        public bool RequiresSharedLogicalOwnerProxyPump => true;
        internal bool IsRemoteProxyRole =>
            m_Role == NetworkCharacter.NetworkRole.RemoteClient;
        internal int CurrentSimulationTick => Runner != null ? Runner.Tick.Raw : 0;
        internal float SimulationDeltaTime => Runner != null && Runner.DeltaTime > 0f
            ? Runner.DeltaTime
            : Mathf.Max(Time.fixedDeltaTime, 0.001f);
        internal bool ShouldGuardRemoteOwnerWrites =>
            Object != null && Object.IsValid && HasStateAuthority &&
            m_Identity != null && m_Identity.IsSpawned &&
            m_Identity.LogicalOwner.IsRealPlayer && !IsLocalLogicalOwner;

        private void Awake()
        {
            CacheComponents();
            RememberCurrentRootPose();
        }

        public IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            CacheComponents(networkCharacter);
            m_Role = role;

            if (m_Driver == null)
            {
                m_Driver = new FusionNativeCharacterDriver();
            }

            m_Driver.AttachMotor(this);
            TryInitializeNetworkState();

            return m_Driver;
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
            m_BackendInitialized = true;
            m_Driver?.AttachMotor(this);

            if (m_Profile != null) m_Driver?.ApplySessionProfile(m_Profile);
            TryInitializeNetworkState();
        }

        public void ApplySessionProfile(NetworkSessionProfile profile)
        {
            m_Profile = profile;
            m_Driver?.ApplySessionProfile(profile);
        }

        public bool TryGetAuthoritativePose(
            out Vector3 position,
            out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (!m_BackendInitialized || !m_HasInitialState ||
                Runner == null || !Runner.IsRunning ||
                Object == null || !Object.IsValid || !HasStateAuthority)
            {
                return false;
            }

            position = NativeState.TRSPData.Position;
            rotation = NativeState.TRSPData.Rotation;
            return IsFinite(position) && IsUsableRotation(rotation);
        }

        public void ResetBackend(NetworkCharacter networkCharacter)
        {
            RestorePresentationHierarchy();
            m_Driver?.ResetNetworkTransientState();
            m_BackendInitialized = false;
            m_HasInitialState = false;
            m_InitialRenderTick = default;
            m_IsServer = false;
            m_IsOwner = false;
            m_IsHost = false;
            m_Role = NetworkCharacter.NetworkRole.None;
            ResetSharedRuntimeState();
            m_ResetReplicatedOwnerInputStatePending = false;
            m_ObservedLogicalOwner = PlayerRef.Invalid;
            m_RootHasRenderPose = false;
            m_ExternalRootWritePresentationUntilTick = int.MinValue;
            m_NextOwnerPoseDiagnosticTime = 0f;
            m_NextOwnerPoseCollisionDiagnosticTime = 0f;
            m_NextOwnerMotionCaptureDiagnosticTime = 0f;
            m_NextOwnerMotionRejectionDiagnosticTime = 0f;
            m_NextOwnerMotionWindowDiagnosticTime = 0f;
            m_NextOwnerPredictionDiagnosticTime = 0f;
            ClearLiveExternalPresentationPose();
            ClearPendingExternalChanges();
        }

        public override void Spawned()
        {
            CacheComponents();
            SubscribeIdentity();
            // Fusion can pool a NetworkObject and invoke Spawned without a matching
            // NetworkCharacter reset. Never let the prior incarnation's input or external
            // movement queues cross this boundary.
            m_Driver?.ResetNetworkTransientState();
            ResetSharedRuntimeState();
            m_ResetReplicatedOwnerInputStatePending = false;
            ClearPendingExternalChanges();
            m_ExternalRootWritePresentationUntilTick = int.MinValue;
            m_NextOwnerPoseDiagnosticTime = 0f;
            m_NextOwnerPoseCollisionDiagnosticTime = 0f;
            m_NextOwnerMotionCaptureDiagnosticTime = 0f;
            m_NextOwnerMotionRejectionDiagnosticTime = 0f;
            m_NextOwnerMotionWindowDiagnosticTime = 0f;
            m_NextOwnerPredictionDiagnosticTime = 0f;
            ClearLiveExternalPresentationPose();
            m_RootHasRenderPose = false;
            m_HasInitialState = false;
            m_InitialRenderTick = default;
            m_SpawnedObserved = true;
            m_ObservedLogicalOwner = m_Identity != null
                ? m_Identity.LogicalOwner
                : PlayerRef.Invalid;
            TryInitializeNetworkState();
            Log($"spawned topology={Runner.Topology} mode={Runner.GameMode} " +
                $"stateAuthority={HasStateAuthority} inputAuthority={HasInputAuthority} " +
                $"logicalOwner={m_Identity?.LogicalOwner}");
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            RestorePresentationHierarchy();
            UnsubscribeIdentity();
            m_SpawnedObserved = false;
            m_HasInitialState = false;
            m_Driver?.ResetNetworkTransientState();
            ResetSharedRuntimeState();
            m_ResetReplicatedOwnerInputStatePending = false;
            ClearPendingExternalChanges();
            m_ObservedLogicalOwner = PlayerRef.Invalid;
            m_InitialRenderTick = default;
            m_RootHasRenderPose = false;
            m_ExternalRootWritePresentationUntilTick = int.MinValue;
            m_NextOwnerPoseDiagnosticTime = 0f;
            m_NextOwnerPoseCollisionDiagnosticTime = 0f;
            m_NextOwnerMotionCaptureDiagnosticTime = 0f;
            m_NextOwnerMotionRejectionDiagnosticTime = 0f;
            m_NextOwnerMotionWindowDiagnosticTime = 0f;
            m_NextOwnerPredictionDiagnosticTime = 0f;
            ClearLiveExternalPresentationPose();
        }

        private void OnDestroy()
        {
            RestorePresentationHierarchy();
            ClearLiveExternalPresentationPose();
            UnsubscribeIdentity();
        }

        private void OnDisable()
        {
            // Unity can disable a NetworkBehaviour without despawning its NetworkObject. Never
            // leave the temporary hierarchy or the static onBeforeRender subscription alive.
            RestorePresentationHierarchy();
            RestoreSharedPredictedSimulationPose();
            ResetSharedPredictedPresentation();
            m_LastSharedOwnerSimulationTick = int.MinValue;
            ClearLiveExternalPresentationPose();
        }

        public override void FixedUpdateNetwork()
        {
            if (!m_BackendInitialized || m_Driver == null || Runner == null || !Runner.IsRunning)
            {
                return;
            }

            ApplyPendingReplicatedOwnerInputReset();

            if (Runner.GameMode == GameMode.Shared)
            {
                FixedUpdateShared();
                return;
            }

            if (!HasStateAuthority && !HasInputAuthority) return;

            int tick = Runner.Tick.Raw;
            bool hasInput = GetInput(out FusionNativeCharacterInput input);
            bool shouldStoreContinuousInput = hasInput;
            if (!hasInput)
            {
                bool stateAuthorityNpc = HasStateAuthority &&
                                         (Object == null || !Object.InputAuthority.IsRealPlayer);
                if (stateAuthorityNpc)
                {
                    // Server-owned NPCs have no Fusion input stream. Their GC2 AI still feeds
                    // the driver's directional sink and must advance on the same native tick.
                    input = m_Driver.CaptureInput(tick);
                    shouldStoreContinuousInput = true;
                }
                else
                {
                    // Fusion can legitimately miss an unreliable input tick. Hold only the last
                    // replicated continuous steering so authority does not stop/start while the
                    // owner predicts. Never replay jump, owner pose, root motion, or force edges.
                    input = default;
                    input.SourceTick = tick;
                    input.Move = NativeState.LastContinuousMove;
                    input.Yaw = NativeState.LastContinuousYaw;
                }
            }

            // The Fusion simulation tick is authoritative metadata; never trust a tick number
            // embedded by a modified client for cooldown or owner-motion authorization checks.
            input.SourceTick = tick;
            if (shouldStoreContinuousInput)
            {
                StoreContinuousInput(input);
            }

            // GC2 Traversal authors an absolute owner pose from Unity Update and Fusion stores
            // that pose in the tick input. Applying the pending render-frame position before
            // Simulate would give the position two writers: the pending queue and OwnerPosition.
            // On a correcting client the queued change would first be rebased on the corrected
            // root, then the historical input would pull it back again. Let OwnerPosition be the
            // sole position writer for that forward tick. Generic external changes, teleports,
            // rotation and scale keep their existing admission path.
            Vector3 restoredTickPosition = transform.position;
            bool hadPendingExternalPosition = m_HasPendingExternalPosition;
            bool ownerPoseOwnsPosition = false;
            if (!Runner.IsResimulation && !m_ForwardExternalChangesHandled)
            {
                ownerPoseOwnsPosition =
                    IsLocalLogicalOwner && input.HasOwnerPose &&
                    m_HasPendingExternalPosition &&
                    !m_PendingExternalTeleport;
                ApplyPendingExternalPose(applyPosition: !ownerPoseOwnsPosition);
                m_ForwardExternalChangesHandled = true;
            }

            bool invokeEvents = !Runner.IsResimulation;
            m_Driver.Simulate(input, Runner.DeltaTime, HasStateAuthority, invokeEvents);
            NativeState.LastProcessedInputTick = input.SourceTick;
            UpdateMotionState();
            LogOwnerPredictionTick(
                hasInput,
                input,
                ownerPoseOwnsPosition,
                hadPendingExternalPosition,
                restoredTickPosition);
        }

        public override void Render()
        {
            if (!m_HasInitialState || Runner == null || m_Driver == null) return;

            if (Runner.Mode == SimulationModes.Server && m_PresentationRoot != null)
            {
                RestorePresentationHierarchy();
            }

            // A dedicated server never presents a character. Authority-owned remote players on a
            // listen host retain their simulation root and interpolate visuals only. Local owners
            // normally use Fusion's native root render lifecycle. During validated GC2 traversal,
            // the authored live pose presents the Character root and Mannequin together so the
            // LateUpdate follow camera and the visible character never run on different clocks.
            if (Runner.Mode != SimulationModes.Server)
            {
                bool locallySimulatedOwner =
                    IsLocalLogicalOwner && ShouldSimulateLocally;
                bool sharedPredictedOwner =
                    Runner.GameMode == GameMode.Shared && locallySimulatedOwner;
                bool useLiveOwnerPresentation =
                    locallySimulatedOwner && ShouldUseLiveExternalPresentationPose;
                bool requiresSimulationRootPresentation =
                    locallySimulatedOwner &&
                    (m_Driver.RequiresSimulationRootPresentation ||
                     IsExternalRootWritePresentationActive);
                bool shouldUsePresentationRoot =
                    (HasStateAuthority && !IsLocalLogicalOwner) ||
                    (!sharedPredictedOwner &&
                     requiresSimulationRootPresentation &&
                     !useLiveOwnerPresentation);

                if (useLiveOwnerPresentation)
                {
                    if (m_PresentationRoot != null) RestorePresentationHierarchy();
                    ApplyLiveExternalRootPresentationPose();
                }
                else if (!shouldUsePresentationRoot)
                {
                    if (sharedPredictedOwner && m_HasLiveExternalPresentationPose)
                    {
                        // The last traversal-authored root pose and the next predicted tick pose
                        // can sit on different clocks. Preserve the pose the local player
                        // actually saw and bridge the handoff in presentation only.
                        RequestSharedPresentationContinuity();
                    }
                    if (m_PresentationRoot != null) RestorePresentationHierarchy();
                    ClearLiveExternalPresentationPose();
                }

                if (useLiveOwnerPresentation)
                {
                    // The live root was already presented above. Do not let NetworkTRSP or the
                    // Shared interpolation path replace it with rollback history this frame.
                }
                else if (sharedPredictedOwner)
                {
                    RenderSharedPredictedOwner();
                }
                else if (shouldUsePresentationRoot)
                {
                    if (TryEnsurePresentationRoot())
                    {
                        NetworkTRSP.Render(
                            this,
                            m_PresentationRoot,
                            false,
                            false,
                            false,
                            ref m_InitialRenderTick);

                        RememberPresentationPose();
                    }
                    else if (!m_PresentationRootWarningIssued)
                    {
                        m_PresentationRootWarningIssued = true;
                        Debug.LogWarning(
                            $"[FusionNativeCharacterMotor] '{name}' has no safe, direct " +
                            "visual-only presentation root. The simulated Character root will " +
                            "remain tick-accurate and will not be interpolated. Assign the GC2 " +
                            "Mannequin (or another visual-only direct child) in Listen Host " +
                            "Presentation.",
                            this);
                    }

                    m_RootHasRenderPose = false;
                }
                else
                {
                    NetworkTRSP.Render(
                        this,
                        transform,
                        locallySimulatedOwner ? false : true,
                        false,
                        false,
                        ref m_InitialRenderTick);
                    m_RootHasRenderPose = locallySimulatedOwner;
                }
            }

            bool grounded = (NativeState.MotionFlags & MotionFlagGrounded) != 0;
            Vector3 renderVelocity = NativeState.Velocity;
            Vector3 traversalPresentationVelocity =
                NativeState.TraversalPresentationVelocity;
            bool traversalPresentationActive =
                (NativeState.MotionFlags & MotionFlagTraversalPresentation) != 0;
            if (TryGetSnapshotsBuffers(
                    out NetworkBehaviourBuffer fromBuffer,
                    out NetworkBehaviourBuffer toBuffer,
                    out float alpha))
            {
                FusionNativeCharacterState fromState =
                    fromBuffer.ReinterpretState<FusionNativeCharacterState>();
                FusionNativeCharacterState toState =
                    toBuffer.ReinterpretState<FusionNativeCharacterState>();
                renderVelocity = Vector3.Lerp(fromState.Velocity, toState.Velocity, alpha);
                bool fromTraversalPresentation =
                    (fromState.MotionFlags & MotionFlagTraversalPresentation) != 0;
                bool toTraversalPresentation =
                    (toState.MotionFlags & MotionFlagTraversalPresentation) != 0;
                if (fromTraversalPresentation && toTraversalPresentation)
                {
                    traversalPresentationVelocity = Vector3.Lerp(
                        fromState.TraversalPresentationVelocity,
                        toState.TraversalPresentationVelocity,
                        alpha);
                    traversalPresentationActive = true;
                }
                else
                {
                    bool useFromState = alpha < 0.5f;
                    traversalPresentationActive = useFromState
                        ? fromTraversalPresentation
                        : toTraversalPresentation;
                    traversalPresentationVelocity = useFromState
                        ? fromState.TraversalPresentationVelocity
                        : toState.TraversalPresentationVelocity;
                }
                grounded = alpha < 0.5f
                    ? (fromState.MotionFlags & MotionFlagGrounded) != 0
                    : (toState.MotionFlags & MotionFlagGrounded) != 0;
            }

            if (!ShouldSimulateLocally)
            {
                if (!IsFinite(traversalPresentationVelocity))
                {
                    traversalPresentationVelocity = Vector3.zero;
                }

                Vector3 presentationVelocity = traversalPresentationActive
                    ? traversalPresentationVelocity
                    : renderVelocity;
                UnitMotionNetworkController motionController =
                    m_NetworkCharacter?.MotionController;
                motionController?.ApplyReplicatedTraversalPresentationDirection(
                    traversalPresentationActive
                        ? traversalPresentationVelocity
                        : Vector3.zero);
                m_Driver.ApplyReplicatedMotion(presentationVelocity, grounded);
            }

            if (!IsLocalLogicalOwner)
            {
                m_NetworkCharacter?.NetworkFacingUnit?.OnServerYawReceived(
                    transform.eulerAngles.y);
            }
        }

        public void Teleport(Vector3? position = null, Quaternion? rotation = null)
        {
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority) return;

            bool controllerWasEnabled = m_Controller != null && m_Controller.enabled;
            if (controllerWasEnabled) m_Controller.enabled = false;
            NetworkTRSP.Teleport(this, transform, position, rotation);
            if (controllerWasEnabled) m_Controller.enabled = true;
            CopyToBuffer();
            if (Runner != null && Runner.GameMode == GameMode.Shared && IsLocalLogicalOwner)
            {
                ResetSharedPredictedPresentation();
                SeedSharedPredictedPose(Runner.Tick.Raw);
            }
        }

        internal bool IsInSimulationTick =>
            Runner != null && Runner.IsRunning && Runner.Stage != default;

        internal bool IsResimulating =>
            Runner != null && Runner.IsRunning && Runner.IsResimulation;

        /// <summary>
        /// Moves a locally predicted owner from its interpolated render pose back to the current
        /// simulation pose before a GC2 render-frame API is allowed to mutate the root. The visual
        /// hierarchy keeps the previous render pose, avoiding a visible pop while the validated
        /// motion window advances the tick-accurate CharacterController.
        /// </summary>
        internal void PrepareForExternalRootWrite()
        {
            if (transform == null || IsInSimulationTick || !m_HasInitialState ||
                !IsLocalLogicalOwner || !ShouldSimulateLocally)
            {
                return;
            }

            RememberLiveExternalPresentationPose();
            m_ExternalRootWritePresentationUntilTick = Math.Max(
                m_ExternalRootWritePresentationUntilTick,
                CurrentSimulationTick + 2);

            // Multiple GC2 mutations can be issued in one render frame. The first call restores
            // the simulation base; subsequent calls must accumulate on the already-mutated root.
            if (HasPendingExternalChanges) return;

            bool useCoherentLiveRoot = ShouldUseLiveExternalPresentationPose;
            if (useCoherentLiveRoot && m_PresentationRoot != null)
            {
                RestorePresentationHierarchy();
            }

            // A local GC2 traversal pose is rendered on the Character root itself. Keeping the
            // Mannequin behind in a wrapper here would make the root-follow camera advance one
            // frame before the visible character. The wrapper remains useful for ordinary
            // rollback/interpolation, but the validated live handoff must move root and visuals
            // together on one presentation clock.
            bool preserveRenderedVisual =
                m_RootHasRenderPose &&
                !useCoherentLiveRoot &&
                TryEnsurePresentationRoot();
            Vector3 presentationPosition = default;
            Quaternion presentationRotation = Quaternion.identity;
            Vector3 presentationScale = Vector3.one;
            if (preserveRenderedVisual)
            {
                presentationPosition = m_PresentationRoot.position;
                presentationRotation = m_PresentationRoot.rotation;
                presentationScale = m_PresentationRoot.lossyScale;
            }

            if (Runner != null && Runner.GameMode == GameMode.Shared && !HasStateAuthority)
            {
                RestoreSharedPredictedSimulationPose();
            }
            else
            {
                CopyToEngine(restoreMotion: true);
            }

            if (preserveRenderedVisual && m_PresentationRoot != null)
            {
                m_PresentationRoot.SetPositionAndRotation(
                    presentationPosition,
                    presentationRotation);
                SetWorldScale(m_PresentationRoot, presentationScale);
                RememberPresentationPose();
            }
        }

        /// <summary>
        /// Captures GC2 movement APIs that are invoked by Instructions, Traversal, reactions,
        /// or root-motion handlers outside FixedUpdateNetwork. Without this handoff the next
        /// prediction restore would correctly restore the prior tick, but accidentally erase
        /// the newly requested gameplay displacement before it can enter Fusion state.
        /// </summary>
        internal void NotifyExternalPositionChanged(bool teleport)
        {
            if (transform == null || IsInSimulationTick) return;

            if (!IsFinite(transform.position))
            {
                RecoverInvalidEnginePose("external absolute position");
                return;
            }

            m_PendingExternalPosition = transform.position;
            m_HasPendingExternalPosition = true;
            m_PendingExternalPositionIsAbsolute = true;
            m_PendingExternalPositionDelta = Vector3.zero;
            m_PendingExternalTeleport |= teleport;
            m_PendingExternalPositionCapturedByInput = false;
            m_PendingExternalPositionApplied = false;
            RememberLiveExternalPresentationPose();

            if (teleport && Object != null && Object.IsValid && HasStateAuthority)
            {
                bool controllerWasEnabled = m_Controller != null && m_Controller.enabled;
                if (controllerWasEnabled) m_Controller.enabled = false;
                NetworkTRSP.Teleport(
                    this,
                    transform,
                    m_PendingExternalPosition,
                    null);
                if (controllerWasEnabled) m_Controller.enabled = true;
            }

            if (teleport && Runner != null && Runner.GameMode == GameMode.Shared &&
                IsLocalLogicalOwner)
            {
                ResetSharedPredictedPresentation();
                SeedSharedPredictedPose(Runner.Tick.Raw);
            }
        }

        internal void NotifyExternalPositionTarget(Vector3 position)
        {
            if (transform == null || IsInSimulationTick || !IsFinite(position)) return;

            // PrepareForExternalRootWrite restores the simulation root before GC2 sweeps the
            // CharacterController. Store the achieved world endpoint, not a relative delta.
            // A client rollback can change the restored base; replaying an old delta on that new
            // base shifts a zipline pose and creates a visible reverse correction. An absolute
            // endpoint is idempotent across restore/resimulation and successive AddPosition calls
            // simply coalesce to the newest GC2-authored pose.
            m_PendingExternalPosition = position;
            m_PendingExternalPositionDelta = Vector3.zero;
            m_HasPendingExternalPosition = true;
            m_PendingExternalPositionIsAbsolute = true;
            m_PendingExternalPositionCapturedByInput = false;
            m_PendingExternalPositionApplied = false;
            RememberLiveExternalPresentationPose();
        }

        internal void NotifyExternalRotationChanged(bool teleport)
        {
            if (transform == null || IsInSimulationTick) return;

            if (!IsUsableRotation(transform.rotation))
            {
                RecoverInvalidEnginePose("external rotation");
                return;
            }

            m_PendingExternalRotation = transform.rotation;
            m_HasPendingExternalRotation = true;
            m_PendingExternalTeleport |= teleport;
            RememberLiveExternalPresentationPose();

            if (teleport && Object != null && Object.IsValid && HasStateAuthority)
            {
                NetworkTRSP.Teleport(
                    this,
                    transform,
                    null,
                    m_PendingExternalRotation);
            }
        }

        internal void NotifyExternalScaleChanged()
        {
            if (transform == null || IsInSimulationTick) return;
            if (!IsFinite(transform.localScale))
            {
                RecoverInvalidEnginePose("external scale");
                return;
            }
            m_PendingExternalScale = transform.localScale;
            m_HasPendingExternalScale = true;
            RememberLiveExternalPresentationPose();
        }

        internal bool EnsureFiniteEnginePose(string context)
        {
            if (HasUsableRootPose(
                    transform.position,
                    transform.rotation,
                    transform.localScale))
            {
                RememberCurrentRootPose();
                return true;
            }

            return RecoverInvalidEnginePose(context);
        }

        void IBeforeAllTicks.BeforeAllTicks(bool resimulation, int tickCount)
        {
            TryInitializeNetworkState();
            if (!m_HasInitialState) return;
            if (!ShouldSimulateLocally)
            {
                // Render leaves ordinary proxies on an interpolated past pose. Restore their
                // current-tick TRSP before prediction/resimulation so local CharacterController
                // collision queries do not disagree with authority near players or walls.
                CopyToEngine(restoreMotion: false);
                return;
            }

            // Pending GC2 Update mutations belong to the present render frame. Do not apply
            // them to a historical replay, but retain them for the forward tick Fusion runs
            // after resimulation. Clearing here would silently lose traversal/root motion
            // whenever an authoritative correction and a render-frame write coincide.

            // Shared logical owners do not have Fusion Input Authority, so Fusion cannot
            // automatically replay their local state. Their explicit prediction history is
            // reconciled in FixedUpdateShared when a newer master acknowledgement arrives.
            if (Runner != null && Runner.GameMode == GameMode.Shared &&
                IsLocalLogicalOwner && !HasStateAuthority)
            {
                RestoreSharedPredictedSimulationPose();
                return;
            }

            CopyToEngine(restoreMotion: true);
            if (!resimulation)
            {
                // Forward admission happens inside FixedUpdateNetwork after GetInput. That is
                // the only point where we can know whether an absolute OwnerPosition already
                // owns this tick's traversal displacement.
                m_ForwardExternalChangesHandled = false;
            }
        }

        void IAfterAllTicks.AfterAllTicks(bool resimulation, int tickCount)
        {
            TryInitializeNetworkState();
            if (!m_HasInitialState) return;
            if (!ShouldWritePredictionState) return;
            CopyToBuffer();
            if (!resimulation)
            {
                // Input collection can occur after Fusion restored a historical pose. Retain a
                // local GC2 endpoint for the next collection pass if this forward loop neither
                // sampled it into OwnerPosition nor applied it through the generic fallback.
                ClearPendingExternalChanges(
                    preserveUnconsumedPosition: IsLocalLogicalOwner);
            }
        }

        void IBeforeCopyPreviousState.BeforeCopyPreviousState()
        {
            TryInitializeNetworkState();
            if (!m_HasInitialState) return;
            if (!ShouldWritePredictionState) return;

            // GC2 Traversal and other animation systems can move the CharacterController between
            // Fusion ticks. The pending endpoint is consumed after GetInput, while NativeState
            // intentionally remains at the old endpoint. Copying the render-frame engine pose here
            // would overwrite Current immediately before Fusion freezes it into Previous, so
            // Previous and the post-tick Current collapse to the same endpoint. That removes render
            // interpolation; after a client correction it can instead create a future -> old pair
            // and a visible reverse step at a zipline endpoint.
            //
            // With no pending render-frame write this remains the standard NetworkTRSP lifecycle:
            // synchronize the engine pose before Fusion freezes Current into Previous.
            if (HasPendingExternalChanges) return;
            CopyToBuffer();
        }

        private void FixedUpdateShared()
        {
            bool localOwner = IsLocalLogicalOwner;
            int tick = Runner.Tick.Raw;

            if (HasStateAuthority)
            {
                if (localOwner)
                {
                    // BeforeAllTicks restored the pre-displacement simulation endpoint and
                    // BeforeCopyPreviousState deliberately skipped the pending GC2 write.
                    // Preserve that old endpoint as Shared presentation Previous; the pending
                    // world target is fed into Simulate below as the tick's sole position writer.
                    if (HasPendingExternalChanges)
                    {
                        CaptureSharedPredictedPreviousStatePose(tick);
                    }
                    else
                    {
                        CaptureSharedPredictedPreviousPose(tick);
                    }
                }

                FusionNativeCharacterInput input;
                bool updateProcessedInputTick = true;
                bool remoteSharedInput = false;
                bool appliedSharedTransient = false;
                int sharedPayloadTick = int.MinValue;
                if (localOwner)
                {
                    input = m_Driver.CaptureInput(tick);
                }
                else if (m_SharedTransientQueue.Count > 0)
                {
                    // Reliable RPC callbacks can arrive in a burst before this behaviour's next
                    // simulation callback. Consume exactly one ordered sample per authority tick
                    // instead of overwriting all but the newest Vault/Jump/root-motion delta.
                    SharedCharacterTransient transient = m_SharedTransientQueue.Dequeue();
                    input = transient.Input;
                    remoteSharedInput = true;
                    appliedSharedTransient = true;
                    sharedPayloadTick = input.SourceTick;

                    // Keep gameplay authorization on authenticated Fusion time while the owner
                    // payload tick remains an acknowledgement sequence only.
                    input.SourceTick = transient.TrustedTick;
                }
                else if (m_HasSharedInput)
                {
                    input = m_LatestSharedInput;
                    remoteSharedInput = true;
                    sharedPayloadTick = input.SourceTick;

                    // The payload tick is useful only as the owner's monotonic sequence and
                    // acknowledgement. RpcInfo.Tick is Fusion-authenticated metadata and is
                    // therefore the only tick allowed to drive jump cooldowns or traversal
                    // authorization on State Authority.
                    input.SourceTick = m_LatestSharedTrustedTick;
                }
                else if (m_Identity == null ||
                         !m_Identity.LogicalOwner.IsRealPlayer)
                {
                    // Centralized Shared-mode NPCs are owned by the master and therefore have
                    // no client intent RPC. Sample their GC2 AI intent directly on the tick.
                    input = m_Driver.CaptureInput(tick);
                }
                else
                {
                    input = default;
                    input.SourceTick = tick;
                    input.Yaw = transform.eulerAngles.y;
                    // No owner input has been processed, so do not acknowledge this authority
                    // tick. Advancing the acknowledgement would make the owner discard predicted
                    // inputs that an unreliable/tick-aligned RPC has not delivered yet.
                    updateProcessedInputTick = false;
                }

                if (localOwner)
                {
                    PrepareSharedLocalExternalPose(ref input);
                }

                m_Driver.Simulate(input, Runner.DeltaTime, authoritative: true,
                    invokeGameplayEvents: !Runner.IsResimulation);
                if (updateProcessedInputTick)
                {
                    NativeState.LastProcessedInputTick = remoteSharedInput
                        ? AdvanceSharedProcessedSourceTick(sharedPayloadTick)
                        : input.SourceTick;
                }
                if (appliedSharedTransient)
                {
                    // This is an application acknowledgement, not a receipt acknowledgement.
                    // Reliable ordering guarantees every earlier transient was dequeued first.
                    NativeState.LastAppliedSharedSourceTick = sharedPayloadTick;
                    FusionNativeCharacterInput acknowledgedInput = input;
                    acknowledgedInput.SourceTick = sharedPayloadTick;
                    LogSharedTransient(
                        "applied-and-acknowledged",
                        acknowledgedInput,
                        input.SourceTick,
                        m_SharedTransientQueue.Count);
                }
                else if (localOwner && HasSharedTransientInput(input))
                {
                    // The Shared master can also be this character's logical owner. Its
                    // locally captured one-shot bypasses the RPC receive queue, but still
                    // needs the same application acknowledgement so the identity can retire
                    // its migration-safe retry entry.
                    NativeState.LastAppliedSharedSourceTick = input.SourceTick;
                }
                UpdateMotionState();
                if (localOwner) CaptureSharedPredictedCurrentPose(tick);
                return;
            }

            if (!localOwner) return;
            SimulateSharedLogicalOwnerProxyTick(tick, restorePredictedPose: false);
        }

        private int AdvanceSharedProcessedSourceTick(int latestPayloadTick)
        {
            int representedTick = NativeState.LastProcessedInputTick;
            if (representedTick == int.MinValue) return latestPayloadTick;

            // State Authority holds continuous Move/Yaw when an unreliable Shared RPC is
            // missing. The replicated baseline must advance with that held simulation too;
            // otherwise the owner restores a newer snapshot and replays the same latency span.
            // LastAppliedSharedSourceTick remains the exact payload acknowledgement used for
            // one-shot traversal/jump/root-motion history.
            long projectedTick = (long)representedTick + 1L;
            return (int)Math.Min(
                int.MaxValue,
                Math.Max((long)latestPayloadTick, projectedTick));
        }

        /// <summary>
        /// Advances a master-owned Shared character for its logical owner. Fusion 2 does not
        /// execute NetworkBehaviour simulation callbacks on Shared proxies, and a Shared peer
        /// cannot opt a foreign-State-Authority object into simulation. FusionRpcRouter therefore
        /// invokes this from its runner-level simulation callback. The ordinary behaviour path
        /// also calls it for forward compatibility; the tick guard prevents two writers.
        /// </summary>
        public void SimulateSharedLogicalOwnerProxyTick(
            int tick,
            bool restorePredictedPose)
        {
            if (!isActiveAndEnabled) return;
            TryInitializeNetworkState();
            if (!m_BackendInitialized || !m_HasInitialState || m_Driver == null ||
                Runner == null || !Runner.IsRunning || Runner.GameMode != GameMode.Shared ||
                Runner.IsResimulation ||
                Object == null || !Object.IsValid || HasStateAuthority ||
                !IsLocalLogicalOwner || tick == m_LastSharedOwnerSimulationTick)
            {
                return;
            }

            m_LastSharedOwnerSimulationTick = tick;
            if (restorePredictedPose)
            {
                // IBeforeAllTicks is skipped together with FixedUpdateNetwork on a proxy. Move
                // the interpolated/rendered root back to the latest predicted simulation endpoint
                // before reconciliation and this tick's CharacterController sweep.
                RestoreSharedPredictedSimulationPose();
            }

            if (!m_SharedProxyPumpDiagnosticIssued)
            {
                m_SharedProxyPumpDiagnosticIssued = true;
                Log($"Shared logical-owner tick pump active tick={tick} " +
                    $"localPlayer={Runner.LocalPlayer} logicalOwner={m_Identity?.LogicalOwner} " +
                    $"isInSimulation={Object.IsInSimulation} restorePose={restorePredictedPose}");
            }

            // Reconciliation restores the last master snapshot and replays unacknowledged
            // intent. Admit this frame's external GC2 pose only after that restore, otherwise an
            // acknowledgement received on the same tick overwrites traversal/root motion before
            // CaptureInput can place it in Shared prediction history.
            bool resetPresentationHistory = ReconcileSharedPrediction(
                out bool authoritativeTeleport);
            if (resetPresentationHistory)
            {
                ResetSharedPredictedPresentation();
            }
            if (authoritativeTeleport)
            {
                // A command authored against the pre-teleport pose is no longer meaningful.
                ClearPendingExternalChanges();
            }

            // Reconciliation/BeforeAllTicks restored the current predicted simulation endpoint.
            // Capture it before Simulate consumes this render frame's traversal target so Shared
            // rendering interpolates old -> new instead of new -> new.
            CaptureSharedPredictedPreviousPose(tick);
            FusionNativeCharacterInput localInput = m_Driver.CaptureInput(tick);
            PrepareSharedLocalExternalPose(ref localInput);
            m_Driver.Simulate(localInput, Runner.DeltaTime, authoritative: false,
                invokeGameplayEvents: !Runner.IsResimulation);
            AppendSharedPrediction(localInput);
            CaptureSharedPredictedCurrentPose(tick);

            // This NetworkTRSP reserves its state explicitly with NetworkBehaviourWeaved.
            // Fusion deliberately skips RPC weaving for manually-weaved behaviours, so the
            // normally-woven identity owns the wire RPC and forwards authenticated input here.
            RpcInvokeInfo invokeInfo = default;
            bool submitted = m_Identity != null &&
                m_Identity.TrySubmitSharedCharacterInput(
                    localInput,
                    out invokeInfo);
            if (submitted && HasSharedTransientInput(localInput))
            {
                // The identity owns the woven reliable RPC and retains a culled send for retry.
                // This owner-side phase, paired with the master's reliably-enqueued and
                // applied-and-acknowledged phases, identifies exactly where a Vault/Jump sample
                // stops without logging every continuous locomotion tick.
                LogSharedTransient(
                    "owner-queued-for-reliable-send",
                    localInput,
                    tick,
                    queuedCount: -1);
            }
            if (!m_SharedSubmitDiagnosticIssued)
            {
                m_SharedSubmitDiagnosticIssued = true;
                Log($"first Shared input submission tick={tick} submitted={submitted} " +
                    $"move={localInput.Move} yaw={localInput.Yaw:F1} rpc={invokeInfo}");
            }

            // Shared predicted owners do not write Fusion state in AfterAllTicks. Once the
            // external pose has been captured into this tick's intent/history, retaining it
            // would reapply the same absolute pose before every subsequent simulation tick.
            ClearPendingExternalChanges();
        }

        /// <summary>
        /// Supplies the render callback Fusion omits for a non-simulated Shared proxy. This is
        /// called only from the runner-level router while the local player object is not in the
        /// Fusion simulation set.
        /// </summary>
        public void RenderSharedLogicalOwnerProxy()
        {
            if (!isActiveAndEnabled ||
                !m_BackendInitialized || !m_HasInitialState || m_Driver == null ||
                Runner == null || !Runner.IsRunning || Runner.GameMode != GameMode.Shared ||
                Object == null || !Object.IsValid || HasStateAuthority ||
                !IsLocalLogicalOwner)
            {
                return;
            }

            Render();
        }

        private bool ReconcileSharedPrediction(out bool authoritativeTeleport)
        {
            authoritativeTeleport = false;
            if (Object == null || !Object.IsValid || Object.LastReceiveTick == default)
            {
                return false;
            }

            int teleportKey = NativeState.TRSPData.TeleportKey;
            authoritativeTeleport =
                m_HasObservedSharedTeleportKey &&
                teleportKey != m_ObservedSharedTeleportKey;
            m_ObservedSharedTeleportKey = teleportKey;
            m_HasObservedSharedTeleportKey = true;

            // LastReceiveTick identifies a new authoritative snapshot. The state carries two
            // owner-clock baselines: LastProcessedInputTick advances while authority holds
            // continuous Move/Yaw, whereas LastAppliedSharedSourceTick acknowledges only an
            // actually received payload. This distinction prevents both double-replayed walking
            // and premature deletion of in-flight Vault/Jump/root-motion samples.
            int authoritativeStateTick = Object.LastReceiveTick.Raw;
            if (authoritativeStateTick == m_LastSharedReconciledStateTick &&
                !authoritativeTeleport)
            {
                return false;
            }
            if (m_LastSharedReconciledStateTick != int.MinValue &&
                authoritativeStateTick < m_LastSharedReconciledStateTick)
            {
                return false;
            }

            Vector3 predictedPosition = transform.position;
            Quaternion predictedRotation = transform.rotation;
            m_LastSharedReconciledStateTick = authoritativeStateTick;
            CopyToEngine(restoreMotion: true);

            if (authoritativeTeleport)
            {
                // Unacknowledged inputs were authored in the pre-teleport coordinate context.
                // In particular, an owner-motion entry can contain an absolute world pose and
                // would otherwise pull the character straight back after CopyToEngine applied
                // the authoritative teleport.
                m_SharedPredictionStart = 0;
                m_SharedPredictionCount = 0;
                return true;
            }

            int historyCountBefore = m_SharedPredictionCount;
            int representedContinuousTick = NativeState.LastProcessedInputTick;
            int acknowledgedTransientTick = NativeState.LastAppliedSharedSourceTick;
            int replayedContinuousCount = 0;
            int replayedTransientCount = 0;
            int writeCount = 0;
            for (int i = 0; i < historyCountBefore; i++)
            {
                int index = (m_SharedPredictionStart + i) % m_SharedPredictionHistory.Length;
                FusionNativeCharacterInput predicted = m_SharedPredictionHistory[index];
                bool replayContinuous =
                    predicted.SourceTick > representedContinuousTick;
                bool hasTransient = HasSharedTransientInput(predicted);
                bool replayTransient =
                    hasTransient &&
                    predicted.SourceTick > acknowledgedTransientTick;
                if (!replayContinuous && !replayTransient) continue;

                FusionNativeCharacterInput replay = predicted;
                if (!replayContinuous)
                {
                    // The restored snapshot already contains held Move/Yaw for this owner tick.
                    // Reapply only the still-unacknowledged one-shot displacement.
                    replay.Move = Vector2.zero;
                    replay.Yaw = transform.eulerAngles.y;
                }
                else
                {
                    replayedContinuousCount++;
                }

                if (!replayTransient)
                {
                    ClearSharedTransientInput(ref replay);
                }
                else
                {
                    replayedTransientCount++;
                }

                m_Driver.Simulate(
                    replay,
                    Runner.DeltaTime,
                    authoritative: false,
                    invokeGameplayEvents: false);

                int destination =
                    (m_SharedPredictionStart + writeCount) % m_SharedPredictionHistory.Length;
                m_SharedPredictionHistory[destination] = predicted;
                writeCount++;
            }

            m_SharedPredictionCount = writeCount;

            float snapDistance = m_Profile != null
                ? Mathf.Max(0.1f, m_Profile.maxReconciliationDistance)
                : 3f;
            bool largeCorrection =
                IsFinite(predictedPosition) &&
                IsFinite(transform.position) &&
                Vector3.Distance(predictedPosition, transform.position) > snapDistance;

            if (!largeCorrection)
            {
                QueueSharedPresentationCorrection(
                    predictedPosition,
                    predictedRotation,
                    transform.position,
                    transform.rotation);
            }

            LogSharedReconciliation(
                authoritativeStateTick,
                representedContinuousTick,
                acknowledgedTransientTick,
                historyCountBefore,
                writeCount,
                replayedContinuousCount,
                replayedTransientCount,
                predictedPosition,
                transform.position,
                largeCorrection ? "snap" :
                    m_SharedPresentationContinuityPending ? "smooth" : "none");

            return authoritativeTeleport || largeCorrection;
        }

        internal static bool HasSharedTransientInput(FusionNativeCharacterInput input)
        {
            return FusionCharacterInputUtility.HasSharedTransientInput(input);
        }

        private static void ClearSharedTransientInput(
            ref FusionNativeCharacterInput input)
        {
            input.Flags &= ~(FusionNativeCharacterInput.FlagJump |
                             FusionNativeCharacterInput.FlagResetVerticalVelocity |
                             FusionNativeCharacterInput.FlagCollisionChanged |
                             FusionNativeCharacterInput.FlagCollisionEnabled);
            if (!input.HasContinuousOwnerPose)
            {
                input.Flags &= ~(FusionNativeCharacterInput.FlagOwnerPose |
                                 FusionNativeCharacterInput.FlagContinuousOwnerPose);
                input.OwnerPosition = Vector3.zero;
            }
            input.RootMotionDelta = Vector3.zero;
            input.RootMotionWeight = 0f;
            input.JumpForce = 0f;
        }

        private void AppendSharedPrediction(FusionNativeCharacterInput input)
        {
            if (m_SharedPredictionCount < m_SharedPredictionHistory.Length)
            {
                int index =
                    (m_SharedPredictionStart + m_SharedPredictionCount) %
                    m_SharedPredictionHistory.Length;
                m_SharedPredictionHistory[index] = input;
                m_SharedPredictionCount++;
                return;
            }

            m_SharedPredictionHistory[m_SharedPredictionStart] = input;
            m_SharedPredictionStart =
                (m_SharedPredictionStart + 1) % m_SharedPredictionHistory.Length;
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
            if (Runner == null || Runner.GameMode != GameMode.Shared || !HasStateAuthority)
            {
                return;
            }

            if (m_Identity == null || !m_Identity.IsSpawned ||
                source != m_Identity.LogicalOwner)
            {
                Log($"rejected Shared input source={source} owner={m_Identity?.LogicalOwner}");
                return;
            }

            // Runner.Tick captured by the logical-owner proxy and RpcInfo.Tick belong to
            // independently corrected Shared-peer clocks. Their offset can legitimately drift
            // over a long session (the reported run reached 9-12 ticks), so an absolute
            // zero-offset admission rule eventually rejects every valid packet. The payload
            // tick is used only as a monotonic owner sequence; all gameplay authorization uses
            // the authenticated RPC tick below.
            if (sourceTick <= m_LastSharedPayloadTick) return;
            if (!IsFinite(move) || !IsFinite(yaw) || !IsFinite(ownerPosition)) return;

            long tickOffset = (long)sourceTick - trustedSourceTick;
            if (Math.Abs(tickOffset) > SharedInputTickOffsetDiagnosticThreshold)
            {
                float now = Time.unscaledTime;
                if (now >= m_NextSharedInputDiagnosticTime)
                {
                    m_NextSharedInputDiagnosticTime = now + 2f;
                    Log(
                        $"accepted Shared input with independent-clock offset " +
                        $"payloadTick={sourceTick} trustedTick={trustedSourceTick} " +
                        $"offset={tickOffset} diagnosticThreshold=" +
                        $"{SharedInputTickOffsetDiagnosticThreshold}");
                }
            }

            bool firstAcceptedInput = !m_HasSharedInput;
            if (move.sqrMagnitude > 1f) move.Normalize();
            bool hasContinuousOwnerPose =
                (flags & FusionNativeCharacterInput.FlagOwnerPose) != 0 &&
                (flags & FusionNativeCharacterInput.FlagContinuousOwnerPose) != 0;
            m_LatestSharedInput = new FusionNativeCharacterInput
            {
                Move = move,
                Yaw = Mathf.Repeat(yaw, 360f),
                SourceTick = sourceTick,
                // Only replaceable interactive-traversal poses may use the unreliable/latest
                // stream. Vault, Jump, PullUp and root-motion edges remain on the separately
                // ordered reliable channel below.
                Flags = hasContinuousOwnerPose
                    ? FusionNativeCharacterInput.FlagOwnerPose |
                      FusionNativeCharacterInput.FlagContinuousOwnerPose
                    : 0,
                OwnerPosition = hasContinuousOwnerPose ? ownerPosition : Vector3.zero,
                RootMotionDelta = Vector3.zero,
                RootMotionWeight = 0f,
                JumpForce = 0f
            };
            m_LatestSharedTrustedTick = trustedSourceTick;
            m_LastSharedPayloadTick = sourceTick;
            m_HasSharedInput = true;
            if (firstAcceptedInput)
            {
                Log($"accepted first Shared input source={source} payloadTick={sourceTick} " +
                    $"trustedTick={trustedSourceTick} offset={tickOffset} " +
                    $"move={move} yaw={m_LatestSharedInput.Yaw:F1}");
            }
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
            if (Runner == null || Runner.GameMode != GameMode.Shared || !HasStateAuthority)
            {
                return;
            }

            if (m_Identity == null || !m_Identity.IsSpawned ||
                source != m_Identity.LogicalOwner)
            {
                LogSharedTransientRejection(
                    "logical-owner-mismatch",
                    source,
                    trustedSourceTick,
                    sourceTick,
                    flags,
                    ownerPosition,
                    rootMotionDelta,
                    rootMotionWeight,
                    jumpForce);
                return;
            }

            // Reliable and unreliable RPC channels do not share an ordering boundary. Keep a
            // separate monotonic sequence so continuous p+1 cannot discard transient p.
            if (sourceTick <= m_LastQueuedSharedTransientTick)
            {
                LogSharedTransientRejection(
                    $"non-monotonic(last={m_LastQueuedSharedTransientTick})",
                    source,
                    trustedSourceTick,
                    sourceTick,
                    flags,
                    ownerPosition,
                    rootMotionDelta,
                    rootMotionWeight,
                    jumpForce);
                return;
            }
            if (sourceTick <= LastAppliedSharedTransientSourceTick)
            {
                LogSharedTransientRejection(
                    $"already-applied(last={LastAppliedSharedTransientSourceTick})",
                    source,
                    trustedSourceTick,
                    sourceTick,
                    flags,
                    ownerPosition,
                    rootMotionDelta,
                    rootMotionWeight,
                    jumpForce);
                return;
            }
            if (!IsFinite(move) || !IsFinite(yaw) || !IsFinite(ownerPosition) ||
                !IsFinite(rootMotionDelta) || !IsFinite(rootMotionWeight) ||
                !IsFinite(jumpForce))
            {
                LogSharedTransientRejection(
                    "non-finite-payload",
                    source,
                    trustedSourceTick,
                    sourceTick,
                    flags,
                    ownerPosition,
                    rootMotionDelta,
                    rootMotionWeight,
                    jumpForce);
                return;
            }

            if (move.sqrMagnitude > 1f) move.Normalize();
            FusionNativeCharacterInput input = new FusionNativeCharacterInput
            {
                Move = move,
                Yaw = Mathf.Repeat(yaw, 360f),
                SourceTick = sourceTick,
                Flags = flags & (FusionNativeCharacterInput.FlagJump |
                                 FusionNativeCharacterInput.FlagOwnerPose |
                                 FusionNativeCharacterInput.FlagResetVerticalVelocity |
                                 FusionNativeCharacterInput.FlagCollisionChanged |
                                 FusionNativeCharacterInput.FlagCollisionEnabled),
                OwnerPosition = ownerPosition,
                RootMotionDelta = rootMotionDelta,
                RootMotionWeight = Mathf.Clamp01(rootMotionWeight),
                JumpForce = Mathf.Max(0f, jumpForce)
            };
            if (!HasSharedTransientInput(input))
            {
                LogSharedTransientRejection(
                    "empty-after-validation",
                    source,
                    trustedSourceTick,
                    sourceTick,
                    flags,
                    ownerPosition,
                    rootMotionDelta,
                    rootMotionWeight,
                    jumpForce);
                return;
            }

            if (m_SharedTransientReceiveOverflowLatched) return;
            if (m_SharedTransientQueue.Count >= SharedTransientReceiveBacklogCapacity)
            {
                // Do not drop one sample and then acknowledge a later sequence: that would tell
                // the owner the missing traversal delta was simulated. Bound memory and stop
                // admitting this stream until the object/session lifecycle resets it.
                m_SharedTransientReceiveOverflowLatched = true;
                Debug.LogError(
                    $"[FusionNativeCharacter] Reliable Shared transient receive backlog " +
                    $"exceeded {SharedTransientReceiveBacklogCapacity} samples for '{name}' " +
                    $"from {source}. Further transients are blocked to preserve ACK integrity; " +
                    "disconnect that player and inspect network/input rate.",
                    this);
                return;
            }

            m_SharedTransientQueue.Enqueue(new SharedCharacterTransient
            {
                Input = input,
                TrustedTick = trustedSourceTick
            });
            m_LastQueuedSharedTransientTick = sourceTick;
            LogSharedTransient(
                "reliably-enqueued",
                input,
                trustedSourceTick,
                m_SharedTransientQueue.Count);
        }

        private void CopyToBuffer()
        {
            if (m_Driver == null) return;
            if (!EnsureFiniteEnginePose("copy to Fusion state")) return;

            NativeState.TRSPData.Position = transform.position;
            NativeState.TRSPData.Rotation = transform.rotation;
            NativeState.TRSPData.Scale = transform.localScale;
            UpdateMotionState();
        }

        private void TryInitializeNetworkState()
        {
            if (m_HasInitialState || !m_BackendInitialized ||
                !m_SpawnedObserved || m_Driver == null ||
                Object == null || !Object.IsValid || Runner == null)
            {
                return;
            }

            // Behaviour order inside NetworkObject is not a contract. Auto-init can create the
            // GC2 driver before or after this behaviour receives Spawned, so state admission is
            // completed only when both halves are ready.
            //
            // Toggling CharacterController also clears Unity's cached pre-spawn pose. Without
            // it, the controller's first Move can restore the prefab position even though
            // Fusion spawned the object elsewhere (the same guard used by Fusion's NCC).
            bool controllerWasEnabled = m_Controller != null && m_Controller.enabled;
            if (controllerWasEnabled) m_Controller.enabled = false;

            bool initializeFromEngine =
                Object.HasStateAuthority && Object.LastReceiveTick == default;
            if (initializeFromEngine)
            {
                NativeState.LastProcessedInputTick = int.MinValue;
                NativeState.LastAppliedSharedSourceTick = int.MinValue;
                NativeState.LastContinuousMove = Vector2.zero;
                NativeState.LastContinuousYaw = transform.eulerAngles.y;
                NativeState.InputStateOwnerRaw = GetLogicalOwnerRawEncoded();
                CopyToBuffer();
            }
            else CopyToEngine(restoreMotion: true);

            if (controllerWasEnabled) m_Controller.enabled = true;

            m_HasInitialState = true;
        }

        private void ApplyPendingExternalPose(bool applyPosition = true)
        {
            if (!m_HasPendingExternalPosition &&
                !m_HasPendingExternalRotation &&
                !m_HasPendingExternalScale)
            {
                return;
            }

            bool validPendingPose =
                (!m_HasPendingExternalPosition ||
                 ((!m_PendingExternalPositionIsAbsolute ||
                   IsFinite(m_PendingExternalPosition)) &&
                  IsFinite(m_PendingExternalPositionDelta))) &&
                (!m_HasPendingExternalRotation ||
                 IsUsableRotation(m_PendingExternalRotation)) &&
                (!m_HasPendingExternalScale || IsFinite(m_PendingExternalScale));
            if (!validPendingPose || !EnsureFiniteEnginePose("before external pose"))
            {
                ReportInvalidPose("rejected pending external pose");
                ClearPendingExternalChanges();
                return;
            }

            bool controllerWasEnabled = m_Controller != null && m_Controller.enabled;
            Vector3 targetPosition = transform.position;
            if (m_HasPendingExternalPosition && applyPosition)
            {
                targetPosition =
                    (m_PendingExternalPositionIsAbsolute
                        ? m_PendingExternalPosition
                        : transform.position) +
                    m_PendingExternalPositionDelta;
            }

            if (m_HasPendingExternalPosition && applyPosition &&
                m_PendingExternalTeleport)
            {
                if (controllerWasEnabled) m_Controller.enabled = false;
                transform.position = targetPosition;
                if (controllerWasEnabled) m_Controller.enabled = true;
                m_PendingExternalPositionApplied = true;
            }
            else if (m_HasPendingExternalPosition && applyPosition)
            {
                if (controllerWasEnabled)
                {
                    Physics.SyncTransforms();
                    m_Controller.Move(targetPosition - transform.position);
                }
                else
                {
                    transform.position = targetPosition;
                }

                m_PendingExternalPositionApplied = true;

            }

            if (m_HasPendingExternalRotation)
            {
                transform.rotation = m_PendingExternalRotation;
            }

            if (m_HasPendingExternalScale)
            {
                transform.localScale = m_PendingExternalScale;
            }

            EnsureFiniteEnginePose("after external pose");
        }

        private void PrepareSharedLocalExternalPose(
            ref FusionNativeCharacterInput input)
        {
            bool hasPendingPositionTarget = TryGetPendingExternalPositionTarget(
                out Vector3 pendingPositionTarget);
            bool ownerPoseOwnsPosition =
                input.HasOwnerPose && hasPendingPositionTarget &&
                !m_PendingExternalTeleport;

            // Shared logical owners capture input after BeforeAll restored the simulation root.
            // Preserve GC2's newer render-frame endpoint in the input copy so both the predicting
            // owner and the Shared master consume the same absolute pose exactly once.
            if (input.HasOwnerPose && hasPendingPositionTarget)
            {
                input.OwnerPosition = pendingPositionTarget;
                m_PendingExternalPositionCapturedByInput = true;
            }

            ApplyPendingExternalPose(applyPosition: !ownerPoseOwnsPosition);
        }

        private bool TryGetPendingExternalPositionTarget(out Vector3 target)
        {
            target = transform != null ? transform.position : Vector3.zero;
            if (!m_HasPendingExternalPosition || transform == null) return false;
            if ((!m_PendingExternalPositionIsAbsolute ||
                 IsFinite(m_PendingExternalPosition)) &&
                IsFinite(m_PendingExternalPositionDelta))
            {
                target =
                    (m_PendingExternalPositionIsAbsolute
                        ? m_PendingExternalPosition
                        : transform.position) +
                    m_PendingExternalPositionDelta;
                return IsFinite(target);
            }

            return false;
        }

        internal bool TryGetPendingExternalOwnerPoseTarget(out Vector3 target)
        {
            target = Vector3.zero;
            if (m_PendingExternalTeleport ||
                !TryGetPendingExternalPositionTarget(out target))
            {
                return false;
            }

            m_PendingExternalPositionCapturedByInput = true;
            return true;
        }

        private void ClearPendingExternalChanges(
            bool preserveUnconsumedPosition = false)
        {
            bool keepPosition =
                preserveUnconsumedPosition &&
                m_HasPendingExternalPosition &&
                !m_PendingExternalTeleport &&
                !m_PendingExternalPositionCapturedByInput &&
                !m_PendingExternalPositionApplied;
            if (!keepPosition)
            {
                m_HasPendingExternalPosition = false;
                m_PendingExternalPositionIsAbsolute = false;
                m_PendingExternalPosition = Vector3.zero;
                m_PendingExternalPositionDelta = Vector3.zero;
            }

            m_HasPendingExternalRotation = false;
            m_HasPendingExternalScale = false;
            m_PendingExternalTeleport = false;
            if (!keepPosition) m_PendingExternalPositionCapturedByInput = false;
            m_PendingExternalPositionApplied = false;
            m_ForwardExternalChangesHandled = false;
        }

        private bool HasPendingExternalChanges =>
            m_HasPendingExternalPosition ||
            m_HasPendingExternalRotation ||
            m_HasPendingExternalScale;

        private void UpdateMotionState()
        {
            if (m_Driver == null) return;

            // Prediction state stores physical tick velocity. GC2 Animim may simultaneously
            // consume a persistent semantic Traversal direction (including an explicit idle
            // zero), which must not be restored as simulation velocity during rollback.
            NativeState.Velocity = m_Driver.SimulationVelocity;
            NativeState.TraversalPresentationVelocity = Vector3.zero;
            NativeState.VerticalSpeed = m_Driver.VerticalSpeed;
            NativeState.LastJumpTick = m_Driver.LastJumpTick;
            NativeState.LastGroundedTick = m_Driver.LastGroundedTick;
            NativeState.LastAcceptedOwnerPoseTick = m_Driver.LastAcceptedOwnerPoseTick;
            NativeState.MotionFlags = 0;
            if (m_Driver.IsGrounded) NativeState.MotionFlags |= MotionFlagGrounded;
            if (m_Driver.VerticalSpeed > 0f) NativeState.MotionFlags |= MotionFlagJumping;

            Character character = m_NetworkCharacter?.Character;
            if (NetworkOwnerMotionAuthorityHooks.IsContinuousOwnerPose(character))
            {
                // The active bit is independent of a non-zero direction. An attached but idle
                // climber must replicate an explicit zero instead of falling back to the
                // pulse-prone displacement velocity. This state also makes the current blend
                // intent available to late joiners without replaying a motion command.
                NativeState.MotionFlags |= MotionFlagTraversalPresentation;
                UnitMotionNetworkController motion = m_NetworkCharacter?.MotionController;
                if (motion != null &&
                    motion.TryGetTraversalPresentationDirection(out Vector3 direction) &&
                    IsFinite(direction))
                {
                    NativeState.TraversalPresentationVelocity = direction;
                }
            }
        }

        private void StoreContinuousInput(FusionNativeCharacterInput input)
        {
            Vector2 move = IsFinite(input.Move) ? input.Move : Vector2.zero;
            if (move.sqrMagnitude > 1f) move.Normalize();
            NativeState.LastContinuousMove = move;
            NativeState.LastContinuousYaw = IsFinite(input.Yaw)
                ? Mathf.Repeat(input.Yaw, 360f)
                : NativeState.LastContinuousYaw;
        }

        private void CopyToEngine(bool restoreMotion)
        {
            if (m_Controller == null) m_Controller = GetComponent<CharacterController>();

            Vector3 position = NativeState.TRSPData.Position;
            Quaternion rotation = NativeState.TRSPData.Rotation;
            Vector3 scale = NativeState.TRSPData.Scale;
            if (!HasUsableRootPose(position, rotation, scale))
            {
                ReportInvalidPose("rejected non-finite Fusion state");
                RecoverInvalidEnginePose("copy from Fusion state");
                return;
            }

            // Prediction restore is a routine pose correction, not a component lifecycle
            // transition. Disabling a CharacterController here invalidates the
            // Physics.IgnoreCollision pairs installed by GC2 TraverseLink (Vault/Jump), so the
            // next authorized owner-pose sweep collides with geometry that Traversal explicitly
            // ignored. Fusion/Ninjutsu's normal path and GC2's own driver both restore the
            // Transform directly.
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
            Physics.SyncTransforms();
            m_RootHasRenderPose = false;
            RememberCurrentRootPose();

            if (restoreMotion && m_Driver != null)
            {
                bool grounded = (NativeState.MotionFlags & MotionFlagGrounded) != 0;
                m_Driver.RestoreSimulationMotion(
                    NativeState.Velocity,
                    NativeState.VerticalSpeed,
                    NativeState.LastJumpTick,
                    NativeState.LastGroundedTick,
                    NativeState.LastAcceptedOwnerPoseTick,
                    grounded);
            }
        }

        private bool RecoverInvalidEnginePose(string context)
        {
            Vector3 position = default;
            Quaternion rotation = Quaternion.identity;
            Vector3 scale = Vector3.one;
            bool hasRecoveryPose = false;

            if (m_HasInitialState && HasUsableRootPose(
                    NativeState.TRSPData.Position,
                    NativeState.TRSPData.Rotation,
                    NativeState.TRSPData.Scale))
            {
                position = NativeState.TRSPData.Position;
                rotation = NativeState.TRSPData.Rotation;
                scale = NativeState.TRSPData.Scale;
                hasRecoveryPose = true;
            }
            else if (m_HasLastValidRootPose)
            {
                position = m_LastValidRootPosition;
                rotation = m_LastValidRootRotation;
                scale = m_LastValidRootScale;
                hasRecoveryPose = true;
            }

            ReportInvalidPose(context);
            ClearPendingExternalChanges();
            if (!hasRecoveryPose) return false;

            bool controllerWasEnabled = m_Controller != null && m_Controller.enabled;
            if (controllerWasEnabled) m_Controller.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            transform.localScale = scale;
            if (controllerWasEnabled) m_Controller.enabled = true;
            Physics.SyncTransforms();

            m_Driver?.ResetVerticalVelocity();
            m_Driver?.ApplyReplicatedMotion(Vector3.zero, false);
            RememberCurrentRootPose();
            return true;
        }

        private void RememberCurrentRootPose()
        {
            if (transform == null || !HasUsableRootPose(
                    transform.position,
                    transform.rotation,
                    transform.localScale))
            {
                return;
            }

            m_LastValidRootPosition = transform.position;
            m_LastValidRootRotation = transform.rotation;
            m_LastValidRootScale = transform.localScale;
            m_HasLastValidRootPose = true;
        }

        private void ReportInvalidPose(string context)
        {
            // A broken pose can otherwise emit errors once per render/tick and recreate the
            // multi-gigabyte logs this guard is intended to prevent.
            if (m_LastInvalidPoseDiagnosticFrame != int.MinValue &&
                Time.frameCount - m_LastInvalidPoseDiagnosticFrame < 120)
            {
                return;
            }

            m_LastInvalidPoseDiagnosticFrame = Time.frameCount;
            Debug.LogError(
                $"[FusionNativeCharacterMotor] Rejected an invalid character pose on " +
                $"'{name}' ({context}). tick={CurrentSimulationTick} " +
                $"enginePosition={transform.position} engineRotation={transform.rotation} " +
                $"engineScale={transform.localScale} pendingPosition={m_PendingExternalPosition} " +
                $"pendingDelta={m_PendingExternalPositionDelta}. Restoring the last finite " +
                $"Fusion/engine pose.",
                this);
        }

        private static bool HasUsableRootPose(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale)
        {
            return IsFinite(position) && IsUsableRotation(rotation) && IsFinite(scale);
        }

        private static bool IsUsableRotation(Quaternion value)
        {
            return IsFinite(value) && value.x * value.x + value.y * value.y +
                value.z * value.z + value.w * value.w > 0.000001f;
        }

        private bool ShouldSimulateLocally
        {
            get
            {
                if (Object == null || !Object.IsValid || Runner == null) return false;
                if (Runner.GameMode == GameMode.Shared)
                {
                    return HasStateAuthority || IsLocalLogicalOwner;
                }

                return HasStateAuthority || HasInputAuthority;
            }
        }

        private bool ShouldWritePredictionState
        {
            get
            {
                if (Object == null || !Object.IsValid || Runner == null) return false;
                if (HasStateAuthority) return true;
                return Runner.GameMode != GameMode.Shared && HasInputAuthority;
            }
        }

        private bool IsLocalLogicalOwner =>
            m_Identity != null &&
            m_Identity.IsSpawned &&
            Runner != null &&
            Runner.LocalPlayer.IsRealPlayer &&
            m_Identity.IsOwnedBy(Runner.LocalPlayer);

        private int GetLogicalOwnerRawEncoded()
        {
            if (m_Identity == null || !m_Identity.IsSpawned) return 0;
            PlayerRef owner = m_Identity.LogicalOwner;
            return owner.IsRealPlayer ? owner.RawEncoded : 0;
        }

        private bool IsExternalRootWritePresentationActive =>
            m_ExternalRootWritePresentationUntilTick != int.MinValue &&
            CurrentSimulationTick <= m_ExternalRootWritePresentationUntilTick;

        private bool ShouldUseLiveExternalPresentationPose
        {
            get
            {
                if (!m_HasLiveExternalPresentationPose || !IsLocalLogicalOwner ||
                    !ShouldSimulateLocally ||
                    !HasUsableRootPose(
                        m_LiveExternalPresentationPosition,
                        m_LiveExternalPresentationRotation,
                        m_LiveExternalPresentationWorldScale))
                {
                    return false;
                }

                if (HasPendingExternalChanges)
                {
                    return true;
                }

                // A traversal authorization window can outlive the last absolute AddPosition
                // sample. Vault and Jump then continue through animation root motion. Never keep
                // writing the cached warp endpoint after the Fusion simulation root has advanced,
                // otherwise Render rewinds the owner to that endpoint every frame.
                if (HasLocalSimulationAdvancedBeyondLivePresentationPose()) return false;

                if (IsExternalRootWritePresentationActive) return true;

                if (Time.unscaledTime > m_LiveExternalPresentationHoldUntil)
                {
                    return false;
                }

                if (!TryGetInterpolatedAuthoritativeRenderPose(
                        out Vector3 renderPosition,
                        out Quaternion renderRotation,
                        out Vector3 renderScale))
                {
                    return false;
                }

                return Vector3.Distance(
                           renderPosition,
                           m_LiveExternalPresentationPosition) >
                       LiveOwnerPresentationPositionTolerance ||
                       Quaternion.Angle(
                           renderRotation,
                           m_LiveExternalPresentationRotation) >
                       LiveOwnerPresentationRotationTolerance ||
                       Vector3.Distance(
                           renderScale,
                           m_LiveExternalPresentationLocalScale) > 0.001f;
            }
        }

        private bool HasLocalSimulationAdvancedBeyondLivePresentationPose()
        {
            Vector3 simulationPosition = NativeState.TRSPData.Position;
            Quaternion simulationRotation = NativeState.TRSPData.Rotation;
            Vector3 simulationScale = NativeState.TRSPData.Scale;

            // A non-authoritative Shared owner predicts outside Fusion's replicated TRSP state.
            // Compare against that local tick pose rather than the delayed master snapshot.
            if (Runner != null && Runner.GameMode == GameMode.Shared &&
                !HasStateAuthority && m_HasSharedPredictedPose)
            {
                simulationPosition = m_SharedCurrentPredictedPosition;
                simulationRotation = m_SharedCurrentPredictedRotation;
                simulationScale = m_SharedCurrentPredictedScale;
            }

            if (!HasUsableRootPose(
                    simulationPosition,
                    simulationRotation,
                    simulationScale))
            {
                return true;
            }

            return Vector3.Distance(
                       simulationPosition,
                       m_LiveExternalPresentationPosition) >
                   LiveOwnerSimulationAdvancePositionTolerance ||
                   Quaternion.Angle(
                       simulationRotation,
                       m_LiveExternalPresentationRotation) >
                   LiveOwnerSimulationAdvanceRotationTolerance ||
                   Vector3.Distance(
                       simulationScale,
                       m_LiveExternalPresentationLocalScale) > 0.001f;
        }

        /// <summary>
        /// Returns the pose NetworkTRSP is about to present, rather than the newer Current
        /// simulation state. A connected owner can finish a short Vault or Jump while Fusion's
        /// snapshot render timeline is still interpolating from the pre-traversal pose. Releasing
        /// the live GC2 pose against Current in that interval lets NetworkTRSP write the root
        /// backwards for a frame and can visually pin the owner in mid-air.
        /// </summary>
        private bool TryGetInterpolatedAuthoritativeRenderPose(
            out Vector3 position,
            out Quaternion rotation,
            out Vector3 scale)
        {
            position = NativeState.TRSPData.Position;
            rotation = NativeState.TRSPData.Rotation;
            scale = NativeState.TRSPData.Scale;

            if (TryGetSnapshotsBuffers(
                    out NetworkBehaviourBuffer fromBuffer,
                    out NetworkBehaviourBuffer toBuffer,
                    out float alpha))
            {
                FusionNativeCharacterState fromState =
                    fromBuffer.ReinterpretState<FusionNativeCharacterState>();
                FusionNativeCharacterState toState =
                    toBuffer.ReinterpretState<FusionNativeCharacterState>();

                NetworkTRSPData fromTrsp = fromState.TRSPData;
                NetworkTRSPData toTrsp = toState.TRSPData;
                float renderAlpha = Mathf.Clamp01(alpha);
                if (fromTrsp.TeleportKey != toTrsp.TeleportKey)
                {
                    // Match NetworkTRSP.Render's teleport boundary selection. Releasing a live
                    // pose to To while NetworkTRSP still selects From would recreate a one-frame
                    // backwards handoff.
                    NetworkTRSPData selected = renderAlpha >= 0.5f
                        ? toTrsp
                        : fromTrsp;
                    position = selected.Position;
                    rotation = selected.Rotation;
                    scale = selected.Scale;
                }
                else
                {
                    position = Vector3.LerpUnclamped(
                        fromTrsp.Position,
                        toTrsp.Position,
                        renderAlpha);
                    rotation = Quaternion.SlerpUnclamped(
                        fromTrsp.Rotation,
                        toTrsp.Rotation,
                        renderAlpha);
                    scale = Vector3.LerpUnclamped(
                        fromTrsp.Scale,
                        toTrsp.Scale,
                        renderAlpha);
                }
            }

            return HasUsableRootPose(position, rotation, scale);
        }

        private void RememberLiveExternalPresentationPose()
        {
            if (transform == null || !IsLocalLogicalOwner || !ShouldSimulateLocally ||
                !HasUsableRootPose(
                    transform.position,
                    transform.rotation,
                    transform.lossyScale))
            {
                return;
            }

            m_LiveExternalPresentationPosition = transform.position;
            m_LiveExternalPresentationRotation = transform.rotation;
            m_LiveExternalPresentationWorldScale = transform.lossyScale;
            m_LiveExternalPresentationLocalScale = transform.localScale;
            m_LiveExternalPresentationHoldUntil =
                Time.unscaledTime + LiveOwnerPresentationHandoffSeconds;
            m_HasLiveExternalPresentationPose = true;
        }

        private void ApplyLiveExternalRootPresentationPose()
        {
            if (transform == null || !m_HasLiveExternalPresentationPose) return;

            transform.SetPositionAndRotation(
                m_LiveExternalPresentationPosition,
                m_LiveExternalPresentationRotation);
            transform.localScale = m_LiveExternalPresentationLocalScale;
            m_RootHasRenderPose = true;

            if (Runner != null && Runner.GameMode == GameMode.Shared &&
                IsLocalLogicalOwner)
            {
                RememberSharedPresentedPose(
                    transform.position,
                    transform.rotation);
            }
        }

        private void ClearLiveExternalPresentationPose()
        {
            m_HasLiveExternalPresentationPose = false;
            m_LiveExternalPresentationPosition = Vector3.zero;
            m_LiveExternalPresentationRotation = Quaternion.identity;
            m_LiveExternalPresentationWorldScale = Vector3.one;
            m_LiveExternalPresentationLocalScale = Vector3.one;
            m_LiveExternalPresentationHoldUntil = 0f;
            m_OwnerPredictionResimulationTicks = 0;
        }

        private void CaptureSharedPredictedPreviousPose(int tick)
        {
            if (transform == null || !HasUsableRootPose(
                    transform.position,
                    transform.rotation,
                    transform.localScale))
            {
                return;
            }

            SetSharedPredictedPreviousPose(
                transform.position,
                transform.rotation,
                transform.localScale,
                tick);
        }

        private void CaptureSharedPredictedPreviousStatePose(int tick)
        {
            SetSharedPredictedPreviousPose(
                NativeState.TRSPData.Position,
                NativeState.TRSPData.Rotation,
                NativeState.TRSPData.Scale,
                tick);
        }

        private void SetSharedPredictedPreviousPose(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            int tick)
        {
            if (!HasUsableRootPose(position, rotation, scale)) return;

            m_SharedPreviousPredictedPosition = position;
            m_SharedPreviousPredictedRotation = rotation;
            m_SharedPreviousPredictedScale = scale;
            m_SharedCurrentPredictedPosition = position;
            m_SharedCurrentPredictedRotation = rotation;
            m_SharedCurrentPredictedScale = scale;
            m_SharedPredictedPoseTick = tick;
            m_HasSharedPredictedPose = true;
            m_RootHasRenderPose = false;
        }

        private void CaptureSharedPredictedCurrentPose(int tick)
        {
            if (transform == null || !HasUsableRootPose(
                    transform.position,
                    transform.rotation,
                    transform.localScale))
            {
                return;
            }

            if (!m_HasSharedPredictedPose || m_SharedPredictedPoseTick != tick)
            {
                CaptureSharedPredictedPreviousPose(tick);
            }

            m_SharedCurrentPredictedPosition = transform.position;
            m_SharedCurrentPredictedRotation = transform.rotation;
            m_SharedCurrentPredictedScale = transform.localScale;
            m_SharedPredictedPoseTick = tick;
            m_HasSharedPredictedPose = true;
            m_RootHasRenderPose = false;
        }

        private void SeedSharedPredictedPose(int tick)
        {
            CaptureSharedPredictedPreviousPose(tick);
            CaptureSharedPredictedCurrentPose(tick);
        }

        private void RenderSharedPredictedOwner()
        {
            if (Runner == null || transform == null) return;
            if (!m_HasSharedPredictedPose)
            {
                SeedSharedPredictedPose(Runner.Tick.Raw);
                if (!m_HasSharedPredictedPose) return;
            }

            float alpha = Mathf.Clamp01(Runner.LocalAlpha);
            Vector3 renderPosition = Vector3.Lerp(
                m_SharedPreviousPredictedPosition,
                m_SharedCurrentPredictedPosition,
                alpha);
            Quaternion renderRotation = Quaternion.Slerp(
                m_SharedPreviousPredictedRotation,
                m_SharedCurrentPredictedRotation,
                alpha);
            Vector3 renderScale = Vector3.Lerp(
                m_SharedPreviousPredictedScale,
                m_SharedCurrentPredictedScale,
                alpha);

            BeginSharedPresentationContinuity(renderPosition, renderRotation);
            if (m_HasSharedPresentationError)
            {
                renderPosition += m_SharedPresentationPositionError;
                renderRotation =
                    m_SharedPresentationRotationError * renderRotation;
            }

            // A Shared logical owner is not in Fusion's native simulation set, so its root and
            // follow camera need this local tick interpolation too. Rendering only a Mannequin
            // wrapper leaves the camera/root stepping at the simulation rate during Vault and
            // Jump even though the visible mesh is smooth.
            transform.SetPositionAndRotation(renderPosition, renderRotation);
            transform.localScale = renderScale;
            m_RootHasRenderPose = true;
            RememberSharedPresentedPose(renderPosition, renderRotation);
            DecaySharedPresentationError();
        }

        private void QueueSharedPresentationCorrection(
            Vector3 predictedPosition,
            Quaternion predictedRotation,
            Vector3 correctedPosition,
            Quaternion correctedRotation)
        {
            if (!HasUsableRootPose(
                    predictedPosition,
                    predictedRotation,
                    Vector3.one) ||
                !HasUsableRootPose(
                    correctedPosition,
                    correctedRotation,
                    Vector3.one))
            {
                return;
            }

            Vector3 positionError = predictedPosition - correctedPosition;
            Quaternion rotationError =
                predictedRotation * Quaternion.Inverse(correctedRotation);
            if (positionError.sqrMagnitude <=
                    SharedPresentationPositionEpsilon *
                    SharedPresentationPositionEpsilon &&
                Quaternion.Angle(Quaternion.identity, rotationError) <=
                    SharedPresentationRotationEpsilon)
            {
                return;
            }

            m_SharedPresentationFallbackPositionError = positionError;
            m_SharedPresentationFallbackRotationError = rotationError;
            m_HasSharedPresentationFallback = true;
            RequestSharedPresentationContinuity();
        }

        private void RequestSharedPresentationContinuity()
        {
            m_SharedPresentationContinuityPending = true;
        }

        private void BeginSharedPresentationContinuity(
            Vector3 basePosition,
            Quaternion baseRotation)
        {
            if (!m_SharedPresentationContinuityPending) return;
            m_SharedPresentationContinuityPending = false;

            Vector3 positionError;
            Quaternion rotationError;
            if (m_HasLastSharedPresentedPose)
            {
                // Rebase from the pose that was actually shown last frame. This also makes a
                // live Traversal -> predicted-tick handoff continuous.
                positionError = m_LastSharedPresentedPosition - basePosition;
                rotationError =
                    m_LastSharedPresentedRotation * Quaternion.Inverse(baseRotation);
            }
            else if (m_HasSharedPresentationFallback)
            {
                // The first correction can arrive before the first Render callback. Preserve
                // the predicted-vs-corrected error captured around CopyToEngine in that case.
                positionError = m_SharedPresentationFallbackPositionError;
                rotationError = m_SharedPresentationFallbackRotationError;
            }
            else
            {
                return;
            }

            m_HasSharedPresentationFallback = false;
            m_SharedPresentationFallbackPositionError = Vector3.zero;
            m_SharedPresentationFallbackRotationError = Quaternion.identity;

            float snapDistance = m_Profile != null
                ? Mathf.Max(0.1f, m_Profile.maxReconciliationDistance)
                : 3f;
            if (!IsFinite(positionError) || !IsUsableRotation(rotationError) ||
                positionError.magnitude > snapDistance)
            {
                ClearSharedPresentationError();
                return;
            }

            m_SharedPresentationPositionError = positionError;
            m_SharedPresentationRotationError = rotationError;
            m_HasSharedPresentationError =
                positionError.sqrMagnitude >
                    SharedPresentationPositionEpsilon *
                    SharedPresentationPositionEpsilon ||
                Quaternion.Angle(Quaternion.identity, rotationError) >
                    SharedPresentationRotationEpsilon;
        }

        private void DecaySharedPresentationError()
        {
            if (!m_HasSharedPresentationError ||
                m_LastSharedPresentationDecayFrame == Time.frameCount)
            {
                return;
            }

            m_LastSharedPresentationDecayFrame = Time.frameCount;
            float reconciliationSpeed = m_Profile != null
                ? Mathf.Max(1f, m_Profile.reconciliationSpeed)
                : 15f;
            float deltaTime = Mathf.Max(0f, Time.unscaledDeltaTime);
            float decay = 1f - Mathf.Exp(-reconciliationSpeed * deltaTime);
            m_SharedPresentationPositionError = Vector3.Lerp(
                m_SharedPresentationPositionError,
                Vector3.zero,
                decay);
            m_SharedPresentationRotationError = Quaternion.Slerp(
                m_SharedPresentationRotationError,
                Quaternion.identity,
                decay);

            if (m_SharedPresentationPositionError.sqrMagnitude <=
                    SharedPresentationPositionEpsilon *
                    SharedPresentationPositionEpsilon &&
                Quaternion.Angle(
                    Quaternion.identity,
                    m_SharedPresentationRotationError) <=
                    SharedPresentationRotationEpsilon)
            {
                ClearSharedPresentationError();
            }
        }

        private void RememberSharedPresentedPose(
            Vector3 position,
            Quaternion rotation)
        {
            if (!IsFinite(position) || !IsUsableRotation(rotation)) return;
            m_LastSharedPresentedPosition = position;
            m_LastSharedPresentedRotation = rotation;
            m_HasLastSharedPresentedPose = true;
        }

        private void ClearSharedPresentationError()
        {
            m_SharedPresentationPositionError = Vector3.zero;
            m_SharedPresentationRotationError = Quaternion.identity;
            m_SharedPresentationFallbackPositionError = Vector3.zero;
            m_SharedPresentationFallbackRotationError = Quaternion.identity;
            m_HasSharedPresentationError = false;
            m_SharedPresentationContinuityPending = false;
            m_HasSharedPresentationFallback = false;
            m_LastSharedPresentationDecayFrame = int.MinValue;
        }

        private void RestoreSharedPredictedSimulationPose()
        {
            if (!m_HasSharedPredictedPose || transform == null) return;
            if (!HasUsableRootPose(
                    m_SharedCurrentPredictedPosition,
                    m_SharedCurrentPredictedRotation,
                    m_SharedCurrentPredictedScale))
            {
                ResetSharedPredictedPresentation();
                return;
            }

            transform.SetPositionAndRotation(
                m_SharedCurrentPredictedPosition,
                m_SharedCurrentPredictedRotation);
            transform.localScale = m_SharedCurrentPredictedScale;
            Physics.SyncTransforms();
            m_RootHasRenderPose = false;
            RememberCurrentRootPose();
        }

        private void ResetSharedPredictedPresentation()
        {
            m_HasSharedPredictedPose = false;
            m_SharedPredictedPoseTick = int.MinValue;
            m_SharedPreviousPredictedPosition = Vector3.zero;
            m_SharedPreviousPredictedRotation = Quaternion.identity;
            m_SharedPreviousPredictedScale = Vector3.one;
            m_SharedCurrentPredictedPosition = Vector3.zero;
            m_SharedCurrentPredictedRotation = Quaternion.identity;
            m_SharedCurrentPredictedScale = Vector3.one;
            ClearSharedPresentationError();
            m_LastSharedPresentedPosition = Vector3.zero;
            m_LastSharedPresentedRotation = Quaternion.identity;
            m_HasLastSharedPresentedPose = false;
        }

        private void ResetSharedRuntimeState()
        {
            m_HasSharedInput = false;
            m_LatestSharedTrustedTick = int.MinValue;
            m_LastSharedPayloadTick = int.MinValue;
            m_LastQueuedSharedTransientTick = int.MinValue;
            m_SharedTransientQueue.Clear();
            m_SharedTransientReceiveOverflowLatched = false;
            m_SharedPredictionStart = 0;
            m_SharedPredictionCount = 0;
            m_LastSharedReconciledStateTick = int.MinValue;
            m_ObservedSharedTeleportKey = 0;
            m_HasObservedSharedTeleportKey = false;
            m_LastSharedOwnerSimulationTick = int.MinValue;
            m_SharedProxyPumpDiagnosticIssued = false;
            m_SharedSubmitDiagnosticIssued = false;
            m_NextSharedInputDiagnosticTime = 0f;
            m_NextSharedTransientSubmitDiagnosticTime = 0f;
            m_NextSharedTransientReceiveDiagnosticTime = 0f;
            m_NextSharedTransientApplyDiagnosticTime = 0f;
            m_NextSharedTransientRejectionDiagnosticTime = 0f;
            m_NextSharedReconcileDiagnosticTime = 0f;
            ResetSharedPredictedPresentation();
        }

        private static Vector3 DivideScale(Vector3 numerator, Vector3 denominator)
        {
            return new Vector3(
                Mathf.Abs(denominator.x) > 0.000001f ? numerator.x / denominator.x : 1f,
                Mathf.Abs(denominator.y) > 0.000001f ? numerator.y / denominator.y : 1f,
                Mathf.Abs(denominator.z) > 0.000001f ? numerator.z / denominator.z : 1f);
        }

        private static void SetWorldScale(Transform target, Vector3 worldScale)
        {
            if (target == null || !IsFinite(worldScale)) return;
            target.localScale = target.parent != null
                ? DivideScale(worldScale, target.parent.lossyScale)
                : worldScale;
        }

        private bool TryEnsurePresentationRoot()
        {
            if (m_PresentationRoot != null)
            {
                Transform selectedVisual = m_ListenHostPresentationVisualRoot;
                if (selectedVisual == null)
                {
                    selectedVisual = m_Driver?.Character?.Animim?.Mannequin;
                }

                bool hierarchyValid =
                    m_PresentationRoot.parent == transform &&
                    m_PresentationVisualRoot != null &&
                    m_PresentationVisualRoot.parent == m_PresentationRoot &&
                    selectedVisual == m_PresentationVisualRoot &&
                    IsSafePresentationContents(m_PresentationVisualRoot);
                if (hierarchyValid) return true;

                // Character selection can replace GC2's Mannequin at runtime. Do not keep
                // rendering an empty/old wrapper while the newly selected visual remains on the
                // tick-stepped Character root.
                if (m_PresentationVisualRoot != null &&
                    m_PresentationVisualRoot.parent != m_PresentationRoot)
                {
                    // The replacement system already took ownership of this Transform; avoid
                    // reparenting it back while tearing down only our wrapper.
                    m_PresentationVisualRoot = null;
                }
                RestorePresentationHierarchy();
            }

            Transform visualRoot = m_ListenHostPresentationVisualRoot;
            if (visualRoot == null)
            {
                Transform mannequin = m_Driver?.Character?.Animim?.Mannequin;
                if (mannequin != null && mannequin.parent == transform)
                {
                    visualRoot = mannequin;
                }
            }

            if (!IsSafePresentationVisualRoot(transform, visualRoot)) return false;

            m_PresentationOriginalSiblingIndex = visualRoot.GetSiblingIndex();
            var presentationObject = new GameObject("__FusionNativePresentation");
            presentationObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            m_PresentationRoot = presentationObject.transform;
            m_PresentationRoot.SetParent(transform, false);
            m_PresentationRoot.SetSiblingIndex(m_PresentationOriginalSiblingIndex);
            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;

            m_PresentationVisualRoot = visualRoot;
            m_PresentationVisualRoot.SetParent(m_PresentationRoot, false);
            if (!m_PresentationBeforeRenderSubscribed)
            {
                Application.onBeforeRender += ReapplyPresentationPose;
                m_PresentationBeforeRenderSubscribed = true;
            }
            return true;
        }

        /// <summary>
        /// A listen-host interpolation target must be a direct child containing presentation
        /// objects only. Reparenting physics or network behaviours would move authoritative
        /// gameplay state onto a past render pose, so such hierarchies are rejected.
        /// </summary>
        public static bool IsSafePresentationVisualRoot(
            Transform characterRoot,
            Transform candidate)
        {
            if (characterRoot == null || candidate == null || candidate == characterRoot)
            {
                return false;
            }

            if (candidate.parent != characterRoot) return false;
            return IsSafePresentationContents(candidate);
        }

        private static bool IsSafePresentationContents(Transform candidate)
        {
            if (candidate == null) return false;
            if (candidate.GetComponentInChildren<CharacterController>(true) != null) return false;
            if (candidate.GetComponentInChildren<Rigidbody>(true) != null) return false;
            if (candidate.GetComponentInChildren<Collider>(true) != null) return false;
            if (candidate.GetComponentInChildren<NetworkObject>(true) != null) return false;
            if (candidate.GetComponentInChildren<NetworkBehaviour>(true) != null) return false;

            return candidate.GetComponentInChildren<Renderer>(true) != null ||
                   candidate.GetComponentInChildren<Animator>(true) != null;
        }

        private void ReapplyPresentationPose()
        {
            if (!m_HasPresentationPose || m_PresentationRoot == null) return;
            m_PresentationRoot.SetPositionAndRotation(
                m_PresentationWorldPosition,
                m_PresentationWorldRotation);
            SetWorldScale(m_PresentationRoot, m_PresentationWorldScale);
        }

        private void RememberPresentationPose()
        {
            if (m_PresentationRoot == null) return;
            m_PresentationWorldPosition = m_PresentationRoot.position;
            m_PresentationWorldRotation = m_PresentationRoot.rotation;
            m_PresentationWorldScale = m_PresentationRoot.lossyScale;
            m_HasPresentationPose = true;
        }

        private void RestorePresentationHierarchy()
        {
            if (m_PresentationBeforeRenderSubscribed)
            {
                Application.onBeforeRender -= ReapplyPresentationPose;
                m_PresentationBeforeRenderSubscribed = false;
            }
            m_HasPresentationPose = false;
            m_PresentationWorldScale = Vector3.one;
            if (m_PresentationRoot == null) return;

            // Remove any render offset before restoring the original prefab hierarchy.
            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;
            if (m_PresentationVisualRoot != null && transform != null)
            {
                m_PresentationVisualRoot.SetParent(transform, false);
                if (m_PresentationOriginalSiblingIndex >= 0)
                {
                    m_PresentationVisualRoot.SetSiblingIndex(
                        Mathf.Min(
                            m_PresentationOriginalSiblingIndex,
                            transform.childCount - 1));
                }
            }

            GameObject presentationObject = m_PresentationRoot.gameObject;
            m_PresentationRoot = null;
            m_PresentationVisualRoot = null;
            m_PresentationOriginalSiblingIndex = -1;
            if (Application.isPlaying) Destroy(presentationObject);
            else DestroyImmediate(presentationObject);
        }

        private void CacheComponents(NetworkCharacter networkCharacter = null)
        {
            if (networkCharacter != null) m_NetworkCharacter = networkCharacter;
            if (m_NetworkCharacter == null) m_NetworkCharacter = GetComponent<NetworkCharacter>();
            if (m_Identity == null) m_Identity = GetComponent<FusionNetworkIdentity>();
            if (m_Controller == null) m_Controller = GetComponent<CharacterController>();

            if (m_Driver == null && m_NetworkCharacter?.ActiveDriver is FusionNativeCharacterDriver driver)
            {
                m_Driver = driver;
            }
        }

        private void SubscribeIdentity()
        {
            if (m_Identity == null) return;
            m_Identity.IdentityChanged -= OnIdentityChanged;
            m_Identity.IdentityChanged += OnIdentityChanged;
        }

        private void UnsubscribeIdentity()
        {
            if (m_Identity != null) m_Identity.IdentityChanged -= OnIdentityChanged;
        }

        private void OnIdentityChanged(FusionNetworkIdentity identity)
        {
            PlayerRef owner = identity != null ? identity.LogicalOwner : PlayerRef.Invalid;
            if (owner == m_ObservedLogicalOwner) return;

            m_ObservedLogicalOwner = owner;
            m_Driver?.ResetNetworkTransientState();
            ResetSharedRuntimeState();
            // Keep this as a deferred consistency check even on a proxy. If Shared master
            // migration later grants this peer State Authority, the replicated owner stamp lets
            // us preserve a same-owner baseline or reset a genuinely reassigned character.
            m_ResetReplicatedOwnerInputStatePending = true;
            ClearPendingExternalChanges();
            m_ExternalRootWritePresentationUntilTick = int.MinValue;
            ClearLiveExternalPresentationPose();
            m_RootHasRenderPose = false;
        }

        private void ApplyPendingReplicatedOwnerInputReset()
        {
            if (!m_ResetReplicatedOwnerInputStatePending || !m_HasInitialState ||
                Object == null || !Object.IsValid || !HasStateAuthority)
            {
                return;
            }

            int currentOwnerRaw = GetLogicalOwnerRawEncoded();
            if (NativeState.InputStateOwnerRaw == currentOwnerRaw)
            {
                // Same-owner State Authority migration: the replicated acknowledgement and
                // prediction baselines are still valid and replaying them would double-apply
                // movement that the old master already simulated.
                m_ResetReplicatedOwnerInputStatePending = false;
                return;
            }

            // Logical ownership can transfer without respawning this NetworkObject. Replicated
            // input acknowledgements belong to the prior owner and must not prune the new
            // owner's prediction history. Preserve the world pose while clearing owner-specific
            // movement metadata and held steering on the next authoritative simulation tick.
            // BeforeAllTicks restores NativeState into the driver before this callback runs, so
            // reset the driver again here; otherwise UpdateMotionState below would immediately
            // copy the previous owner's velocity/jump/accepted-pose metadata back into state.
            m_Driver?.ResetNetworkTransientState();
            NativeState.LastProcessedInputTick = int.MinValue;
            NativeState.LastAppliedSharedSourceTick = int.MinValue;
            NativeState.LastContinuousMove = Vector2.zero;
            NativeState.LastContinuousYaw = transform.eulerAngles.y;
            NativeState.Velocity = Vector3.zero;
            NativeState.TraversalPresentationVelocity = Vector3.zero;
            NativeState.VerticalSpeed = 0f;
            NativeState.LastJumpTick = int.MinValue;
            NativeState.LastGroundedTick = m_Driver?.LastGroundedTick ?? int.MinValue;
            NativeState.LastAcceptedOwnerPoseTick = int.MinValue;
            NativeState.InputStateOwnerRaw = currentOwnerRaw;
            UpdateMotionState();
            m_ResetReplicatedOwnerInputStatePending = false;
        }

        public void StateAuthorityChanged()
        {
            // Shared master migration can change State Authority while LogicalOwner remains
            // identical, so IdentityChanged alone cannot protect these per-authority queues.
            m_Driver?.ResetNetworkTransientState();
            ResetSharedRuntimeState();
            // Mark both edges for a stamped consistency check. ApplyPending... is authority-gated,
            // and a stale flag is harmless because a matching owner stamp becomes a no-op. This
            // avoids depending on whether Fusion exposes the new authority role before or after
            // invoking this callback.
            m_ResetReplicatedOwnerInputStatePending = Object != null && Object.IsValid;
            ClearPendingExternalChanges();
            m_ExternalRootWritePresentationUntilTick = int.MinValue;
            m_NextOwnerPoseDiagnosticTime = 0f;
            m_NextOwnerPoseCollisionDiagnosticTime = 0f;
            m_NextOwnerMotionCaptureDiagnosticTime = 0f;
            m_NextOwnerMotionRejectionDiagnosticTime = 0f;
            m_NextOwnerMotionWindowDiagnosticTime = 0f;
            m_NextOwnerPredictionDiagnosticTime = 0f;
            ClearLiveExternalPresentationPose();
            m_RootHasRenderPose = false;
            m_InitialRenderTick = default;
        }

        /// <summary>
        /// Reports only noteworthy authoritative owner-pose decisions. A time gate prevents
        /// Fusion resimulation from turning one correction into a console flood.
        /// </summary>
        internal void LogOwnerPoseValidation(
            bool accepted,
            int sourceTick,
            Vector3 current,
            Vector3 requested,
            Vector3 applied,
            float distance,
            float kineticDistance,
            float authorityDistance)
        {
            if (!m_LogDiagnostics) return;

            bool requiresAuthorizedCatchUp = distance > kineticDistance + 0.001f;
            if (accepted && !requiresAuthorizedCatchUp) return;

            float now = Time.unscaledTime;
            if (now < m_NextOwnerPoseDiagnosticTime) return;
            m_NextOwnerPoseDiagnosticTime = now + 0.5f;

            string decision = accepted
                ? "accepted-authorized-catchup"
                : "rejected-distance";
            Log(
                $"owner-pose {decision} simulationTick={CurrentSimulationTick} " +
                $"sourceTick={sourceTick} operation={m_Driver?.ServerOwnerMotionOperation ?? 0u} " +
                $"distance={distance:F3} kinetic={kineticDistance:F3} " +
                $"authority={authorityDistance:F3} current={current:F3} " +
                $"requested={requested:F3} applied={applied:F3}");
        }

        internal void LogOwnerPoseCollisionBlocked(
            int sourceTick,
            bool authoritative,
            Vector3 current,
            Vector3 requested,
            Vector3 applied,
            CollisionFlags collisionFlags,
            float residualDistance,
            float applicationTolerance)
        {
            if (!m_LogDiagnostics) return;

            float now = Time.unscaledTime;
            if (now < m_NextOwnerPoseCollisionDiagnosticTime) return;
            m_NextOwnerPoseCollisionDiagnosticTime = now + 0.25f;

            Log(
                $"owner-pose blocked-by-controller simulationTick={CurrentSimulationTick} " +
                $"sourceTick={sourceTick} authoritative={authoritative} " +
                $"operation={m_Driver?.ServerOwnerMotionOperation ?? 0u} " +
                $"flags={collisionFlags} residual={residualDistance:F3} " +
                $"tolerance={applicationTolerance:F3} current={current:F3} " +
                $"requested={requested:F3} applied={applied:F3}");
        }

        internal void LogOwnerMotionCapture(
            int tick,
            bool ownerMotionActive,
            bool hasPendingOwnerPosition,
            bool includesOwnerPose,
            Vector3 ownerPosition,
            Vector3 rootMotionDelta,
            float rootMotionWeight)
        {
            if (!m_LogDiagnostics || !ownerMotionActive) return;

            float now = Time.unscaledTime;
            if (now < m_NextOwnerMotionCaptureDiagnosticTime) return;
            m_NextOwnerMotionCaptureDiagnosticTime = now + 0.5f;

            string mode = includesOwnerPose
                ? "absolute-owner-pose"
                : rootMotionWeight > 0.001f ||
                  rootMotionDelta.sqrMagnitude > 0.000001f
                    ? "root-motion"
                    : "no-displacement";
            Log(
                $"owner-motion capture tick={tick} mode={mode} " +
                $"pendingPosition={hasPendingOwnerPosition} owner={ownerPosition:F3} " +
                $"rootDelta={rootMotionDelta:F3} rootWeight={rootMotionWeight:F3}");
        }

        internal void LogOwnerMotionRejection(
            string reason,
            int sourceTick,
            Vector3 requestedDelta,
            float weight)
        {
            if (!m_LogDiagnostics) return;

            float now = Time.unscaledTime;
            if (now < m_NextOwnerMotionRejectionDiagnosticTime) return;
            m_NextOwnerMotionRejectionDiagnosticTime = now + 0.25f;

            Log(
                $"owner-motion rejected reason='{reason}' " +
                $"simulationTick={CurrentSimulationTick} sourceTick={sourceTick} " +
                $"operation={m_Driver?.ServerOwnerMotionOperation ?? 0u} " +
                $"window={m_Driver?.ServerOwnerMotionFromTick ?? int.MinValue}.." +
                $"{m_Driver?.ServerOwnerMotionUntilTick ?? int.MinValue} " +
                $"delta={requestedDelta:F3} weight={weight:F3}");
        }

        internal void LogServerOwnerMotionWindow(
            string action,
            uint operationId,
            int fromTick,
            int untilTick)
        {
            if (!m_LogDiagnostics) return;

            float now = Time.unscaledTime;
            bool terminal = string.Equals(action, "closed", StringComparison.Ordinal);
            if (!terminal && now < m_NextOwnerMotionWindowDiagnosticTime) return;
            m_NextOwnerMotionWindowDiagnosticTime = now + 0.75f;

            Log(
                $"server owner-motion window {action} " +
                $"simulationTick={CurrentSimulationTick} operation={operationId} " +
                $"range={fromTick}..{untilTick}");
        }

        private void LogSharedTransient(
            string phase,
            FusionNativeCharacterInput input,
            int trustedTick,
            int queuedCount)
        {
            if (!m_LogDiagnostics) return;

            float now = Time.unscaledTime;
            float nextDiagnosticTime;
            if (string.Equals(
                    phase,
                    "owner-queued-for-reliable-send",
                    StringComparison.Ordinal))
            {
                nextDiagnosticTime = m_NextSharedTransientSubmitDiagnosticTime;
                if (now < nextDiagnosticTime) return;
                m_NextSharedTransientSubmitDiagnosticTime = now + 0.5f;
            }
            else if (string.Equals(phase, "reliably-enqueued", StringComparison.Ordinal))
            {
                nextDiagnosticTime = m_NextSharedTransientReceiveDiagnosticTime;
                if (now < nextDiagnosticTime) return;
                m_NextSharedTransientReceiveDiagnosticTime = now + 0.5f;
            }
            else
            {
                nextDiagnosticTime = m_NextSharedTransientApplyDiagnosticTime;
                if (now < nextDiagnosticTime) return;
                m_NextSharedTransientApplyDiagnosticTime = now + 0.5f;
            }

            Log(
                $"Shared transient {phase} payloadTick={input.SourceTick} " +
                $"trustedTick={trustedTick} queue={queuedCount} " +
                $"ownerPose={input.HasOwnerPose} jump={input.HasJump} " +
                $"rootWeight={input.RootMotionWeight:F2} " +
                $"rootDelta={input.RootMotionDelta:F3} move={input.Move} " +
                $"yaw={input.Yaw:F1}");
        }

        private void LogSharedReconciliation(
            int authoritativeStateTick,
            int representedContinuousTick,
            int acknowledgedTransientTick,
            int historyBefore,
            int historyAfter,
            int replayedContinuous,
            int replayedTransient,
            Vector3 predictedBefore,
            Vector3 predictedAfter,
            string presentationMode)
        {
            if (!m_LogDiagnostics) return;

            float correctionDistance =
                IsFinite(predictedBefore) && IsFinite(predictedAfter)
                    ? Vector3.Distance(predictedBefore, predictedAfter)
                    : float.PositiveInfinity;
            if (replayedTransient == 0 && correctionDistance < 0.05f)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (now < m_NextSharedReconcileDiagnosticTime) return;
            m_NextSharedReconcileDiagnosticTime = now + 0.5f;

            Log(
                $"Shared reconciliation stateTick={authoritativeStateTick} " +
                $"continuousBaseline={representedContinuousTick} " +
                $"transientAck={acknowledgedTransientTick} " +
                $"history={historyBefore}->{historyAfter} " +
                $"replayedContinuous={replayedContinuous} " +
                $"replayedTransient={replayedTransient} " +
                $"correction={correctionDistance:F3} " +
                $"presentation={presentationMode} " +
                $"before={predictedBefore:F3} after={predictedAfter:F3}");
        }

        private void LogSharedTransientRejection(
            string reason,
            PlayerRef source,
            int trustedTick,
            int payloadTick,
            int flags,
            Vector3 ownerPosition,
            Vector3 rootMotionDelta,
            float rootMotionWeight,
            float jumpForce)
        {
            if (!m_LogDiagnostics) return;

            float now = Time.unscaledTime;
            if (now < m_NextSharedTransientRejectionDiagnosticTime) return;
            m_NextSharedTransientRejectionDiagnosticTime = now + 0.5f;

            Log(
                $"Shared transient rejected reason={reason} source={source} " +
                $"owner={m_Identity?.LogicalOwner} payloadTick={payloadTick} " +
                $"trustedTick={trustedTick} flags={flags} ownerPosition={ownerPosition:F3} " +
                $"rootDelta={rootMotionDelta:F3} rootWeight={rootMotionWeight:F2} " +
                $"jumpForce={jumpForce:F3}");
        }

        private void LogOwnerPredictionTick(
            bool hasInput,
            FusionNativeCharacterInput input,
            bool ownerPoseOwnedPendingPosition,
            bool hadPendingExternalPosition,
            Vector3 restoredTickPosition)
        {
            if (!m_LogDiagnostics || !IsLocalLogicalOwner ||
                (!input.HasOwnerPose && !hadPendingExternalPosition))
            {
                return;
            }

            if (Runner?.IsResimulation == true)
            {
                m_OwnerPredictionResimulationTicks++;
                return;
            }

            float now = Time.unscaledTime;
            if (now < m_NextOwnerPredictionDiagnosticTime) return;
            m_NextOwnerPredictionDiagnosticTime = now + 0.5f;
            int replayedTicks = m_OwnerPredictionResimulationTicks;
            m_OwnerPredictionResimulationTicks = 0;

            string inputTarget = input.HasOwnerPose
                ? input.OwnerPosition.ToString("F3")
                : "<none>";
            string pendingTarget = hadPendingExternalPosition
                ? (m_PendingExternalPositionIsAbsolute
                    ? m_PendingExternalPosition
                    : restoredTickPosition + m_PendingExternalPositionDelta).ToString("F3")
                : "<none>";
            Log(
                $"owner-prediction simulationTick={CurrentSimulationTick} " +
                $"inputTick={(Runner != null ? Runner.InputTick.Raw : 0)} " +
                $"hasInput={hasInput} ownerPose={input.HasOwnerPose} " +
                $"resimulatedTicks={replayedTicks} " +
                $"pendingPosition={hadPendingExternalPosition} " +
                $"singleWriter={ownerPoseOwnedPendingPosition} " +
                $"restored={restoredTickPosition:F3} inputTarget={inputTarget} " +
                $"pendingTarget={pendingTarget} final={transform.position:F3} " +
                $"velocity={NativeState.Velocity:F3} " +
                $"traversalPresentation={NativeState.TraversalPresentationVelocity:F3} " +
                $"traversalPresentationActive=" +
                $"{(NativeState.MotionFlags & MotionFlagTraversalPresentation) != 0} " +
                $"livePresentation={ShouldUseLiveExternalPresentationPose} " +
                $"liveTarget={(m_HasLiveExternalPresentationPose ? m_LiveExternalPresentationPosition.ToString("F3") : "<none>")}");
        }

        /// <summary>
        /// Called by the runner's single input collector. Returns false for non-owners,
        /// Shared mode, and characters that have not completed backend initialization.
        /// </summary>
        public bool TryConsumeNetworkInput(NetworkRunner runner, NetworkInput input)
        {
            if (runner == null || runner != Runner || !runner.IsRunning ||
                runner.GameMode == GameMode.Shared || m_Driver == null ||
                !m_BackendInitialized || !IsLocalLogicalOwner)
            {
                return false;
            }

            FusionNativeCharacterInput characterInput =
                m_Driver.CaptureInput(runner.InputTick.Raw);
            input.Set(characterInput);
            return true;
        }

        /// <summary>
        /// Variant useful to custom runner callback implementations that aggregate before
        /// assigning the final Fusion NetworkInput value.
        /// </summary>
        public bool TryGetNetworkInput(
            NetworkRunner runner,
            out FusionNativeCharacterInput characterInput)
        {
            characterInput = default;
            if (runner == null || runner != Runner || !runner.IsRunning ||
                runner.GameMode == GameMode.Shared || m_Driver == null ||
                !m_BackendInitialized || !IsLocalLogicalOwner)
            {
                return false;
            }

            characterInput = m_Driver.CaptureInput(runner.InputTick.Raw);
            return true;
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[FusionNativeCharacter] {name}: {message}", this);
        }

        private struct SharedCharacterTransient
        {
            public FusionNativeCharacterInput Input;
            public int TrustedTick;
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector2 value) =>
            IsFinite(value.x) && IsFinite(value.y);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    }
}
