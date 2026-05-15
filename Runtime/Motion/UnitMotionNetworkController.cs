using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative motion controller that validates and routes all motion commands.
    /// Synchronizes configuration changes and provides server-validated dash/teleport methods.
    /// </summary>
    [Title("Network Motion Controller")]
    [Image(typeof(IconChip), ColorTheme.Type.Purple)]
    [Category("Network Motion Controller")]
    [Description("Server-authoritative motion controller with network synchronization. " +
                 "Validates dash/teleport, syncs config changes, routes move commands.")]
    [Serializable]
    public class UnitMotionNetworkController : TUnitMotion
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [SerializeField] private float m_Speed = 4f;
        [SerializeField] private EnablerFloat m_Rotation = new EnablerFloat(true, 1800f);

        [SerializeField] private float m_Mass = 80f;
        [SerializeField] private float m_Height = 2.0f;
        [SerializeField] private float m_Radius = 0.2f;

        [SerializeField] private float m_GravityUpwards = -9.81f;
        [SerializeField] private float m_GravityDownwards = -9.81f;
        [SerializeField] private float m_TerminalVelocity = -53f;

        [SerializeField] private MotionAcceleration m_Acceleration;
        [SerializeField] private MotionJump m_Jump;
        [SerializeField] private MotionDash m_Dash;
        
        [Header("Network Settings")]
        [SerializeField] private bool m_IsServer = false;
        [SerializeField] private float m_MaxTeleportDistance = 50f;
        [SerializeField] private float m_MaxDashDistance = 20f;
        [SerializeField] private int m_MaxPendingCallbacks = 128;
        [SerializeField] private float m_PendingCallbackTimeoutSeconds = 8f;
        [SerializeField] private bool m_LogDashDiagnostics = false;
        [SerializeField] private bool m_LogNavigationDiagnostics = false;
        [SerializeField] private float m_NavigationDiagnosticsInterval = 0.5f;

        // MEMBERS: -------------------------------------------------------------------------------

        private struct PendingCallbackEntry
        {
            public Action<NetworkMotionResult> Callback;
            public float CreatedAt;
        }

        private const float MOVE_DIRECTION_MINIMUM_SEND_INTERVAL = 0.05f;
        private const float MOVE_DIRECTION_HEARTBEAT_INTERVAL = 0.12f;
        private const float MOVE_DIRECTION_CHANGE_THRESHOLD = 0.05f;

        [NonSerialized] private byte m_ConfigVersion;
        [NonSerialized] private NetworkMotionConfig m_LastSentConfig;
        [NonSerialized] private Queue<NetworkMotionCommand> m_PendingCommands;
        [NonSerialized] private ushort m_CommandSequence;
        [NonSerialized] private float m_LastDashTime;
        [NonSerialized] private int m_DashesUsed;
        [NonSerialized] private Dictionary<ushort, PendingCallbackEntry> m_PendingCallbacks;
        [NonSerialized] private List<ushort> m_PendingCallbackRemovalBuffer;
        [NonSerialized] private HashSet<ushort> m_PredictedCommandSequences;
        [NonSerialized] private Coroutine m_NavigationRoutine;
        [NonSerialized] private float m_LastNavigationDiagnosticTime;
        [NonSerialized] private bool m_HasSentMoveDirection;
        [NonSerialized] private Vector3 m_LastSentMoveDirectionVelocity;
        [NonSerialized] private Space m_LastSentMoveDirectionSpace;
        [NonSerialized] private int m_LastSentMoveDirectionPriority;
        [NonSerialized] private float m_LastMoveDirectionSentAt;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when config changes and needs to be sent to clients/server.
        /// </summary>
        public event Action<NetworkMotionConfig> OnConfigChanged;

        /// <summary>
        /// Fired when a motion command needs to be sent to the server (client-side).
        /// </summary>
        public event Action<NetworkMotionCommand> OnSendCommand;

        /// <summary>
        /// Fired when a command result should be sent to the client (server-side).
        /// </summary>
        public event Action<NetworkMotionResult> OnSendResult;

        /// <summary>
        /// Fired when a validated command should be broadcast to all clients (server-side).
        /// </summary>
        public event Action<NetworkMotionCommand> OnBroadcastCommand;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------
        
        public override float JumpForce
        {
            get => this.m_Jump.JumpForce;
            set
            {
                if (Math.Abs(this.m_Jump.JumpForce - value) > 0.001f)
                {
                    this.m_Jump.JumpForce = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float LinearSpeed
        {
            get => this.m_Speed;
            set
            {
                if (Math.Abs(this.m_Speed - value) > 0.001f)
                {
                    this.m_Speed = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float AngularSpeed
        {
            get => this.m_Rotation.IsEnabled ? this.m_Rotation.Value : -1f;
            set
            {
                float oldValue = this.m_Rotation.IsEnabled ? this.m_Rotation.Value : -1f;
                if (Math.Abs(oldValue - value) > 0.001f)
                {
                    if (value < 0f)
                    {
                        this.m_Rotation.IsEnabled = false;
                        this.m_Rotation.Value = -1f;
                    }
                    else
                    {
                        this.m_Rotation.IsEnabled = true;
                        this.m_Rotation.Value = value;
                    }
                    MarkConfigDirty();
                }
            }
        }

        public override float Mass
        {
            get => this.m_Mass;
            set => this.m_Mass = value; // Mass doesn't need network sync for movement
        }

        public override float Height
        {
            get => this.m_Height;
            set => this.m_Height = value;
        }

        public override float Radius
        {
            get => this.m_Radius;
            set => this.m_Radius = value;
        }

        public override bool CanJump
        {
            get => this.m_Jump.CanJump && !this.Character.Busy.AreLegsBusy;
            set
            {
                if (this.m_Jump.CanJump != value)
                {
                    this.m_Jump.CanJump = value;
                    MarkConfigDirty();
                }
            }
        }

        public override int AirJumps
        {
            get => m_Jump.AirJumps;
            set
            {
                if (m_Jump.AirJumps != value)
                {
                    m_Jump.AirJumps = value;
                    MarkConfigDirty();
                }
            }
        }

        public override int DashInSuccession
        {
            get => this.m_Dash.InSuccession;
            set
            {
                if (this.m_Dash.InSuccession != value)
                {
                    this.m_Dash.InSuccession = value;
                    MarkConfigDirty();
                }
            }
        }

        public override bool DashInAir
        {
            get => this.m_Dash.DashInAir;
            set
            {
                if (this.m_Dash.DashInAir != value)
                {
                    this.m_Dash.DashInAir = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float DashCooldown
        {
            get => this.m_Dash.Cooldown;
            set
            {
                if (Math.Abs(this.m_Dash.Cooldown - value) > 0.001f)
                {
                    this.m_Dash.Cooldown = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float GravityUpwards
        {
            get => this.m_GravityUpwards;
            set
            {
                if (Math.Abs(this.m_GravityUpwards - value) > 0.001f)
                {
                    this.m_GravityUpwards = value;
                    MarkConfigDirty();
                }
            }
        }
        
        public override float GravityDownwards
        {
            get => this.m_GravityDownwards;
            set
            {
                if (Math.Abs(this.m_GravityDownwards - value) > 0.001f)
                {
                    this.m_GravityDownwards = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float TerminalVelocity
        {
            get => this.m_TerminalVelocity;
            set
            {
                if (Math.Abs(this.m_TerminalVelocity - value) > 0.001f)
                {
                    this.m_TerminalVelocity = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float JumpCooldown
        {
            get => this.m_Jump.JumpCooldown;
            set
            {
                if (Math.Abs(this.m_Jump.JumpCooldown - value) > 0.001f)
                {
                    this.m_Jump.JumpCooldown = value;
                    MarkConfigDirty();
                }
            }
        }

        public override bool UseAcceleration
        {
            get => this.m_Acceleration.UseAcceleration;
            set
            {
                if (this.m_Acceleration.UseAcceleration != value)
                {
                    this.m_Acceleration.UseAcceleration = value;
                    MarkConfigDirty();
                }
            }
        }

        public override float Acceleration
        {
            get => this.m_Acceleration.Acceleration;
            set => this.m_Acceleration.Acceleration = value;
        }

        public override float Deceleration
        {
            get => this.m_Acceleration.Deceleration;
            set => this.m_Acceleration.Deceleration = value;
        }
        
        public bool IsServer
        {
            get => m_IsServer;
            set => m_IsServer = value;
        }

        // CONSTRUCTORS: --------------------------------------------------------------------------

        public UnitMotionNetworkController() : base()
        {
            this.m_Acceleration = new MotionAcceleration();
            this.m_Jump = new MotionJump();
            this.m_Dash = new MotionDash();
            this.m_PendingCommands = new Queue<NetworkMotionCommand>(16);
            this.m_PendingCallbacks = new Dictionary<ushort, PendingCallbackEntry>(16);
            this.m_PendingCallbackRemovalBuffer = new List<ushort>(8);
            this.m_PredictedCommandSequences = new HashSet<ushort>();
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            EnsureRuntimeStateInitialized();
            this.m_ConfigVersion = 0;
            this.m_CommandSequence = 1;
            this.m_LastDashTime = -1000f;
            this.m_DashesUsed = 0;
            this.m_PendingCallbacks.Clear();
            this.m_PredictedCommandSequences.Clear();
            this.m_NavigationRoutine = null;
            this.m_LastNavigationDiagnosticTime = -1000f;
            this.m_HasSentMoveDirection = false;
            this.m_LastSentMoveDirectionVelocity = Vector3.zero;
            this.m_LastSentMoveDirectionSpace = Space.World;
            this.m_LastSentMoveDirectionPriority = 0;
            this.m_LastMoveDirectionSentAt = -1000f;
            
            // Send initial config
            SendConfigUpdate();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            PublishExplicitNavigationVelocityForAnimation();
        }

        // CONFIG SYNCHRONIZATION: ----------------------------------------------------------------

        private void MarkConfigDirty()
        {
            m_ConfigVersion++;
            SendConfigUpdate();
        }

        private void SendConfigUpdate()
        {
            var config = CreateCurrentConfig();
            
            // Only send if actually changed
            if (!config.Equals(m_LastSentConfig))
            {
                m_LastSentConfig = config;
                OnConfigChanged?.Invoke(config);
            }
        }

        /// <summary>
        /// Get the current motion configuration for network transmission.
        /// </summary>
        public NetworkMotionConfig GetCurrentConfig()
        {
            return CreateCurrentConfig();
        }

        private NetworkMotionConfig CreateCurrentConfig()
        {
            return NetworkMotionConfig.Create(
                m_Speed,
                m_Rotation.IsEnabled ? m_Rotation.Value : -1f,
                m_GravityUpwards,
                m_GravityDownwards,
                m_TerminalVelocity,
                m_Jump.JumpForce,
                m_Jump.JumpCooldown,
                m_Jump.AirJumps,
                m_Dash.InSuccession,
                m_Dash.Cooldown,
                m_Jump.CanJump,
                m_Dash.DashInAir,
                m_Acceleration.UseAcceleration,
                m_ConfigVersion
            );
        }

        /// <summary>
        /// Apply a config received from the network.
        /// Call this on clients when receiving server config updates.
        /// </summary>
        public void ApplyNetworkConfig(NetworkMotionConfig config)
        {
            // Skip if same version or older
            if (config.configVersion <= m_ConfigVersion && config.configVersion != 0)
                return;

            m_Speed = config.GetLinearSpeed();
            if (config.GetAngularSpeed() < 0)
            {
                m_Rotation.IsEnabled = false;
            }
            else
            {
                m_Rotation.IsEnabled = true;
                m_Rotation.Value = config.GetAngularSpeed();
            }
            
            m_GravityUpwards = config.GetGravityUp();
            m_GravityDownwards = config.GetGravityDown();
            m_TerminalVelocity = config.GetTerminalVelocity();
            m_Jump.JumpForce = config.GetJumpForce();
            m_Jump.JumpCooldown = config.GetJumpCooldown();
            m_Jump.AirJumps = config.airJumps;
            m_Dash.InSuccession = config.dashSuccession;
            m_Dash.Cooldown = config.GetDashCooldown();
            m_Jump.CanJump = config.CanJump;
            m_Dash.DashInAir = config.DashInAir;
            m_Acceleration.UseAcceleration = config.UseAcceleration;
            
            m_ConfigVersion = config.configVersion;
        }

        // NETWORK MOTION COMMANDS: ---------------------------------------------------------------

        /// <summary>
        /// Request a server-validated dash/transient movement.
        /// </summary>
        public void RequestDash(Vector3 direction, float speed, float duration, float fade, 
            Action<NetworkMotionResult> callback = null)
        {
            ushort sequence = NextCommandSequence(!m_IsServer && callback != null);
            var command = NetworkMotionCommand.CreateDash(
                direction.normalized, speed, duration, fade, sequence);
            
            if (m_IsServer)
            {
                // Server validates and applies immediately
                var result = ValidateDashCommand(command);
                if (result.approved)
                {
                    ApplyDashLocally(command);
                    OnBroadcastCommand?.Invoke(command);
                }
                callback?.Invoke(result);
            }
            else
            {
                // Client sends to server for validation
                RegisterPendingCallback(sequence, callback);
                OnSendCommand?.Invoke(command);
            }
        }

        /// <summary>
        /// Sends a dash command after the caller has already predicted and applied the
        /// local GC2 dash. Used by network-aware visual scripting instructions that
        /// need immediate local responsiveness while still letting the server validate
        /// and replicate the semantic dash to other peers.
        /// </summary>
        public void SubmitPredictedDash(
            Vector3 direction,
            float speed,
            float gravity,
            float duration,
            float fade)
        {
            ushort sequence = NextCommandSequence(false);
            var command = NetworkMotionCommand.CreateDash(
                direction.normalized,
                speed,
                duration,
                fade,
                sequence,
                gravity);

            m_PredictedCommandSequences.Add(sequence);

            LogDash(
                $"submit predicted seq={sequence} server={m_IsServer} " +
                $"dir={FormatVector(command.GetDirection())} speed={command.GetSpeed():F2} " +
                $"gravity={command.GetGravity():F2} duration={command.GetDuration():F2} fade={command.GetFade():F2} " +
                $"sendHook={(OnSendCommand != null)} broadcastHook={(OnBroadcastCommand != null)}");

            // Character.Dash.Execute writes the GC2 motion transient (MoveDirection)
            // but our network drivers (UnitDriverNetworkServer / UnitDriverNetworkClient)
            // do not read MoveDirection in OnUpdate -- they only translate via raw input
            // or explicit AddPosition. The host (server+owner) therefore never moves, and
            // even on a regular client the local dash visual depends on server reconciliation.
            // Drive translation explicitly here on the owner so the impulse is felt instantly
            // on the dasher's own view, regardless of host or client.
            if (this.Character != null && this.Character.gameObject.activeInHierarchy)
            {
                this.Character.StartCoroutine(
                    DriveServerDashRoutine(direction.normalized, speed, duration));
            }

            if (m_IsServer)
            {
                if (OnBroadcastCommand == null)
                {
                    LogDash($"cannot broadcast host/server dash seq={sequence}: OnBroadcastCommand has no listeners");
                }

                OnBroadcastCommand?.Invoke(command);
                return;
            }

            if (OnSendCommand == null)
            {
                LogDash($"cannot send dash seq={sequence}: OnSendCommand has no listeners");
            }

            OnSendCommand?.Invoke(command);
        }

        public bool ConsumePredictedCommand(NetworkMotionCommand command)
        {
            EnsureRuntimeStateInitialized();
            return m_PredictedCommandSequences.Remove(command.sequenceNumber);
        }

        /// <summary>
        /// Request a server-validated teleport.
        /// </summary>
        public void RequestTeleport(Vector3 position, float rotationY = float.NaN, 
            Action<NetworkMotionResult> callback = null)
        {
            float rotation = float.IsNaN(rotationY) 
                ? this.Character.transform.eulerAngles.y 
                : rotationY;
            
            ushort sequence = NextCommandSequence(!m_IsServer && callback != null);
            var command = NetworkMotionCommand.CreateTeleport(position, rotation, sequence);
            
            if (m_IsServer)
            {
                var result = ValidateTeleportCommand(command);
                if (result.approved)
                {
                    ApplyTeleportLocally(command);
                    OnBroadcastCommand?.Invoke(command);
                }
                callback?.Invoke(result);
            }
            else
            {
                RegisterPendingCallback(sequence, callback);
                OnSendCommand?.Invoke(command);
            }
        }

        /// <summary>
        /// Network-aware MoveToDirection.
        /// On server: applies directly and broadcasts.
        /// On client: sends command to server.
        /// </summary>
        public override void MoveToDirection(Vector3 velocity, Space space, int priority)
        {
            if (m_IsServer)
            {
                ushort sequence = NextCommandSequence(false);

                // Server applies directly
                StopNavigationRoutine();
                base.MoveToDirection(velocity, space, priority);
                
                // Broadcast to clients
                var command = NetworkMotionCommand.CreateMoveToDirection(
                    velocity, space == Space.World, priority, sequence);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                if (!ShouldSendMoveDirectionCommand(velocity, space, priority))
                {
                    return;
                }

                ushort sequence = NextCommandSequence(false);

                StopNavigationRoutine();

                // Client sends to server
                var command = NetworkMotionCommand.CreateMoveToDirection(
                    velocity, space == Space.World, priority, sequence);
                OnSendCommand?.Invoke(command);
                
                // Optionally apply locally for prediction (commented out for strict server-authority)
                // base.MoveToDirection(velocity, space, priority);
            }
        }

        /// <summary>
        /// Network-aware StopToDirection. Also cancels explicit network navigation routines.
        /// </summary>
        public override void StopToDirection(int priority)
        {
            ushort sequence = NextCommandSequence(false);
            StopNavigationRoutine();
            base.StopToDirection(priority);
            MarkMoveDirectionCommandSent(Vector3.zero, Space.World, priority, Time.unscaledTime);

            var command = NetworkMotionCommand.CreateStopDirection(priority, sequence);
            if (m_IsServer)
            {
                MarkOwnerPredictedCommand(sequence);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                m_PredictedCommandSequences.Add(sequence);
                if (OnSendCommand == null)
                {
                    LogNavigation($"cannot send StopDirection seq={sequence}: OnSendCommand has no listeners");
                }

                OnSendCommand?.Invoke(command);
            }
        }

        /// <summary>
        /// Network-aware MoveToLocation.
        /// Routes through server for validation.
        /// </summary>
        public override void MoveToLocation(Location location, float stopDistance,
            Action<Character, bool> onFinish, int priority)
        {
            Vector3 targetPosition = location.GetPosition(this.Character.gameObject);
            ushort sequence = NextCommandSequence(false);
            GetNavigationDriveDecision(out string driveReason);
            LogNavigation(
                $"request MoveTo seq={sequence} server={m_IsServer} {FormatNetworkContext()} " +
                $"driver={FormatDriver()} canExplicitDrive={driveReason} " +
                $"currentFeet={FormatVector(this.Character.Feet)} target={FormatVector(targetPosition)} " +
                $"stop={stopDistance:F2} priority={priority} sendHook={(OnSendCommand != null)} " +
                $"broadcastHook={(OnBroadcastCommand != null)} callback={(onFinish != null)}");
            
            if (m_IsServer)
            {
                StopNavigationRoutine();
                base.MoveToLocation(location, stopDistance, onFinish, priority);
                StartMoveToPositionRoutine(targetPosition, stopDistance);
                
                var command = NetworkMotionCommand.CreateMoveToPosition(
                    targetPosition, stopDistance, priority, sequence);
                MarkOwnerPredictedCommand(sequence);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                var command = NetworkMotionCommand.CreateMoveToPosition(
                    targetPosition, stopDistance, priority, sequence);
                m_PredictedCommandSequences.Add(sequence);
                StopNavigationRoutine();
                base.MoveToLocation(location, stopDistance, onFinish, priority);
                StartMoveToPositionRoutine(targetPosition, stopDistance);
                if (OnSendCommand == null)
                {
                    LogNavigation($"cannot send MoveTo seq={sequence}: OnSendCommand has no listeners");
                }
                OnSendCommand?.Invoke(command);
            }
        }

        /// <summary>
        /// Network-aware StartFollowingTarget.
        /// Routes follow intent through the server and explicitly drives network drivers locally.
        /// </summary>
        public override void StartFollowingTarget(Transform target, float minRadius,
            float maxRadius, int priority)
        {
            if (target == null) return;

            minRadius = Mathf.Max(0f, minRadius);
            maxRadius = Mathf.Max(minRadius, maxRadius);

            ushort sequence = NextCommandSequence(false);
            uint targetNetworkId = TryGetNetworkCharacterId(target, out uint resolvedId)
                ? resolvedId
                : 0u;
            Vector3 fallbackPosition = target.position;
            GetNavigationDriveDecision(out string driveReason);
            LogNavigation(
                $"request Follow seq={sequence} server={m_IsServer} {FormatNetworkContext()} " +
                $"driver={FormatDriver()} canExplicitDrive={driveReason} target='{target.name}' " +
                $"targetId={targetNetworkId} currentFeet={FormatVector(this.Character.Feet)} " +
                $"targetPos={FormatVector(fallbackPosition)} min={minRadius:F2} max={maxRadius:F2} " +
                $"priority={priority} sendHook={(OnSendCommand != null)} broadcastHook={(OnBroadcastCommand != null)}");

            if (m_IsServer)
            {
                StopNavigationRoutine();
                base.StartFollowingTarget(target, minRadius, maxRadius, priority);
                StartFollowTargetRoutine(target, targetNetworkId, fallbackPosition, minRadius, maxRadius);

                var command = NetworkMotionCommand.CreateFollowTarget(
                    targetNetworkId,
                    fallbackPosition,
                    minRadius,
                    maxRadius,
                    priority,
                    sequence);
                MarkOwnerPredictedCommand(sequence);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                var command = NetworkMotionCommand.CreateFollowTarget(
                    targetNetworkId,
                    fallbackPosition,
                    minRadius,
                    maxRadius,
                    priority,
                    sequence);
                m_PredictedCommandSequences.Add(sequence);
                StopNavigationRoutine();
                base.StartFollowingTarget(target, minRadius, maxRadius, priority);
                StartFollowTargetRoutine(target, targetNetworkId, fallbackPosition, minRadius, maxRadius);
                if (OnSendCommand == null)
                {
                    LogNavigation($"cannot send Follow seq={sequence}: OnSendCommand has no listeners");
                }
                OnSendCommand?.Invoke(command);
            }
        }

        /// <summary>
        /// Network-aware StopFollowingTarget.
        /// </summary>
        public override void StopFollowingTarget(int priority)
        {
            ushort sequence = NextCommandSequence(false);
            LogNavigation(
                $"request StopFollow seq={sequence} server={m_IsServer} {FormatNetworkContext()} " +
                $"driver={FormatDriver()} priority={priority} sendHook={(OnSendCommand != null)} " +
                $"broadcastHook={(OnBroadcastCommand != null)}");

            StopNavigationRoutine();
            base.StopFollowingTarget(priority);

            var command = NetworkMotionCommand.CreateStopFollow(priority, sequence);
            if (m_IsServer)
            {
                MarkOwnerPredictedCommand(sequence);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                m_PredictedCommandSequences.Add(sequence);
                if (OnSendCommand == null)
                {
                    LogNavigation($"cannot send StopFollow seq={sequence}: OnSendCommand has no listeners");
                }
                OnSendCommand?.Invoke(command);
            }
        }

        // SERVER-SIDE VALIDATION: ----------------------------------------------------------------

        /// <summary>
        /// Process a command received from a client (server-side only) with strict sender validation.
        /// </summary>
        public NetworkMotionResult ProcessClientCommand(NetworkMotionCommand command, uint senderClientId)
        {
            if (IsNavigationCommand(command.commandType))
            {
                LogNavigation(
                    $"server received {FormatNavigationCommand(command)} from sender={senderClientId} " +
                    $"{FormatNetworkContext()} driver={FormatDriver()}");
            }

            if (!m_IsServer)
            {
                LogDash($"server rejected {command.commandType} seq={command.sequenceNumber}: controller is not server");
                LogNavigation($"server rejected {command.commandType} seq={command.sequenceNumber}: controller is not server");
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
            }

            uint actorNetworkId = ResolveActorNetworkId();
            if (!NetworkTransportBridge.IsValidClientId(senderClientId) || actorNetworkId == 0)
            {
                SecurityIntegration.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    "Core",
                    $"Motion command rejected: missing sender/actor context. sender={senderClientId}, actor={actorNetworkId}");

                LogDash(
                    $"server rejected {command.commandType} seq={command.sequenceNumber}: " +
                    $"missing sender/actor sender={senderClientId} actor={actorNetworkId}");
                LogNavigation(
                    $"server rejected {command.commandType} seq={command.sequenceNumber}: " +
                    $"missing sender/actor sender={senderClientId} actor={actorNetworkId}");

                NetworkMotionResult rejected = NetworkMotionResult.Rejected(
                    command.sequenceNumber,
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
                OnSendResult?.Invoke(rejected);
                return rejected;
            }

            uint correlationId = NetworkCorrelation.Compose(actorNetworkId, (uint)command.sequenceNumber);
            if (!SecurityIntegration.ValidateCoreRequest(
                    senderClientId,
                    actorNetworkId,
                    correlationId,
                    command.commandType.ToString()))
            {
                LogDash(
                    $"server rejected {command.commandType} seq={command.sequenceNumber}: " +
                    $"security validation failed sender={senderClientId} actor={actorNetworkId}");
                LogNavigation(
                    $"server rejected {command.commandType} seq={command.sequenceNumber}: " +
                    $"security validation failed sender={senderClientId} actor={actorNetworkId}");

                NetworkMotionResult rejected = NetworkMotionResult.Rejected(
                    command.sequenceNumber,
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
                OnSendResult?.Invoke(rejected);
                return rejected;
            }

            NetworkMotionResult result = ProcessValidatedClientCommand(command);
            if (command.commandType == NetworkMotionCommandType.Dash)
            {
                LogDash(
                    $"server processed dash seq={command.sequenceNumber} approved={result.approved} " +
                    $"reason={result.rejectionReason}");
            }
            else if (IsNavigationCommand(command.commandType))
            {
                LogNavigation(
                    $"server processed {command.commandType} seq={command.sequenceNumber} " +
                    $"approved={result.approved} reason={result.rejectionReason}");
            }
            OnSendResult?.Invoke(result);
            return result;
        }

        private NetworkMotionResult ProcessValidatedClientCommand(NetworkMotionCommand command)
        {
            switch (command.commandType)
            {
                case NetworkMotionCommandType.Dash:
                    NetworkMotionResult result = ValidateDashCommand(command);
                    if (result.approved)
                    {
                        LogDash(
                            $"server applying validated dash seq={command.sequenceNumber} " +
                            $"dir={FormatVector(command.GetDirection())} speed={command.GetSpeed():F2} " +
                            $"gravity={command.GetGravity():F2} duration={command.GetDuration():F2}");
                        ApplyDashLocally(command);
                        if (OnBroadcastCommand == null)
                        {
                            LogDash($"server cannot broadcast dash seq={command.sequenceNumber}: OnBroadcastCommand has no listeners");
                        }
                        OnBroadcastCommand?.Invoke(command);
                    }
                    else
                    {
                        LogDash(
                            $"server rejected dash seq={command.sequenceNumber} reason={result.rejectionReason} " +
                            $"dir={FormatVector(command.GetDirection())} speed={command.GetSpeed():F2} " +
                            $"duration={command.GetDuration():F2}");
                    }
                    return result;

                case NetworkMotionCommandType.Teleport:
                    result = ValidateTeleportCommand(command);
                    if (result.approved)
                    {
                        ApplyTeleportLocally(command);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    return result;

                case NetworkMotionCommandType.MoveToDirection:
                    result = ValidateMoveDirectionCommand(command);
                    if (result.approved)
                    {
                        Vector3 velocity = command.GetVelocity();
                        Space space = command.IsWorldSpace() ? Space.World : Space.Self;
                        StopNavigationRoutine();
                        base.MoveToDirection(velocity, space, command.priority);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    return result;

                case NetworkMotionCommandType.MoveToPosition:
                    result = ValidateMovePositionCommand(command);
                    if (result.approved)
                    {
                        Vector3 position = command.GetPosition();
                        float stopDist = command.GetStopDistance();
                        Location loc = new Location(position);
                        LogNavigation(
                            $"server applying MoveTo seq={command.sequenceNumber} " +
                            $"target={FormatVector(position)} stop={stopDist:F2} driver={FormatDriver()}");
                        StopNavigationRoutine();
                        base.MoveToLocation(loc, stopDist, null, command.priority);
                        StartMoveToPositionRoutine(position, stopDist);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    return result;

                case NetworkMotionCommandType.FollowTarget:
                    result = ValidateFollowTargetCommand(command);
                    if (result.approved)
                    {
                        LogNavigation(
                            $"server applying Follow seq={command.sequenceNumber} " +
                            $"targetId={command.targetNetworkId} fallback={FormatVector(command.GetPosition())} " +
                            $"min={command.GetFollowMinRadius():F2} max={command.GetFollowMaxRadius():F2} " +
                            $"driver={FormatDriver()}");
                        ApplyFollowCommandLocally(command, driveExplicitly: true);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    return result;

                case NetworkMotionCommandType.StopFollow:
                    result = NetworkMotionResult.Approved(command.sequenceNumber);
                    LogNavigation(
                        $"server applying StopFollow seq={command.sequenceNumber} priority={command.priority} " +
                        $"driver={FormatDriver()}");
                    StopNavigationRoutine();
                    base.StopFollowingTarget(command.priority);
                    OnBroadcastCommand?.Invoke(command);
                    return result;

                case NetworkMotionCommandType.Jump:
                case NetworkMotionCommandType.ForceJump:
                    result = ValidateJumpCommand(command);
                    if (result.approved)
                    {
                        float force = command.GetJumpForce();
                        if (command.commandType == NetworkMotionCommandType.ForceJump)
                            base.ForceJump(force);
                        else
                            base.Jump(force);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    return result;

                case NetworkMotionCommandType.StopDirection:
                    result = NetworkMotionResult.Approved(command.sequenceNumber);
                    StopNavigationRoutine();
                    base.StopToDirection(command.priority);
                    OnBroadcastCommand?.Invoke(command);
                    return result;

                default:
                    return NetworkMotionResult.Rejected(command.sequenceNumber, 
                        NetworkMotionResult.REJECT_NOT_ALLOWED);
            }
        }

        /// <summary>
        /// Apply a server-broadcast command locally (client-side).
        /// </summary>
        public void ApplyBroadcastCommand(NetworkMotionCommand command)
        {
            if (IsNavigationCommand(command.commandType))
            {
                LogNavigation(
                    $"apply broadcast {FormatNavigationCommand(command)} server={m_IsServer} " +
                    $"{FormatNetworkContext()} driver={FormatDriver()}");
            }

            switch (command.commandType)
            {
                case NetworkMotionCommandType.Dash:
                    LogDash(
                        $"applying dash broadcast seq={command.sequenceNumber} " +
                        $"dir={FormatVector(command.GetDirection())} speed={command.GetSpeed():F2} " +
                        $"gravity={command.GetGravity():F2} duration={command.GetDuration():F2}");
                    ApplyDashLocally(command);
                    break;

                case NetworkMotionCommandType.Teleport:
                    ApplyTeleportLocally(command);
                    break;

                case NetworkMotionCommandType.MoveToDirection:
                    Vector3 velocity = command.GetVelocity();
                    Space space = command.IsWorldSpace() ? Space.World : Space.Self;
                    StopNavigationRoutine();
                    base.MoveToDirection(velocity, space, command.priority);
                    break;

                case NetworkMotionCommandType.MoveToPosition:
                    if (m_IsServer) break;
                    Vector3 position = command.GetPosition();
                    float stopDist = command.GetStopDistance();
                    Location loc = new Location(position);
                    base.MoveToLocation(loc, stopDist, null, command.priority);
                    break;

                case NetworkMotionCommandType.FollowTarget:
                    if (m_IsServer) break;
                    ApplyFollowCommandLocally(command, driveExplicitly: false);
                    break;

                case NetworkMotionCommandType.StopFollow:
                    if (m_IsServer) break;
                    StopNavigationRoutine();
                    base.StopFollowingTarget(command.priority);
                    break;

                case NetworkMotionCommandType.Jump:
                    base.Jump(command.GetJumpForce());
                    break;

                case NetworkMotionCommandType.ForceJump:
                    base.ForceJump(command.GetJumpForce());
                    break;

                case NetworkMotionCommandType.StopDirection:
                    StopNavigationRoutine();
                    base.StopToDirection(command.priority);
                    break;
            }
        }

        /// <summary>
        /// Handle a command result from the server (client-side).
        /// </summary>
        public void HandleCommandResult(NetworkMotionResult result)
        {
            PruneExpiredPendingCallbacks(Time.time);

            if (m_PendingCallbacks.TryGetValue(result.commandSequence, out PendingCallbackEntry pending))
            {
                pending.Callback?.Invoke(result);
                m_PendingCallbacks.Remove(result.commandSequence);
            }
        }

        // VALIDATION METHODS: --------------------------------------------------------------------

        private NetworkMotionResult ValidateDashCommand(NetworkMotionCommand command)
        {
            float currentTime = Time.time;
            
            // Check cooldown
            if (currentTime - m_LastDashTime < m_Dash.Cooldown && m_DashesUsed >= m_Dash.InSuccession)
            {
                LogDash(
                    $"dash validation failed cooldown seq={command.sequenceNumber} " +
                    $"elapsed={currentTime - m_LastDashTime:F2} cooldown={m_Dash.Cooldown:F2} " +
                    $"used={m_DashesUsed} succession={m_Dash.InSuccession}");
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_COOLDOWN);
            }
            
            // Check if in air when not allowed
            if (!m_Dash.DashInAir && !this.Character.Driver.IsGrounded)
            {
                LogDash(
                    $"dash validation failed grounded seq={command.sequenceNumber}: " +
                    $"dashInAir={m_Dash.DashInAir} grounded={this.Character.Driver.IsGrounded}");
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
            }
            
            // Check dash distance
            float dashDistance = command.GetSpeed() * command.GetDuration();
            if (dashDistance > m_MaxDashDistance)
            {
                LogDash(
                    $"dash validation failed distance seq={command.sequenceNumber}: " +
                    $"distance={dashDistance:F2} max={m_MaxDashDistance:F2}");
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_TOO_FAR);
            }
            
            return NetworkMotionResult.Approved(command.sequenceNumber);
        }

        private NetworkMotionResult ValidateTeleportCommand(NetworkMotionCommand command)
        {
            Vector3 targetPosition = command.GetPosition();
            Vector3 currentPosition = this.Character.transform.position;
            
            // Check distance
            float distance = Vector3.Distance(currentPosition, targetPosition);
            if (distance > m_MaxTeleportDistance)
            {
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_TOO_FAR);
            }
            
            // Could add additional validation:
            // - Check if target position is valid (not inside geometry)
            // - Check if player has teleport ability/cooldown
            // - Check line of sight requirements
            
            return NetworkMotionResult.Approved(command.sequenceNumber);
        }

        private bool ShouldSendMoveDirectionCommand(Vector3 velocity, Space space, int priority)
        {
            float now = Time.unscaledTime;
            const float minimumInterval = MOVE_DIRECTION_MINIMUM_SEND_INTERVAL;
            const float heartbeatInterval = MOVE_DIRECTION_HEARTBEAT_INTERVAL;
            const float changeThreshold = MOVE_DIRECTION_CHANGE_THRESHOLD;

            if (!m_HasSentMoveDirection)
            {
                MarkMoveDirectionCommandSent(velocity, space, priority, now);
                return true;
            }

            float elapsed = now - m_LastMoveDirectionSentAt;
            bool isStopped = velocity.sqrMagnitude <= 0.0001f;
            bool wasStopped = m_LastSentMoveDirectionVelocity.sqrMagnitude <= 0.0001f;
            bool startOrStop = isStopped != wasStopped;
            bool contextChanged = space != m_LastSentMoveDirectionSpace ||
                priority != m_LastSentMoveDirectionPriority;
            bool velocityChanged = (velocity - m_LastSentMoveDirectionVelocity).sqrMagnitude >=
                changeThreshold * changeThreshold;

            if (startOrStop || contextChanged)
            {
                MarkMoveDirectionCommandSent(velocity, space, priority, now);
                return true;
            }

            if (elapsed < minimumInterval)
            {
                return false;
            }

            if (isStopped)
            {
                return false;
            }

            if (velocityChanged || elapsed >= heartbeatInterval)
            {
                MarkMoveDirectionCommandSent(velocity, space, priority, now);
                return true;
            }

            return false;
        }

        private void MarkMoveDirectionCommandSent(Vector3 velocity, Space space, int priority, float timestamp)
        {
            m_HasSentMoveDirection = true;
            m_LastSentMoveDirectionVelocity = velocity;
            m_LastSentMoveDirectionSpace = space;
            m_LastSentMoveDirectionPriority = priority;
            m_LastMoveDirectionSentAt = timestamp;
        }

        private NetworkMotionResult ValidateMoveDirectionCommand(NetworkMotionCommand command)
        {
            Vector3 velocity = command.GetVelocity();
            
            // Check if speed is within allowed limits
            float speedMagnitude = velocity.magnitude;
            if (speedMagnitude > m_Speed * 1.5f) // Allow 50% margin for network variance
            {
                // Clamp and approve with correction
                Vector3 corrected = velocity.normalized * m_Speed;
                return NetworkMotionResult.ApprovedWithCorrection(command.sequenceNumber, corrected);
            }
            
            return NetworkMotionResult.Approved(command.sequenceNumber);
        }

        private NetworkMotionResult ValidateMovePositionCommand(NetworkMotionCommand command)
        {
            // MoveToPosition is generally safe as the server controls actual movement
            return NetworkMotionResult.Approved(command.sequenceNumber);
        }

        private NetworkMotionResult ValidateFollowTargetCommand(NetworkMotionCommand command)
        {
            if (command.GetFollowMaxRadius() < command.GetFollowMinRadius())
            {
                return NetworkMotionResult.Rejected(command.sequenceNumber,
                    NetworkMotionResult.REJECT_INVALID_POSITION);
            }

            return NetworkMotionResult.Approved(command.sequenceNumber);
        }

        private NetworkMotionResult ValidateJumpCommand(NetworkMotionCommand command)
        {
            // Check if can jump
            if (!m_Jump.CanJump && command.commandType != NetworkMotionCommandType.ForceJump)
            {
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
            }
            
            // Validate jump force
            float requestedForce = command.GetJumpForce();
            if (requestedForce > m_Jump.JumpForce * 1.5f)
            {
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
            }
            
            return NetworkMotionResult.Approved(command.sequenceNumber);
        }

        // LOCAL APPLICATION: ---------------------------------------------------------------------

        private void ApplyFollowCommandLocally(NetworkMotionCommand command, bool driveExplicitly)
        {
            Transform target = ResolveFollowTarget(command.targetNetworkId);
            Vector3 fallbackPosition = command.GetPosition();
            float minRadius = command.GetFollowMinRadius();
            float maxRadius = Mathf.Max(minRadius, command.GetFollowMaxRadius());

            LogNavigation(
                $"apply Follow locally seq={command.sequenceNumber} driveExplicitly={driveExplicitly} " +
                $"resolvedTarget={(target != null ? target.name : "null")} targetId={command.targetNetworkId} " +
                $"fallback={FormatVector(fallbackPosition)} min={minRadius:F2} max={maxRadius:F2} " +
                $"driver={FormatDriver()}");

            StopNavigationRoutine();

            if (target != null)
            {
                base.StartFollowingTarget(target, minRadius, maxRadius, command.priority);
            }
            else
            {
                base.MoveToLocation(new Location(fallbackPosition), minRadius, null, command.priority);
            }

            if (!driveExplicitly) return;

            StartFollowTargetRoutine(
                target,
                command.targetNetworkId,
                fallbackPosition,
                minRadius,
                maxRadius);
        }

        private void StartMoveToPositionRoutine(Vector3 targetPosition, float stopDistance)
        {
            if (!GetNavigationDriveDecision(out string reason))
            {
                LogNavigation(
                    $"MoveTo explicit drive not started: {reason} target={FormatVector(targetPosition)} " +
                    $"stop={stopDistance:F2} {FormatNetworkContext()}");
                return;
            }

            if (this.Character == null || !this.Character.gameObject.activeInHierarchy)
            {
                LogNavigation(
                    $"MoveTo explicit drive not started: inactive or missing Character " +
                    $"target={FormatVector(targetPosition)} stop={stopDistance:F2}");
                return;
            }

            LogNavigation(
                $"MoveTo explicit drive started target={FormatVector(targetPosition)} " +
                $"stop={stopDistance:F2} driver={FormatDriver()} currentFeet={FormatVector(this.Character.Feet)}");

            m_NavigationRoutine = this.Character.StartCoroutine(
                DriveMoveToPositionRoutine(targetPosition, Mathf.Max(0.01f, stopDistance)));
        }

        private void StartFollowTargetRoutine(Transform target, uint targetNetworkId,
            Vector3 fallbackPosition, float minRadius, float maxRadius)
        {
            if (!GetNavigationDriveDecision(out string reason))
            {
                LogNavigation(
                    $"Follow explicit drive not started: {reason} targetId={targetNetworkId} " +
                    $"target={(target != null ? target.name : "null")} fallback={FormatVector(fallbackPosition)} " +
                    $"min={minRadius:F2} max={maxRadius:F2} {FormatNetworkContext()}");
                return;
            }

            if (this.Character == null || !this.Character.gameObject.activeInHierarchy)
            {
                LogNavigation(
                    $"Follow explicit drive not started: inactive or missing Character " +
                    $"targetId={targetNetworkId} target={(target != null ? target.name : "null")}");
                return;
            }

            LogNavigation(
                $"Follow explicit drive started targetId={targetNetworkId} " +
                $"target={(target != null ? target.name : "null")} fallback={FormatVector(fallbackPosition)} " +
                $"min={minRadius:F2} max={maxRadius:F2} driver={FormatDriver()} " +
                $"currentFeet={FormatVector(this.Character.Feet)}");

            m_NavigationRoutine = this.Character.StartCoroutine(
                DriveFollowTargetRoutine(
                    target,
                    targetNetworkId,
                    fallbackPosition,
                    Mathf.Max(0f, minRadius),
                    Mathf.Max(minRadius, maxRadius)));
        }

        private void StopNavigationRoutine()
        {
            if (m_NavigationRoutine == null) return;

            LogNavigation($"stopping explicit navigation routine driver={FormatDriver()} {FormatNetworkContext()}");

            if (this.Character != null)
            {
                this.Character.StopCoroutine(m_NavigationRoutine);
            }

            m_NavigationRoutine = null;
            SetDriverExternalMoveDirection(Vector3.zero);
        }

        private void PublishExplicitNavigationVelocityForAnimation()
        {
            if (m_NavigationRoutine == null) return;
            if (this.Character?.Driver == null) return;

            Vector3 direction = this.MoveDirection;
            direction.y = 0f;

            if (direction.sqrMagnitude <= 0.0001f)
            {
                SetDriverExternalMoveDirection(Vector3.zero);
                return;
            }

            SetDriverExternalMoveDirection(direction.normalized * ResolveNavigationSpeed());
        }

        private void SetDriverExternalMoveDirection(Vector3 velocity)
        {
            switch (this.Character?.Driver)
            {
                case UnitDriverNetworkClient clientDriver:
                    clientDriver.SetExternalMoveDirection(velocity);
                    break;

                case UnitDriverNetworkServer serverDriver:
                    serverDriver.SetExternalMoveDirection(velocity);
                    break;
            }
        }

        private bool GetNavigationDriveDecision(out string reason)
        {
            if (this.Character?.Driver == null)
            {
                reason = "false: missing Character.Driver";
                return false;
            }

            if (this.Character.Driver is UnitDriverNetworkRemote)
            {
                reason = $"false: remote interpolation driver ({FormatDriver()})";
                return false;
            }

            if (this.Character.Driver is UnitDriverNetworkServer ||
                this.Character.Driver is UnitDriverNetworkClient)
            {
                reason = $"true: supported network controller driver ({FormatDriver()})";
                return true;
            }

            reason = $"false: unsupported driver ({FormatDriver()})";
            return false;
        }

        private System.Collections.IEnumerator DriveMoveToPositionRoutine(
            Vector3 targetPosition,
            float stopDistance)
        {
            int steps = 0;
            string exitReason = "not started";

            while (true)
            {
                bool shouldContinue = TryCreateNavigationStep(
                    targetPosition,
                    stopDistance,
                    out Vector3 step,
                    out Vector3 direction,
                    out string stepReason);

                if (!shouldContinue)
                {
                    exitReason = stepReason;
                    break;
                }

                if (step.sqrMagnitude > 0.0000001f)
                {
                    Vector3 before = this.Character.transform.position;
                    this.Character.Driver.AddPosition(step);
                    Vector3 after = this.Character.transform.position;
                    Vector3 actual = after - before;
                    steps++;

                    if (actual.sqrMagnitude <= 0.0000001f)
                    {
                        LogNavigationThrottled(
                            $"MoveTo AddPosition produced no transform delta requested={FormatVector(step)} " +
                            $"target={FormatVector(targetPosition)} feet={FormatVector(this.Character.Feet)} " +
                            $"driver={FormatDriver()} reason={stepReason}");
                    }
                    else
                    {
                        LogNavigationThrottled(
                            $"MoveTo step requested={FormatVector(step)} actual={FormatVector(actual)} " +
                            $"target={FormatVector(targetPosition)} feet={FormatVector(this.Character.Feet)} " +
                            $"moveDirection={FormatVector(this.MoveDirection)} driver={FormatDriver()}");
                    }
                }
                else
                {
                    LogNavigationThrottled(
                        $"MoveTo waiting without step reason={stepReason} target={FormatVector(targetPosition)} " +
                        $"feet={FormatVector(this.Character.Feet)} moveDirection={FormatVector(this.MoveDirection)} " +
                        $"speed={ResolveNavigationSpeed():F2} driver={FormatDriver()}");
                }

                RotateTowardsNavigation(direction);
                yield return null;
            }

            LogNavigation(
                $"MoveTo explicit drive ended reason={exitReason} steps={steps} " +
                $"target={FormatVector(targetPosition)} finalFeet={FormatVector(this.Character?.Feet ?? Vector3.zero)} " +
                $"driver={FormatDriver()}");

            m_NavigationRoutine = null;
            SetDriverExternalMoveDirection(Vector3.zero);
        }

        private System.Collections.IEnumerator DriveFollowTargetRoutine(
            Transform target,
            uint targetNetworkId,
            Vector3 fallbackPosition,
            float minRadius,
            float maxRadius)
        {
            bool isFollowing = true;
            int steps = 0;
            string exitReason = "not started";

            while (this.Character?.Driver != null && !this.Character.IsDead)
            {
                if (target == null && targetNetworkId != 0)
                {
                    target = ResolveFollowTarget(targetNetworkId);
                }

                Vector3 targetPosition = target != null ? target.position : fallbackPosition;
                float distance = HorizontalDistance(this.Character.Feet, targetPosition);

                if (isFollowing && distance <= minRadius)
                {
                    isFollowing = false;
                }
                else if (!isFollowing && distance > maxRadius)
                {
                    isFollowing = true;
                }

                Vector3 lookDirection = targetPosition - this.Character.Feet;
                lookDirection.y = 0f;
                if (lookDirection.sqrMagnitude > 0.0001f)
                {
                    RotateTowardsNavigation(lookDirection.normalized);
                }

                if (isFollowing &&
                    TryCreateNavigationStep(
                        targetPosition,
                        minRadius,
                        out Vector3 step,
                        out Vector3 direction,
                        out string stepReason))
                {
                    if (step.sqrMagnitude > 0.0000001f)
                    {
                        Vector3 before = this.Character.transform.position;
                        this.Character.Driver.AddPosition(step);
                        Vector3 after = this.Character.transform.position;
                        Vector3 actual = after - before;
                        steps++;

                        if (actual.sqrMagnitude <= 0.0000001f)
                        {
                            LogNavigationThrottled(
                                $"Follow AddPosition produced no transform delta requested={FormatVector(step)} " +
                                $"target={FormatVector(targetPosition)} feet={FormatVector(this.Character.Feet)} " +
                                $"targetId={targetNetworkId} driver={FormatDriver()} reason={stepReason}");
                        }
                        else
                        {
                            LogNavigationThrottled(
                                $"Follow step requested={FormatVector(step)} actual={FormatVector(actual)} " +
                                $"target={FormatVector(targetPosition)} feet={FormatVector(this.Character.Feet)} " +
                                $"distance={distance:F2} targetId={targetNetworkId} driver={FormatDriver()}");
                        }
                    }
                    else
                    {
                        LogNavigationThrottled(
                            $"Follow waiting without step reason={stepReason} target={FormatVector(targetPosition)} " +
                            $"feet={FormatVector(this.Character.Feet)} distance={distance:F2} " +
                            $"moveDirection={FormatVector(this.MoveDirection)} driver={FormatDriver()}");
                    }

                    RotateTowardsNavigation(direction);
                }
                else if (target == null && targetNetworkId == 0)
                {
                    exitReason = "static fallback target reached or unavailable";
                    break;
                }
                else if (!isFollowing)
                {
                    LogNavigationThrottled(
                        $"Follow holding distance={distance:F2} min={minRadius:F2} max={maxRadius:F2} " +
                        $"target={FormatVector(targetPosition)} feet={FormatVector(this.Character.Feet)} " +
                        $"driver={FormatDriver()}");
                }

                yield return null;
            }

            if (this.Character?.Driver == null)
            {
                exitReason = "missing driver";
            }
            else if (this.Character.IsDead)
            {
                exitReason = "character dead";
            }

            LogNavigation(
                $"Follow explicit drive ended reason={exitReason} steps={steps} targetId={targetNetworkId} " +
                $"finalFeet={FormatVector(this.Character?.Feet ?? Vector3.zero)} driver={FormatDriver()}");

            m_NavigationRoutine = null;
            SetDriverExternalMoveDirection(Vector3.zero);
        }

        private bool TryCreateNavigationStep(Vector3 targetPosition, float stopDistance,
            out Vector3 step, out Vector3 direction, out string reason)
        {
            step = Vector3.zero;
            direction = Vector3.zero;
            reason = "none";

            if (this.Character?.Driver == null)
            {
                reason = "missing Character.Driver";
                return false;
            }

            if (this.Character.IsDead)
            {
                reason = "character is dead";
                return false;
            }

            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                reason = "waiting: Time.deltaTime <= 0";
                return true;
            }

            Vector3 source = this.Character.Feet;
            Vector3 toTarget = targetPosition - source;
            toTarget.y = 0f;

            float distance = toTarget.magnitude;
            if (distance <= stopDistance)
            {
                reason = $"reached: distance={distance:F2} stop={stopDistance:F2}";
                return false;
            }

            direction = ResolveNavigationDirection(toTarget);
            if (direction.sqrMagnitude <= 0.0001f)
            {
                reason = $"waiting: zero direction distance={distance:F2}";
                return true;
            }

            float speed = ResolveNavigationSpeed();
            if (speed <= 0f)
            {
                reason = $"waiting: speed={speed:F2} linearSpeed={this.LinearSpeed:F2}";
                return true;
            }

            float maxStep = Mathf.Max(0f, distance - stopDistance);
            step = direction.normalized * Mathf.Min(speed * dt, maxStep);
            reason = $"moving: distance={distance:F2} stop={stopDistance:F2} speed={speed:F2} dt={dt:F3}";
            return true;
        }

        private Vector3 ResolveNavigationDirection(Vector3 toTarget)
        {
            Vector3 motionDirection = this.MoveDirection;
            motionDirection.y = 0f;

            return motionDirection.sqrMagnitude > 0.0001f
                ? motionDirection.normalized
                : toTarget.normalized;
        }

        private float ResolveNavigationSpeed()
        {
            return Mathf.Max(0f, this.LinearSpeed);
        }

        private void RotateTowardsNavigation(Vector3 direction)
        {
            if (this.Character?.Driver == null) return;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f) return;

            Quaternion targetRotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
            float angularSpeed = this.AngularSpeed;
            Quaternion rotation = angularSpeed >= 0f
                ? Quaternion.RotateTowards(
                    this.Character.transform.rotation,
                    targetRotation,
                    angularSpeed * Time.deltaTime)
                : targetRotation;

            this.Character.Driver.SetRotation(rotation);
        }

        private static float HorizontalDistance(Vector3 source, Vector3 target)
        {
            source.y = 0f;
            target.y = 0f;
            return Vector3.Distance(source, target);
        }

        private static bool TryGetNetworkCharacterId(Transform target, out uint networkId)
        {
            networkId = 0;
            if (target == null) return false;

            NetworkCharacter networkCharacter = target.GetComponentInParent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return false;

            networkId = networkCharacter.NetworkId;
            return true;
        }

        private static Transform ResolveFollowTarget(uint targetNetworkId)
        {
            if (targetNetworkId == 0 || !NetworkTransportBridge.HasActive) return null;

            Character targetCharacter = NetworkTransportBridge.Active.ResolveCharacter(targetNetworkId);
            return targetCharacter != null ? targetCharacter.transform : null;
        }

        private void MarkOwnerPredictedCommand(ushort sequence)
        {
            NetworkCharacter networkCharacter = this.Character != null
                ? this.Character.GetComponent<NetworkCharacter>()
                : null;

            if (networkCharacter == null || !networkCharacter.IsOwnerInstance) return;

            m_PredictedCommandSequences.Add(sequence);
        }

        private void ApplyDashLocally(NetworkMotionCommand command)
        {
            Vector3 direction = command.GetDirection();
            float speed = command.GetSpeed();
            float duration = command.GetDuration();
            float fade = command.GetFade();
            float gravity = command.GetGravity();

            // On a RemoteClient the active driver is UnitDriverNetworkRemote which
            // owns the transform via interpolated server snapshots. Calling
            // Character.Dash.Execute there has no effect on position because the
            // remote driver's AddPosition is a no-op and OnUpdate overwrites the
            // transform every frame from the snapshot buffer. Instead, inject
            // forward-projected snapshots that match the dash trajectory so the
            // remote representation moves at full fidelity for the duration.
            var networkCharacter = this.Character != null
                ? this.Character.GetComponent<NetworkCharacter>()
                : null;

            if (networkCharacter != null && networkCharacter.CurrentRole == NetworkCharacter.NetworkRole.RemoteClient)
            {
                var remoteDriver = networkCharacter.RemoteDriver;
                if (remoteDriver != null)
                {
                    LogDash(
                        $"BeginPredictedDash (remote) seq={command.sequenceNumber} character={this.Character.name} " +
                        $"dir={FormatVector(direction)} speed={speed:F2} gravity={gravity:F2} " +
                        $"duration={duration:F2} fade={fade:F2}");

                    remoteDriver.BeginPredictedDash(direction, speed, duration, gravity);

                    // Mark legs busy so subsequent commands honor the dash window.
                    // Schedule release because BeginPredictedDash bypasses
                    // Character.Dash.Execute's OnDashFinish (which is what
                    // normally calls RemoveLegsBusy). Without this, legs stay
                    // busy forever and subsequent gameplay (e.g. Block.RaiseGuard,
                    // server-side ProcessBlockRequest) sees Busy.IsBusy=true and
                    // rejects.
                    if (this.Character?.Busy != null && !this.Character.Busy.AreLegsBusy)
                    {
                        this.Character.Busy.MakeLegsBusy();
                        this.Character.StartCoroutine(ClearLegsBusyAfter(duration));
                    }

                    BumpDashCooldown();
                    return;
                }
            }

            // On the host viewing a non-locally-owned character (role=Server,
            // not owner) Character.Dash.Execute relies on Character internals
            // that may not run for a server-only proxy and the client's input
            // stream is concurrently moving the character through the server
            // driver. Drive the dash explicitly via the Character.Driver's
            // AddPosition each frame so the host's authoritative copy of the
            // dashing character actually translates and broadcasts the
            // resulting position to other clients.
            bool isServerProxyForRemoteOwner =
                networkCharacter != null &&
                networkCharacter.CurrentRole == NetworkCharacter.NetworkRole.Server &&
                !networkCharacter.IsOwnerInstance;

            if (isServerProxyForRemoteOwner)
            {
                LogDash(
                    $"server manual dash drive seq={command.sequenceNumber} character={this.Character.name} " +
                    $"dir={FormatVector(direction)} speed={speed:F2} gravity={gravity:F2} " +
                    $"duration={duration:F2} fade={fade:F2}");

                if (this.Character?.Busy != null && !this.Character.Busy.AreLegsBusy)
                {
                    this.Character.Busy.MakeLegsBusy();
                }

                this.Character.StartCoroutine(DriveServerDashRoutine(direction, speed, duration, releaseLegsBusy: true));
                BumpDashCooldown();
                return;
            }

            if (this.Character?.Dash != null)
            {
                LogDash(
                    $"Character.Dash.Execute seq={command.sequenceNumber} character={this.Character.name} " +
                    $"dir={FormatVector(direction)} speed={speed:F2} gravity={gravity:F2} " +
                    $"duration={duration:F2} fade={fade:F2} legsBusy={this.Character.Busy.AreLegsBusy}");

                if (!this.Character.Busy.AreLegsBusy)
                {
                    this.Character.Busy.MakeLegsBusy();
                }

                _ = this.Character.Dash.Execute(direction, speed, gravity, duration, fade);
            }
            else
            {
                LogDash(
                    $"SetMotionTransient fallback seq={command.sequenceNumber}: Character.Dash missing " +
                    $"dir={FormatVector(direction)} speed={speed:F2} duration={duration:F2} fade={fade:F2}");
                base.SetMotionTransient(direction, speed, duration, fade);
            }

            BumpDashCooldown();
        }

        private void BumpDashCooldown()
        {
            float now = Time.time;
            if (now - m_LastDashTime > m_Dash.Cooldown)
            {
                m_DashesUsed = 0;
            }
            m_DashesUsed++;
            m_LastDashTime = now;
        }

        private System.Collections.IEnumerator DriveServerDashRoutine(Vector3 direction, float speed, float duration, bool releaseLegsBusy = false)
        {
            if (direction.sqrMagnitude <= 0f)
            {
                if (releaseLegsBusy && this.Character?.Busy != null && this.Character.Busy.AreLegsBusy)
                {
                    this.Character.Busy.RemoveLegsBusy();
                }
                yield break;
            }
            Vector3 worldDirection = direction.normalized;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (this.Character == null || this.Character.Driver == null)
                {
                    if (releaseLegsBusy && this.Character?.Busy != null && this.Character.Busy.AreLegsBusy)
                    {
                        this.Character.Busy.RemoveLegsBusy();
                    }
                    yield break;
                }

                float dt = Time.deltaTime;
                if (dt <= 0f) { yield return null; continue; }

                Vector3 step = worldDirection * speed * dt;
                this.Character.Driver.AddPosition(step);

                elapsed += dt;
                yield return null;
            }

            // Mirror Character.Dash.Execute's OnDashFinish behavior for code
            // paths that bypass it (server-proxy of a remote owner). Without
            // this, Busy.AreLegsBusy stays true on the server forever and the
            // server rejects subsequent block requests with CharacterBusy.
            if (releaseLegsBusy && this.Character?.Busy != null && this.Character.Busy.AreLegsBusy)
            {
                this.Character.Busy.RemoveLegsBusy();
            }
        }

        private System.Collections.IEnumerator ClearLegsBusyAfter(float delaySeconds)
        {
            if (delaySeconds > 0f) yield return new UnityEngine.WaitForSeconds(delaySeconds);
            if (this.Character?.Busy != null && this.Character.Busy.AreLegsBusy)
            {
                this.Character.Busy.RemoveLegsBusy();
            }
        }

        private void LogDash(string message)
        {
            if (!m_LogDashDiagnostics) return;
            string characterName = this.Character != null ? this.Character.name : "NoCharacter";
            Debug.Log($"[NetworkDashDebug][MotionController] {characterName}: {message}");
        }

        private void LogNavigation(string message)
        {
            if (!m_LogNavigationDiagnostics) return;
            string characterName = this.Character != null ? this.Character.name : "NoCharacter";
            Debug.Log($"[NetworkNavigationDebug][MotionController] {characterName}: {message}");
        }

        private void LogNavigationThrottled(string message)
        {
            if (!m_LogNavigationDiagnostics) return;

            float interval = Mathf.Max(0.05f, m_NavigationDiagnosticsInterval);
            if (Time.time - m_LastNavigationDiagnosticTime < interval) return;

            m_LastNavigationDiagnosticTime = Time.time;
            LogNavigation(message);
        }

        private static bool IsNavigationCommand(NetworkMotionCommandType commandType)
        {
            return commandType == NetworkMotionCommandType.MoveToPosition ||
                   commandType == NetworkMotionCommandType.FollowTarget ||
                   commandType == NetworkMotionCommandType.StopFollow;
        }

        private string FormatNavigationCommand(NetworkMotionCommand command)
        {
            return command.commandType switch
            {
                NetworkMotionCommandType.MoveToPosition =>
                    $"MoveTo seq={command.sequenceNumber} target={FormatVector(command.GetPosition())} " +
                    $"stop={command.GetStopDistance():F2} priority={command.priority}",
                NetworkMotionCommandType.FollowTarget =>
                    $"Follow seq={command.sequenceNumber} targetId={command.targetNetworkId} " +
                    $"fallback={FormatVector(command.GetPosition())} min={command.GetFollowMinRadius():F2} " +
                    $"max={command.GetFollowMaxRadius():F2} priority={command.priority}",
                NetworkMotionCommandType.StopFollow =>
                    $"StopFollow seq={command.sequenceNumber} priority={command.priority}",
                _ => $"{command.commandType} seq={command.sequenceNumber}"
            };
        }

        private string FormatDriver()
        {
            return this.Character?.Driver != null
                ? this.Character.Driver.GetType().Name
                : "null";
        }

        private string FormatNetworkContext()
        {
            NetworkCharacter networkCharacter = this.Character != null
                ? this.Character.GetComponent<NetworkCharacter>()
                : null;

            return networkCharacter != null
                ? $"netId={networkCharacter.NetworkId} role={networkCharacter.CurrentRole} owner={networkCharacter.IsOwnerInstance}"
                : "netId=0 role=NoNetworkCharacter owner=false";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private void ApplyTeleportLocally(NetworkMotionCommand command)
        {
            Vector3 position = command.GetPosition();
            float rotationY = command.GetRotationY();
            
            this.Character.Driver.SetPosition(position, true);
            this.Character.Driver.SetRotation(Quaternion.Euler(0f, rotationY, 0f));
        }

        private uint ResolveActorNetworkId()
        {
            if (this.Character == null) return 0;

            var networkCharacter = this.Character.GetComponent<NetworkCharacter>();
            if (networkCharacter == null) return 0;
            return networkCharacter.NetworkId;
        }

        private void EnsureRuntimeStateInitialized()
        {
            m_PendingCommands ??= new Queue<NetworkMotionCommand>(16);
            m_PendingCallbacks ??= new Dictionary<ushort, PendingCallbackEntry>(16);
            m_PendingCallbackRemovalBuffer ??= new List<ushort>(8);
            m_PredictedCommandSequences ??= new HashSet<ushort>();
        }

        private ushort NextCommandSequence(bool reservePendingSlot)
        {
            EnsureRuntimeStateInitialized();
            PruneExpiredPendingCallbacks(Time.time);

            const int maxAttempts = ushort.MaxValue;
            for (int i = 0; i < maxAttempts; i++)
            {
                ushort sequence = m_CommandSequence;
                m_CommandSequence++;

                if (sequence == 0)
                {
                    continue;
                }

                if (!reservePendingSlot || !m_PendingCallbacks.ContainsKey(sequence))
                {
                    return sequence;
                }
            }

            if (reservePendingSlot)
            {
                EvictOldestPendingCallback();

                for (int i = 0; i < maxAttempts; i++)
                {
                    ushort sequence = m_CommandSequence;
                    m_CommandSequence++;

                    if (sequence == 0) continue;
                    if (!m_PendingCallbacks.ContainsKey(sequence))
                    {
                        return sequence;
                    }
                }
            }

            return 1;
        }

        private void RegisterPendingCallback(ushort sequence, Action<NetworkMotionResult> callback)
        {
            if (callback == null) return;

            EnsureRuntimeStateInitialized();
            PruneExpiredPendingCallbacks(Time.time);

            int maxPending = Mathf.Max(1, m_MaxPendingCallbacks);
            if (m_PendingCallbacks.Count >= maxPending)
            {
                EvictOldestPendingCallback();
            }

            m_PendingCallbacks[sequence] = new PendingCallbackEntry
            {
                Callback = callback,
                CreatedAt = Time.time
            };
        }

        private void PruneExpiredPendingCallbacks(float now)
        {
            EnsureRuntimeStateInitialized();
            if (m_PendingCallbacks.Count == 0) return;

            float timeout = Mathf.Max(0.25f, m_PendingCallbackTimeoutSeconds);
            m_PendingCallbackRemovalBuffer.Clear();

            foreach (KeyValuePair<ushort, PendingCallbackEntry> entry in m_PendingCallbacks)
            {
                if (now - entry.Value.CreatedAt >= timeout)
                {
                    m_PendingCallbackRemovalBuffer.Add(entry.Key);
                }
            }

            for (int i = 0; i < m_PendingCallbackRemovalBuffer.Count; i++)
            {
                ushort sequence = m_PendingCallbackRemovalBuffer[i];
                if (!m_PendingCallbacks.TryGetValue(sequence, out PendingCallbackEntry pending))
                {
                    continue;
                }

                m_PendingCallbacks.Remove(sequence);
                pending.Callback?.Invoke(NetworkMotionResult.Rejected(sequence, NetworkMotionResult.REJECT_TIMEOUT));
            }
        }

        private void EvictOldestPendingCallback()
        {
            EnsureRuntimeStateInitialized();
            if (m_PendingCallbacks.Count == 0) return;

            ushort oldestSequence = 0;
            float oldestTime = float.MaxValue;
            bool found = false;

            foreach (KeyValuePair<ushort, PendingCallbackEntry> entry in m_PendingCallbacks)
            {
                if (!found || entry.Value.CreatedAt < oldestTime)
                {
                    found = true;
                    oldestSequence = entry.Key;
                    oldestTime = entry.Value.CreatedAt;
                }
            }

            if (!found) return;

            if (m_PendingCallbacks.TryGetValue(oldestSequence, out PendingCallbackEntry pending))
            {
                m_PendingCallbacks.Remove(oldestSequence);
                pending.Callback?.Invoke(NetworkMotionResult.Rejected(oldestSequence, NetworkMotionResult.REJECT_TIMEOUT));
            }
        }
    }
}
