using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compressed NavMesh destination command.
    /// Sent from client to server to request pathfinding.
    /// Total size: 14 bytes
    /// </summary>
    [Serializable]
    public struct NetworkNavMeshCommand : IEquatable<NetworkNavMeshCommand>
    {
        // Command type
        public byte CommandType;
        
        // Sequence number for ordering
        public ushort Sequence;
        
        // Target position (fixed-point, 0.01m precision)
        public int TargetX;
        public int TargetY;
        public int TargetZ;
        
        // Packed flags: [0] StopImmediately, [1-7] Reserved
        public byte Flags;
        
        // COMMAND TYPES: -------------------------------------------------------------------------
        
        public const byte CMD_MOVE_TO_POSITION = 0;
        public const byte CMD_MOVE_TO_DIRECTION = 1;
        public const byte CMD_STOP = 2;
        public const byte CMD_WARP = 3;
        
        // FLAGS: ---------------------------------------------------------------------------------
        
        public const byte FLAG_STOP_IMMEDIATE = 1;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkNavMeshCommand CreateMoveToPosition(Vector3 target, ushort sequence)
        {
            return new NetworkNavMeshCommand
            {
                CommandType = CMD_MOVE_TO_POSITION,
                Sequence = sequence,
                TargetX = Mathf.RoundToInt(target.x * 100f),
                TargetY = Mathf.RoundToInt(target.y * 100f),
                TargetZ = Mathf.RoundToInt(target.z * 100f),
                Flags = 0
            };
        }
        
        public static NetworkNavMeshCommand CreateMoveToDirection(Vector3 direction, ushort sequence)
        {
            // Direction is normalized, so we can use higher precision
            return new NetworkNavMeshCommand
            {
                CommandType = CMD_MOVE_TO_DIRECTION,
                Sequence = sequence,
                TargetX = Mathf.RoundToInt(direction.x * 10000f),
                TargetY = Mathf.RoundToInt(direction.y * 10000f),
                TargetZ = Mathf.RoundToInt(direction.z * 10000f),
                Flags = 0
            };
        }
        
        public static NetworkNavMeshCommand CreateStop(ushort sequence, bool immediate = false)
        {
            return new NetworkNavMeshCommand
            {
                CommandType = CMD_STOP,
                Sequence = sequence,
                Flags = immediate ? FLAG_STOP_IMMEDIATE : (byte)0
            };
        }
        
        public static NetworkNavMeshCommand CreateWarp(Vector3 position, ushort sequence)
        {
            return new NetworkNavMeshCommand
            {
                CommandType = CMD_WARP,
                Sequence = sequence,
                TargetX = Mathf.RoundToInt(position.x * 100f),
                TargetY = Mathf.RoundToInt(position.y * 100f),
                TargetZ = Mathf.RoundToInt(position.z * 100f),
                Flags = 0
            };
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public Vector3 GetTargetPosition()
        {
            return new Vector3(TargetX / 100f, TargetY / 100f, TargetZ / 100f);
        }
        
        public Vector3 GetDirection()
        {
            return new Vector3(TargetX / 10000f, TargetY / 10000f, TargetZ / 10000f);
        }
        
        public bool HasFlag(byte flag) => (Flags & flag) != 0;
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkNavMeshCommand other)
        {
            return CommandType == other.CommandType && Sequence == other.Sequence;
        }
        
        public override bool Equals(object obj) => obj is NetworkNavMeshCommand other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(CommandType, Sequence);
    }
    
    /// <summary>
    /// Compressed NavMesh path state from server.
    /// Contains path corners for client to follow.
    /// Variable size: 8 + (6 * cornerCount) bytes
    /// </summary>
    [Serializable]
    public struct NetworkNavMeshPathState : IEquatable<NetworkNavMeshPathState>
    {
        // Sequence number this responds to
        public ushort CommandSequence;
        
        // Path status
        public byte PathStatus; // 0=Complete, 1=Partial, 2=Invalid
        
        // Number of corners in path
        public byte CornerCount;
        
        // Current agent position (for validation)
        public int PositionX;
        public int PositionY;
        public int PositionZ;
        
        // Rotation Y
        public ushort RotationY;
        
        // Path corners (fixed-point, up to 16 corners)
        // Each corner is 6 bytes (short x, short y, short z relative to first corner)
        public short[] CornerOffsets; // Packed as [x0,y0,z0, x1,y1,z1, ...]
        
        // PATH STATUS: ---------------------------------------------------------------------------
        
        public const byte STATUS_COMPLETE = 0;
        public const byte STATUS_PARTIAL = 1;
        public const byte STATUS_INVALID = 2;
        public const byte STATUS_NONE = 3;
        
        // Max corners to sync (reasonable for most paths)
        public const int MAX_CORNERS = 16;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkNavMeshPathState Create(
            Vector3 agentPosition,
            float rotationY,
            ushort commandSequence,
            byte pathStatus,
            Vector3[] corners)
        {
            var state = new NetworkNavMeshPathState
            {
                CommandSequence = commandSequence,
                PathStatus = pathStatus,
                PositionX = Mathf.RoundToInt(agentPosition.x * 100f),
                PositionY = Mathf.RoundToInt(agentPosition.y * 100f),
                PositionZ = Mathf.RoundToInt(agentPosition.z * 100f),
                RotationY = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f)
            };
            
            if (corners != null && corners.Length > 0)
            {
                int count = Mathf.Min(corners.Length, MAX_CORNERS);
                state.CornerCount = (byte)count;
                state.CornerOffsets = new short[count * 3];
                
                // First corner is absolute (relative to agent position for better precision)
                Vector3 basePos = agentPosition;
                
                for (int i = 0; i < count; i++)
                {
                    Vector3 offset = corners[i] - basePos;
                    state.CornerOffsets[i * 3 + 0] = (short)Mathf.Clamp(offset.x * 100f, -32767f, 32767f);
                    state.CornerOffsets[i * 3 + 1] = (short)Mathf.Clamp(offset.y * 100f, -32767f, 32767f);
                    state.CornerOffsets[i * 3 + 2] = (short)Mathf.Clamp(offset.z * 100f, -32767f, 32767f);
                }
            }
            else
            {
                state.CornerCount = 0;
                state.CornerOffsets = Array.Empty<short>();
            }
            
            return state;
        }
        
        public static NetworkNavMeshPathState CreateNoPath(Vector3 position, float rotationY, ushort sequence)
        {
            return new NetworkNavMeshPathState
            {
                CommandSequence = sequence,
                PathStatus = STATUS_NONE,
                CornerCount = 0,
                CornerOffsets = Array.Empty<short>(),
                PositionX = Mathf.RoundToInt(position.x * 100f),
                PositionY = Mathf.RoundToInt(position.y * 100f),
                PositionZ = Mathf.RoundToInt(position.z * 100f),
                RotationY = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f)
            };
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public Vector3 GetPosition()
        {
            return new Vector3(PositionX / 100f, PositionY / 100f, PositionZ / 100f);
        }
        
        public float GetRotationY()
        {
            return RotationY / 65535f * 360f;
        }
        
        public Vector3[] GetCorners()
        {
            if (CornerOffsets == null || CornerCount == 0)
                return Array.Empty<Vector3>();
            
            Vector3 basePos = GetPosition();
            Vector3[] corners = new Vector3[CornerCount];
            
            for (int i = 0; i < CornerCount; i++)
            {
                corners[i] = basePos + new Vector3(
                    CornerOffsets[i * 3 + 0] / 100f,
                    CornerOffsets[i * 3 + 1] / 100f,
                    CornerOffsets[i * 3 + 2] / 100f
                );
            }
            
            return corners;
        }
        
        public bool IsPathValid => PathStatus == STATUS_COMPLETE || PathStatus == STATUS_PARTIAL;
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkNavMeshPathState other)
        {
            return CommandSequence == other.CommandSequence && PathStatus == other.PathStatus;
        }
        
        public override bool Equals(object obj) => obj is NetworkNavMeshPathState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(CommandSequence, PathStatus);
    }
    
    /// <summary>
    /// Lightweight position update for NavMesh agents between path syncs.
    /// Total size: 10 bytes
    /// </summary>
    [Serializable]
    public struct NetworkNavMeshPositionUpdate : IEquatable<NetworkNavMeshPositionUpdate>
    {
        // Current position (fixed-point)
        public int PositionX;
        public int PositionY;
        public int PositionZ;
        
        // Rotation Y
        public ushort RotationY;
        
        // Current corner index agent is moving towards
        public byte CurrentCornerIndex;
        
        // Movement speed (for interpolation)
        public byte SpeedPercent; // 0-255 maps to 0-100% of max speed
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkNavMeshPositionUpdate Create(
            Vector3 position,
            float rotationY,
            int cornerIndex,
            float currentSpeed,
            float maxSpeed)
        {
            return new NetworkNavMeshPositionUpdate
            {
                PositionX = Mathf.RoundToInt(position.x * 100f),
                PositionY = Mathf.RoundToInt(position.y * 100f),
                PositionZ = Mathf.RoundToInt(position.z * 100f),
                RotationY = (ushort)(Mathf.Repeat(rotationY, 360f) / 360f * 65535f),
                CurrentCornerIndex = (byte)Mathf.Clamp(cornerIndex, 0, 255),
                SpeedPercent = (byte)(maxSpeed > 0 ? Mathf.Clamp01(currentSpeed / maxSpeed) * 255f : 0)
            };
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public Vector3 GetPosition()
        {
            return new Vector3(PositionX / 100f, PositionY / 100f, PositionZ / 100f);
        }
        
        public float GetRotationY()
        {
            return RotationY / 65535f * 360f;
        }
        
        public float GetSpeedPercent()
        {
            return SpeedPercent / 255f;
        }
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkNavMeshPositionUpdate other)
        {
            return PositionX == other.PositionX && 
                   PositionY == other.PositionY && 
                   PositionZ == other.PositionZ;
        }
        
        public override bool Equals(object obj) => obj is NetworkNavMeshPositionUpdate other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(PositionX, PositionY, PositionZ);
    }
    
    /// <summary>
    /// Configuration for NavMesh network sync.
    /// </summary>
    [Serializable]
    public struct NetworkNavMeshConfig
    {
        [Tooltip("Position updates per second when moving")]
        public float PositionSendRate;
        
        [Tooltip("Minimum distance change to send position update")]
        public float PositionThreshold;
        
        [Tooltip("Distance threshold to teleport instead of interpolate")]
        public float TeleportThreshold;
        
        [Tooltip("Interpolation buffer time in seconds")]
        public float InterpolationBuffer;
        
        [Tooltip("How long to extrapolate before snapping")]
        public float MaxExtrapolationTime;
        
        [Tooltip("Enable path corner sync (disable for server-only pathfinding)")]
        public bool SyncPathCorners;
        
        /// <summary>
        /// Default settings for AI/NPC characters.
        /// Lower update rate, higher thresholds.
        /// </summary>
        public static NetworkNavMeshConfig Default => new NetworkNavMeshConfig
        {
            PositionSendRate = 20f,
            PositionThreshold = 0.05f,
            TeleportThreshold = 5f,
            InterpolationBuffer = 0.1f,
            MaxExtrapolationTime = 0.5f,
            SyncPathCorners = true
        };
        
        /// <summary>
        /// Settings optimized for player-controlled click-to-move characters.
        /// Higher update rate, lower thresholds for responsiveness.
        /// </summary>
        public static NetworkNavMeshConfig Player => new NetworkNavMeshConfig
        {
            PositionSendRate = 30f,
            PositionThreshold = 0.03f,
            TeleportThreshold = 3f,
            InterpolationBuffer = 0.05f,
            MaxExtrapolationTime = 0.3f,
            SyncPathCorners = true
        };
        
        /// <summary>
        /// Settings optimized for large numbers of AI agents.
        /// Lower update rate to reduce bandwidth.
        /// </summary>
        public static NetworkNavMeshConfig MassAI => new NetworkNavMeshConfig
        {
            PositionSendRate = 10f,
            PositionThreshold = 0.1f,
            TeleportThreshold = 5f,
            InterpolationBuffer = 0.15f,
            MaxExtrapolationTime = 0.5f,
            SyncPathCorners = true
        };
    }
}
