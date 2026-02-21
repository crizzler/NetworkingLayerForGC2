using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-optimized animation parameters for synchronization.
    /// Packed into 6 bytes for efficient transmission.
    /// </summary>
    [Serializable]
    public struct NetworkAnimimState : IEquatable<NetworkAnimimState>
    {
        // Speed packed as bytes (-1 to 1 range, 0.01 precision)
        public sbyte speedX;      // 1 byte
        public sbyte speedY;      // 1 byte  
        public sbyte speedZ;      // 1 byte
        
        // Pivot speed packed (-180 to 180 degrees/sec, 1.5 deg precision)
        public sbyte pivotSpeed;  // 1 byte
        
        // Grounded + Stand packed (4 bits each)
        public byte groundedStand; // 1 byte
        
        // Flags
        public byte flags;        // 1 byte
        
        public const byte FLAG_GROUNDED = 1;
        public const byte FLAG_STANDING = 2;
        
        // Total: 6 bytes
        
        /// <summary>
        /// Pack animation state for network transmission.
        /// </summary>
        public static NetworkAnimimState Create(
            Vector3 localSpeed,
            float pivotSpeed,
            bool isGrounded,
            float standLevel)
        {
            byte flags = 0;
            if (isGrounded) flags |= FLAG_GROUNDED;
            if (standLevel > 0.5f) flags |= FLAG_STANDING;
            
            // Pack grounded (0-1) and stand (0-1) into single byte
            byte groundedPacked = (byte)(Mathf.Clamp01(isGrounded ? 1f : 0f) * 15f);
            byte standPacked = (byte)(Mathf.Clamp01(standLevel) * 15f);
            byte groundedStand = (byte)((groundedPacked << 4) | standPacked);
            
            return new NetworkAnimimState
            {
                speedX = (sbyte)Mathf.Clamp(localSpeed.x * 127f, -127f, 127f),
                speedY = (sbyte)Mathf.Clamp(localSpeed.y * 127f, -127f, 127f),
                speedZ = (sbyte)Mathf.Clamp(localSpeed.z * 127f, -127f, 127f),
                pivotSpeed = (sbyte)Mathf.Clamp(pivotSpeed / 1.5f, -127f, 127f),
                groundedStand = groundedStand,
                flags = flags
            };
        }
        
        public Vector3 GetLocalSpeed()
        {
            return new Vector3(
                speedX / 127f,
                speedY / 127f,
                speedZ / 127f
            );
        }
        
        public float GetPivotSpeed() => pivotSpeed * 1.5f;
        
        public float GetGrounded() => ((groundedStand >> 4) & 0x0F) / 15f;
        public float GetStandLevel() => (groundedStand & 0x0F) / 15f;
        
        public bool IsGrounded => (flags & FLAG_GROUNDED) != 0;
        
        public bool Equals(NetworkAnimimState other)
        {
            return speedX == other.speedX &&
                   speedY == other.speedY &&
                   speedZ == other.speedZ &&
                   pivotSpeed == other.pivotSpeed &&
                   groundedStand == other.groundedStand;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(speedX, speedY, speedZ, pivotSpeed, groundedStand);
        }
        
        /// <summary>
        /// Check if values changed enough to warrant a network update.
        /// </summary>
        public bool HasSignificantChange(NetworkAnimimState other, int threshold = 3)
        {
            return Math.Abs(speedX - other.speedX) > threshold ||
                   Math.Abs(speedY - other.speedY) > threshold ||
                   Math.Abs(speedZ - other.speedZ) > threshold ||
                   Math.Abs(pivotSpeed - other.pivotSpeed) > threshold ||
                   groundedStand != other.groundedStand;
        }
        
#if UNITY_NETCODE
        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref speedX);
            serializer.SerializeValue(ref speedY);
            serializer.SerializeValue(ref speedZ);
            serializer.SerializeValue(ref pivotSpeed);
            serializer.SerializeValue(ref groundedStand);
            serializer.SerializeValue(ref flags);
        }
#endif
    }
    
    /// <summary>
    /// Server-authoritative animation unit that syncs animator parameters across the network.
    /// Use this instead of UnitAnimimKinematic when you need server-controlled animation state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This unit synchronizes the core locomotion parameters that drive blend trees:
    /// Speed-X/Y/Z, Pivot, Grounded, and Stand. The server calculates these values
    /// authoritatively and broadcasts them to clients.
    /// </para>
    /// <para>
    /// Use this when:
    /// - Animation state affects gameplay (e.g., animation events trigger damage)
    /// - You need precise animation sync for competitive play
    /// - Anti-cheat requirements for animation state
    /// </para>
    /// <para>
    /// For most games, the default UnitAnimimKinematic with local calculation is sufficient
    /// since animations are derived from already-synced position data.
    /// </para>
    /// </remarks>
    [Title("Network Kinematic (Server-Authoritative)")]
    [Image(typeof(IconCharacterRun), ColorTheme.Type.Blue)]
    
    [Category("Network/Network Kinematic")]
    [Description("Server-authoritative animation parameters that sync across the network. " +
                 "Use when animation state affects gameplay or for competitive anti-cheat.")]
    
    [Serializable]
    public class UnitAnimimNetworkKinematic : TUnitAnimim
    {
        private const float DECAY_PIVOT = 5f;
        private const float DECAY_GROUNDED = 10f;
        private const float DECAY_STAND = 5f;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATIC PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static readonly int K_SPEED_X = Animator.StringToHash("Speed-X");
        private static readonly int K_SPEED_Y = Animator.StringToHash("Speed-Y");
        private static readonly int K_SPEED_Z = Animator.StringToHash("Speed-Z");
        private static readonly int K_SPEED_XZ = Animator.StringToHash("Speed-XZ");
        private static readonly int K_SPEED_YZ = Animator.StringToHash("Speed-YZ");
        private static readonly int K_SPEED_XY = Animator.StringToHash("Speed-XY");
        
        private static readonly int K_INTENT_X = Animator.StringToHash("Intent-X");
        private static readonly int K_INTENT_Y = Animator.StringToHash("Intent-Y");
        private static readonly int K_INTENT_Z = Animator.StringToHash("Intent-Z");
        
        private static readonly int K_SPEED = Animator.StringToHash("Speed");
        private static readonly int K_PIVOT_SPEED = Animator.StringToHash("Pivot");

        private static readonly int K_GROUNDED = Animator.StringToHash("Grounded");
        private static readonly int K_STAND = Animator.StringToHash("Stand");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EXPOSED MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if UNITY_NETCODE
        [Header("Network Settings")]
        [Tooltip("Minimum change threshold before sending update (0-127 scale)")]
        [SerializeField] private int m_ChangeThreshold = 3;
        
        [Tooltip("Maximum updates per second")]
        [SerializeField] private float m_MaxUpdateRate = 20f;
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [NonSerialized] private NetworkCharacter m_NetworkCharacter;
        [NonSerialized] private bool m_IsNetworkInitialized;
        
        // Current animation state (smoothed values)
        [NonSerialized] private Vector3 m_LocalSpeed;
        [NonSerialized] private Vector3 m_Intent;
        [NonSerialized] private float m_PivotSpeed;
        [NonSerialized] private float m_Grounded;
        [NonSerialized] private float m_Stand;
        
        // Network sync state
        [NonSerialized] private NetworkAnimimState m_LastSentState;
        [NonSerialized] private NetworkAnimimState m_TargetState;
        [NonSerialized] private float m_LastSendTime;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Current local speed (normalized).</summary>
        public Vector3 LocalSpeed => m_LocalSpeed;
        
        /// <summary>Current pivot speed in degrees/second.</summary>
        public float PivotSpeed => m_PivotSpeed;
        
        /// <summary>Whether network sync is active.</summary>
        public bool IsNetworkInitialized => m_IsNetworkInitialized;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            m_LocalSpeed = Vector3.zero;
            m_Intent = Vector3.zero;
            m_PivotSpeed = 0f;
            m_Grounded = 1f;
            m_Stand = 1f;
            
            // Try to find NetworkCharacter
            m_NetworkCharacter = character.GetComponent<NetworkCharacter>();
            
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnAnimimUnitRegistered(this);
                m_IsNetworkInitialized = true;
            }
        }
        
        public override void OnDispose(Character character)
        {
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnAnimimUnitUnregistered();
            }
            
            base.OnDispose(character);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UPDATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public override void OnUpdate()
        {
            base.OnUpdate();
            
            if (m_Animator == null) return;
            if (!m_Animator.gameObject.activeInHierarchy) return;
            
            m_Animator.updateMode = Character.Time.UpdateTime == TimeMode.UpdateMode.GameTime
                ? AnimatorUpdateMode.Normal
                : AnimatorUpdateMode.UnscaledTime;
            
#if UNITY_NETCODE
            if (m_IsNetworkInitialized && m_NetworkCharacter != null)
            {
                UpdateNetworked();
            }
            else
            {
                UpdateLocal();
            }
#else
            UpdateLocal();
#endif
        }
        
        private void UpdateLocal()
        {
            // Standard local calculation (same as UnitAnimimKinematic)
            CalculateAnimationValues();
            ApplyToAnimator();
        }
        
#if UNITY_NETCODE
        private void UpdateNetworked()
        {
            var role = m_NetworkCharacter.CurrentRole;
            
            switch (role)
            {
                case NetworkCharacter.NetworkRole.Server:
                    UpdateAsServer();
                    break;
                    
                case NetworkCharacter.NetworkRole.LocalClient:
                    UpdateAsLocalClient();
                    break;
                    
                case NetworkCharacter.NetworkRole.RemoteClient:
                    UpdateAsRemoteClient();
                    break;
                    
                default:
                    UpdateLocal();
                    break;
            }
        }
        
        private void UpdateAsServer()
        {
            // Server calculates authoritative values
            CalculateAnimationValues();
            
            // Check if we should broadcast
            var currentState = NetworkAnimimState.Create(m_LocalSpeed, m_PivotSpeed, m_Grounded > 0.5f, m_Stand);
            
            float timeSinceLastSend = Time.time - m_LastSendTime;
            float minSendInterval = 1f / m_MaxUpdateRate;
            
            if (timeSinceLastSend >= minSendInterval && 
                currentState.HasSignificantChange(m_LastSentState, m_ChangeThreshold))
            {
                m_LastSentState = currentState;
                m_LastSendTime = Time.time;
                // NetworkCharacter will handle broadcasting
            }
            
            ApplyToAnimator();
        }
        
        private void UpdateAsLocalClient()
        {
            // Local client calculates locally for responsiveness
            CalculateAnimationValues();
            
            // Send to server if changed significantly
            var currentState = NetworkAnimimState.Create(m_LocalSpeed, m_PivotSpeed, m_Grounded > 0.5f, m_Stand);
            
            float timeSinceLastSend = Time.time - m_LastSendTime;
            float minSendInterval = 1f / m_MaxUpdateRate;
            
            if (timeSinceLastSend >= minSendInterval && 
                currentState.HasSignificantChange(m_LastSentState, m_ChangeThreshold))
            {
                m_LastSentState = currentState;
                m_LastSendTime = Time.time;
                m_NetworkCharacter.RequestAnimimUpdate(currentState);
            }
            
            ApplyToAnimator();
        }
        
        private void UpdateAsRemoteClient()
        {
            // Remote client interpolates toward target state
            float deltaTime = Character.Time.DeltaTime;
            float decay = Mathf.Lerp(1f, 25f, m_SmoothTime);
            
            Vector3 targetSpeed = m_TargetState.GetLocalSpeed();
            m_LocalSpeed.x = MathUtils.ExponentialDecay(m_LocalSpeed.x, targetSpeed.x, decay, deltaTime);
            m_LocalSpeed.y = MathUtils.ExponentialDecay(m_LocalSpeed.y, targetSpeed.y, decay, deltaTime);
            m_LocalSpeed.z = MathUtils.ExponentialDecay(m_LocalSpeed.z, targetSpeed.z, decay, deltaTime);
            
            m_PivotSpeed = MathUtils.ExponentialDecay(m_PivotSpeed, m_TargetState.GetPivotSpeed(), DECAY_PIVOT, deltaTime);
            m_Grounded = MathUtils.ExponentialDecay(m_Grounded, m_TargetState.GetGrounded(), DECAY_GROUNDED, deltaTime);
            m_Stand = MathUtils.ExponentialDecay(m_Stand, m_TargetState.GetStandLevel(), DECAY_STAND, deltaTime);
            
            // Intent is calculated locally since it's not synced
            IUnitMotion motion = Character.Motion;
            m_Intent = motion.LinearSpeed > float.Epsilon
                ? Vector3.ClampMagnitude(Transform.InverseTransformDirection(motion.MoveDirection) / motion.LinearSpeed, 1f)
                : Vector3.zero;
            
            ApplyToAnimator();
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORK CALLBACKS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when receiving animation state from the server.
        /// </summary>
        public void OnServerStateReceived(NetworkAnimimState state)
        {
            m_TargetState = state;
        }
        
        /// <summary>
        /// Gets the current state for network transmission.
        /// </summary>
        public NetworkAnimimState GetCurrentState()
        {
            return NetworkAnimimState.Create(m_LocalSpeed, m_PivotSpeed, m_Grounded > 0.5f, m_Stand);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void CalculateAnimationValues()
        {
            IUnitMotion motion = Character.Motion;
            IUnitDriver driver = Character.Driver;
            IUnitFacing facing = Character.Facing;
            
            float deltaTime = Character.Time.DeltaTime;
            float decay = Mathf.Lerp(1f, 25f, m_SmoothTime);
            
            // Calculate target values
            Vector3 targetIntent = motion.LinearSpeed > float.Epsilon
                ? Vector3.ClampMagnitude(Transform.InverseTransformDirection(motion.MoveDirection) / motion.LinearSpeed, 1f)
                : Vector3.zero;
            
            Vector3 targetSpeed = motion.LinearSpeed > float.Epsilon
                ? driver.LocalMoveDirection / motion.LinearSpeed
                : Vector3.zero;
            
            float targetPivot = facing.PivotSpeed;
            float targetGrounded = driver.IsGrounded ? 1f : 0f;
            float targetStand = motion.StandLevel.Current;
            
            // Smooth values
            m_LocalSpeed.x = MathUtils.ExponentialDecay(m_LocalSpeed.x, targetSpeed.x, decay, deltaTime);
            m_LocalSpeed.y = MathUtils.ExponentialDecay(m_LocalSpeed.y, targetSpeed.y, decay, deltaTime);
            m_LocalSpeed.z = MathUtils.ExponentialDecay(m_LocalSpeed.z, targetSpeed.z, decay, deltaTime);
            
            m_Intent.x = MathUtils.ExponentialDecay(m_Intent.x, targetIntent.x, decay, deltaTime);
            m_Intent.y = MathUtils.ExponentialDecay(m_Intent.y, targetIntent.y, decay, deltaTime);
            m_Intent.z = MathUtils.ExponentialDecay(m_Intent.z, targetIntent.z, decay, deltaTime);
            
            m_PivotSpeed = MathUtils.ExponentialDecay(m_PivotSpeed, targetPivot, DECAY_PIVOT, deltaTime);
            m_Grounded = MathUtils.ExponentialDecay(m_Grounded, targetGrounded, DECAY_GROUNDED, deltaTime);
            m_Stand = MathUtils.ExponentialDecay(m_Stand, targetStand, DECAY_STAND, deltaTime);
        }
        
        private void ApplyToAnimator()
        {
            m_Animator.SetFloat(K_SPEED_X, m_LocalSpeed.x);
            m_Animator.SetFloat(K_SPEED_Y, m_LocalSpeed.y);
            m_Animator.SetFloat(K_SPEED_Z, m_LocalSpeed.z);
            m_Animator.SetFloat(K_SPEED, m_LocalSpeed.magnitude);
            m_Animator.SetFloat(K_SPEED_XZ, new Vector2(m_LocalSpeed.x, m_LocalSpeed.z).magnitude);
            m_Animator.SetFloat(K_SPEED_XY, new Vector2(m_LocalSpeed.x, m_LocalSpeed.y).magnitude);
            m_Animator.SetFloat(K_SPEED_YZ, new Vector2(m_LocalSpeed.y, m_LocalSpeed.z).magnitude);
            
            m_Animator.SetFloat(K_INTENT_X, m_Intent.x);
            m_Animator.SetFloat(K_INTENT_Y, m_Intent.y);
            m_Animator.SetFloat(K_INTENT_Z, m_Intent.z);
            
            m_Animator.SetFloat(K_PIVOT_SPEED, m_PivotSpeed);
            m_Animator.SetFloat(K_GROUNDED, m_Grounded);
            m_Animator.SetFloat(K_STAND, m_Stand);
        }
    }
}
