using System;
using GameCreator.Runtime.Characters;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction
{
    public abstract class PurrDictionNetworkCharacterControllerBase<TInput, TState> :
        PredictedIdentity<TInput, TState>,
        INetworkCharacterPredictionBackend,
        IPurrDictionNativeMovementBackend
        where TInput : struct, IPredictedData
        where TState : struct, IPredictedData<TState>
    {
        private const uint PURRNET_PREDICTED_ID_NAMESPACE = 0x80000000u;
        private const float DEFAULT_MAX_SPEED_MULTIPLIER = 1.2f;
        private const float DEFAULT_MAX_OWNER_POSE_DISTANCE = 3f;
        private const float MIN_USABLE_SCALE = 0.0001f;
        private const float MAX_USABLE_SCALE = 1000f;
        private const int SERVER_MOTION_AUTHORIZATION_CAPACITY = 8;
        private const int TRUSTED_POSE_HISTORY_CAPACITY = 128;
        private const string SECURITY_MODULE_CORE = "Core";

        [Header("Server Authority")]
        [SerializeField] private bool m_EnableServerSecurityValidation = true;

        [Header("Presentation")]
        [Tooltip(
            "Optional direct visual-only child of the Character root. PurrDiction keeps the " +
            "gameplay root and collider on the current prediction tick and applies interpolated " +
            "view state only to this hierarchy. The GC2 Mannequin is resolved automatically when safe.")]
        [SerializeField] private Transform m_PresentationVisualRoot;

        private PredictionManager m_RegisteredPredictionManager;
        private bool m_PredictionRegistered;
        private bool m_QueuedSpawnRegistration;
        private bool m_MissingPredictionManagerWarned;
        private bool m_RuntimeIsServer;
        private bool m_RuntimeIsOwner;
        private NetworkCharacter.NetworkRole m_RuntimeRole;
        private bool m_MissingSecurityContextViolationRecorded;
        private float m_MaxSpeedMultiplier = DEFAULT_MAX_SPEED_MULTIPLIER;
        private float m_MaxOwnerPoseDistance = DEFAULT_MAX_OWNER_POSE_DISTANCE;
        private uint m_SecurityActorNetworkId;
        private uint m_SecurityOwnerClientId = NetworkTransportBridge.InvalidClientId;

        private readonly PurrDictionPendingExternalPose m_PendingExternalPose = new();
        private bool m_HasOwnerMotionWindow;
        private ulong m_OwnerMotionUntilTick;
        private readonly ServerMotionAuthorization[] m_ServerMotionAuthorizations =
            new ServerMotionAuthorization[SERVER_MOTION_AUTHORIZATION_CAPACITY];
        private int m_ServerMotionAuthorizationCount;
        private readonly TrustedPoseHistoryEntry[] m_TrustedPoseHistory =
            new TrustedPoseHistoryEntry[TRUSTED_POSE_HISTORY_CAPACITY];
        private int m_TrustedPoseHistoryWriteIndex;
        private int m_TrustedPoseHistoryCount;

        private Vector3 m_LastValidRootPosition;
        private Quaternion m_LastValidRootRotation = Quaternion.identity;
        private Vector3 m_LastValidRootScale = Vector3.one;
        private bool m_HasLastValidRootPose;
        private int m_LastInvalidPoseDiagnosticFrame = int.MinValue;

        private Transform m_PresentationRoot;
        private Transform m_ResolvedPresentationVisualRoot;
        private int m_PresentationOriginalSiblingIndex = -1;
        private Vector3 m_PresentationWorldPosition;
        private Quaternion m_PresentationWorldRotation = Quaternion.identity;
        private Vector3 m_PresentationLocalScale = Vector3.one;
        private bool m_HasPresentationPose;
        private bool m_PresentationBeforeRenderSubscribed;
        private bool m_PresentationRootWarningIssued;
        private Character m_RagdollSubscribedCharacter;

        private struct ServerMotionAuthorization
        {
            public ulong FromTick;
            public ulong UntilTick;
            public uint OperationId;
        }

        private struct TrustedPoseHistoryEntry
        {
            public ulong Tick;
            public PurrDictionExternalPoseCommand Command;
        }

        protected NetworkCharacter NetworkCharacterComponent { get; private set; }
        protected Character GameCreatorCharacter { get; private set; }
        protected NetworkIdentity PurrNetIdentity { get; private set; }

        protected bool ServerSecurityValidationEnabled => m_EnableServerSecurityValidation;
        protected bool IsAuthoritativeServer => m_RuntimeIsServer || isServer;
        protected bool ShouldValidateServerSecurity =>
            m_EnableServerSecurityValidation && IsAuthoritativeServer;
        protected float MaxSpeedMultiplier => Mathf.Max(1f, m_MaxSpeedMultiplier);
        protected uint SecurityActorNetworkId => m_SecurityActorNetworkId;
        protected uint SecurityOwnerClientId => m_SecurityOwnerClientId;
        protected ulong CurrentPredictionTick => predictionManager != null
            ? predictionManager.localTickInContext
            : 0;
        protected bool IsPredictionReplay => predictionManager != null && predictionManager.isReplaying;
        protected float PredictionDelta => predictionManager != null && predictionManager.tickDelta > 0f
            ? predictionManager.tickDelta
            : 1f / 60f;
        protected bool IsServerOwnerMotionAuthorized =>
            IsServerMotionTickAuthorized(CurrentPredictionTick);

        bool IPurrDictionNativeMovementBackend.CanAuthorLocalIntent => CanAuthorLocalIntent;
        bool IPurrDictionNativeMovementBackend.CanAuthorTrustedServerPose =>
            IsAuthoritativeServer && !CanAuthorLocalIntent;
        bool IPurrDictionNativeMovementBackend.IsOwnerMotionWindowActive => IsOwnerMotionWindowActive;

        public bool CanAuthorLocalIntent
        {
            get
            {
                if (predictionManager != null) return isController;
                if (m_RuntimeRole == NetworkCharacter.NetworkRole.RemoteClient) return false;
                return m_RuntimeIsOwner || m_RuntimeRole == NetworkCharacter.NetworkRole.Server;
            }
        }

        public bool IsOwnerMotionWindowActive =>
            m_HasOwnerMotionWindow && CurrentPredictionTick <= m_OwnerMotionUntilTick;

        public NetworkPredictionBackend Backend => NetworkPredictionBackend.PurrDiction;

        public abstract IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role);

        public void Initialize(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        {
            EnsureBaseReferences(networkCharacter);
            m_RuntimeIsServer = isServer;
            m_RuntimeIsOwner = isOwner;
            m_RuntimeRole = role;
            if (ShouldValidateServerSecurity)
            {
                SecurityIntegration.EnsureSecurityManagerInitialized(true);
            }

            RefreshSecurityContext();
            OnBackendInitialized(networkCharacter, role, isServer, isOwner, isHost);
            TryRegisterWithPredictionManager();
            RefreshSecurityContext();
        }

        public virtual void ApplySessionProfile(NetworkSessionProfile profile)
        {
            if (profile != null)
            {
                m_MaxSpeedMultiplier = Mathf.Max(1f, profile.maxSpeedMultiplier);
                m_MaxOwnerPoseDistance = Mathf.Max(
                    0.1f,
                    profile.maxReconciliationDistance);
            }

            // PurrDiction follows PurrNet's tick module. GC2 session profiles still drive
            // the non-movement bridge layer through NetworkCharacter and transport bridges.
        }

        public void ResetBackend(NetworkCharacter networkCharacter)
        {
            UnregisterFromPredictionManager();
            RestorePresentationHierarchy();
            UnsubscribeRagdollSafety();
            m_PendingExternalPose.Clear();
            OnBackendReset(networkCharacter);

            NetworkCharacterComponent = null;
            GameCreatorCharacter = null;
            PurrNetIdentity = null;
            m_QueuedSpawnRegistration = false;
            m_MissingPredictionManagerWarned = false;
            m_RuntimeIsServer = false;
            m_RuntimeIsOwner = false;
            m_RuntimeRole = NetworkCharacter.NetworkRole.None;
            m_MissingSecurityContextViolationRecorded = false;
            m_SecurityActorNetworkId = 0;
            m_SecurityOwnerClientId = NetworkTransportBridge.InvalidClientId;
            m_HasOwnerMotionWindow = false;
            m_OwnerMotionUntilTick = 0;
            m_ServerMotionAuthorizationCount = 0;
            m_TrustedPoseHistoryWriteIndex = 0;
            m_TrustedPoseHistoryCount = 0;
        }

        protected virtual void Awake()
        {
            EnsureBaseReferences(GetComponent<NetworkCharacter>());
            RememberCurrentRootPose();
        }

        protected override void OnDestroy()
        {
            RestorePresentationHierarchy();
            UnsubscribeRagdollSafety();
            m_PredictionRegistered = false;
            m_RegisteredPredictionManager = null;
            base.OnDestroy();
        }

        public override void OnViewOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner)
        {
            base.OnViewOwnerChanged(oldOwner, newOwner);
            RefreshSecurityContext();
        }

        protected virtual void OnBackendInitialized(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        { }

        protected virtual void OnBackendReset(NetworkCharacter networkCharacter)
        { }

        protected void EnsureBaseReferences(NetworkCharacter networkCharacter = null)
        {
            if (networkCharacter != null) NetworkCharacterComponent = networkCharacter;
            if (NetworkCharacterComponent == null) NetworkCharacterComponent = GetComponent<NetworkCharacter>();
            if (GameCreatorCharacter == null) GameCreatorCharacter = GetComponent<Character>();
            if (PurrNetIdentity == null) PurrNetIdentity = GetComponentInParent<NetworkIdentity>();
            RefreshRagdollSafetySubscription();
        }

        protected void RefreshSecurityContext()
        {
            EnsureBaseReferences();

            m_SecurityActorNetworkId = NetworkCharacterComponent != null
                ? NetworkCharacterComponent.NetworkId
                : 0;
            m_SecurityOwnerClientId = ResolveOwnerClientId();

            if (m_SecurityActorNetworkId == 0 ||
                !NetworkTransportBridge.IsValidClientId(m_SecurityOwnerClientId))
            {
                return;
            }

            m_MissingSecurityContextViolationRecorded = false;
            SecurityIntegration.RegisterActorOwnership(m_SecurityActorNetworkId, m_SecurityOwnerClientId);
        }

        protected bool TryResolveSecurityContext(out uint ownerClientId, out uint actorNetworkId)
        {
            RefreshSecurityContext();

            ownerClientId = m_SecurityOwnerClientId;
            actorNetworkId = m_SecurityActorNetworkId;
            return actorNetworkId != 0 && NetworkTransportBridge.IsValidClientId(ownerClientId);
        }

        protected bool ValidateServerCoreRequest(ushort sequence, string requestType)
        {
            if (!ShouldValidateServerSecurity) return true;

            if (sequence == 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"{requestType}: missing command sequence");
                return false;
            }

            if (!TryResolveSecurityContext(out uint ownerClientId, out uint actorNetworkId))
            {
                RecordMissingSecurityContextOnce(requestType);
                return false;
            }

            uint correlationId = NetworkCorrelation.Compose(actorNetworkId, (uint)sequence);
            return SecurityIntegration.ValidateCoreRequest(
                ownerClientId,
                actorNetworkId,
                correlationId,
                requestType);
        }

        protected bool ValidateServerCorePosition(
            Vector3 position,
            Vector3 velocity,
            float maxSpeed,
            string requestType)
        {
            if (!ShouldValidateServerSecurity) return true;

            if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(maxSpeed) || maxSpeed <= 0f)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"{requestType}: invalid movement state position={position}, velocity={velocity}, maxSpeed={maxSpeed}");
                return false;
            }

            if (!TryResolveSecurityContext(out uint ownerClientId, out uint actorNetworkId))
            {
                RecordMissingSecurityContextOnce(requestType);
                return false;
            }

            return SecurityIntegration.ValidateCorePositionUpdate(
                ownerClientId,
                actorNetworkId,
                position,
                velocity,
                maxSpeed);
        }

        protected void RecordCoreSecurityViolation(SecurityViolationType type, string details)
        {
            if (!ShouldValidateServerSecurity) return;

            RefreshSecurityContext();
            uint ownerClientId = NetworkTransportBridge.IsValidClientId(m_SecurityOwnerClientId)
                ? m_SecurityOwnerClientId
                : NetworkTransportBridge.InvalidClientId;

            SecurityIntegration.RecordViolation(
                ownerClientId,
                m_SecurityActorNetworkId,
                type,
                SECURITY_MODULE_CORE,
                details);
        }

        protected static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        protected static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) &&
                   IsFinite(value.y) &&
                   IsFinite(value.z) &&
                   IsFinite(value.w);
        }

        protected static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        protected static bool IsUsableRotation(Quaternion value)
        {
            if (!IsFinite(value)) return false;
            float magnitude = value.x * value.x + value.y * value.y +
                              value.z * value.z + value.w * value.w;
            return magnitude > 0.000001f;
        }

        protected static bool IsUsableScale(Vector3 value)
        {
            if (!IsFinite(value)) return false;
            return Mathf.Abs(value.x) >= MIN_USABLE_SCALE &&
                   Mathf.Abs(value.y) >= MIN_USABLE_SCALE &&
                   Mathf.Abs(value.z) >= MIN_USABLE_SCALE &&
                   Mathf.Abs(value.x) <= MAX_USABLE_SCALE &&
                   Mathf.Abs(value.y) <= MAX_USABLE_SCALE &&
                   Mathf.Abs(value.z) <= MAX_USABLE_SCALE;
        }

        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            if (!IsFinite(durationSeconds) || durationSeconds <= 0f) return;
            ulong untilTick = AddTicksSaturating(
                CurrentPredictionTick,
                SecondsToTicks(durationSeconds));
            if (!m_HasOwnerMotionWindow || untilTick > m_OwnerMotionUntilTick)
            {
                m_OwnerMotionUntilTick = untilTick;
            }
            m_HasOwnerMotionWindow = true;
        }

        public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0)
        {
            if (!IsFinite(durationSeconds) || durationSeconds <= 0f) return;

            ulong currentTick = CurrentPredictionTick;
            ulong untilTick = AddTicksSaturating(currentTick, SecondsToTicks(durationSeconds));
            int lastIndex = m_ServerMotionAuthorizationCount - 1;
            if (lastIndex >= 0)
            {
                ServerMotionAuthorization latest = m_ServerMotionAuthorizations[lastIndex];
                bool sameOperation = operationId == 0 || latest.OperationId == 0 ||
                                     latest.OperationId == operationId;
                if (sameOperation && currentTick <= AddTicksSaturating(latest.UntilTick, 1))
                {
                    latest.UntilTick = Math.Max(latest.UntilTick, untilTick);
                    if (operationId != 0) latest.OperationId = operationId;
                    m_ServerMotionAuthorizations[lastIndex] = latest;
                    return;
                }
            }

            if (m_ServerMotionAuthorizationCount == SERVER_MOTION_AUTHORIZATION_CAPACITY)
            {
                Array.Copy(
                    m_ServerMotionAuthorizations,
                    1,
                    m_ServerMotionAuthorizations,
                    0,
                    SERVER_MOTION_AUTHORIZATION_CAPACITY - 1);
                m_ServerMotionAuthorizationCount--;
            }

            m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount++] =
                new ServerMotionAuthorization
                {
                    FromTick = currentTick,
                    UntilTick = untilTick,
                    OperationId = operationId
                };
        }

        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            if (m_ServerMotionAuthorizationCount <= 0) return;

            ulong currentTick = CurrentPredictionTick;

            if (graceSeconds <= 0f)
            {
                // Close every overlapping operation because this interface has no operation
                // identifier on close. Preserve authorization for historical ticks so a later
                // rollback still evaluates the same security policy as the original tick.
                for (int i = m_ServerMotionAuthorizationCount - 1; i >= 0; i--)
                {
                    ServerMotionAuthorization authorization =
                        m_ServerMotionAuthorizations[i];
                    if (currentTick < authorization.FromTick ||
                        currentTick > authorization.UntilTick)
                    {
                        continue;
                    }

                    if (currentTick > 0 && authorization.FromTick < currentTick)
                    {
                        authorization.UntilTick = currentTick - 1;
                        m_ServerMotionAuthorizations[i] = authorization;
                        continue;
                    }

                    int moveCount = m_ServerMotionAuthorizationCount - i - 1;
                    if (moveCount > 0)
                    {
                        Array.Copy(
                            m_ServerMotionAuthorizations,
                            i + 1,
                            m_ServerMotionAuthorizations,
                            i,
                            moveCount);
                    }

                    m_ServerMotionAuthorizationCount--;
                    m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount] = default;
                }
                return;
            }

            ulong closeTick = AddTicksSaturating(
                currentTick,
                SecondsToTicks(graceSeconds));
            for (int i = 0; i < m_ServerMotionAuthorizationCount; i++)
            {
                ServerMotionAuthorization authorization =
                    m_ServerMotionAuthorizations[i];
                if (currentTick < authorization.FromTick ||
                    currentTick > authorization.UntilTick)
                {
                    continue;
                }

                authorization.UntilTick = Math.Min(authorization.UntilTick, closeTick);
                m_ServerMotionAuthorizations[i] = authorization;
            }
        }

        public void QueueExternalPosition(Vector3 value, bool absolute, bool teleport)
        {
            if (!CanAuthorLocalIntent && !IsAuthoritativeServer) return;
            if (!IsFinite(value)) return;
            m_PendingExternalPose.QueuePosition(value, absolute, teleport);
        }

        public void QueueExternalRotation(Quaternion value, bool absolute)
        {
            if (!CanAuthorLocalIntent && !IsAuthoritativeServer) return;
            if (!IsUsableRotation(value)) return;
            m_PendingExternalPose.QueueRotation(value.normalized, absolute);
        }

        public void QueueExternalScale(Vector3 value, bool absolute)
        {
            if (!CanAuthorLocalIntent && !IsAuthoritativeServer) return;
            if (!IsFinite(value)) return;
            m_PendingExternalPose.QueueScale(value, absolute);
        }

        protected bool CapturePendingExternalPose(ref PurrDictionExternalPoseCommand command)
        {
            if (!m_PendingExternalPose.TryConsume(CurrentPredictionTick, out var pending))
            {
                return false;
            }

            command = pending;
            return true;
        }

        protected bool TryConsumeTrustedServerPose(out PurrDictionExternalPoseCommand command)
        {
            if (!IsAuthoritativeServer || isController)
            {
                command = default;
                return false;
            }

            ulong tick = CurrentPredictionTick;
            if (IsPredictionReplay)
            {
                return TryGetTrustedPoseFromHistory(tick, out command);
            }

            if (!m_PendingExternalPose.TryConsume(tick, out command))
            {
                // PurrDiction's initial-observer path rolls back the latest state and performs
                // one restore simulation without setting isReplaying. Re-offer a command from
                // that state/input boundary; the sequence stored in state makes this idempotent
                // during ordinary forward simulation.
                if (TryGetTrustedPoseFromHistory(tick, out command)) return true;
                return tick > 0 && TryGetTrustedPoseFromHistory(tick - 1, out command);
            }

            m_TrustedPoseHistory[m_TrustedPoseHistoryWriteIndex] =
                new TrustedPoseHistoryEntry
                {
                    Tick = tick,
                    Command = command
                };
            m_TrustedPoseHistoryWriteIndex =
                (m_TrustedPoseHistoryWriteIndex + 1) % TRUSTED_POSE_HISTORY_CAPACITY;
            if (m_TrustedPoseHistoryCount < TRUSTED_POSE_HISTORY_CAPACITY)
            {
                m_TrustedPoseHistoryCount++;
            }
            return true;
        }

        protected float CaptureAuthoritativeRootMotionAllowance()
        {
            if (!ShouldValidateServerSecurity) return 1f;
            if (IsServerOwnerMotionAuthorized) return 1f;
            return GameCreatorCharacter != null
                ? Mathf.Clamp01(GameCreatorCharacter.RootMotionPosition)
                : 0f;
        }

        protected bool SanitizeExternalPose(
            ref PurrDictionExternalPoseCommand command,
            string requestType)
        {
            if (!command.HasCommand)
            {
                command = default;
                return true;
            }

            const ushort validFlags =
                PurrDictionExternalPoseCommand.FLAG_POSITION |
                PurrDictionExternalPoseCommand.FLAG_POSITION_ABSOLUTE |
                PurrDictionExternalPoseCommand.FLAG_ROTATION |
                PurrDictionExternalPoseCommand.FLAG_ROTATION_ABSOLUTE |
                PurrDictionExternalPoseCommand.FLAG_SCALE |
                PurrDictionExternalPoseCommand.FLAG_SCALE_ABSOLUTE |
                PurrDictionExternalPoseCommand.FLAG_TELEPORT;

            bool valid = command.sequence != 0 &&
                         (command.flags & unchecked((ushort)~validFlags)) == 0 &&
                         (!command.HasPosition || IsFinite(command.position)) &&
                         (!command.HasRotation || IsUsableRotation(command.rotation)) &&
                         (!command.HasScale || IsFinite(command.scale));
            if (!valid)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"{requestType}: malformed external pose sequence={command.sequence} flags={command.flags}");
                command = default;
                return false;
            }

            if (!command.HasPosition)
            {
                command.flags &= unchecked((ushort)~(
                    PurrDictionExternalPoseCommand.FLAG_POSITION_ABSOLUTE |
                    PurrDictionExternalPoseCommand.FLAG_TELEPORT));
            }
            if (!command.HasRotation)
            {
                command.flags &= unchecked((ushort)~PurrDictionExternalPoseCommand.FLAG_ROTATION_ABSOLUTE);
            }
            else
            {
                command.rotation = command.rotation.normalized;
            }
            if (!command.HasScale)
            {
                command.flags &= unchecked((ushort)~PurrDictionExternalPoseCommand.FLAG_SCALE_ABSOLUTE);
            }

            return true;
        }

        protected bool TryResolveExternalPose(
            PurrDictionExternalPoseCommand command,
            Vector3 currentPosition,
            Quaternion currentRotation,
            Vector3 currentScale,
            ref ushort lastSequence,
            float delta,
            bool trustedServerCommand,
            out PurrDictionResolvedExternalPose resolved)
        {
            resolved = default;
            if (!command.HasCommand || !IsSequenceNewer(command.sequence, lastSequence)) return false;

            // A rejected command is still acknowledged in authoritative state so an extrapolated
            // or duplicated packet cannot repeatedly exercise validation.
            lastSequence = command.sequence;
            if (!SanitizeExternalPose(ref command, "PurrDictionExternalPose")) return false;

            bool remoteOwnerCommand = IsAuthoritativeServer && !isController && !trustedServerCommand;
            if (remoteOwnerCommand && !IsServerMotionTickAuthorized(CurrentPredictionTick))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.UnauthorizedAction,
                    $"PurrDictionExternalPose: owner pose outside an approved server window " +
                    $"sequence={command.sequence} tick={CurrentPredictionTick}");
                return false;
            }

            if (remoteOwnerCommand && command.HasScale)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.UnauthorizedAction,
                    $"PurrDictionExternalPose: client-authored scale rejected " +
                    $"sequence={command.sequence}");
                return false;
            }

            resolved.hasPosition = command.HasPosition;
            resolved.hasRotation = command.HasRotation;
            resolved.hasScale = command.HasScale;
            resolved.teleport = command.IsTeleport;
            resolved.position = command.PositionIsAbsolute
                ? command.position
                : currentPosition + command.position;
            resolved.rotation = command.RotationIsAbsolute
                ? command.rotation
                : currentRotation * command.rotation;
            resolved.scale = command.ScaleIsAbsolute
                ? command.scale
                : currentScale + command.scale;

            if ((resolved.hasPosition && !IsFinite(resolved.position)) ||
                (resolved.hasRotation && !IsUsableRotation(resolved.rotation)) ||
                (resolved.hasScale && !IsUsableScale(resolved.scale)))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionExternalPose: resolved invalid pose sequence={command.sequence}");
                resolved = default;
                return false;
            }

            if (resolved.hasPosition && remoteOwnerCommand)
            {
                if (NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                        GameCreatorCharacter,
                        resolved.position,
                        out string rejection))
                {
                    RecordCoreSecurityViolation(
                        SecurityViolationType.InvalidTarget,
                        $"PurrDictionExternalPose: position rejected: {rejection}");
                    resolved = default;
                    return false;
                }

                if (resolved.teleport)
                {
                    RecordCoreSecurityViolation(
                        SecurityViolationType.UnauthorizedAction,
                        "PurrDictionExternalPose: client-authored teleport rejected");
                    resolved = default;
                    return false;
                }

                float maxDistance = Mathf.Min(
                    m_MaxOwnerPoseDistance,
                    ResolveMaxAllowedHorizontalSpeed() * Mathf.Max(delta, 0.001f) + 0.1f);
                resolved.position = Vector3.MoveTowards(
                    currentPosition,
                    resolved.position,
                    Mathf.Max(0.1f, maxDistance));
            }

            resolved.rotation = resolved.rotation.normalized;
            return true;
        }

        protected bool TryResolveFiniteRootPose(
            Vector3 position,
            Quaternion rotation,
            Vector3 scale,
            out Vector3 safePosition,
            out Quaternion safeRotation,
            out Vector3 safeScale)
        {
            if (IsFinite(position) && IsUsableRotation(rotation) && IsUsableScale(scale))
            {
                safePosition = position;
                safeRotation = rotation.normalized;
                safeScale = scale;
                RememberRootPose(safePosition, safeRotation, safeScale);
                return true;
            }

            if (!m_HasLastValidRootPose) RememberCurrentRootPose();
            safePosition = m_HasLastValidRootPose ? m_LastValidRootPosition : Vector3.zero;
            safeRotation = m_HasLastValidRootPose ? m_LastValidRootRotation : Quaternion.identity;
            safeScale = m_HasLastValidRootPose ? m_LastValidRootScale : Vector3.one;

            if (m_LastInvalidPoseDiagnosticFrame != Time.frameCount)
            {
                m_LastInvalidPoseDiagnosticFrame = Time.frameCount;
                Debug.LogError(
                    $"[GC2 PurrDiction] Rejected a non-finite or unusable root pose on '{name}'. " +
                    $"position={position}, rotation={rotation}, scale={scale}. Restoring the last valid tick pose.",
                    this);
            }
            return false;
        }

        protected void RememberRootPose(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (!IsFinite(position) || !IsUsableRotation(rotation) || !IsUsableScale(scale)) return;
            m_LastValidRootPosition = position;
            m_LastValidRootRotation = rotation.normalized;
            m_LastValidRootScale = scale;
            m_HasLastValidRootPose = true;
        }

        protected void ApplyPresentationView(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (!IsFinite(position) || !IsUsableRotation(rotation) || !IsUsableScale(scale)) return;
            if (GameCreatorCharacter?.Ragdoll?.IsRagdoll == true)
            {
                RestorePresentationHierarchy();
                return;
            }
            if (!TryEnsurePresentationRoot())
            {
                if (!m_PresentationRootWarningIssued)
                {
                    m_PresentationRootWarningIssued = true;
                    Debug.LogWarning(
                        $"[GC2 PurrDiction] '{name}' has no safe direct visual-only child. " +
                        "The Character root remains tick-accurate, but this character cannot use " +
                        "render interpolation until a safe GC2 Mannequin is assigned.",
                        this);
                }
                return;
            }

            m_PresentationWorldPosition = position;
            m_PresentationWorldRotation = rotation.normalized;
            m_PresentationLocalScale = DivideScale(scale, transform.localScale);
            m_HasPresentationPose = true;
            ReapplyPresentationPose();
        }

        public static bool IsSafePresentationVisualRoot(Transform characterRoot, Transform candidate)
        {
            if (characterRoot == null || candidate == null || candidate == characterRoot) return false;
            if (candidate.parent != characterRoot) return false;
            return IsSafePresentationHierarchy(candidate);
        }

        private static bool IsSafePresentationHierarchy(Transform candidate)
        {
            if (candidate == null) return false;
            if (candidate.GetComponentInChildren<CharacterController>(true) != null) return false;
            if (candidate.GetComponentInChildren<Rigidbody>(true) != null) return false;
            if (candidate.GetComponentInChildren<Collider>(true) != null) return false;
            if (candidate.GetComponentInChildren<NetworkBehaviour>(true) != null) return false;
            if (candidate.GetComponentInChildren<PredictedIdentity>(true) != null) return false;

            return candidate.GetComponentInChildren<Renderer>(true) != null ||
                   candidate.GetComponentInChildren<Animator>(true) != null;
        }

        protected float ResolveMaxAllowedHorizontalSpeed()
        {
            float speed = GameCreatorCharacter?.Motion != null
                ? GameCreatorCharacter.Motion.LinearSpeed
                : 0f;

            return Mathf.Max(1f, speed * MaxSpeedMultiplier);
        }

        private bool IsServerMotionTickAuthorized(ulong tick)
        {
            for (int i = m_ServerMotionAuthorizationCount - 1; i >= 0; i--)
            {
                ServerMotionAuthorization authorization = m_ServerMotionAuthorizations[i];
                if (tick >= authorization.FromTick && tick <= authorization.UntilTick)
                {
                    return true;
                }

            }

            return false;
        }

        private bool TryGetTrustedPoseFromHistory(
            ulong tick,
            out PurrDictionExternalPoseCommand command)
        {
            for (int i = 0; i < m_TrustedPoseHistoryCount; i++)
            {
                int index = (m_TrustedPoseHistoryWriteIndex - 1 - i +
                             TRUSTED_POSE_HISTORY_CAPACITY) %
                            TRUSTED_POSE_HISTORY_CAPACITY;
                TrustedPoseHistoryEntry entry = m_TrustedPoseHistory[index];
                if (entry.Tick != tick) continue;
                command = entry.Command;
                return true;
            }

            command = default;
            return false;
        }

        private ulong SecondsToTicks(float seconds)
        {
            int tickRate = predictionManager != null && predictionManager.tickRate > 0
                ? predictionManager.tickRate
                : 60;
            return (ulong)Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(0f, seconds) * tickRate));
        }

        private static ulong AddTicksSaturating(ulong value, ulong ticks)
        {
            return ulong.MaxValue - value < ticks ? ulong.MaxValue : value + ticks;
        }

        private static bool IsSequenceNewer(ushort sequence, ushort previous)
        {
            return sequence != previous && (short)(sequence - previous) > 0;
        }

        private void RememberCurrentRootPose()
        {
            if (transform == null) return;
            RememberRootPose(transform.position, transform.rotation, transform.localScale);
        }

        private bool TryEnsurePresentationRoot()
        {
            if (GameCreatorCharacter?.Ragdoll?.IsRagdoll == true)
            {
                RestorePresentationHierarchy();
                return false;
            }

            if (m_PresentationRoot != null)
            {
                bool safe = m_ResolvedPresentationVisualRoot != null &&
                            m_ResolvedPresentationVisualRoot.parent == m_PresentationRoot &&
                            IsSafePresentationHierarchy(m_PresentationRoot);
                if (safe) return true;

                // Ragdoll and runtime equipment systems can add physics components after the
                // wrapper was initially validated. Never keep an interpolated parent above a
                // hierarchy that has become gameplay/physics-bearing.
                RestorePresentationHierarchy();
                return false;
            }

            Transform visualRoot = m_PresentationVisualRoot;
            if (visualRoot == null)
            {
                Transform mannequin = GameCreatorCharacter?.Animim?.Mannequin;
                if (mannequin != null && mannequin.parent == transform) visualRoot = mannequin;
            }

            if (!IsSafePresentationVisualRoot(transform, visualRoot)) return false;

            m_PresentationOriginalSiblingIndex = visualRoot.GetSiblingIndex();
            var presentationObject = new GameObject("__PurrDictionPresentation")
            {
                hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave
            };
            m_PresentationRoot = presentationObject.transform;
            m_PresentationRoot.SetParent(transform, false);
            m_PresentationRoot.SetSiblingIndex(m_PresentationOriginalSiblingIndex);
            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;

            m_ResolvedPresentationVisualRoot = visualRoot;
            m_ResolvedPresentationVisualRoot.SetParent(m_PresentationRoot, false);
            if (!m_PresentationBeforeRenderSubscribed)
            {
                Application.onBeforeRender += ReapplyPresentationPose;
                m_PresentationBeforeRenderSubscribed = true;
            }
            return true;
        }

        private void ReapplyPresentationPose()
        {
            if (!m_HasPresentationPose || m_PresentationRoot == null) return;
            m_PresentationRoot.SetPositionAndRotation(
                m_PresentationWorldPosition,
                m_PresentationWorldRotation);
            m_PresentationRoot.localScale = m_PresentationLocalScale;
        }

        private void RestorePresentationHierarchy()
        {
            if (m_PresentationBeforeRenderSubscribed)
            {
                Application.onBeforeRender -= ReapplyPresentationPose;
                m_PresentationBeforeRenderSubscribed = false;
            }

            m_HasPresentationPose = false;
            if (m_PresentationRoot == null) return;

            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;
            if (m_ResolvedPresentationVisualRoot != null && transform != null)
            {
                m_ResolvedPresentationVisualRoot.SetParent(transform, false);
                if (m_PresentationOriginalSiblingIndex >= 0)
                {
                    m_ResolvedPresentationVisualRoot.SetSiblingIndex(
                        Mathf.Min(m_PresentationOriginalSiblingIndex, transform.childCount - 1));
                }
            }

            GameObject presentationObject = m_PresentationRoot.gameObject;
            m_PresentationRoot = null;
            m_ResolvedPresentationVisualRoot = null;
            m_PresentationOriginalSiblingIndex = -1;
            if (Application.isPlaying) Destroy(presentationObject);
            else DestroyImmediate(presentationObject);
        }

        private static Vector3 DivideScale(Vector3 desired, Vector3 current)
        {
            return new Vector3(
                Mathf.Abs(current.x) > MIN_USABLE_SCALE ? desired.x / current.x : 1f,
                Mathf.Abs(current.y) > MIN_USABLE_SCALE ? desired.y / current.y : 1f,
                Mathf.Abs(current.z) > MIN_USABLE_SCALE ? desired.z / current.z : 1f);
        }

        private void RefreshRagdollSafetySubscription()
        {
            if (m_RagdollSubscribedCharacter == GameCreatorCharacter) return;
            UnsubscribeRagdollSafety();
            if (GameCreatorCharacter?.Ragdoll == null) return;

            m_RagdollSubscribedCharacter = GameCreatorCharacter;
            m_RagdollSubscribedCharacter.Ragdoll.EventBeforeStartRagdoll +=
                HandleBeforeStartRagdoll;
        }

        private void UnsubscribeRagdollSafety()
        {
            if (m_RagdollSubscribedCharacter?.Ragdoll != null)
            {
                m_RagdollSubscribedCharacter.Ragdoll.EventBeforeStartRagdoll -=
                    HandleBeforeStartRagdoll;
            }
            m_RagdollSubscribedCharacter = null;
        }

        private void HandleBeforeStartRagdoll()
        {
            RestorePresentationHierarchy();
        }

        private uint ResolveOwnerClientId()
        {
            if (owner.HasValue)
            {
                return PlayerIdToClientId(owner.Value);
            }

            if (PurrNetIdentity != null && PurrNetIdentity.owner.HasValue)
            {
                return PlayerIdToClientId(PurrNetIdentity.owner.Value);
            }

            if (m_SecurityActorNetworkId != 0 &&
                NetworkTransportBridge.Active != null &&
                NetworkTransportBridge.Active.TryGetCharacterOwner(
                    m_SecurityActorNetworkId,
                    out uint ownerClientId))
            {
                return ownerClientId;
            }

            return NetworkTransportBridge.InvalidClientId;
        }

        private static uint PlayerIdToClientId(PlayerID playerId)
        {
            ulong raw = playerId.id;
            if (raw > uint.MaxValue) return NetworkTransportBridge.InvalidClientId;
            return (uint)raw;
        }

        private void RecordMissingSecurityContextOnce(string requestType)
        {
            if (m_MissingSecurityContextViolationRecorded) return;
            m_MissingSecurityContextViolationRecorded = true;

            uint ownerClientId = NetworkTransportBridge.IsValidClientId(m_SecurityOwnerClientId)
                ? m_SecurityOwnerClientId
                : NetworkTransportBridge.InvalidClientId;

            SecurityIntegration.RecordViolation(
                ownerClientId,
                m_SecurityActorNetworkId,
                SecurityViolationType.InvalidTarget,
                SECURITY_MODULE_CORE,
                $"{requestType}: unresolved PurrDiction actor ownership context " +
                $"actor={m_SecurityActorNetworkId}, owner={m_SecurityOwnerClientId}");
        }

        private void TryRegisterWithPredictionManager()
        {
            EnsureBaseReferences();

            if (predictionManager != null)
            {
                m_RegisteredPredictionManager = predictionManager;
                m_PredictionRegistered = true;
                return;
            }

            if (m_PredictionRegistered) return;

            if (PurrNetIdentity != null && !PurrNetIdentity.isSpawned)
            {
                if (!m_QueuedSpawnRegistration)
                {
                    m_QueuedSpawnRegistration = true;
                    PurrNetIdentity.QueueOnSpawned(TryRegisterWithPredictionManager);
                }

                return;
            }

            if (!PredictionManager.TryGetInstance(gameObject.scene.handle, out PredictionManager manager) ||
                manager == null)
            {
                WarnMissingPredictionManagerOnce();
                return;
            }

            if (!TryResolvePredictedObjectId(out PredictedObjectID objectId))
            {
                Debug.LogWarning(
                    $"[GC2 PurrDiction] Could not resolve a PurrNet NetworkIdentity object id for '{name}'. " +
                    "PurrDiction movement prediction will not run for this character.",
                    this);
                return;
            }

            PlayerID? owner = PurrNetIdentity != null ? PurrNetIdentity.owner : null;
            manager.RegisterInstance(gameObject, objectId, owner, reset: false, triggedOnRemovedFromPool: false);

            m_RegisteredPredictionManager = manager;
            m_PredictionRegistered = true;
            RefreshSecurityContext();
        }

        private bool TryResolvePredictedObjectId(out PredictedObjectID objectId)
        {
            objectId = default;
            if (PurrNetIdentity == null) return false;

            ulong purrNetObjectId = PurrNetIdentity.objectId;
            if (purrNetObjectId == 0 || purrNetObjectId > int.MaxValue) return false;

            objectId = new PredictedObjectID(PURRNET_PREDICTED_ID_NAMESPACE | (uint)purrNetObjectId);
            return true;
        }

        private void WarnMissingPredictionManagerOnce()
        {
            if (m_MissingPredictionManagerWarned) return;
            m_MissingPredictionManagerWarned = true;

            Debug.LogWarning(
                $"[GC2 PurrDiction] No PurrDiction PredictionManager was found in scene '{gameObject.scene.name}'. " +
                "Run the PurrNet Scene Setup Wizard with Prediction Backend set to PurrDiction.",
                this);
        }

        private void UnregisterFromPredictionManager()
        {
            if (!m_PredictionRegistered && predictionManager == null) return;

            PredictionManager manager = predictionManager != null
                ? predictionManager
                : m_RegisteredPredictionManager;

            if (manager != null)
            {
                manager.UnregisterInstance(this);
            }

            predictionManager = null;
            m_RegisteredPredictionManager = null;
            m_PredictionRegistered = false;
        }
    }
}
