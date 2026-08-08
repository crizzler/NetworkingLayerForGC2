#if ARAWN_GC2_FUSION_KCC
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using Fusion;
using Fusion.Addons.KCC;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.KCC
{
    /// <summary>
    /// Strongly typed Advanced KCC adapter. This component belongs to the nested, foot-space
    /// Fusion KCC Motor NetworkObject; its backend proxy and GC2 Character stay on the parent.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Advanced KCC Motor Body")]
    [DefaultExecutionOrder(-140)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(global::Fusion.Addons.KCC.KCC))]
    public sealed class FusionKccMotorBody : NetworkBehaviour,
        IFusionKccRuntimeAdapter,
        IStateAuthorityChanged
    {
        private const int SharedTransientCapacity = 128;
        private const float PositionEpsilon = 0.0001f;

        [Header("GC2 Root")]
        [SerializeField] private FusionKccCharacterBackend m_Backend;

        [Header("Advanced KCC")]
        [SerializeField] private global::Fusion.Addons.KCC.KCC m_Kcc;
        [SerializeField] private FusionGc2KccProcessor m_Gc2Processor;
        [SerializeField] private EnvironmentProcessor m_EnvironmentProcessor;

        [Header("Diagnostics")]
        [SerializeField] private bool m_LogDiagnostics;
        [SerializeField, Min(0.1f)] private float m_DiagnosticInterval = 1f;

        [Networked] private int LastJumpTick { get; set; }
        [Networked] private int LastGroundedTick { get; set; }
        [Networked] private NetworkBool WasGrounded { get; set; }
        [Networked] private int AppliedBackendTeleportSequence { get; set; }
        [Networked] private int AppliedBackendMotorCommandSequence { get; set; }
        [Networked] public int LastAppliedSharedTransientSourceTick { get; private set; }
        [Networked] private float ReplicatedCapsuleHeight { get; set; }
        [Networked] private float ReplicatedCapsuleRadius { get; set; }
        [Networked] private NetworkBool ReplicatedCollisionEnabled { get; set; }

        private NetworkCharacter m_NetworkCharacter;
        private FusionNetworkIdentity m_Identity;
        private Character m_Character;
        private Transform m_Root;
        private FusionKccCharacterDriver m_Driver;
        private NetworkCharacter.NetworkRole m_Role;
        private NetworkSessionProfile m_Profile;
        private bool m_AdapterInitialized;

        private FusionNativeCharacterInput m_LastContinuousInput;
        private bool m_HasLastContinuousInput;
        private FusionNativeCharacterInput m_LatestSharedInput;
        private bool m_HasSharedInput;
        private int m_LatestSharedTrustedTick = int.MinValue;
        private int m_LastSharedContinuousPayloadTick = int.MinValue;
        private int m_LastSharedTransientPayloadTick = int.MinValue;
        private readonly Queue<SharedTransient> m_SharedTransients =
            new Queue<SharedTransient>(16);
        private bool m_SharedOverflowLatched;

        private bool m_CollisionEnabled = true;
        private bool m_NotifyAcceptedOwnerPoseAfterSimulation;

        private Func<global::Fusion.Addons.KCC.KCC, Collider, bool>
            m_PreviousResolveCollision;
        private Func<global::Fusion.Addons.KCC.KCC, Collider, bool>
            m_InstalledResolveCollision;

        private int m_LastSharedOwnerPumpTick = int.MinValue;
        private int m_LastRenderFrame = int.MinValue;
        private float m_NextAuthorityRequestTime;
        private float m_NextDiagnosticTime;
        private Vector3 m_LastFixedRootPosition;
        private Vector3 m_LastRenderRootPosition;
        private bool m_HasLastFixedRootPosition;
        private bool m_HasLastRenderRootPosition;

        public bool RequiresSharedLogicalOwnerProxyPump =>
            SharedAuthorityMode ==
            FusionKccSharedAuthorityMode.SharedMasterMovementAuthority;

        public bool IsGrounded => m_Kcc != null && m_Kcc.IsSpawned &&
                                  m_Kcc.Data.IsGrounded;
        public Vector3 FloorNormal => IsGrounded
            ? m_Kcc.Data.GroundNormal
            : Vector3.up;
        public float SkinWidth => m_Kcc != null
            ? Mathf.Max(0f, m_Kcc.Settings.Extent)
            : 0.035f;
        public bool CollisionEnabled => m_CollisionEnabled;
        public bool IsRemoteProxyRole =>
            m_Role == NetworkCharacter.NetworkRole.RemoteClient;
        public bool CanApplyAuthoritativeTeleport =>
            m_Backend != null && m_Backend.CanApplyAuthoritativeKccCommands;
        internal bool CanCaptureLocalMovementCommands =>
            !IsRemoteProxyRole && IsLocalLogicalOwner;
        public int CurrentTick => Runner != null ? Runner.Tick.Raw : 0;
        public float SimulationDeltaTime => Runner != null
            ? Mathf.Max(Runner.DeltaTime, 0.001f)
            : Mathf.Max(Time.fixedDeltaTime, 0.001f);

        private FusionKccSharedAuthorityMode SharedAuthorityMode =>
            m_Backend != null
                ? m_Backend.SharedAuthorityMode
                : FusionKccSharedAuthorityMode.OwnerMovementAuthority;

        private bool IsLocalLogicalOwner => m_Identity != null &&
                                            m_Identity.IsLocalLogicalOwner;

        private bool HasCentralMovementAuthority => Object != null &&
                                                    Object.IsValid &&
                                                    Object.HasStateAuthority;

        private float ActiveHeight => m_Kcc != null
            ? Mathf.Max(0.1f, m_Kcc.Settings.Height)
            : m_Character?.Motion != null
                ? Mathf.Max(0.1f, m_Character.Motion.Height)
                : 2f;

        private float ActiveRadius => m_Kcc != null
            ? Mathf.Max(0.01f, m_Kcc.Settings.Radius)
            : m_Character?.Motion != null
                ? Mathf.Max(0.01f, m_Character.Motion.Radius)
                : 0.35f;

        private void Awake()
        {
            CacheComponents();
            ConfigureManualUpdate();
        }

        private void OnDestroy()
        {
            RestoreSelfCollisionFilter();
        }

        public override void Spawned()
        {
            CacheComponents();
            ConfigureManualUpdate();
            ResetNetworkState();
            ConfigureKccAuthorityBehaviour();
            InstallSelfCollisionFilter();

            // A peer can spawn this child from replicated state while also becoming the new
            // Shared master. Preserve the received ACK/command baselines in that case; initialize
            // them only for a genuinely new authority-owned object.
            if (Object.HasStateAuthority && Object.LastReceiveTick == default)
            {
                LastJumpTick = int.MinValue;
                LastGroundedTick = int.MinValue;
                LastAppliedSharedTransientSourceTick = int.MinValue;
                WasGrounded = false;
                ReplicatedCapsuleHeight = m_Character?.Motion != null
                    ? Mathf.Max(0.1f, m_Character.Motion.Height)
                    : Mathf.Max(0.1f, m_Kcc.Settings.Height);
                ReplicatedCapsuleRadius = m_Character?.Motion != null
                    ? Mathf.Clamp(
                        m_Character.Motion.Radius,
                        0.01f,
                        ReplicatedCapsuleHeight * 0.5f)
                    : Mathf.Max(0.01f, m_Kcc.Settings.Radius);
                ReplicatedCollisionEnabled = m_CollisionEnabled;
                AppliedBackendTeleportSequence = m_Backend != null &&
                    m_Backend.TryGetAuthoritativeTeleport(
                        out int sequence,
                        out _,
                        out _,
                        out _)
                    ? sequence
                    : 0;
                AppliedBackendMotorCommandSequence = m_Backend != null &&
                    m_Backend.TryGetAuthoritativeMotorCommand(
                        out int motorCommandSequence,
                        out _,
                        out _,
                        out _)
                    ? motorCommandSequence
                    : 0;
            }
            else if (Object.HasStateAuthority)
            {
                m_CollisionEnabled = ReplicatedCollisionEnabled;
            }

            ApplyReplicatedScale();
            HandleSharedAuthorityHandoff(force: true);
            LogDiagnostic("spawned", default, false, 0f);
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            ResetNetworkState();
            RestoreSelfCollisionFilter();
            // Keep manual update enabled while this companion exists. Switching KCC back to its
            // automatic callbacks here can let it simulate once more after this body has stopped
            // supplying GC2 intent, using stale processor state during teardown or pooling.
        }

        public override void FixedUpdateNetwork()
        {
            if (!EnsureRuntimeReady()) return;
            HandleSharedAuthorityHandoff(force: false);

            int tick = Runner.Tick.Raw;
            bool hasInput = TryResolveSimulationInput(
                tick,
                out FusionNativeCharacterInput input,
                out int sharedTransientPayloadTick);
            if (!hasInput && !ShouldAdvanceWithoutInput()) return;

            float verticalVelocityBefore = m_Kcc.FixedData.RealVelocity.y;
            PrepareKccTick(input, tick);
            m_Kcc.ManualFixedUpdate();
            if (Object.HasStateAuthority)
            {
                ReplicatedCollisionEnabled = m_CollisionEnabled;
            }
            ApplyRootFromKcc(m_Kcc.FixedData, render: false);
            ApplyReplicatedScale();
            UpdateGroundedStateAfterSimulation(tick, verticalVelocityBefore);
            NotifyAcceptedOwnerPoseAfterSimulation();
            if (sharedTransientPayloadTick != int.MinValue &&
                Object.HasStateAuthority &&
                sharedTransientPayloadTick > LastAppliedSharedTransientSourceTick)
            {
                // This is an application acknowledgement, not an RPC receipt acknowledgement.
                // Advancing it only after ManualFixedUpdate completes lets the logical owner
                // safely retain/retry finite traversal, jump and motor commands across migration.
                LastAppliedSharedTransientSourceTick = sharedTransientPayloadTick;
            }

            if (m_Driver != null)
            {
                m_Driver.ApplySimulationVelocity(m_Kcc.FixedData.RealVelocity);
            }

            LogDiagnostic(
                "fixed",
                input,
                hasInput,
                m_HasLastFixedRootPosition
                    ? Vector3.Distance(m_LastFixedRootPosition, m_Root.position)
                    : 0f);
            m_LastFixedRootPosition = m_Root.position;
            m_HasLastFixedRootPosition = true;
        }

        public override void Render()
        {
            RenderInternal();
        }

        public void StateAuthorityChanged()
        {
            ResetSharedReceiveState();
            if (Object != null && Object.IsValid && Object.HasStateAuthority)
            {
                m_CollisionEnabled = ReplicatedCollisionEnabled;
            }
            ConfigureKccAuthorityBehaviour();
            LogDiagnostic("state-authority-changed", default, false, 0f);
        }

        public IUnitDriver CreateDriver(
            FusionKccCharacterBackend backend,
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            CacheComponents(backend, networkCharacter);
            m_Role = role;
            m_Driver ??= new FusionKccCharacterDriver();
            m_Driver.AttachMotor(this);
            m_Gc2Processor?.Bind(m_Driver, m_EnvironmentProcessor);
            return m_Driver;
        }

        public void Initialize(
            FusionKccCharacterBackend backend,
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            CacheComponents(backend, networkCharacter);
            m_Role = role;
            m_AdapterInitialized = true;
            m_Driver?.AttachMotor(this);
            m_Gc2Processor?.Bind(m_Driver, m_EnvironmentProcessor);
            ConfigureManualUpdate();
            ConfigureKccAuthorityBehaviour();
            InstallSelfCollisionFilter();
        }

        public void ApplySessionProfile(NetworkSessionProfile profile)
        {
            m_Profile = profile;
        }

        public void Shutdown()
        {
            m_AdapterInitialized = false;
            m_LastContinuousInput = default;
            m_HasLastContinuousInput = false;
            RestoreSelfCollisionFilter();
            if (m_Gc2Processor != null)
            {
                m_Gc2Processor.SetContinuousIntent(
                    Vector3.zero,
                    m_Root != null ? m_Root.eulerAngles.y : 0f,
                    0f,
                    Physics.gravity,
                    updateKinematics: false,
                    forceGrounded: false,
                    terminalVelocity: -53f,
                    locomotionRootMotionWeight: 0f,
                    renderRootMotionVelocity: Vector3.zero);
            }
        }

        public bool TryGetAuthoritativePose(
            out Vector3 position,
            out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.identity;
            if (m_Kcc == null || !m_Kcc.IsSpawned) return false;

            KCCData data = m_Kcc.FixedData;
            position = FootToRoot(data.TargetPosition);
            rotation = data.TransformRotation;
            return IsFinite(position) && IsFinite(rotation);
        }

        public bool TryConsumeNetworkInput(NetworkRunner runner, NetworkInput input)
        {
            if (!CanCollectFusionInput(runner)) return false;
            FusionNativeCharacterInput characterInput =
                m_Driver.CaptureInput(runner.InputTick.Raw);
            input.Set(characterInput);
            return true;
        }

        public bool TryGetNetworkInput(
            NetworkRunner runner,
            out FusionNativeCharacterInput characterInput)
        {
            characterInput = default;
            if (!CanCollectFusionInput(runner)) return false;
            characterInput = m_Driver.CaptureInput(runner.InputTick.Raw);
            return true;
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
            if (!CanAcceptSharedInput(source) ||
                sourceTick <= m_LastSharedContinuousPayloadTick ||
                !IsFinite(move) || !IsFinite(yaw) || !IsFinite(ownerPosition))
            {
                return;
            }

            if (move.sqrMagnitude > 1f) move.Normalize();
            bool continuousOwnerPose =
                (flags & FusionNativeCharacterInput.FlagOwnerPose) != 0 &&
                (flags & FusionNativeCharacterInput.FlagContinuousOwnerPose) != 0;
            m_LatestSharedInput = new FusionNativeCharacterInput
            {
                Move = move,
                Yaw = Mathf.Repeat(yaw, 360f),
                SourceTick = sourceTick,
                Flags = continuousOwnerPose
                    ? FusionNativeCharacterInput.FlagOwnerPose |
                      FusionNativeCharacterInput.FlagContinuousOwnerPose
                    : 0,
                OwnerPosition = continuousOwnerPose
                    ? ownerPosition
                    : Vector3.zero,
                RootMotionDelta = Vector3.zero,
                RootMotionWeight = 0f,
                JumpForce = 0f
            };
            m_LatestSharedTrustedTick = trustedSourceTick;
            m_LastSharedContinuousPayloadTick = sourceTick;
            m_HasSharedInput = true;
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
            if (!CanAcceptSharedInput(source) ||
                sourceTick <= LastAppliedSharedTransientSourceTick ||
                sourceTick <= m_LastSharedTransientPayloadTick ||
                !IsFinite(move) || !IsFinite(yaw) || !IsFinite(ownerPosition) ||
                !IsFinite(rootMotionDelta) || !IsFinite(rootMotionWeight) ||
                !IsFinite(jumpForce))
            {
                return;
            }

            if (m_SharedOverflowLatched) return;
            if (m_SharedTransients.Count >= SharedTransientCapacity)
            {
                m_SharedOverflowLatched = true;
                Debug.LogError(
                    $"[FusionKCC] Reliable Shared transient backlog exceeded " +
                    $"{SharedTransientCapacity} samples for '{name}'. Input is blocked " +
                    "until the object/session is reset to preserve ordered one-shots.",
                    this);
                return;
            }

            if (move.sqrMagnitude > 1f) move.Normalize();
            int transientFlags = flags &
                (FusionNativeCharacterInput.FlagJump |
                 FusionNativeCharacterInput.FlagOwnerPose |
                 FusionNativeCharacterInput.FlagResetVerticalVelocity |
                 FusionNativeCharacterInput.FlagCollisionChanged |
                 FusionNativeCharacterInput.FlagCollisionEnabled);
            if ((transientFlags & FusionNativeCharacterInput.FlagCollisionChanged) == 0)
            {
                transientFlags &= ~FusionNativeCharacterInput.FlagCollisionEnabled;
            }

            FusionNativeCharacterInput transient = new FusionNativeCharacterInput
            {
                Move = move,
                Yaw = Mathf.Repeat(yaw, 360f),
                SourceTick = sourceTick,
                Flags = transientFlags,
                OwnerPosition = ownerPosition,
                RootMotionDelta = rootMotionDelta,
                RootMotionWeight = Mathf.Clamp01(rootMotionWeight),
                JumpForce = Mathf.Max(0f, jumpForce)
            };
            if (!FusionCharacterInputUtility.HasSharedTransientInput(transient)) return;

            m_SharedTransients.Enqueue(new SharedTransient
            {
                Input = transient,
                TrustedTick = trustedSourceTick
            });
            m_LastSharedTransientPayloadTick = sourceTick;
        }

        public void SimulateSharedLogicalOwnerProxyTick(
            int tick,
            bool restorePredictedPose)
        {
            if (!RequiresSharedLogicalOwnerProxyPump ||
                Runner == null || !Runner.IsRunning ||
                Runner.GameMode != GameMode.Shared ||
                !IsLocalLogicalOwner || m_Driver == null ||
                tick == m_LastSharedOwnerPumpTick)
            {
                return;
            }

            m_LastSharedOwnerPumpTick = tick;
            FusionNativeCharacterInput input = m_Driver.CaptureInput(tick);
            bool submitted = m_Identity != null &&
                             m_Identity.TrySubmitSharedCharacterInput(
                                 input,
                                 out _);
            LogDiagnostic("shared-owner-submit", input, submitted, 0f);
        }

        public void RenderSharedLogicalOwnerProxy()
        {
            if (!RequiresSharedLogicalOwnerProxyPump) return;
            RenderInternal();
        }

        public void QueueAuthoritativeTeleport(
            Vector3 footPosition,
            Quaternion rotation)
        {
            m_Backend?.QueueAuthoritativeTeleport(
                footPosition,
                rotation,
                hardTeleport: true);
        }

        public void UpdateQueuedTeleportRotation(Quaternion rotation)
        {
            m_Backend?.UpdateQueuedTeleportRotation(rotation);
        }

        public void RequestAuthoritativeScale(Vector3 scale)
        {
            m_Backend?.RequestAuthoritativeScale(scale);
        }

        public Vector3 GetRequestedOrReplicatedRootScale(Vector3 fallback)
        {
            return m_Backend != null
                ? m_Backend.GetRequestedOrReplicatedRootScale(fallback)
                : fallback;
        }

        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            m_Backend?.OpenOwnerMotionWindow(durationSeconds);
        }

        public bool IsOwnerMotionActive(int tick) =>
            m_Backend?.IsOwnerMotionActive(tick) == true;

        public void OpenServerOwnerMotionWindow(
            float durationSeconds,
            uint operationId = 0)
        {
            m_Backend?.OpenServerOwnerMotionWindow(durationSeconds, operationId);
        }

        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            m_Backend?.CloseServerOwnerMotionWindow(graceSeconds);
        }

        public void RequestVerticalVelocityReset()
        {
            if (CanCaptureLocalMovementCommands)
            {
                m_Driver?.QueueLocalVerticalVelocityReset();
                return;
            }

            // Server-side operations can legitimately target a remote logical owner. The root
            // backend authority gate rejects this request on ordinary non-authoritative proxies.
            m_Backend?.QueueAuthoritativeVerticalVelocityReset();
        }

        public void SetCollisionEnabled(bool enabled)
        {
            if (CanCaptureLocalMovementCommands)
            {
                m_Driver?.QueueLocalCollisionChange(enabled);
                return;
            }

            m_Backend?.QueueAuthoritativeCollision(enabled);
        }

        private void RenderInternal()
        {
            if (m_LastRenderFrame == Time.frameCount || !EnsureRuntimeReady()) return;
            m_LastRenderFrame = Time.frameCount;
            HandleSharedAuthorityHandoff(force: false);

            if (IsLocalLogicalOwner && m_Driver != null &&
                m_Kcc.IsPredictingInRenderUpdate)
            {
                FusionNativeCharacterInput renderIntent =
                    m_Driver.CaptureRenderIntent(CurrentTick);
                m_Driver.CaptureRenderRootMotion(
                    out Vector3 renderRootMotionVelocity,
                    out float renderRootMotionWeight);
                SetContinuousKccIntent(
                    renderIntent,
                    renderRootMotionVelocity,
                    renderRootMotionWeight);
            }

            m_Kcc.ManualRenderUpdate();
            ApplyRootFromKcc(m_Kcc.RenderData, render: true);
            ApplyReplicatedScale();
            if (m_Driver != null)
            {
                m_Driver.ApplySimulationVelocity(m_Kcc.RenderData.RealVelocity);
            }

            LogDiagnostic(
                "render",
                default,
                false,
                m_HasLastRenderRootPosition
                    ? Vector3.Distance(m_LastRenderRootPosition, m_Root.position)
                    : 0f);
            m_LastRenderRootPosition = m_Root.position;
            m_HasLastRenderRootPosition = true;
        }

        private bool TryResolveSimulationInput(
            int tick,
            out FusionNativeCharacterInput input,
            out int sharedTransientPayloadTick)
        {
            input = default;
            input.SourceTick = tick;
            input.Yaw = m_Root != null ? m_Root.eulerAngles.y : 0f;
            sharedTransientPayloadTick = int.MinValue;

            if (Runner.GameMode == GameMode.Shared)
            {
                if (SharedAuthorityMode ==
                    FusionKccSharedAuthorityMode.OwnerMovementAuthority)
                {
                    if (!Object.HasStateAuthority) return false;
                    if (m_Identity != null &&
                        m_Identity.LogicalOwner.IsRealPlayer &&
                        !IsLocalLogicalOwner)
                    {
                        return false;
                    }

                    if (m_Driver == null) return false;
                    input = m_Driver.CaptureInput(tick);
                    RememberContinuousInput(input);
                    return true;
                }

                if (!Object.HasStateAuthority) return false;
                if (IsLocalLogicalOwner && m_Driver != null)
                {
                    input = m_Driver.CaptureInput(tick);
                    if (FusionCharacterInputUtility.HasSharedTransientInput(input))
                    {
                        // The Shared master can also be the local logical owner. Keep its state
                        // metadata consistent with remotely submitted one-shots even though no
                        // RPC retry queue is required on this peer.
                        sharedTransientPayloadTick = input.SourceTick;
                    }
                    RememberContinuousInput(input);
                    return true;
                }

                if (m_SharedTransients.Count > 0)
                {
                    SharedTransient transient = m_SharedTransients.Dequeue();
                    input = transient.Input;
                    sharedTransientPayloadTick = input.SourceTick;
                    input.SourceTick = transient.TrustedTick;
                    RememberContinuousInput(input);
                    return true;
                }

                if (m_HasSharedInput)
                {
                    input = m_LatestSharedInput;
                    input.SourceTick = m_LatestSharedTrustedTick;
                    RememberContinuousInput(input);
                    return true;
                }

                if (m_Identity == null || !m_Identity.LogicalOwner.IsRealPlayer)
                {
                    if (m_Driver == null) return false;
                    input = m_Driver.CaptureInput(tick);
                    RememberContinuousInput(input);
                    return true;
                }

                return TryHoldContinuousInput(tick, out input);
            }

            if (GetInput(out FusionNativeCharacterInput fusionInput))
            {
                input = fusionInput;
                RememberContinuousInput(input);
                return true;
            }

            bool stateAuthorityNpc = Object.HasStateAuthority &&
                (m_Identity == null || !m_Identity.LogicalOwner.IsRealPlayer);
            if (m_Driver != null &&
                ((Object.HasStateAuthority && IsLocalLogicalOwner) ||
                 stateAuthorityNpc))
            {
                // Dedicated/server-owned NPCs have no Fusion input stream. Their GC2 AI still
                // records direction/NavMesh intent and must advance in Fusion's fixed tick.
                input = m_Driver.CaptureInput(tick);
                RememberContinuousInput(input);
                return true;
            }

            return Object.HasStateAuthority && TryHoldContinuousInput(tick, out input);
        }

        private bool ShouldAdvanceWithoutInput()
        {
            if (Runner == null || Object == null || !Object.IsValid) return false;
            if (Runner.GameMode == GameMode.Shared &&
                SharedAuthorityMode ==
                FusionKccSharedAuthorityMode.OwnerMovementAuthority &&
                m_Identity != null && m_Identity.LogicalOwner.IsRealPlayer &&
                !IsLocalLogicalOwner)
            {
                return false;
            }
            return Object.IsInSimulation;
        }

        private void PrepareKccTick(FusionNativeCharacterInput input, int tick)
        {
            m_NotifyAcceptedOwnerPoseAfterSimulation = false;
            int commandTick = ResolveAuthoritativeInputTick(input, tick);
            Vector2 move = IsFinite(input.Move) ? input.Move : Vector2.zero;
            if (move.sqrMagnitude > 1f) move.Normalize();
            float yaw = IsFinite(input.Yaw)
                ? Mathf.Repeat(input.Yaw, 360f)
                : m_Root.eulerAngles.y;

            SynchronizeSimulationCapsule();

            bool hasOwnerPose = TryValidateOwnerPose(
                input,
                commandTick,
                out Vector3 ownerFootPosition);
            ValidateRootMotion(
                input,
                commandTick,
                hasOwnerPose,
                out Vector3 rootMotionDelta,
                out float rootMotionWeight);

            Vector3 jumpImpulse = Vector3.zero;
            if (!hasOwnerPose && input.HasJump && CanApplyJump(commandTick))
            {
                float requested = IsFinite(input.JumpForce)
                    ? Mathf.Max(0f, input.JumpForce)
                    : 0f;
                float maximum = m_Character?.Motion != null
                    ? Mathf.Max(
                        m_Character.Motion.JumpForce,
                        m_Character.Motion.IsJumpingForce)
                    : requested;
                float force = Object.HasStateAuthority
                    ? Mathf.Min(requested, maximum)
                    : requested;
                if (force <= 0f) force = maximum;
                if (force > 0f)
                {
                    jumpImpulse = Vector3.up * force;
                    LastJumpTick = commandTick;
                    if (!Runner.IsResimulation) m_Character?.OnJump(force);
                }
            }

            bool hasBackendTeleport = TryGetPendingBackendTeleport(
                out Vector3 backendTeleportFoot,
                out Quaternion backendTeleportRotation,
                out bool backendHardTeleport);
            bool hasTeleport = hasBackendTeleport || hasOwnerPose;
            Vector3 teleportFoot = hasBackendTeleport
                ? backendTeleportFoot
                : ownerFootPosition;
            bool isTeleport = hasBackendTeleport && backendHardTeleport;
            Quaternion targetRotation = hasBackendTeleport
                ? backendTeleportRotation
                : Quaternion.Euler(0f, yaw, 0f);
            if (hasBackendTeleport)
            {
                yaw = targetRotation.eulerAngles.y;
                rootMotionDelta = Vector3.zero;
                rootMotionWeight = 0f;
            }

            bool hasBackendMotorCommand = TryGetPendingBackendMotorCommand(
                out bool backendResetVerticalVelocity,
                out bool backendCollisionChanged,
                out bool backendCollisionEnabled);
            bool resetVerticalVelocity = input.HasResetVerticalVelocity ||
                                         (hasBackendMotorCommand &&
                                          backendResetVerticalVelocity);
            bool collisionChanged = input.HasCollisionChange ||
                                    (hasBackendMotorCommand &&
                                     backendCollisionChanged);
            bool collisionEnabled = hasBackendMotorCommand && backendCollisionChanged
                ? backendCollisionEnabled
                : input.HasCollisionChange
                    ? input.CollisionEnabled
                    : m_CollisionEnabled;
            if (collisionChanged)
            {
                m_CollisionEnabled = collisionEnabled;
            }

            input.Move = move;
            input.Yaw = yaw;
            input.RootMotionDelta = rootMotionDelta;
            input.RootMotionWeight = rootMotionWeight;
            SetContinuousKccIntent(input);
            m_Gc2Processor.QueueTickCommands(
                hasTeleport,
                teleportFoot,
                isTeleport,
                rootMotionDelta,
                rootMotionWeight,
                jumpImpulse,
                resetVerticalVelocity,
                collisionChanged,
                collisionEnabled);
        }

        private void SetContinuousKccIntent(
            FusionNativeCharacterInput input,
            Vector3 renderRootMotionVelocity = default,
            float renderRootMotionWeight = -1f)
        {
            Vector2 move = IsFinite(input.Move) ? input.Move : Vector2.zero;
            if (move.sqrMagnitude > 1f) move.Normalize();
            float yaw = IsFinite(input.Yaw)
                ? Mathf.Repeat(input.Yaw, 360f)
                : m_Root != null
                    ? m_Root.eulerAngles.y
                    : 0f;
            float speed = m_Character?.Motion != null
                ? Mathf.Max(0f, m_Character.Motion.LinearSpeed)
                : 0f;
            Vector3 direction = new Vector3(move.x, 0f, move.y);
            float gravityY = Physics.gravity.y;
            if (m_Character?.Motion != null)
            {
                KCCData data = m_Kcc != null && m_Kcc.IsInFixedUpdate
                    ? m_Kcc.FixedData
                    : m_Kcc?.RenderData;
                gravityY = data != null && data.DynamicVelocity.y >= 0f
                    ? m_Character.Motion.GravityUpwards
                    : m_Character.Motion.GravityDownwards;
            }
            float gravityInfluence = m_Driver != null
                ? Mathf.Max(0f, m_Driver.CurrentGravityInfluence)
                : 1f;
            bool updateKinematics = m_Driver == null ||
                                    m_Driver.UpdateKinematicsEnabled;
            bool forceGrounded = m_Driver?.ForceGroundedValue == true;
            float terminalVelocity = m_Character?.Motion != null
                ? m_Character.Motion.TerminalVelocity
                : -53f;
            float locomotionRootMotionWeight = renderRootMotionWeight >= 0f &&
                                               IsFinite(renderRootMotionWeight)
                ? Mathf.Clamp01(renderRootMotionWeight)
                : IsFinite(input.RootMotionWeight)
                    ? Mathf.Clamp01(input.RootMotionWeight)
                    : 0f;

            m_Gc2Processor.SetContinuousIntent(
                direction,
                yaw,
                speed,
                Vector3.up * gravityY * gravityInfluence,
                updateKinematics,
                forceGrounded,
                terminalVelocity,
                locomotionRootMotionWeight,
                IsFinite(renderRootMotionVelocity)
                    ? renderRootMotionVelocity
                    : Vector3.zero);
        }

        private bool TryGetPendingBackendTeleport(
            out Vector3 footPosition,
            out Quaternion rotation,
            out bool hardTeleport)
        {
            footPosition = default;
            rotation = Quaternion.identity;
            hardTeleport = false;
            if (m_Backend == null ||
                !m_Backend.TryGetAuthoritativeTeleport(
                    out int sequence,
                    out footPosition,
                    out rotation,
                    out hardTeleport) ||
                sequence == AppliedBackendTeleportSequence)
            {
                return false;
            }

            AppliedBackendTeleportSequence = sequence;
            return true;
        }

        private bool TryGetPendingBackendMotorCommand(
            out bool resetVerticalVelocity,
            out bool collisionChanged,
            out bool collisionEnabled)
        {
            resetVerticalVelocity = false;
            collisionChanged = false;
            collisionEnabled = false;
            if (m_Backend == null ||
                !m_Backend.TryGetAuthoritativeMotorCommand(
                    out int sequence,
                    out resetVerticalVelocity,
                    out collisionChanged,
                    out collisionEnabled) ||
                sequence == AppliedBackendMotorCommandSequence)
            {
                return false;
            }

            AppliedBackendMotorCommandSequence = sequence;
            return true;
        }

        private int ResolveAuthoritativeInputTick(
            FusionNativeCharacterInput input,
            int simulationTick)
        {
            // Shared-master RPC routing replaces SourceTick with RpcInfo.Tick before this point.
            // Use that authenticated clock for cooldowns and server motion windows. Host/Client,
            // Dedicated Server and owner-authoritative Shared simulation stay on Runner.Tick.
            return Runner != null && Runner.GameMode == GameMode.Shared &&
                   SharedAuthorityMode ==
                   FusionKccSharedAuthorityMode.SharedMasterMovementAuthority
                ? input.SourceTick
                : simulationTick;
        }

        private void ValidateRootMotion(
            FusionNativeCharacterInput input,
            int tick,
            bool hasOwnerPose,
            out Vector3 rootMotionDelta,
            out float rootMotionWeight)
        {
            rootMotionDelta = IsFinite(input.RootMotionDelta)
                ? input.RootMotionDelta
                : Vector3.zero;
            rootMotionWeight = IsFinite(input.RootMotionWeight)
                ? Mathf.Clamp01(input.RootMotionWeight)
                : 0f;
            if (hasOwnerPose || rootMotionWeight <= 0.001f)
            {
                rootMotionDelta = Vector3.zero;
                rootMotionWeight = 0f;
                return;
            }

            bool animationAllowsRootMotion =
                m_Character != null && m_Character.RootMotionPosition > 0.001f;
            bool centralized = HasCentralizedMovementAuthority;
            bool windowAllowsRootMotion = centralized
                ? m_Backend?.IsServerMotionTickAuthorized(tick) == true
                : m_Backend?.IsOwnerMotionActive(tick) == true;
            if (!animationAllowsRootMotion && !windowAllowsRootMotion)
            {
                if (rootMotionDelta.sqrMagnitude > 0.000001f)
                {
                    LogDiagnostic(
                        centralized
                            ? "root-motion-rejected-server-window"
                            : "root-motion-rejected-owner-window",
                        input,
                        true,
                        rootMotionDelta.magnitude);
                }
                rootMotionDelta = Vector3.zero;
                rootMotionWeight = 0f;
                return;
            }

            if (animationAllowsRootMotion)
            {
                rootMotionWeight = Mathf.Min(
                    rootMotionWeight,
                    Mathf.Clamp01(m_Character.RootMotionPosition));
            }

            // Validate author-authored displacement on every movement authority. In Host mode
            // this is the server; in owner-authoritative Shared mode it is a local safety limit
            // (the documented topology remains less cheat-resistant).
            if (Object != null && Object.IsValid && Object.HasStateAuthority)
            {
                float speedMultiplier = m_Profile != null
                    ? Mathf.Max(1f, m_Profile.maxSpeedMultiplier)
                    : 1.2f;
                float configuredSpeed = m_Character?.Motion != null
                    ? Mathf.Max(0f, m_Character.Motion.LinearSpeed)
                    : 0f;
                float maximumDelta = configuredSpeed * speedMultiplier *
                                     SimulationDeltaTime;
                if (maximumDelta > 0f)
                {
                    rootMotionDelta = Vector3.ClampMagnitude(
                        rootMotionDelta,
                        maximumDelta);
                }
                else
                {
                    rootMotionDelta = Vector3.zero;
                    rootMotionWeight = 0f;
                }
            }
        }

        private bool CanApplyJump(int tick)
        {
            if (m_Character?.Motion == null || !m_Character.Motion.CanJump) return false;

            int cooldownTicks = Mathf.Max(
                1,
                Mathf.CeilToInt(
                    Mathf.Max(0f, m_Character.Motion.JumpCooldown) /
                    SimulationDeltaTime));
            if (LastJumpTick != int.MinValue && tick - LastJumpTick < cooldownTicks)
            {
                return false;
            }

            bool grounded = m_Driver?.IsGrounded == true;
            bool gameplayAllowsJump = m_Character.Jump == null ||
                                      m_Character.Jump.CanJump();
            if (grounded || gameplayAllowsJump) return true;

            int coyoteTicks = Mathf.Max(
                1,
                Mathf.CeilToInt(0.15f / SimulationDeltaTime));
            int sinceGrounded = tick - LastGroundedTick;
            return LastGroundedTick != int.MinValue && sinceGrounded >= 0 &&
                   sinceGrounded <= coyoteTicks;
        }

        private void UpdateGroundedStateAfterSimulation(
            int tick,
            float verticalVelocityBefore)
        {
            bool grounded = m_Driver?.ForceGroundedValue == true ||
                            m_Kcc.FixedData.IsGrounded;
            bool wasGrounded = WasGrounded;
            if (grounded) LastGroundedTick = tick;
            WasGrounded = grounded;

            if (!wasGrounded && grounded && !Runner.IsResimulation)
            {
                m_Character?.OnLand(verticalVelocityBefore);
            }
        }

        private void NotifyAcceptedOwnerPoseAfterSimulation()
        {
            if (!m_NotifyAcceptedOwnerPoseAfterSimulation ||
                Runner.IsResimulation || m_Character == null)
            {
                m_NotifyAcceptedOwnerPoseAfterSimulation = false;
                return;
            }

            Vector3 acceptedRoot = FootToRoot(m_Kcc.FixedData.TargetPosition);
            m_NotifyAcceptedOwnerPoseAfterSimulation = false;
            if (IsFinite(acceptedRoot))
            {
                NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(
                    m_Character,
                    acceptedRoot);
            }
        }

        private bool HasCentralizedMovementAuthority =>
            Object != null && Object.IsValid && Object.HasStateAuthority &&
            (Runner.GameMode != GameMode.Shared ||
             SharedAuthorityMode ==
             FusionKccSharedAuthorityMode.SharedMasterMovementAuthority);

        private void SynchronizeSimulationCapsule()
        {
            if (m_Kcc == null || m_Character?.Motion == null ||
                Object == null || !Object.IsValid)
            {
                return;
            }

            bool simulationAuthority = Object.HasStateAuthority ||
                                       Object.HasInputAuthority;
            float height = simulationAuthority
                ? Mathf.Max(0.1f, m_Character.Motion.Height)
                : ReplicatedCapsuleHeight > 0f
                    ? ReplicatedCapsuleHeight
                    : Mathf.Max(0.1f, m_Kcc.Settings.Height);
            float radius = simulationAuthority
                ? Mathf.Clamp(m_Character.Motion.Radius, 0.01f, height * 0.5f)
                : ReplicatedCapsuleRadius > 0f
                    ? Mathf.Clamp(ReplicatedCapsuleRadius, 0.01f, height * 0.5f)
                    : Mathf.Max(0.01f, m_Kcc.Settings.Radius);
            if (Object.HasStateAuthority)
            {
                ReplicatedCapsuleHeight = height;
                ReplicatedCapsuleRadius = radius;
                ReplicatedCollisionEnabled = m_CollisionEnabled;
            }
            else
            {
                m_CollisionEnabled = ReplicatedCollisionEnabled;
            }
            if (!Mathf.Approximately(m_Kcc.Settings.Height, height) ||
                !Mathf.Approximately(m_Kcc.Settings.Radius, radius) ||
                (m_CollisionEnabled
                    ? m_Kcc.Settings.Shape != EKCCShape.Capsule
                    : m_Kcc.Settings.Shape != EKCCShape.None))
            {
                EKCCShape shape = m_CollisionEnabled
                    ? EKCCShape.Capsule
                    : EKCCShape.None;
                m_Kcc.SetShape(shape, radius, height);
                m_Kcc.Settings.Extent = Mathf.Clamp(
                    radius * 0.1f,
                    0.01f,
                    radius * 0.25f);
            }
        }

        private bool TryValidateOwnerPose(
            FusionNativeCharacterInput input,
            int tick,
            out Vector3 ownerFootPosition)
        {
            ownerFootPosition = Vector3.zero;
            if (!input.HasOwnerPose || !IsFinite(input.OwnerPosition)) return false;

            bool centralizedAuthority = Object.HasStateAuthority &&
                (Runner.GameMode != GameMode.Shared ||
                 SharedAuthorityMode ==
                 FusionKccSharedAuthorityMode.SharedMasterMovementAuthority);
            if (centralizedAuthority &&
                (m_Backend == null ||
                 !m_Backend.IsServerMotionTickAuthorized(tick)))
            {
                LogDiagnostic("owner-pose-rejected-window", input, true, 0f);
                return false;
            }

            if (centralizedAuthority &&
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    m_Character,
                    input.OwnerPosition,
                    out _))
            {
                LogDiagnostic("owner-pose-rejected-gameplay", input, true, 0f);
                return false;
            }

            Vector3 currentRoot = FootToRoot(m_Kcc.FixedData.TargetPosition);
            float distance = Vector3.Distance(currentRoot, input.OwnerPosition);
            float reconciliationEnvelope = m_Profile != null
                ? Mathf.Max(0.1f, m_Profile.maxReconciliationDistance)
                : 3f;
            float speedMultiplier = m_Profile != null
                ? Mathf.Max(1f, m_Profile.maxSpeedMultiplier)
                : 1.2f;
            float kineticEnvelope =
                Mathf.Max(0f, m_Character?.Motion?.LinearSpeed ?? 0f) *
                speedMultiplier * SimulationDeltaTime + 0.1f;
            float maximumDistance = Mathf.Max(
                reconciliationEnvelope,
                kineticEnvelope);
            if (centralizedAuthority && distance > maximumDistance)
            {
                LogDiagnostic("owner-pose-rejected-distance", input, true, distance);
                return false;
            }

            ownerFootPosition = RootToFoot(input.OwnerPosition);
            if (centralizedAuthority && !Runner.IsResimulation)
            {
                // KCC can depenetrate or constrain this requested pose. Notify traversal only
                // after ManualFixedUpdate with the actual accepted root position.
                m_NotifyAcceptedOwnerPoseAfterSimulation = true;
            }
            return true;
        }

        private void RememberContinuousInput(FusionNativeCharacterInput input)
        {
            m_LastContinuousInput = new FusionNativeCharacterInput
            {
                Move = IsFinite(input.Move) ? input.Move : Vector2.zero,
                Yaw = IsFinite(input.Yaw) ? input.Yaw : 0f,
                SourceTick = input.SourceTick,
                Flags = input.HasContinuousOwnerPose
                    ? FusionNativeCharacterInput.FlagOwnerPose |
                      FusionNativeCharacterInput.FlagContinuousOwnerPose
                    : 0,
                OwnerPosition = input.HasContinuousOwnerPose &&
                                IsFinite(input.OwnerPosition)
                    ? input.OwnerPosition
                    : Vector3.zero,
                RootMotionDelta = Vector3.zero,
                RootMotionWeight = 0f,
                JumpForce = 0f
            };
            m_HasLastContinuousInput = true;
        }

        private bool TryHoldContinuousInput(
            int tick,
            out FusionNativeCharacterInput input)
        {
            input = default;
            if (!m_HasLastContinuousInput) return false;
            input = m_LastContinuousInput;
            input.SourceTick = tick;
            return true;
        }

        private bool CanCollectFusionInput(NetworkRunner runner)
        {
            return runner != null && runner == Runner && runner.IsRunning &&
                   runner.GameMode != GameMode.Shared && m_Driver != null &&
                   isActiveAndEnabled && m_AdapterInitialized && IsLocalLogicalOwner;
        }

        private bool CanAcceptSharedInput(PlayerRef source)
        {
            return isActiveAndEnabled && m_AdapterInitialized &&
                   Runner != null && Runner.IsRunning &&
                   Runner.GameMode == GameMode.Shared &&
                   SharedAuthorityMode ==
                   FusionKccSharedAuthorityMode.SharedMasterMovementAuthority &&
                   Object != null && Object.IsValid && Object.HasStateAuthority &&
                   m_Identity != null && m_Identity.IsSpawned &&
                   source == m_Identity.LogicalOwner;
        }

        private void HandleSharedAuthorityHandoff(bool force)
        {
            if (Runner == null || !Runner.IsRunning || Object == null ||
                !Object.IsValid || Runner.GameMode != GameMode.Shared ||
                SharedAuthorityMode !=
                FusionKccSharedAuthorityMode.OwnerMovementAuthority ||
                m_Identity == null || !m_Identity.IsSpawned ||
                !m_Identity.LogicalOwner.IsRealPlayer)
            {
                return;
            }

            float now = Time.unscaledTime;
            if (!force && now < m_NextAuthorityRequestTime) return;
            m_NextAuthorityRequestTime = now + 0.25f;

            if (Object.HasStateAuthority)
            {
                if (IsLocalLogicalOwner) return;
                Object.ReleaseStateAuthority();
                LogDiagnostic("shared-authority-release", default, false, 0f);
                return;
            }

            if (!IsLocalLogicalOwner || Object.StateAuthority.IsRealPlayer) return;
            Object.RequestStateAuthority();
            LogDiagnostic("shared-authority-request", default, false, 0f);
        }

        private void ConfigureKccAuthorityBehaviour()
        {
            if (m_Kcc == null) return;
            KCCSettings settings = m_Kcc.Settings;
            bool sharedMasterMovement = Runner != null &&
                Runner.GameMode == GameMode.Shared &&
                SharedAuthorityMode ==
                FusionKccSharedAuthorityMode.SharedMasterMovementAuthority;
            bool interpolateSharedMasterInputAuthority = sharedMasterMovement &&
                (Object == null || !Object.IsValid || !Object.HasStateAuthority);
            settings.InputAuthorityBehavior = interpolateSharedMasterInputAuthority
                ? EKCCAuthorityBehavior.PredictFixed_InterpolateRender
                : EKCCAuthorityBehavior.PredictFixed_PredictRender;
            settings.StateAuthorityBehavior = IsLocalLogicalOwner
                ? EKCCAuthorityBehavior.PredictFixed_PredictRender
                : EKCCAuthorityBehavior.PredictFixed_InterpolateRender;
            settings.ProxyInterpolationMode = EKCCInterpolationMode.Full;
            // A joined Shared-master owner is intentionally fully interpolated and pays the
            // documented round-trip movement latency. Predicting only look rotation would make
            // its presentation disagree with the same authoritative KCC snapshot.
            settings.ForcePredictedLookRotation =
                !interpolateSharedMasterInputAuthority;
            settings.AllowClientTeleports = false;
        }

        private void InstallSelfCollisionFilter()
        {
            if (m_Kcc == null || m_Root == null) return;
            if (m_InstalledResolveCollision != null &&
                m_Kcc.ResolveCollision == m_InstalledResolveCollision)
            {
                return;
            }

            // KCC's networked ignore list requires one NetworkObject and exactly one Collider
            // on every ignored GameObject. GC2 hitboxes/equipment commonly do not satisfy that
            // restriction, so use the public collision resolver and preserve customer logic.
            m_PreviousResolveCollision = m_Kcc.ResolveCollision;
            m_InstalledResolveCollision = ResolveKccCollision;
            m_Kcc.ResolveCollision = m_InstalledResolveCollision;
        }

        private void RestoreSelfCollisionFilter()
        {
            if (m_Kcc != null && m_InstalledResolveCollision != null &&
                m_Kcc.ResolveCollision == m_InstalledResolveCollision)
            {
                m_Kcc.ResolveCollision = m_PreviousResolveCollision;
            }
            m_InstalledResolveCollision = null;
            m_PreviousResolveCollision = null;
        }

        private bool ResolveKccCollision(
            global::Fusion.Addons.KCC.KCC kcc,
            Collider collider)
        {
            if (collider != null && m_Root != null &&
                collider.transform.IsChildOf(m_Root))
            {
                return false;
            }
            return m_PreviousResolveCollision?.Invoke(kcc, collider) ?? true;
        }

        private void ApplyRootFromKcc(KCCData data, bool render)
        {
            if (data == null || m_Root == null || m_Kcc == null) return;
            Vector3 footPosition = data.TargetPosition;
            Quaternion rotation = data.TransformRotation;
            if (!IsFinite(footPosition) || !IsFinite(rotation)) return;

            Vector3 rootPosition = FootToRoot(footPosition);
            m_Root.SetPositionAndRotation(rootPosition, rotation);
            KeepMotorAtUnitWorldScale();

            // Moving the parent root also moves the nested motor Transform. Restore it from KCC
            // data so the foot-space NetworkTRSP remains the single simulation/render source.
            m_Kcc.SynchronizeTransform(
                synchronizePosition: true,
                synchronizeRotation: true,
                allowAntiJitter: render,
                moveRigidbody: true);
        }

        private void ApplyReplicatedScale()
        {
            if (m_Root == null || Object == null || !Object.IsValid) return;
            Vector3 scale = m_Backend != null
                ? m_Backend.GetRequestedOrReplicatedRootScale(m_Root.localScale)
                : m_Root.localScale;
            if (!IsFinite(scale) || scale.sqrMagnitude <= 0.000001f) return;
            if ((m_Root.localScale - scale).sqrMagnitude > 0.000001f)
            {
                m_Root.localScale = scale;
            }
            KeepMotorAtUnitWorldScale();
        }

        private void KeepMotorAtUnitWorldScale()
        {
            Transform motor = m_Kcc != null ? m_Kcc.transform : transform;
            Transform parent = motor.parent;
            if (parent == null)
            {
                motor.localScale = Vector3.one;
                return;
            }

            Vector3 parentScale = parent.lossyScale;
            motor.localScale = new Vector3(
                SafeInverse(parentScale.x),
                SafeInverse(parentScale.y),
                SafeInverse(parentScale.z));
        }

        private Vector3 FootToRoot(Vector3 footPosition) =>
            footPosition + Vector3.up * (ActiveHeight * 0.5f);

        private Vector3 RootToFoot(Vector3 rootPosition) =>
            rootPosition - Vector3.up * (ActiveHeight * 0.5f);

        private bool EnsureRuntimeReady()
        {
            if (!isActiveAndEnabled) return false;
            if (!m_AdapterInitialized) return false;
            CacheComponents();
            ConfigureManualUpdate();
            return m_Kcc != null && m_Kcc.IsSpawned && m_Gc2Processor != null &&
                   m_Root != null && Object != null && Object.IsValid;
        }

        private void CacheComponents(
            FusionKccCharacterBackend backend = null,
            NetworkCharacter networkCharacter = null)
        {
            if (backend != null) m_Backend = backend;
            m_Backend ??= GetComponentInParent<FusionKccCharacterBackend>(true);
            m_NetworkCharacter = networkCharacter != null
                ? networkCharacter
                : m_Backend != null
                    ? m_Backend.Character
                    : GetComponentInParent<NetworkCharacter>(true);
            m_Identity = m_Backend != null
                ? m_Backend.Identity
                : GetComponentInParent<FusionNetworkIdentity>(true);
            m_Character = m_NetworkCharacter != null
                ? m_NetworkCharacter.Character
                : GetComponentInParent<Character>(true);
            m_Root = m_Character != null
                ? m_Character.transform
                : m_Backend != null
                    ? m_Backend.transform
                    : transform.parent;
            m_Kcc ??= GetComponent<global::Fusion.Addons.KCC.KCC>();
            m_Gc2Processor ??= ResolveProcessor<FusionGc2KccProcessor>();
            m_EnvironmentProcessor ??= ResolveProcessor<EnvironmentProcessor>();
            if (m_Driver != null)
            {
                m_Driver.AttachMotor(this);
                m_Gc2Processor?.Bind(m_Driver, m_EnvironmentProcessor);
            }
        }

        private TProcessor ResolveProcessor<TProcessor>()
            where TProcessor : Component, IKCCProcessor
        {
            // Serialized references are retained above. When one is missing, prefer the exact
            // processors registered with this KCC; processor GameObjects are intentionally
            // separate because Photon marks the KCCProcessor base as DisallowMultipleComponent.
            UnityEngine.Object[] configuredProcessors = m_Kcc?.Settings?.Processors;
            if (configuredProcessors != null)
            {
                for (int i = 0; i < configuredProcessors.Length; i++)
                {
                    UnityEngine.Object configured = configuredProcessors[i];
                    if (configured is TProcessor typed)
                    {
                        return typed;
                    }
                    if (configured != null &&
                        KCCUtility.ResolveProcessor(
                            configured,
                            out IKCCProcessor processor) &&
                        processor is TProcessor resolved)
                    {
                        return resolved;
                    }
                }
            }

            // Migration/fallback lookup is scoped to this motor hierarchy. Exclude a processor
            // owned by another nested motor so a temporarily mixed prefab cannot cross-wire two
            // characters based on component traversal order.
            TProcessor[] children = GetComponentsInChildren<TProcessor>(true);
            for (int i = 0; i < children.Length; i++)
            {
                TProcessor candidate = children[i];
                if (candidate != null &&
                    candidate.GetComponentInParent<FusionKccMotorBody>(true) == this)
                {
                    return candidate;
                }
            }

            return null;
        }

        private void ConfigureManualUpdate()
        {
            if (m_Kcc == null) return;
            if (!m_Kcc.HasManualUpdate)
            {
                m_Kcc.SetManualUpdate(true);
            }
        }

        private void ResetNetworkState()
        {
            ResetSharedReceiveState();
            m_NotifyAcceptedOwnerPoseAfterSimulation = false;
            m_LastSharedOwnerPumpTick = int.MinValue;
            m_LastRenderFrame = int.MinValue;
            m_NextAuthorityRequestTime = 0f;
            m_HasLastFixedRootPosition = false;
            m_HasLastRenderRootPosition = false;
        }

        private void ResetSharedReceiveState()
        {
            m_LastContinuousInput = default;
            m_HasLastContinuousInput = false;
            m_LatestSharedInput = default;
            m_HasSharedInput = false;
            m_LatestSharedTrustedTick = int.MinValue;
            m_LastSharedContinuousPayloadTick = int.MinValue;
            m_LastSharedTransientPayloadTick = int.MinValue;
            m_SharedTransients.Clear();
            m_SharedOverflowLatched = false;
        }

        private void LogDiagnostic(
            string phase,
            FusionNativeCharacterInput input,
            bool hasInput,
            float poseDelta)
        {
            if (!m_LogDiagnostics) return;
            float now = Time.unscaledTime;
            if (now < m_NextDiagnosticTime &&
                phase != "state-authority-changed" &&
                phase != "shared-authority-release" &&
                phase != "shared-authority-request")
            {
                return;
            }
            m_NextDiagnosticTime = now + Mathf.Max(0.1f, m_DiagnosticInterval);

            Vector3 fixedFoot = m_Kcc != null && m_Kcc.IsSpawned
                ? m_Kcc.FixedData.TargetPosition
                : Vector3.zero;
            Vector3 renderFoot = m_Kcc != null && m_Kcc.IsSpawned
                ? m_Kcc.RenderData.TargetPosition
                : Vector3.zero;
            float predictionError = m_Kcc != null && m_Kcc.IsSpawned
                ? m_Kcc.PredictionError.magnitude
                : 0f;
            bool ownerWindow = m_Backend?.IsOwnerMotionActive(CurrentTick) == true;
            bool serverWindow =
                m_Backend?.IsServerMotionTickAuthorized(CurrentTick) == true;
            Debug.Log(
                $"[FusionKCC] phase={phase} object='{name}' topology={Runner?.GameMode} " +
                $"tick={CurrentTick} inputTick={input.SourceTick} hasInput={hasInput} " +
                $"stateAuthority={Object?.StateAuthority} localOwner={IsLocalLogicalOwner} " +
                $"role={m_Role} policy={SharedAuthorityMode} " +
                $"predictionError={predictionError:F4} poseDelta={poseDelta:F4} " +
                $"fixedFoot={fixedFoot:F3} renderFoot={renderFoot:F3} " +
                $"root={(m_Root != null ? m_Root.position.ToString("F3") : "<none>")} " +
                $"rootFootOffset={(ActiveHeight * 0.5f):F3} " +
                $"rootMotion={input.RootMotionDelta:F3}/{input.RootMotionWeight:F2} " +
                $"ownerPose={input.HasOwnerPose} continuous={input.HasContinuousOwnerPose} " +
                $"ownerWindow={ownerWindow} serverWindow={serverWindow}",
                this);
        }

        private struct SharedTransient
        {
            public FusionNativeCharacterInput Input;
            public int TrustedTick;
        }

        private static float SafeInverse(float value) =>
            Mathf.Abs(value) > 0.000001f ? 1f / value : 1f;

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector2 value) =>
            IsFinite(value.x) && IsFinite(value.y);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) &&
            IsFinite(value.w);
    }
}
#endif
