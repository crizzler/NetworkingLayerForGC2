using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Characters.Animim;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compact command to play an animation state on a specific layer.
    /// Used for persistent animations like idle, locomotion states.
    /// Total size: 16 bytes + variable identifier
    /// </summary>
    [Serializable]
    public struct NetworkStateCommand : IEquatable<NetworkStateCommand>
    {
        // Packed flags: [0-2] BlendMode, [3] RootMotion, [4-6] StateType, [7] Reserved
        public byte Flags;
        
        // Layer index (0-255)
        public byte Layer;
        
        // Animation timing packed into half-floats
        public ushort DelayIn;      // Half-float (0-65504 range)
        public ushort Speed;        // Half-float (0-65504 range)
        public ushort Weight;       // Normalized (0-65535 maps to 0-1)
        public ushort TransitionIn; // Half-float
        public ushort TransitionOut;// Half-float
        public ushort Duration;     // Half-float (0 = infinite)
        
        // Animation identifier - can be:
        // - Hash of AnimationClip name (for simple clips)
        // - Hash of State ScriptableObject name
        // - Instance ID (for runtime-created states)
        public int AnimationId;
        
        // CONSTANTS: -----------------------------------------------------------------------------
        
        private const float HALF_FLOAT_MAX = 65504f;
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        public BlendMode BlendMode
        {
            get => (BlendMode)(Flags & 0x07);
            set => Flags = (byte)((Flags & ~0x07) | ((int)value & 0x07));
        }
        
        public bool RootMotion
        {
            get => (Flags & 0x08) != 0;
            set => Flags = value ? (byte)(Flags | 0x08) : (byte)(Flags & ~0x08);
        }
        
        public NetworkStateType StateType
        {
            get => (NetworkStateType)((Flags >> 4) & 0x07);
            set => Flags = (byte)((Flags & ~0x70) | (((int)value & 0x07) << 4));
        }
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkStateCommand Create(
            int animationId,
            NetworkStateType stateType,
            int layer,
            BlendMode blendMode,
            ConfigState config)
        {
            return new NetworkStateCommand
            {
                AnimationId = animationId,
                Layer = (byte)Mathf.Clamp(layer, 0, 255),
                BlendMode = blendMode,
                RootMotion = config.RootMotion,
                StateType = stateType,
                DelayIn = PackHalfFloat(config.DelayIn),
                Speed = PackHalfFloat(config.Speed),
                Weight = (ushort)(Mathf.Clamp01(config.Weight) * 65535f),
                TransitionIn = PackHalfFloat(config.TransitionIn),
                TransitionOut = PackHalfFloat(config.TransitionOut),
                Duration = PackHalfFloat(config.Duration)
            };
        }
        
        public ConfigState ToConfigState()
        {
            return new ConfigState(
                delayIn: UnpackHalfFloat(DelayIn),
                speed: UnpackHalfFloat(Speed),
                weight: Weight / 65535f,
                transitionIn: UnpackHalfFloat(TransitionIn),
                transitionOut: UnpackHalfFloat(TransitionOut)
            )
            {
                Duration = UnpackHalfFloat(Duration),
                RootMotion = RootMotion
            };
        }
        
        // COMPRESSION HELPERS: -------------------------------------------------------------------
        
        private static ushort PackHalfFloat(float value)
        {
            return (ushort)(Mathf.Clamp(value, 0, HALF_FLOAT_MAX) / HALF_FLOAT_MAX * 65535f);
        }
        
        private static float UnpackHalfFloat(ushort packed)
        {
            return packed / 65535f * HALF_FLOAT_MAX;
        }
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkStateCommand other)
        {
            return Flags == other.Flags &&
                   Layer == other.Layer &&
                   AnimationId == other.AnimationId;
        }
        
        public override bool Equals(object obj) => obj is NetworkStateCommand other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Flags, Layer, AnimationId);
    }
    
    /// <summary>
    /// Compact command to play a gesture (one-shot animation).
    /// Total size: 14 bytes
    /// </summary>
    [Serializable]
    public struct NetworkGestureCommand : IEquatable<NetworkGestureCommand>
    {
        // Packed flags: [0-2] BlendMode, [3] RootMotion, [4] StopPrevious, [5-7] Reserved
        public byte Flags;
        
        // Animation timing packed
        public ushort DelayIn;
        public ushort Duration;
        public ushort Speed;
        public ushort TransitionIn;
        public ushort TransitionOut;
        
        // Hash of the AnimationClip name
        public int ClipHash;
        
        // CONSTANTS: -----------------------------------------------------------------------------
        
        private const float HALF_FLOAT_MAX = 65504f;
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        public BlendMode BlendMode
        {
            get => (BlendMode)(Flags & 0x07);
            set => Flags = (byte)((Flags & ~0x07) | ((int)value & 0x07));
        }
        
        public bool RootMotion
        {
            get => (Flags & 0x08) != 0;
            set => Flags = value ? (byte)(Flags | 0x08) : (byte)(Flags & ~0x08);
        }
        
        public bool StopPreviousGestures
        {
            get => (Flags & 0x10) != 0;
            set => Flags = value ? (byte)(Flags | 0x10) : (byte)(Flags & ~0x10);
        }
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkGestureCommand Create(
            int clipHash,
            BlendMode blendMode,
            ConfigGesture config,
            bool stopPrevious)
        {
            return new NetworkGestureCommand
            {
                ClipHash = clipHash,
                BlendMode = blendMode,
                RootMotion = config.RootMotion,
                StopPreviousGestures = stopPrevious,
                DelayIn = PackHalfFloat(config.DelayIn),
                Duration = PackHalfFloat(config.Duration),
                Speed = PackHalfFloat(config.Speed),
                TransitionIn = PackHalfFloat(config.TransitionIn),
                TransitionOut = PackHalfFloat(config.TransitionOut)
            };
        }
        
        public ConfigGesture ToConfigGesture()
        {
            return new ConfigGesture(
                delayIn: UnpackHalfFloat(DelayIn),
                duration: UnpackHalfFloat(Duration),
                speed: UnpackHalfFloat(Speed),
                rootMotion: RootMotion,
                transitionIn: UnpackHalfFloat(TransitionIn),
                transitionOut: UnpackHalfFloat(TransitionOut)
            );
        }
        
        // COMPRESSION HELPERS: -------------------------------------------------------------------
        
        private static ushort PackHalfFloat(float value)
        {
            return (ushort)(Mathf.Clamp(value, 0, HALF_FLOAT_MAX) / HALF_FLOAT_MAX * 65535f);
        }
        
        private static float UnpackHalfFloat(ushort packed)
        {
            return packed / 65535f * HALF_FLOAT_MAX;
        }
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkGestureCommand other)
        {
            return ClipHash == other.ClipHash && Flags == other.Flags;
        }
        
        public override bool Equals(object obj) => obj is NetworkGestureCommand other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ClipHash, Flags);
    }
    
    /// <summary>
    /// Command to stop an animation state on a layer.
    /// Total size: 6 bytes
    /// </summary>
    [Serializable]
    public struct NetworkStopStateCommand
    {
        public byte Layer;
        public ushort Delay;
        public ushort TransitionOut;
        
        private const float HALF_FLOAT_MAX = 65504f;
        
        public static NetworkStopStateCommand Create(int layer, float delay, float transitionOut)
        {
            return new NetworkStopStateCommand
            {
                Layer = (byte)Mathf.Clamp(layer, 0, 255),
                Delay = (ushort)(Mathf.Clamp(delay, 0, HALF_FLOAT_MAX) / HALF_FLOAT_MAX * 65535f),
                TransitionOut = (ushort)(Mathf.Clamp(transitionOut, 0, HALF_FLOAT_MAX) / HALF_FLOAT_MAX * 65535f)
            };
        }
        
        public float GetDelay() => Delay / 65535f * HALF_FLOAT_MAX;
        public float GetTransitionOut() => TransitionOut / 65535f * HALF_FLOAT_MAX;
    }
    
    /// <summary>
    /// Command to stop gestures.
    /// Total size: 5 bytes
    /// </summary>
    [Serializable]
    public struct NetworkStopGestureCommand
    {
        // If 0, stops all gestures. If non-zero, stops specific clip.
        public int ClipHash;
        public ushort Delay;
        public ushort TransitionOut;
        
        private const float HALF_FLOAT_MAX = 65504f;
        
        public static NetworkStopGestureCommand Create(int clipHash, float delay, float transitionOut)
        {
            return new NetworkStopGestureCommand
            {
                ClipHash = clipHash,
                Delay = (ushort)(Mathf.Clamp(delay, 0, HALF_FLOAT_MAX) / HALF_FLOAT_MAX * 65535f),
                TransitionOut = (ushort)(Mathf.Clamp(transitionOut, 0, HALF_FLOAT_MAX) / HALF_FLOAT_MAX * 65535f)
            };
        }
        
        public float GetDelay() => Delay / 65535f * HALF_FLOAT_MAX;
        public float GetTransitionOut() => TransitionOut / 65535f * HALF_FLOAT_MAX;
    }
    
    /// <summary>
    /// Type of animation state source.
    /// </summary>
    public enum NetworkStateType : byte
    {
        AnimationClip = 0,
        RuntimeController = 1,
        StateAsset = 2
    }
    
    /// <summary>
    /// Registry for mapping animation assets to network-safe IDs.
    /// This allows efficient transmission of animation references.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NetworkAnimationRegistry",
        menuName = "Game Creator/Network/Animation Registry")]
    public class NetworkAnimationRegistry : ScriptableObject
    {
        [Serializable]
        public struct AnimationEntry
        {
            public int NetworkId;
            public AnimationClip Clip;
            public State StateAsset;
            public RuntimeAnimatorController Controller;
        }
        
        [SerializeField] private AnimationEntry[] m_Entries = Array.Empty<AnimationEntry>();
        
        private System.Collections.Generic.Dictionary<int, AnimationEntry> m_LookupById;
        private System.Collections.Generic.Dictionary<int, int> m_HashToId;
        
        // INITIALIZATION: ------------------------------------------------------------------------
        
        private void OnEnable()
        {
            BuildLookups();
        }
        
        private void BuildLookups()
        {
            m_LookupById = new System.Collections.Generic.Dictionary<int, AnimationEntry>();
            m_HashToId = new System.Collections.Generic.Dictionary<int, int>();
            
            foreach (var entry in m_Entries)
            {
                m_LookupById[entry.NetworkId] = entry;
                
                if (entry.Clip != null)
                    m_HashToId[StableHashUtility.GetStableHash(entry.Clip)] = entry.NetworkId;
                if (entry.StateAsset != null)
                    m_HashToId[StableHashUtility.GetStableHash(entry.StateAsset)] = entry.NetworkId;
                if (entry.Controller != null)
                    m_HashToId[StableHashUtility.GetStableHash(entry.Controller)] = entry.NetworkId;
            }
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        public bool TryGetEntry(int networkId, out AnimationEntry entry)
        {
            if (m_LookupById == null) BuildLookups();
            return m_LookupById.TryGetValue(networkId, out entry);
        }
        
        public bool TryGetNetworkId(AnimationClip clip, out int networkId)
        {
            if (m_HashToId == null) BuildLookups();
            return m_HashToId.TryGetValue(StableHashUtility.GetStableHash(clip), out networkId);
        }
        
        public bool TryGetNetworkId(State state, out int networkId)
        {
            if (m_HashToId == null) BuildLookups();
            return m_HashToId.TryGetValue(StableHashUtility.GetStableHash(state), out networkId);
        }
        
        public int GetClipHash(AnimationClip clip) => StableHashUtility.GetStableHash(clip);
        public int GetStateHash(State state) => StableHashUtility.GetStableHash(state);
    }
}
