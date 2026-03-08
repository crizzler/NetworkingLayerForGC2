using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compressed input state for network transmission.
    /// Uses fixed-point encoding to minimize bandwidth.
    /// Total size: 8 bytes (vs 24+ bytes for raw Vector3 + flags)
    /// </summary>
    [Serializable]
    public struct NetworkInputState : IEquatable<NetworkInputState>
    {
        // Input direction encoded as shorts (-32768 to 32767 mapped to -1 to 1)
        public short inputX;
        public short inputY;
        
        // Sequence number for ordering and reconciliation
        public ushort sequenceNumber;
        
        // Packed flags: Jump(1), Dash(2), Sprint(4), Crouch(8), Custom(16-128)
        public byte flags;
        
        // Delta time in milliseconds (capped at 255ms)
        public byte deltaTimeMs;
        
        // Flag constants
        public const byte FLAG_JUMP = 1;
        public const byte FLAG_DASH = 2;
        public const byte FLAG_SPRINT = 4;
        public const byte FLAG_CROUCH = 8;
        public const byte FLAG_CUSTOM_1 = 16;
        public const byte FLAG_CUSTOM_2 = 32;
        public const byte FLAG_CUSTOM_3 = 64;
        public const byte FLAG_CUSTOM_4 = 128;
        
        /// <summary>
        /// Creates a compressed input state from raw input.
        /// </summary>
        public static NetworkInputState Create(Vector2 input, ushort sequence, float deltaTime, byte flags = 0)
        {
            return new NetworkInputState
            {
                inputX = (short)Mathf.Clamp(input.x * 32767f, -32767f, 32767f),
                inputY = (short)Mathf.Clamp(input.y * 32767f, -32767f, 32767f),
                sequenceNumber = sequence,
                flags = flags,
                deltaTimeMs = (byte)Mathf.Clamp(deltaTime * 1000f, 1f, 255f)
            };
        }
        
        /// <summary>
        /// Gets the decompressed input direction.
        /// </summary>
        public Vector2 GetInputDirection()
        {
            return new Vector2(inputX / 32767f, inputY / 32767f);
        }
        
        /// <summary>
        /// Gets the delta time in seconds.
        /// </summary>
        public float GetDeltaTime()
        {
            return deltaTimeMs / 1000f;
        }
        
        public bool HasFlag(byte flag) => (flags & flag) != 0;
        
        public bool Equals(NetworkInputState other)
        {
            return inputX == other.inputX && 
                   inputY == other.inputY && 
                   sequenceNumber == other.sequenceNumber &&
                   flags == other.flags;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(inputX, inputY, sequenceNumber, flags);
        }
    }
    
    /// <summary>
    /// Compressed position state for network transmission.
    /// Uses fixed-point encoding for position (supports -32768 to 32767 range with 0.01 precision).
    /// Total size: 14 bytes (vs 28+ bytes for raw position + velocity)
    /// </summary>
    [Serializable]
    public struct NetworkPositionState : IEquatable<NetworkPositionState>
    {
        // Position encoded as fixed-point (multiply by 100 for 0.01 precision)
        public int positionX;
        public int positionY;
        public int positionZ;
        
        // Rotation Y as short (0-360 degrees mapped to 0-65535)
        public ushort rotationY;
        
        // Vertical velocity encoded (multiply by 100)
        public short verticalVelocity;
        
        // Flags: IsGrounded(1), IsJumping(2), IsDashing(4), etc.
        public byte flags;
        
        // Sequence number this state responds to
        public ushort lastProcessedInput;
        
        public const byte FLAG_GROUNDED = 1;
        public const byte FLAG_JUMPING = 2;
        public const byte FLAG_DASHING = 4;
        public const byte FLAG_SPRINTING = 8;
        
        /// <summary>
        /// Creates a compressed position state.
        /// </summary>
        public static NetworkPositionState Create(
            Vector3 position, 
            float rotationY, 
            float verticalVel,
            ushort lastInput,
            bool isGrounded,
            bool isJumping)
        {
            byte flags = 0;
            if (isGrounded) flags |= FLAG_GROUNDED;
            if (isJumping) flags |= FLAG_JUMPING;
            
            return new NetworkPositionState
            {
                positionX = Mathf.RoundToInt(position.x * 100f),
                positionY = Mathf.RoundToInt(position.y * 100f),
                positionZ = Mathf.RoundToInt(position.z * 100f),
                rotationY = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f),
                verticalVelocity = (short)Mathf.Clamp(verticalVel * 100f, -32767f, 32767f),
                flags = flags,
                lastProcessedInput = lastInput
            };
        }
        
        /// <summary>
        /// Gets the decompressed position.
        /// </summary>
        public Vector3 GetPosition()
        {
            return new Vector3(positionX / 100f, positionY / 100f, positionZ / 100f);
        }
        
        /// <summary>
        /// Gets the rotation Y in degrees.
        /// </summary>
        public float GetRotationY()
        {
            return rotationY / 65535f * 360f;
        }
        
        /// <summary>
        /// Gets the vertical velocity.
        /// </summary>
        public float GetVerticalVelocity()
        {
            return verticalVelocity / 100f;
        }
        
        public bool IsGrounded => (flags & FLAG_GROUNDED) != 0;
        public bool IsJumping => (flags & FLAG_JUMPING) != 0;
        public bool IsDashing => (flags & FLAG_DASHING) != 0;
        
        public bool Equals(NetworkPositionState other)
        {
            return positionX == other.positionX &&
                   positionY == other.positionY &&
                   positionZ == other.positionZ &&
                   rotationY == other.rotationY;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(positionX, positionY, positionZ, rotationY);
        }
    }
    
    /// <summary>
    /// Configuration for network character behavior.
    /// </summary>
    [Serializable]
    public class NetworkCharacterConfig
    {
        [Header("Input Sending")]
        [Tooltip("How many inputs to send per second (server tick rate match recommended)")]
        [Range(10, 60)]
        public int inputSendRate = 30;
        
        [Tooltip("How many recent inputs to include for redundancy")]
        [Range(1, 5)]
        public int inputRedundancy = 3;
        
        [Header("Reconciliation")]
        [Tooltip("Position error threshold before reconciliation (in units)")]
        [Range(0.01f, 1f)]
        public float reconciliationThreshold = 0.1f;
        
        [Tooltip("How fast to interpolate during reconciliation")]
        [Range(5f, 30f)]
        public float reconciliationSpeed = 15f;
        
        [Tooltip("Max distance to allow smooth reconciliation (larger = teleport)")]
        [Range(1f, 10f)]
        public float maxReconciliationDistance = 3f;
        
        [Header("Interpolation")]
        [Tooltip("Interpolation delay for remote characters (in seconds)")]
        [Range(0.05f, 0.3f)]
        public float interpolationDelay = 0.1f;
        
        [Tooltip("How many position snapshots to buffer")]
        [Range(3, 20)]
        public int snapshotBufferSize = 10;
        
        [Header("Anti-Cheat")]
        [Tooltip("Max allowed speed multiplier before flagging")]
        [Range(1.1f, 2f)]
        public float maxSpeedMultiplier = 1.2f;
        
        [Tooltip("How many violations before action")]
        [Range(3, 20)]
        public int violationThreshold = 5;
    }
    
    /// <summary>
    /// Snapshot for position interpolation on remote characters.
    /// </summary>
    public struct PositionSnapshot
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public float rotationY;
        public float verticalVelocity;
        public double timestamp;
        public byte flags;
        
        public static PositionSnapshot Create(NetworkPositionState state, double time)
        {
            return new PositionSnapshot
            {
                position = state.GetPosition(),
                rotation = Quaternion.Euler(0f, state.GetRotationY(), 0f),
                velocity = Vector3.zero, // Will be calculated from position deltas
                rotationY = state.GetRotationY(),
                verticalVelocity = state.GetVerticalVelocity(),
                timestamp = time,
                flags = state.flags
            };
        }
    }
}
