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
        
        // Animation timing packed as fixed-point values with 0.001 precision.
        public ushort DelayIn;
        public ushort Speed;
        public ushort Weight;       // Normalized (0-65535 maps to 0-1)
        public ushort TransitionIn;
        public ushort TransitionOut;
        public ushort Duration;     // 0 = infinite
        
        // Animation identifier - can be:
        // - Hash of AnimationClip name (for simple clips)
        // - Hash of State ScriptableObject name
        // - Instance ID (for runtime-created states)
        public int AnimationId;
        
        // CONSTANTS: -----------------------------------------------------------------------------
        
        private const float PACK_SCALE = 1000f;
        private const float PACK_MAX = 65535f / PACK_SCALE;
        
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
                DelayIn = PackFixedFloat(config.DelayIn),
                Speed = PackFixedFloat(config.Speed),
                Weight = (ushort)(Mathf.Clamp01(config.Weight) * 65535f),
                TransitionIn = PackFixedFloat(config.TransitionIn),
                TransitionOut = PackFixedFloat(config.TransitionOut),
                Duration = PackFixedFloat(config.Duration)
            };
        }
        
        public ConfigState ToConfigState()
        {
            return new ConfigState(
                delayIn: UnpackFixedFloat(DelayIn),
                speed: UnpackFixedFloat(Speed),
                weight: Weight / 65535f,
                transitionIn: UnpackFixedFloat(TransitionIn),
                transitionOut: UnpackFixedFloat(TransitionOut)
            )
            {
                Duration = UnpackFixedFloat(Duration),
                RootMotion = RootMotion
            };
        }
        
        // COMPRESSION HELPERS: -------------------------------------------------------------------
        
        private static ushort PackFixedFloat(float value)
        {
            return (ushort)Mathf.RoundToInt(Mathf.Clamp(value, 0f, PACK_MAX) * PACK_SCALE);
        }
        
        private static float UnpackFixedFloat(ushort packed)
        {
            return packed / PACK_SCALE;
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
        
        private const float PACK_SCALE = 1000f;
        private const float PACK_MAX = 65535f / PACK_SCALE;
        
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
                DelayIn = PackFixedFloat(config.DelayIn),
                Duration = PackFixedFloat(config.Duration),
                Speed = PackFixedFloat(config.Speed),
                TransitionIn = PackFixedFloat(config.TransitionIn),
                TransitionOut = PackFixedFloat(config.TransitionOut)
            };
        }
        
        public ConfigGesture ToConfigGesture()
        {
            return new ConfigGesture(
                delayIn: UnpackFixedFloat(DelayIn),
                duration: UnpackFixedFloat(Duration),
                speed: UnpackFixedFloat(Speed),
                rootMotion: RootMotion,
                transitionIn: UnpackFixedFloat(TransitionIn),
                transitionOut: UnpackFixedFloat(TransitionOut)
            );
        }
        
        // COMPRESSION HELPERS: -------------------------------------------------------------------
        
        private static ushort PackFixedFloat(float value)
        {
            return (ushort)Mathf.RoundToInt(Mathf.Clamp(value, 0f, PACK_MAX) * PACK_SCALE);
        }
        
        private static float UnpackFixedFloat(ushort packed)
        {
            return packed / PACK_SCALE;
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
        
        private const float PACK_SCALE = 1000f;
        private const float PACK_MAX = 65535f / PACK_SCALE;
        
        public static NetworkStopStateCommand Create(int layer, float delay, float transitionOut)
        {
            return new NetworkStopStateCommand
            {
                Layer = (byte)Mathf.Clamp(layer, 0, 255),
                Delay = PackFixedFloat(delay),
                TransitionOut = PackFixedFloat(transitionOut)
            };
        }
        
        public float GetDelay() => Delay / PACK_SCALE;
        public float GetTransitionOut() => TransitionOut / PACK_SCALE;

        private static ushort PackFixedFloat(float value)
        {
            return (ushort)Mathf.RoundToInt(Mathf.Clamp(value, 0f, PACK_MAX) * PACK_SCALE);
        }
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
        
        private const float PACK_SCALE = 1000f;
        private const float PACK_MAX = 65535f / PACK_SCALE;
        
        public static NetworkStopGestureCommand Create(int clipHash, float delay, float transitionOut)
        {
            return new NetworkStopGestureCommand
            {
                ClipHash = clipHash,
                Delay = PackFixedFloat(delay),
                TransitionOut = PackFixedFloat(transitionOut)
            };
        }
        
        public float GetDelay() => Delay / PACK_SCALE;
        public float GetTransitionOut() => TransitionOut / PACK_SCALE;

        private static ushort PackFixedFloat(float value)
        {
            return (ushort)Mathf.RoundToInt(Mathf.Clamp(value, 0f, PACK_MAX) * PACK_SCALE);
        }
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
        
        public bool TryGetEntry(int networkIdOrStableHash, out AnimationEntry entry)
        {
            if (m_LookupById == null) BuildLookups();

            if (m_LookupById.TryGetValue(networkIdOrStableHash, out entry))
            {
                return true;
            }

            if (m_HashToId != null &&
                m_HashToId.TryGetValue(networkIdOrStableHash, out int networkId))
            {
                return m_LookupById.TryGetValue(networkId, out entry);
            }

            entry = default;
            return false;
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

        public bool TryGetNetworkId(RuntimeAnimatorController controller, out int networkId)
        {
            if (m_HashToId == null) BuildLookups();
            return m_HashToId.TryGetValue(StableHashUtility.GetStableHash(controller), out networkId);
        }
        
        public int GetClipHash(AnimationClip clip) => StableHashUtility.GetStableHash(clip);
        public int GetStateHash(State state) => StableHashUtility.GetStableHash(state);
    }
}
