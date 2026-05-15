using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compressed motion configuration for network transmission.
    /// Sent when motion properties change (speed, gravity, jump, etc.).
    /// Total size: 20 bytes
    /// </summary>
    [Serializable]
    public struct NetworkMotionConfig : IEquatable<NetworkMotionConfig>
    {
        // Speed and rotation (half precision would be 4 bytes, using shorts for 4 bytes)
        public ushort linearSpeed;      // 0-655.35 m/s with 0.01 precision
        public ushort angularSpeed;     // 0-6553.5 deg/s with 0.1 precision
        
        // Physics
        public short gravityUp;         // -327.67 to 327.67 with 0.01 precision
        public short gravityDown;       // -327.67 to 327.67 with 0.01 precision
        public short terminalVelocity;  // -327.67 to 327.67 with 0.01 precision
        
        // Jump
        public ushort jumpForce;        // 0-655.35 with 0.01 precision
        public byte jumpCooldownMs;     // 0-2.55s with 0.01 precision
        public byte airJumps;           // 0-255
        
        // Dash
        public byte dashSuccession;     // 0-255
        public byte dashCooldownMs;     // 0-2.55s with 0.01 precision (stored as centiseconds)
        
        // Flags: CanJump(1), DashInAir(2), UseAcceleration(4)
        public byte flags;
        
        // Sequence for change detection
        public byte configVersion;
        
        public const byte FLAG_CAN_JUMP = 1;
        public const byte FLAG_DASH_IN_AIR = 2;
        public const byte FLAG_USE_ACCELERATION = 4;
        
        /// <summary>
        /// Creates a compressed motion config from values.
        /// </summary>
        public static NetworkMotionConfig Create(
            float linearSpeed,
            float angularSpeed,
            float gravityUp,
            float gravityDown,
            float terminalVelocity,
            float jumpForce,
            float jumpCooldown,
            int airJumps,
            int dashSuccession,
            float dashCooldown,
            bool canJump,
            bool dashInAir,
            bool useAcceleration,
            byte version)
        {
            byte flags = 0;
            if (canJump) flags |= FLAG_CAN_JUMP;
            if (dashInAir) flags |= FLAG_DASH_IN_AIR;
            if (useAcceleration) flags |= FLAG_USE_ACCELERATION;
            
            return new NetworkMotionConfig
            {
                linearSpeed = (ushort)Mathf.Clamp(linearSpeed * 100f, 0f, 65535f),
                angularSpeed = (ushort)Mathf.Clamp(angularSpeed * 10f, 0f, 65535f),
                gravityUp = (short)Mathf.Clamp(gravityUp * 100f, -32767f, 32767f),
                gravityDown = (short)Mathf.Clamp(gravityDown * 100f, -32767f, 32767f),
                terminalVelocity = (short)Mathf.Clamp(terminalVelocity * 100f, -32767f, 32767f),
                jumpForce = (ushort)Mathf.Clamp(jumpForce * 100f, 0f, 65535f),
                jumpCooldownMs = (byte)Mathf.Clamp(jumpCooldown * 100f, 0f, 255f),
                airJumps = (byte)Mathf.Clamp(airJumps, 0, 255),
                dashSuccession = (byte)Mathf.Clamp(dashSuccession, 0, 255),
                dashCooldownMs = (byte)Mathf.Clamp(dashCooldown * 100f, 0f, 255f),
                flags = flags,
                configVersion = version
            };
        }
        
        public float GetLinearSpeed() => linearSpeed / 100f;
        public float GetAngularSpeed() => angularSpeed / 10f;
        public float GetGravityUp() => gravityUp / 100f;
        public float GetGravityDown() => gravityDown / 100f;
        public float GetTerminalVelocity() => terminalVelocity / 100f;
        public float GetJumpForce() => jumpForce / 100f;
        public float GetJumpCooldown() => jumpCooldownMs / 100f;
        public float GetDashCooldown() => dashCooldownMs / 100f;
        
        public bool CanJump => (flags & FLAG_CAN_JUMP) != 0;
        public bool DashInAir => (flags & FLAG_DASH_IN_AIR) != 0;
        public bool UseAcceleration => (flags & FLAG_USE_ACCELERATION) != 0;
        
        public bool Equals(NetworkMotionConfig other)
        {
            return linearSpeed == other.linearSpeed &&
                   angularSpeed == other.angularSpeed &&
                   gravityUp == other.gravityUp &&
                   gravityDown == other.gravityDown &&
                   jumpForce == other.jumpForce &&
                   flags == other.flags;
        }
        
        public override int GetHashCode()
        {
            return HashCode.Combine(linearSpeed, angularSpeed, gravityUp, gravityDown, jumpForce, flags);
        }
    }
    
    /// <summary>
    /// Network command types for motion actions.
    /// </summary>
    public enum NetworkMotionCommandType : byte
    {
        None = 0,
        MoveToDirection = 1,
        StopDirection = 2,
        MoveToPosition = 3,
        StopPosition = 4,
        Dash = 5,
        Teleport = 6,
        Jump = 7,
        ForceJump = 8,
        SetTransient = 9,
        FollowTarget = 10,
        StopFollow = 11
    }
    
    /// <summary>
    /// Compressed motion command for network transmission.
    /// Used for MoveToDirection, MoveToPosition, Dash, Teleport, etc.
    /// Fixed size for all command types. Transport packers serialize fields explicitly.
    /// </summary>
    [Serializable]
    public struct NetworkMotionCommand : IEquatable<NetworkMotionCommand>
    {
        public NetworkMotionCommandType commandType;
        public byte priority;
        public ushort sequenceNumber;
        
        // Position/Direction data (12 bytes)
        public int dataX;   // Position X or Direction X (fixed point * 100)
        public int dataY;   // Position Y or Direction Y (fixed point * 100)
        public int dataZ;   // Position Z or Direction Z (fixed point * 100)
        
        // Additional parameters
        public ushort param1;  // Speed, Force, Duration, etc.
        public ushort param2;  // StopDistance, Fade, etc.
        public ushort param3;  // Gravity or future command-specific scalar
        public uint targetNetworkId; // Follow target NetworkCharacter id, when applicable
        
        /// <summary>
        /// Create a MoveToDirection command.
        /// </summary>
        public static NetworkMotionCommand CreateMoveToDirection(
            Vector3 velocity, 
            bool isWorldSpace,
            int priority,
            ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.MoveToDirection,
                priority = (byte)Mathf.Clamp(priority, 0, 255),
                sequenceNumber = sequence,
                dataX = Mathf.RoundToInt(velocity.x * 100f),
                dataY = Mathf.RoundToInt(velocity.y * 100f),
                dataZ = Mathf.RoundToInt(velocity.z * 100f),
                param1 = (ushort)(isWorldSpace ? 1 : 0),
                param2 = 0
            };
        }
        
        /// <summary>
        /// Create a StopDirection command.
        /// </summary>
        public static NetworkMotionCommand CreateStopDirection(int priority, ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.StopDirection,
                priority = (byte)Mathf.Clamp(priority, 0, 255),
                sequenceNumber = sequence
            };
        }
        
        /// <summary>
        /// Create a MoveToPosition command.
        /// </summary>
        public static NetworkMotionCommand CreateMoveToPosition(
            Vector3 position, 
            float stopDistance,
            int priority,
            ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.MoveToPosition,
                priority = (byte)Mathf.Clamp(priority, 0, 255),
                sequenceNumber = sequence,
                dataX = Mathf.RoundToInt(position.x * 100f),
                dataY = Mathf.RoundToInt(position.y * 100f),
                dataZ = Mathf.RoundToInt(position.z * 100f),
                param1 = (ushort)Mathf.Clamp(stopDistance * 100f, 0f, 65535f),
                param2 = 0
            };
        }

        /// <summary>
        /// Create a FollowTarget command.
        /// </summary>
        public static NetworkMotionCommand CreateFollowTarget(
            uint targetNetworkId,
            Vector3 fallbackPosition,
            float minRadius,
            float maxRadius,
            int priority,
            ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.FollowTarget,
                priority = (byte)Mathf.Clamp(priority, 0, 255),
                sequenceNumber = sequence,
                dataX = Mathf.RoundToInt(fallbackPosition.x * 100f),
                dataY = Mathf.RoundToInt(fallbackPosition.y * 100f),
                dataZ = Mathf.RoundToInt(fallbackPosition.z * 100f),
                param1 = (ushort)Mathf.Clamp(minRadius * 100f, 0f, 65535f),
                param2 = (ushort)Mathf.Clamp(maxRadius * 100f, 0f, 65535f),
                targetNetworkId = targetNetworkId
            };
        }

        /// <summary>
        /// Create a StopFollow command.
        /// </summary>
        public static NetworkMotionCommand CreateStopFollow(int priority, ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.StopFollow,
                priority = (byte)Mathf.Clamp(priority, 0, 255),
                sequenceNumber = sequence
            };
        }
        
        /// <summary>
        /// Create a Dash command (transient movement).
        /// </summary>
        public static NetworkMotionCommand CreateDash(
            Vector3 direction,
            float speed,
            float duration,
            float fade,
            ushort sequence,
            float gravity = 1f)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.Dash,
                sequenceNumber = sequence,
                dataX = Mathf.RoundToInt(direction.x * 1000f), // Higher precision for normalized direction
                dataY = Mathf.RoundToInt(direction.y * 1000f),
                dataZ = Mathf.RoundToInt(direction.z * 1000f),
                param1 = (ushort)Mathf.Clamp(speed * 10f, 0f, 65535f),
                // Pack duration and fade into param2 (8 bits each, 0-2.55s)
                param2 = (ushort)(((byte)Mathf.Clamp(duration * 100f, 0f, 255f) << 8) | 
                                  (byte)Mathf.Clamp(fade * 100f, 0f, 255f)),
                param3 = (ushort)Mathf.Clamp(gravity * 100f, 0f, 65535f)
            };
        }
        
        /// <summary>
        /// Create a Teleport command.
        /// </summary>
        public static NetworkMotionCommand CreateTeleport(
            Vector3 position,
            float rotationY,
            ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = NetworkMotionCommandType.Teleport,
                sequenceNumber = sequence,
                dataX = Mathf.RoundToInt(position.x * 100f),
                dataY = Mathf.RoundToInt(position.y * 100f),
                dataZ = Mathf.RoundToInt(position.z * 100f),
                param1 = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f),
                param2 = 0
            };
        }
        
        /// <summary>
        /// Create a Jump command.
        /// </summary>
        public static NetworkMotionCommand CreateJump(float force, bool isForced, ushort sequence)
        {
            return new NetworkMotionCommand
            {
                commandType = isForced ? NetworkMotionCommandType.ForceJump : NetworkMotionCommandType.Jump,
                sequenceNumber = sequence,
                param1 = (ushort)Mathf.Clamp(force * 100f, 0f, 65535f),
                param2 = 0
            };
        }
        
        // Decompression helpers
        public Vector3 GetPosition() => new Vector3(dataX / 100f, dataY / 100f, dataZ / 100f);
        public Vector3 GetDirection() => new Vector3(dataX / 1000f, dataY / 1000f, dataZ / 1000f);
        public Vector3 GetVelocity() => new Vector3(dataX / 100f, dataY / 100f, dataZ / 100f);
        public float GetStopDistance() => param1 / 100f;
        public float GetSpeed() => param1 / 10f;
        public float GetDuration() => ((param2 >> 8) & 0xFF) / 100f;
        public float GetFade() => (param2 & 0xFF) / 100f;
        public float GetGravity() => param3 / 100f;
        public float GetRotationY() => param1 / 65535f * 360f;
        public float GetJumpForce() => param1 / 100f;
        public float GetFollowMinRadius() => param1 / 100f;
        public float GetFollowMaxRadius() => param2 / 100f;
        public bool IsWorldSpace() => param1 == 1;
        
        public bool Equals(NetworkMotionCommand other)
        {
            return commandType == other.commandType &&
                   sequenceNumber == other.sequenceNumber &&
                   dataX == other.dataX &&
                   dataY == other.dataY &&
                   dataZ == other.dataZ &&
                   param1 == other.param1 &&
                   param2 == other.param2 &&
                   param3 == other.param3 &&
                   targetNetworkId == other.targetNetworkId;
        }
        
        public override int GetHashCode()
        {
            HashCode hash = new HashCode();
            hash.Add(commandType);
            hash.Add(sequenceNumber);
            hash.Add(dataX);
            hash.Add(dataY);
            hash.Add(dataZ);
            hash.Add(param1);
            hash.Add(param2);
            hash.Add(param3);
            hash.Add(targetNetworkId);
            return hash.ToHashCode();
        }
    }
    
    /// <summary>
    /// Result of a motion command validation on the server.
    /// </summary>
    [Serializable]
    public struct NetworkMotionResult
    {
        public ushort commandSequence;
        public bool approved;
        public byte rejectionReason;
        
        // If approved with modification, the corrected values
        public int correctedX;
        public int correctedY;
        public int correctedZ;
        
        public const byte REJECT_NONE = 0;
        public const byte REJECT_COOLDOWN = 1;
        public const byte REJECT_INVALID_POSITION = 2;
        public const byte REJECT_TOO_FAR = 3;
        public const byte REJECT_BLOCKED = 4;
        public const byte REJECT_NOT_ALLOWED = 5;
        public const byte REJECT_TIMEOUT = 6;
        
        public static NetworkMotionResult Approved(ushort sequence)
        {
            return new NetworkMotionResult
            {
                commandSequence = sequence,
                approved = true,
                rejectionReason = REJECT_NONE
            };
        }
        
        public static NetworkMotionResult ApprovedWithCorrection(ushort sequence, Vector3 correctedPosition)
        {
            return new NetworkMotionResult
            {
                commandSequence = sequence,
                approved = true,
                rejectionReason = REJECT_NONE,
                correctedX = Mathf.RoundToInt(correctedPosition.x * 100f),
                correctedY = Mathf.RoundToInt(correctedPosition.y * 100f),
                correctedZ = Mathf.RoundToInt(correctedPosition.z * 100f)
            };
        }
        
        public static NetworkMotionResult Rejected(ushort sequence, byte reason)
        {
            return new NetworkMotionResult
            {
                commandSequence = sequence,
                approved = false,
                rejectionReason = reason
            };
        }
        
        public Vector3 GetCorrectedPosition() => new Vector3(
            correctedX / 100f, 
            correctedY / 100f, 
            correctedZ / 100f
        );
    }
}
