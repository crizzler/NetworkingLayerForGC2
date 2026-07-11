using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compressed input state for network transmission.
    /// Uses fixed-point encoding to minimize bandwidth.
    /// Total size: 10 bytes normally, 23 bytes when carrying an owner-authority pose sample.
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

        // Owner-facing yaw (0-360 degrees mapped to 0-65535)
        public ushort rotationY;

        // Optional authority metadata. Position is only serialized when AUTHORITY_FLAG_POSITION is set.
        public byte authorityFlags;
        public int authorityPositionX;
        public int authorityPositionY;
        public int authorityPositionZ;
        
        // Flag constants
        public const byte FLAG_JUMP = 1;
        public const byte FLAG_DASH = 2;
        public const byte FLAG_SPRINT = 4;
        public const byte FLAG_CROUCH = 8;
        public const byte FLAG_CUSTOM_1 = 16;
        public const byte FLAG_CUSTOM_2 = 32;
        public const byte FLAG_CUSTOM_3 = 64;
        public const byte FLAG_CUSTOM_4 = 128;

        public const byte AUTHORITY_FLAG_POSITION = 1;
        
        /// <summary>
        /// Creates a compressed input state from raw input.
        /// </summary>
        public static NetworkInputState Create(
            Vector2 input,
            ushort sequence,
            float deltaTime,
            byte flags = 0,
            float rotationY = 0f,
            Vector3? ownerAuthorityPosition = null)
        {
            NetworkInputState state = new NetworkInputState
            {
                inputX = (short)Mathf.Clamp(input.x * 32767f, -32767f, 32767f),
                inputY = (short)Mathf.Clamp(input.y * 32767f, -32767f, 32767f),
                sequenceNumber = sequence,
                flags = flags,
                deltaTimeMs = (byte)Mathf.Clamp(deltaTime * 1000f, 1f, 255f),
                rotationY = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f)
            };

            if (ownerAuthorityPosition.HasValue)
            {
                state.SetOwnerAuthorityPosition(ownerAuthorityPosition.Value);
            }

            return state;
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

        /// <summary>
        /// Gets the decompressed owner-facing yaw in degrees.
        /// </summary>
        public float GetRotationY()
        {
            return rotationY / 65535f * 360f;
        }
        
        public bool HasFlag(byte flag) => (flags & flag) != 0;

        public bool HasOwnerAuthorityPosition => (authorityFlags & AUTHORITY_FLAG_POSITION) != 0;

        public void SetOwnerAuthorityPosition(Vector3 position)
        {
            authorityFlags |= AUTHORITY_FLAG_POSITION;
            authorityPositionX = Mathf.RoundToInt(position.x * 100f);
            authorityPositionY = Mathf.RoundToInt(position.y * 100f);
            authorityPositionZ = Mathf.RoundToInt(position.z * 100f);
        }

        public Vector3 GetOwnerAuthorityPosition()
        {
            return new Vector3(
                authorityPositionX / 100f,
                authorityPositionY / 100f,
                authorityPositionZ / 100f
            );
        }
        
        public bool Equals(NetworkInputState other)
        {
            return inputX == other.inputX &&
                   inputY == other.inputY &&
                   sequenceNumber == other.sequenceNumber &&
                   flags == other.flags &&
                   deltaTimeMs == other.deltaTimeMs &&
                   rotationY == other.rotationY &&
                   authorityFlags == other.authorityFlags &&
                   authorityPositionX == other.authorityPositionX &&
                   authorityPositionY == other.authorityPositionY &&
                   authorityPositionZ == other.authorityPositionZ;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(
                inputX,
                inputY,
                sequenceNumber,
                flags,
                deltaTimeMs,
                rotationY,
                authorityFlags,
                HashCode.Combine(authorityPositionX, authorityPositionY, authorityPositionZ));
        }
    }
    
    /// <summary>
    /// Compressed position state for network transmission.
    /// Uses fixed-point encoding for position (supports -32768 to 32767 range with 0.01 precision).
    /// Total size: 43 bytes with motion support data.
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

        // Authoritative move velocity encoded (multiply by 100). This is used by
        // remote animation parameters; position/rotation remain snapshot-driven.
        public short moveVelocityX;
        public short moveVelocityY;
        public short moveVelocityZ;

        // Optional support/platform frame. World position/rotation remain populated as fallback.
        public uint supportId;
        public int supportLocalPositionX;
        public int supportLocalPositionY;
        public int supportLocalPositionZ;
        public ushort supportLocalYaw;
        
        // Flags: IsGrounded(1), IsJumping(2), IsDashing(4), etc.
        public byte flags;
        
        // Sequence number this state responds to
        public ushort lastProcessedInput;
        
        public const byte FLAG_GROUNDED = 1;
        public const byte FLAG_JUMPING = 2;
        public const byte FLAG_DASHING = 4;
        public const byte FLAG_SPRINTING = 8;
        public const byte FLAG_HAS_MOVE_VELOCITY = 16;
        public const byte FLAG_HAS_SUPPORT = 32;
        
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
            return Create(
                position,
                rotationY,
                verticalVel,
                lastInput,
                isGrounded,
                isJumping,
                Vector3.zero,
                false
            );
        }

        public static NetworkPositionState Create(
            Vector3 position,
            float rotationY,
            float verticalVel,
            ushort lastInput,
            bool isGrounded,
            bool isJumping,
            Vector3 moveVelocity)
        {
            return Create(
                position,
                rotationY,
                verticalVel,
                lastInput,
                isGrounded,
                isJumping,
                moveVelocity,
                true
            );
        }

        public static NetworkPositionState Create(
            Vector3 position,
            float rotationY,
            float verticalVel,
            ushort lastInput,
            bool isGrounded,
            bool isJumping,
            Vector3 moveVelocity,
            uint supportId,
            Vector3 supportLocalPosition,
            float supportLocalYaw)
        {
            NetworkPositionState state = Create(
                position,
                rotationY,
                verticalVel,
                lastInput,
                isGrounded,
                isJumping,
                moveVelocity,
                true
            );

            state.SetSupport(supportId, supportLocalPosition, supportLocalYaw);
            return state;
        }

        private static NetworkPositionState Create(
            Vector3 position,
            float rotationY,
            float verticalVel,
            ushort lastInput,
            bool isGrounded,
            bool isJumping,
            Vector3 moveVelocity,
            bool hasMoveVelocity)
        {
            byte flags = 0;
            if (isGrounded) flags |= FLAG_GROUNDED;
            if (isJumping) flags |= FLAG_JUMPING;
            if (hasMoveVelocity) flags |= FLAG_HAS_MOVE_VELOCITY;
            
            return new NetworkPositionState
            {
                positionX = Mathf.RoundToInt(position.x * 100f),
                positionY = Mathf.RoundToInt(position.y * 100f),
                positionZ = Mathf.RoundToInt(position.z * 100f),
                rotationY = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f),
                verticalVelocity = (short)Mathf.Clamp(verticalVel * 100f, -32767f, 32767f),
                moveVelocityX = (short)Mathf.Clamp(moveVelocity.x * 100f, -32767f, 32767f),
                moveVelocityY = (short)Mathf.Clamp(moveVelocity.y * 100f, -32767f, 32767f),
                moveVelocityZ = (short)Mathf.Clamp(moveVelocity.z * 100f, -32767f, 32767f),
                flags = flags,
                lastProcessedInput = lastInput
            };
        }

        public void SetSupport(uint supportId, Vector3 localPosition, float localYaw)
        {
            if (supportId == 0)
            {
                ClearSupport();
                return;
            }

            flags |= FLAG_HAS_SUPPORT;
            this.supportId = supportId;
            supportLocalPositionX = Mathf.RoundToInt(localPosition.x * 100f);
            supportLocalPositionY = Mathf.RoundToInt(localPosition.y * 100f);
            supportLocalPositionZ = Mathf.RoundToInt(localPosition.z * 100f);
            supportLocalYaw = (ushort)(Mathf.Repeat(localYaw, 360f) / 360f * 65535f);
        }

        public void ClearSupport()
        {
            flags &= unchecked((byte)~FLAG_HAS_SUPPORT);
            supportId = 0;
            supportLocalPositionX = 0;
            supportLocalPositionY = 0;
            supportLocalPositionZ = 0;
            supportLocalYaw = 0;
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

        public Vector3 GetMoveVelocity()
        {
            return new Vector3(
                moveVelocityX / 100f,
                moveVelocityY / 100f,
                moveVelocityZ / 100f
            );
        }

        public Vector3 GetSupportLocalPosition()
        {
            return new Vector3(
                supportLocalPositionX / 100f,
                supportLocalPositionY / 100f,
                supportLocalPositionZ / 100f
            );
        }

        public float GetSupportLocalYaw()
        {
            return supportLocalYaw / 65535f * 360f;
        }
        
        public bool IsGrounded => (flags & FLAG_GROUNDED) != 0;
        public bool IsJumping => (flags & FLAG_JUMPING) != 0;
        public bool IsDashing => (flags & FLAG_DASHING) != 0;
        public bool HasMoveVelocity => (flags & FLAG_HAS_MOVE_VELOCITY) != 0;
        public bool HasSupport => (flags & FLAG_HAS_SUPPORT) != 0 && supportId != 0;
        
        public bool Equals(NetworkPositionState other)
        {
            return positionX == other.positionX &&
                   positionY == other.positionY &&
                   positionZ == other.positionZ &&
                   rotationY == other.rotationY &&
                   verticalVelocity == other.verticalVelocity &&
                   moveVelocityX == other.moveVelocityX &&
                   moveVelocityY == other.moveVelocityY &&
                   moveVelocityZ == other.moveVelocityZ &&
                   supportId == other.supportId &&
                   supportLocalPositionX == other.supportLocalPositionX &&
                   supportLocalPositionY == other.supportLocalPositionY &&
                   supportLocalPositionZ == other.supportLocalPositionZ &&
                   supportLocalYaw == other.supportLocalYaw &&
                   flags == other.flags &&
                   lastProcessedInput == other.lastProcessedInput;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(
                positionX,
                positionY,
                positionZ,
                rotationY,
                verticalVelocity,
                HashCode.Combine(moveVelocityX, moveVelocityY, moveVelocityZ, flags),
                HashCode.Combine(supportId, supportLocalPositionX, supportLocalPositionY, supportLocalPositionZ),
                HashCode.Combine(supportLocalYaw, lastProcessedInput));
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
        public uint supportId;
        public Vector3 supportLocalPosition;
        public float supportLocalYaw;

        public bool HasSupport => (flags & NetworkPositionState.FLAG_HAS_SUPPORT) != 0 && supportId != 0;
        
        public static PositionSnapshot Create(NetworkPositionState state, double time)
        {
            return new PositionSnapshot
            {
                position = state.GetPosition(),
                rotation = Quaternion.Euler(0f, state.GetRotationY(), 0f),
                velocity = state.HasMoveVelocity ? state.GetMoveVelocity() : Vector3.zero,
                rotationY = state.GetRotationY(),
                verticalVelocity = state.GetVerticalVelocity(),
                timestamp = time,
                flags = state.flags,
                supportId = state.supportId,
                supportLocalPosition = state.GetSupportLocalPosition(),
                supportLocalYaw = state.GetSupportLocalYaw()
            };
        }
    }
}
