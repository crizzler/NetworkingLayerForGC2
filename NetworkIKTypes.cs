using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compact network state for RigLookTo IK system.
    /// Syncs the look target position for remote players.
    /// Total size: 13 bytes
    /// </summary>
    [Serializable]
    public struct NetworkLookToState : IEquatable<NetworkLookToState>
    {
        // Packed flags: [0] HasTarget, [1-7] Reserved
        public byte Flags;
        
        // Target position (compressed to half-floats for XZ, full float for Y)
        // World position, centered relative to character for better precision
        public short TargetX;      // Relative to character, ±327m range at 0.01m precision
        public short TargetY;      // Absolute Y (±327m range)
        public short TargetZ;      // Relative to character
        
        // Weight (0-255 maps to 0-1)
        public byte Weight;
        
        // Target layer (for priority)
        public byte Layer;
        
        // Optional: target network object ID (if tracking a networked entity)
        public ushort TargetNetworkId;
        
        // CONSTANTS: -----------------------------------------------------------------------------
        
        private const float POSITION_SCALE = 100f; // 0.01m precision, ±327m range
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        public bool HasTarget
        {
            get => (Flags & 0x01) != 0;
            set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
        }
        
        public bool HasNetworkTarget
        {
            get => (Flags & 0x02) != 0;
            set => Flags = value ? (byte)(Flags | 0x02) : (byte)(Flags & ~0x02);
        }
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        /// <summary>
        /// Create a look state targeting a world position.
        /// </summary>
        public static NetworkLookToState Create(
            Vector3 targetPosition,
            Vector3 characterPosition,
            float weight,
            int layer)
        {
            Vector3 relative = targetPosition - characterPosition;
            
            return new NetworkLookToState
            {
                Flags = 0x01, // HasTarget = true
                TargetX = PackPosition(relative.x),
                TargetY = PackPosition(targetPosition.y), // Y is absolute
                TargetZ = PackPosition(relative.z),
                Weight = (byte)(Mathf.Clamp01(weight) * 255f),
                Layer = (byte)Mathf.Clamp(layer, 0, 255),
                TargetNetworkId = 0
            };
        }
        
        /// <summary>
        /// Create a look state targeting a networked object.
        /// </summary>
        public static NetworkLookToState CreateWithNetworkTarget(
            ushort networkObjectId,
            float weight,
            int layer)
        {
            return new NetworkLookToState
            {
                Flags = 0x03, // HasTarget | HasNetworkTarget
                TargetNetworkId = networkObjectId,
                Weight = (byte)(Mathf.Clamp01(weight) * 255f),
                Layer = (byte)Mathf.Clamp(layer, 0, 255)
            };
        }
        
        /// <summary>
        /// Create an empty state (no look target).
        /// </summary>
        public static NetworkLookToState CreateEmpty()
        {
            return new NetworkLookToState { Flags = 0 };
        }
        
        /// <summary>
        /// Get world position from state.
        /// </summary>
        public Vector3 GetTargetPosition(Vector3 characterPosition)
        {
            return new Vector3(
                characterPosition.x + UnpackPosition(TargetX),
                UnpackPosition(TargetY), // Y is absolute
                characterPosition.z + UnpackPosition(TargetZ)
            );
        }
        
        public float GetWeight() => Weight / 255f;
        
        // COMPRESSION: ---------------------------------------------------------------------------
        
        private static short PackPosition(float value)
        {
            return (short)Mathf.Clamp(value * POSITION_SCALE, short.MinValue, short.MaxValue);
        }
        
        private static float UnpackPosition(short packed)
        {
            return packed / POSITION_SCALE;
        }
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkLookToState other)
        {
            return Flags == other.Flags &&
                   TargetX == other.TargetX &&
                   TargetY == other.TargetY &&
                   TargetZ == other.TargetZ &&
                   TargetNetworkId == other.TargetNetworkId;
        }
        
        public override bool Equals(object obj) => obj is NetworkLookToState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Flags, TargetX, TargetY, TargetZ, TargetNetworkId);
    }
    
    /// <summary>
    /// Compact network state for RigAimTowards IK system.
    /// Syncs the aim pitch/yaw for remote players.
    /// Total size: 5 bytes
    /// </summary>
    [Serializable]
    public struct NetworkAimState : IEquatable<NetworkAimState>
    {
        // Pitch angle (-90 to +90 degrees, packed)
        public short Pitch; // ±90° at 0.01° precision
        
        // Yaw angle (-180 to +180 degrees, packed)
        public short Yaw;   // ±180° at 0.01° precision
        
        // Weight (0-255)
        public byte Weight;
        
        // CONSTANTS: -----------------------------------------------------------------------------
        
        private const float ANGLE_SCALE = 100f; // 0.01° precision
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkAimState Create(float pitch, float yaw, float weight)
        {
            return new NetworkAimState
            {
                Pitch = (short)(Mathf.Clamp(pitch, -90f, 90f) * ANGLE_SCALE),
                Yaw = (short)(Mathf.Clamp(yaw, -180f, 180f) * ANGLE_SCALE),
                Weight = (byte)(Mathf.Clamp01(weight) * 255f)
            };
        }
        
        public float GetPitch() => Pitch / ANGLE_SCALE;
        public float GetYaw() => Yaw / ANGLE_SCALE;
        public float GetWeight() => Weight / 255f;
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkAimState other)
        {
            return Pitch == other.Pitch && Yaw == other.Yaw;
        }
        
        public override bool Equals(object obj) => obj is NetworkAimState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Pitch, Yaw);
    }
    
    /// <summary>
    /// Combined IK state for efficient batch transmission.
    /// Contains all synced IK states in one packet.
    /// Total size: ~20 bytes (varies based on active rigs)
    /// </summary>
    [Serializable]
    public struct NetworkIKState : IEquatable<NetworkIKState>
    {
        // Bitfield indicating which IK systems are active
        // [0] LookTo, [1] Aim, [2-7] Reserved for future IK types
        public byte ActiveRigs;
        
        // IK states (only relevant if corresponding bit is set)
        public NetworkLookToState LookTo;
        public NetworkAimState Aim;
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        public bool HasLookTo
        {
            get => (ActiveRigs & 0x01) != 0;
            set => ActiveRigs = value ? (byte)(ActiveRigs | 0x01) : (byte)(ActiveRigs & ~0x01);
        }
        
        public bool HasAim
        {
            get => (ActiveRigs & 0x02) != 0;
            set => ActiveRigs = value ? (byte)(ActiveRigs | 0x02) : (byte)(ActiveRigs & ~0x02);
        }
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkIKState CreateEmpty()
        {
            return new NetworkIKState { ActiveRigs = 0 };
        }
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkIKState other)
        {
            if (ActiveRigs != other.ActiveRigs) return false;
            if (HasLookTo && !LookTo.Equals(other.LookTo)) return false;
            if (HasAim && !Aim.Equals(other.Aim)) return false;
            return true;
        }
        
        public override bool Equals(object obj) => obj is NetworkIKState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ActiveRigs, LookTo, Aim);
    }
    
    /// <summary>
    /// Configuration for IK network sync behavior.
    /// </summary>
    [Serializable]
    public struct NetworkIKConfig
    {
        [Tooltip("Send rate for IK updates (Hz)")]
        public float SendRate;
        
        [Tooltip("Only send when IK state changes significantly")]
        public bool DeltaCompression;
        
        [Tooltip("Minimum position change to trigger update (meters)")]
        public float PositionThreshold;
        
        [Tooltip("Minimum angle change to trigger update (degrees)")]
        public float AngleThreshold;
        
        [Tooltip("Smooth interpolation time for remote IK (seconds)")]
        public float InterpolationTime;
        
        public static NetworkIKConfig Default => new NetworkIKConfig
        {
            SendRate = 20f,
            DeltaCompression = true,
            PositionThreshold = 0.05f,
            AngleThreshold = 2f,
            InterpolationTime = 0.1f
        };
    }
}
