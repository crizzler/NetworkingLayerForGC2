using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

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

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private byte m_ConfigVersion;
        [NonSerialized] private NetworkMotionConfig m_LastSentConfig;
        [NonSerialized] private Queue<NetworkMotionCommand> m_PendingCommands;
        [NonSerialized] private ushort m_CommandSequence;
        [NonSerialized] private float m_LastDashTime;
        [NonSerialized] private int m_DashesUsed;
        [NonSerialized] private Dictionary<ushort, Action<NetworkMotionResult>> m_PendingCallbacks;

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
            this.m_PendingCallbacks = new Dictionary<ushort, Action<NetworkMotionResult>>(16);
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            this.m_ConfigVersion = 0;
            this.m_CommandSequence = 0;
            this.m_LastDashTime = -1000f;
            this.m_DashesUsed = 0;
            
            // Send initial config
            SendConfigUpdate();
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
            var command = NetworkMotionCommand.CreateDash(
                direction.normalized, speed, duration, fade, m_CommandSequence);
            
            if (callback != null)
            {
                m_PendingCallbacks[m_CommandSequence] = callback;
            }
            
            m_CommandSequence++;
            
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
                OnSendCommand?.Invoke(command);
            }
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
            
            var command = NetworkMotionCommand.CreateTeleport(position, rotation, m_CommandSequence);
            
            if (callback != null)
            {
                m_PendingCallbacks[m_CommandSequence] = callback;
            }
            
            m_CommandSequence++;
            
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
                // Server applies directly
                base.MoveToDirection(velocity, space, priority);
                
                // Broadcast to clients
                var command = NetworkMotionCommand.CreateMoveToDirection(
                    velocity, space == Space.World, priority, m_CommandSequence++);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                // Client sends to server
                var command = NetworkMotionCommand.CreateMoveToDirection(
                    velocity, space == Space.World, priority, m_CommandSequence++);
                OnSendCommand?.Invoke(command);
                
                // Optionally apply locally for prediction (commented out for strict server-authority)
                // base.MoveToDirection(velocity, space, priority);
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
            
            if (m_IsServer)
            {
                base.MoveToLocation(location, stopDistance, onFinish, priority);
                
                var command = NetworkMotionCommand.CreateMoveToPosition(
                    targetPosition, stopDistance, priority, m_CommandSequence++);
                OnBroadcastCommand?.Invoke(command);
            }
            else
            {
                var command = NetworkMotionCommand.CreateMoveToPosition(
                    targetPosition, stopDistance, priority, m_CommandSequence++);
                OnSendCommand?.Invoke(command);
                
                // Store callback for when server confirms
                // Note: Full implementation would need to track pending move callbacks
            }
        }

        // SERVER-SIDE VALIDATION: ----------------------------------------------------------------

        /// <summary>
        /// Process a command received from a client (server-side only).
        /// </summary>
        public NetworkMotionResult ProcessClientCommand(NetworkMotionCommand command)
        {
            if (!m_IsServer)
            {
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
            }

            NetworkMotionResult result;

            switch (command.commandType)
            {
                case NetworkMotionCommandType.Dash:
                    result = ValidateDashCommand(command);
                    if (result.approved)
                    {
                        ApplyDashLocally(command);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    break;

                case NetworkMotionCommandType.Teleport:
                    result = ValidateTeleportCommand(command);
                    if (result.approved)
                    {
                        ApplyTeleportLocally(command);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    break;

                case NetworkMotionCommandType.MoveToDirection:
                    result = ValidateMoveDirectionCommand(command);
                    if (result.approved)
                    {
                        Vector3 velocity = command.GetVelocity();
                        Space space = command.IsWorldSpace() ? Space.World : Space.Self;
                        base.MoveToDirection(velocity, space, command.priority);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    break;

                case NetworkMotionCommandType.MoveToPosition:
                    result = ValidateMovePositionCommand(command);
                    if (result.approved)
                    {
                        Vector3 position = command.GetPosition();
                        float stopDist = command.GetStopDistance();
                        Location loc = new Location(position);
                        base.MoveToLocation(loc, stopDist, null, command.priority);
                        OnBroadcastCommand?.Invoke(command);
                    }
                    break;

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
                    break;

                case NetworkMotionCommandType.StopDirection:
                    result = NetworkMotionResult.Approved(command.sequenceNumber);
                    base.StopToDirection(command.priority);
                    OnBroadcastCommand?.Invoke(command);
                    break;

                default:
                    result = NetworkMotionResult.Rejected(command.sequenceNumber, 
                        NetworkMotionResult.REJECT_NOT_ALLOWED);
                    break;
            }

            OnSendResult?.Invoke(result);
            return result;
        }

        /// <summary>
        /// Apply a server-broadcast command locally (client-side).
        /// </summary>
        public void ApplyBroadcastCommand(NetworkMotionCommand command)
        {
            switch (command.commandType)
            {
                case NetworkMotionCommandType.Dash:
                    ApplyDashLocally(command);
                    break;

                case NetworkMotionCommandType.Teleport:
                    ApplyTeleportLocally(command);
                    break;

                case NetworkMotionCommandType.MoveToDirection:
                    Vector3 velocity = command.GetVelocity();
                    Space space = command.IsWorldSpace() ? Space.World : Space.Self;
                    base.MoveToDirection(velocity, space, command.priority);
                    break;

                case NetworkMotionCommandType.MoveToPosition:
                    Vector3 position = command.GetPosition();
                    float stopDist = command.GetStopDistance();
                    Location loc = new Location(position);
                    base.MoveToLocation(loc, stopDist, null, command.priority);
                    break;

                case NetworkMotionCommandType.Jump:
                    base.Jump(command.GetJumpForce());
                    break;

                case NetworkMotionCommandType.ForceJump:
                    base.ForceJump(command.GetJumpForce());
                    break;

                case NetworkMotionCommandType.StopDirection:
                    base.StopToDirection(command.priority);
                    break;
            }
        }

        /// <summary>
        /// Handle a command result from the server (client-side).
        /// </summary>
        public void HandleCommandResult(NetworkMotionResult result)
        {
            if (m_PendingCallbacks.TryGetValue(result.commandSequence, out var callback))
            {
                callback?.Invoke(result);
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
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_COOLDOWN);
            }
            
            // Check if in air when not allowed
            if (!m_Dash.DashInAir && !this.Character.Driver.IsGrounded)
            {
                return NetworkMotionResult.Rejected(command.sequenceNumber, 
                    NetworkMotionResult.REJECT_NOT_ALLOWED);
            }
            
            // Check dash distance
            float dashDistance = command.GetSpeed() * command.GetDuration();
            if (dashDistance > m_MaxDashDistance)
            {
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

        private void ApplyDashLocally(NetworkMotionCommand command)
        {
            Vector3 direction = command.GetDirection();
            float speed = command.GetSpeed();
            float duration = command.GetDuration();
            float fade = command.GetFade();
            
            base.SetMotionTransient(direction, speed, duration, fade);
            
            m_LastDashTime = Time.time;
            m_DashesUsed++;
            
            // Reset dash counter if cooldown passed
            if (Time.time - m_LastDashTime > m_Dash.Cooldown)
            {
                m_DashesUsed = 1;
            }
        }

        private void ApplyTeleportLocally(NetworkMotionCommand command)
        {
            Vector3 position = command.GetPosition();
            float rotationY = command.GetRotationY();
            
            this.Character.Driver.SetPosition(position, true);
            this.Character.Driver.SetRotation(Quaternion.Euler(0f, rotationY, 0f));
        }
    }
}
