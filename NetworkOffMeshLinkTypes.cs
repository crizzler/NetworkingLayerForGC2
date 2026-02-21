using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Types of off-mesh link traversal.
    /// Used to determine how clients should animate the traversal.
    /// </summary>
    public enum OffMeshLinkTraversalType : byte
    {
        /// <summary>Auto traverse - simple linear movement between points</summary>
        Auto = 0,
        
        /// <summary>Jump - parabolic arc movement</summary>
        Jump = 1,
        
        /// <summary>Climb - vertical movement with climbing animation</summary>
        Climb = 2,
        
        /// <summary>Drop - falling movement, potentially with landing</summary>
        Drop = 3,
        
        /// <summary>Ladder - vertical ladder traversal</summary>
        Ladder = 4,
        
        /// <summary>Crawl - horizontal low traversal (under obstacles)</summary>
        Crawl = 5,
        
        /// <summary>Vault - quick obstacle hop</summary>
        Vault = 6,
        
        /// <summary>Swim - water traversal</summary>
        Swim = 7,
        
        /// <summary>Teleport - instant position change (no interpolation)</summary>
        Teleport = 8,
        
        /// <summary>Custom - uses custom animation curve data</summary>
        Custom = 255
    }
    
    /// <summary>
    /// Compressed off-mesh link traversal start command.
    /// Sent from server when agent begins traversing an off-mesh link.
    /// Total size: 28 bytes
    /// </summary>
    [Serializable]
    public struct NetworkOffMeshLinkStart : IEquatable<NetworkOffMeshLinkStart>
    {
        // Link identification
        /// <summary>Unique ID for this link instance (hash of GameObject)</summary>
        public int LinkId;
        
        /// <summary>Sequence number for ordering</summary>
        public ushort Sequence;
        
        /// <summary>Type of traversal (determines animation)</summary>
        public byte TraversalType;
        
        /// <summary>Packed flags</summary>
        public byte Flags;
        
        // Start position (fixed-point, 0.01m precision)
        public int StartX;
        public int StartY;
        public int StartZ;
        
        // End position (fixed-point, 0.01m precision)
        public int EndX;
        public int EndY;
        public int EndZ;
        
        /// <summary>Expected duration in centiseconds (0.01s precision, max ~655 seconds)</summary>
        public ushort DurationCs;
        
        /// <summary>Server timestamp when traversal started (for sync)</summary>
        public float ServerTime;
        
        // FLAGS: ---------------------------------------------------------------------------------
        
        /// <summary>Traversal is ascending (going up)</summary>
        public const byte FLAG_ASCENDING = 1;
        
        /// <summary>Traversal uses root motion (animation-driven movement)</summary>
        public const byte FLAG_ROOT_MOTION = 2;
        
        /// <summary>Traversal should snap agent to end on completion</summary>
        public const byte FLAG_SNAP_END = 4;
        
        /// <summary>Link has custom animation data (NetworkOffMeshLinkAnimation follows)</summary>
        public const byte FLAG_HAS_ANIMATION = 8;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkOffMeshLinkStart Create(
            int linkId,
            ushort sequence,
            Vector3 startPosition,
            Vector3 endPosition,
            float duration,
            OffMeshLinkTraversalType traversalType,
            bool ascending,
            bool rootMotion,
            bool snapEnd,
            bool hasAnimation,
            float serverTime)
        {
            byte flags = 0;
            if (ascending) flags |= FLAG_ASCENDING;
            if (rootMotion) flags |= FLAG_ROOT_MOTION;
            if (snapEnd) flags |= FLAG_SNAP_END;
            if (hasAnimation) flags |= FLAG_HAS_ANIMATION;
            
            return new NetworkOffMeshLinkStart
            {
                LinkId = linkId,
                Sequence = sequence,
                TraversalType = (byte)traversalType,
                Flags = flags,
                StartX = Mathf.RoundToInt(startPosition.x * 100f),
                StartY = Mathf.RoundToInt(startPosition.y * 100f),
                StartZ = Mathf.RoundToInt(startPosition.z * 100f),
                EndX = Mathf.RoundToInt(endPosition.x * 100f),
                EndY = Mathf.RoundToInt(endPosition.y * 100f),
                EndZ = Mathf.RoundToInt(endPosition.z * 100f),
                DurationCs = (ushort)Mathf.Clamp(duration * 100f, 0f, 65535f),
                ServerTime = serverTime
            };
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public Vector3 GetStartPosition() => new Vector3(StartX / 100f, StartY / 100f, StartZ / 100f);
        public Vector3 GetEndPosition() => new Vector3(EndX / 100f, EndY / 100f, EndZ / 100f);
        public float GetDuration() => DurationCs / 100f;
        public OffMeshLinkTraversalType GetTraversalType() => (OffMeshLinkTraversalType)TraversalType;
        
        public bool IsAscending => (Flags & FLAG_ASCENDING) != 0;
        public bool UsesRootMotion => (Flags & FLAG_ROOT_MOTION) != 0;
        public bool SnapToEnd => (Flags & FLAG_SNAP_END) != 0;
        public bool HasAnimation => (Flags & FLAG_HAS_ANIMATION) != 0;
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkOffMeshLinkStart other)
        {
            return LinkId == other.LinkId && Sequence == other.Sequence;
        }
        
        public override bool Equals(object obj) => obj is NetworkOffMeshLinkStart other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LinkId, Sequence);
    }
    
    /// <summary>
    /// Off-mesh link progress update.
    /// Sent periodically during traversal to sync position.
    /// Total size: 8 bytes
    /// </summary>
    [Serializable]
    public struct NetworkOffMeshLinkProgress : IEquatable<NetworkOffMeshLinkProgress>
    {
        /// <summary>Link ID being traversed</summary>
        public int LinkId;
        
        /// <summary>Progress through traversal (0-65535 maps to 0-1)</summary>
        public ushort Progress;
        
        /// <summary>Reserved for future use</summary>
        public ushort Reserved;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkOffMeshLinkProgress Create(int linkId, float progress)
        {
            return new NetworkOffMeshLinkProgress
            {
                LinkId = linkId,
                Progress = (ushort)(Mathf.Clamp01(progress) * 65535f),
                Reserved = 0
            };
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public float GetProgress() => Progress / 65535f;
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkOffMeshLinkProgress other) => LinkId == other.LinkId;
        public override bool Equals(object obj) => obj is NetworkOffMeshLinkProgress other && Equals(other);
        public override int GetHashCode() => LinkId.GetHashCode();
    }
    
    /// <summary>
    /// Off-mesh link traversal completion.
    /// Sent when agent finishes traversing a link.
    /// Total size: 16 bytes
    /// </summary>
    [Serializable]
    public struct NetworkOffMeshLinkComplete : IEquatable<NetworkOffMeshLinkComplete>
    {
        /// <summary>Link ID that was traversed</summary>
        public int LinkId;
        
        /// <summary>Sequence matching the start command</summary>
        public ushort Sequence;
        
        /// <summary>Completion status</summary>
        public byte Status;
        
        /// <summary>Reserved</summary>
        public byte Reserved;
        
        // Final position (fixed-point)
        public int FinalX;
        public int FinalY;
        public int FinalZ;
        
        // STATUS: --------------------------------------------------------------------------------
        
        public const byte STATUS_SUCCESS = 0;
        public const byte STATUS_INTERRUPTED = 1;
        public const byte STATUS_FAILED = 2;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkOffMeshLinkComplete Create(
            int linkId,
            ushort sequence,
            Vector3 finalPosition,
            byte status = STATUS_SUCCESS)
        {
            return new NetworkOffMeshLinkComplete
            {
                LinkId = linkId,
                Sequence = sequence,
                Status = status,
                FinalX = Mathf.RoundToInt(finalPosition.x * 100f),
                FinalY = Mathf.RoundToInt(finalPosition.y * 100f),
                FinalZ = Mathf.RoundToInt(finalPosition.z * 100f)
            };
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public Vector3 GetFinalPosition() => new Vector3(FinalX / 100f, FinalY / 100f, FinalZ / 100f);
        public bool IsSuccess => Status == STATUS_SUCCESS;
        public bool WasInterrupted => Status == STATUS_INTERRUPTED;
        public bool Failed => Status == STATUS_FAILED;
        
        // EQUALITY: ------------------------------------------------------------------------------
        
        public bool Equals(NetworkOffMeshLinkComplete other)
        {
            return LinkId == other.LinkId && Sequence == other.Sequence;
        }
        
        public override bool Equals(object obj) => obj is NetworkOffMeshLinkComplete other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LinkId, Sequence);
    }
    
    /// <summary>
    /// Custom animation data for off-mesh link traversal.
    /// Sent after NetworkOffMeshLinkStart when FLAG_HAS_ANIMATION is set.
    /// Variable size: 4 + (4 * keyframeCount) bytes
    /// </summary>
    [Serializable]
    public struct NetworkOffMeshLinkAnimation
    {
        /// <summary>Animation clip hash (for lookup)</summary>
        public int AnimationHash;
        
        /// <summary>Number of keyframes in curve</summary>
        public byte KeyframeCount;
        
        /// <summary>Curve type: 0=Linear, 1=EaseInOut, 2=Custom</summary>
        public byte CurveType;
        
        /// <summary>Animation speed multiplier (0.1x to 10x)</summary>
        public ushort SpeedMultiplier; // Fixed point, divide by 100
        
        /// <summary>Custom curve keyframes (time, value pairs as bytes)</summary>
        public byte[] Keyframes; // Each pair: [time 0-255 -> 0-1], [value 0-255 -> 0-1]
        
        // CURVE TYPES: ---------------------------------------------------------------------------
        
        public const byte CURVE_LINEAR = 0;
        public const byte CURVE_EASE_IN_OUT = 1;
        public const byte CURVE_EASE_IN = 2;
        public const byte CURVE_EASE_OUT = 3;
        public const byte CURVE_CUSTOM = 255;
        
        public const int MAX_KEYFRAMES = 16;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------
        
        public static NetworkOffMeshLinkAnimation CreateLinear(int animHash, float speed)
        {
            return new NetworkOffMeshLinkAnimation
            {
                AnimationHash = animHash,
                CurveType = CURVE_LINEAR,
                SpeedMultiplier = (ushort)Mathf.Clamp(speed * 100f, 10f, 1000f),
                KeyframeCount = 0,
                Keyframes = Array.Empty<byte>()
            };
        }
        
        public static NetworkOffMeshLinkAnimation CreateEaseInOut(int animHash, float speed)
        {
            return new NetworkOffMeshLinkAnimation
            {
                AnimationHash = animHash,
                CurveType = CURVE_EASE_IN_OUT,
                SpeedMultiplier = (ushort)Mathf.Clamp(speed * 100f, 10f, 1000f),
                KeyframeCount = 0,
                Keyframes = Array.Empty<byte>()
            };
        }
        
        public static NetworkOffMeshLinkAnimation CreateCustom(int animHash, float speed, AnimationCurve curve)
        {
            var anim = new NetworkOffMeshLinkAnimation
            {
                AnimationHash = animHash,
                CurveType = CURVE_CUSTOM,
                SpeedMultiplier = (ushort)Mathf.Clamp(speed * 100f, 10f, 1000f)
            };
            
            // Compress curve keyframes
            int count = Mathf.Min(curve.keys.Length, MAX_KEYFRAMES);
            anim.KeyframeCount = (byte)count;
            anim.Keyframes = new byte[count * 2];
            
            for (int i = 0; i < count; i++)
            {
                var key = curve.keys[i];
                anim.Keyframes[i * 2] = (byte)(Mathf.Clamp01(key.time) * 255f);
                anim.Keyframes[i * 2 + 1] = (byte)(Mathf.Clamp01(key.value) * 255f);
            }
            
            return anim;
        }
        
        // GETTERS: -------------------------------------------------------------------------------
        
        public float GetSpeed() => SpeedMultiplier / 100f;
        
        /// <summary>
        /// Evaluate progress through the traversal curve.
        /// </summary>
        public float Evaluate(float t)
        {
            t = Mathf.Clamp01(t);
            
            switch (CurveType)
            {
                case CURVE_LINEAR:
                    return t;
                    
                case CURVE_EASE_IN_OUT:
                    // Smoothstep
                    return t * t * (3f - 2f * t);
                    
                case CURVE_EASE_IN:
                    return t * t;
                    
                case CURVE_EASE_OUT:
                    return 1f - (1f - t) * (1f - t);
                    
                case CURVE_CUSTOM:
                    return EvaluateCustomCurve(t);
                    
                default:
                    return t;
            }
        }
        
        private float EvaluateCustomCurve(float t)
        {
            if (KeyframeCount < 2 || Keyframes == null) return t;
            
            // Find surrounding keyframes
            float prevTime = 0f, prevValue = 0f;
            float nextTime = 1f, nextValue = 1f;
            
            for (int i = 0; i < KeyframeCount; i++)
            {
                float keyTime = Keyframes[i * 2] / 255f;
                float keyValue = Keyframes[i * 2 + 1] / 255f;
                
                if (keyTime <= t)
                {
                    prevTime = keyTime;
                    prevValue = keyValue;
                }
                else
                {
                    nextTime = keyTime;
                    nextValue = keyValue;
                    break;
                }
            }
            
            // Lerp between keyframes
            float range = nextTime - prevTime;
            if (range <= 0f) return prevValue;
            
            float localT = (t - prevTime) / range;
            return Mathf.Lerp(prevValue, nextValue, localT);
        }
    }
    
    /// <summary>
    /// Configuration for off-mesh link network synchronization.
    /// </summary>
    [Serializable]
    public class NetworkOffMeshLinkConfig
    {
        [Tooltip("How often to send progress updates during traversal (Hz)")]
        public float ProgressSendRate = 10f;
        
        [Tooltip("Minimum progress change to trigger an update")]
        public float ProgressThreshold = 0.05f;
        
        [Tooltip("Buffer time for client interpolation (seconds)")]
        public float InterpolationBuffer = 0.05f;
        
        [Tooltip("Maximum time client can extrapolate beyond last update (seconds)")]
        public float MaxExtrapolationTime = 0.2f;
        
        [Tooltip("Distance threshold to snap instead of interpolate on traversal start")]
        public float SnapThreshold = 0.5f;
        
        [Tooltip("Whether to sync custom animation curves (increases bandwidth)")]
        public bool SyncCustomCurves = true;
        
        [Tooltip("Whether to send progress updates or just start/end (reduces bandwidth)")]
        public bool SendProgressUpdates = true;
    }
    
    /// <summary>
    /// State tracking for client-side off-mesh link traversal.
    /// </summary>
    public class OffMeshLinkTraversalState
    {
        public int LinkId;
        public ushort Sequence;
        public Vector3 StartPosition;
        public Vector3 EndPosition;
        public float Duration;
        public float StartTime;
        public float ServerStartTime;
        public OffMeshLinkTraversalType TraversalType;
        public bool IsAscending;
        public bool UsesRootMotion;
        public bool SnapToEnd;
        public NetworkOffMeshLinkAnimation? Animation;
        
        // Current state
        public float CurrentProgress;
        public float LastProgressUpdateTime;
        public float LastServerProgress;
        public bool IsComplete;
        
        /// <summary>
        /// Calculate current position along the traversal path.
        /// </summary>
        public Vector3 GetPosition(float progress)
        {
            float curvedProgress = Animation.HasValue 
                ? Animation.Value.Evaluate(progress)
                : GetDefaultCurve(progress);
            
            return Vector3.Lerp(StartPosition, EndPosition, curvedProgress);
        }
        
        /// <summary>
        /// Calculate position with vertical arc for jump traversal.
        /// </summary>
        public Vector3 GetPositionWithArc(float progress, float arcHeight)
        {
            float curvedProgress = Animation.HasValue 
                ? Animation.Value.Evaluate(progress)
                : GetDefaultCurve(progress);
            
            Vector3 basePos = Vector3.Lerp(StartPosition, EndPosition, curvedProgress);
            
            // Add parabolic arc
            float arcOffset = 4f * arcHeight * progress * (1f - progress);
            basePos.y += arcOffset;
            
            return basePos;
        }
        
        private float GetDefaultCurve(float t)
        {
            switch (TraversalType)
            {
                case OffMeshLinkTraversalType.Jump:
                case OffMeshLinkTraversalType.Vault:
                    // Quick start, smooth landing
                    return Mathf.Sin(t * Mathf.PI * 0.5f);
                    
                case OffMeshLinkTraversalType.Climb:
                case OffMeshLinkTraversalType.Ladder:
                    // Smooth ease in/out
                    return t * t * (3f - 2f * t);
                    
                case OffMeshLinkTraversalType.Drop:
                    // Fast (gravity-like)
                    return t * t;
                    
                case OffMeshLinkTraversalType.Teleport:
                    // Instant
                    return 1f;
                    
                default:
                    return t;
            }
        }
        
        /// <summary>
        /// Calculate expected progress based on elapsed time.
        /// </summary>
        public float GetExpectedProgress(float currentTime)
        {
            if (Duration <= 0f) return 1f;
            float elapsed = currentTime - StartTime;
            return Mathf.Clamp01(elapsed / Duration);
        }
        
        /// <summary>
        /// Interpolate progress smoothly from server updates.
        /// </summary>
        public float InterpolateProgress(float currentTime, float bufferTime)
        {
            if (IsComplete) return 1f;
            
            // Calculate expected progress with buffer
            float bufferedTime = currentTime - bufferTime;
            float expectedProgress = GetExpectedProgress(bufferedTime);
            
            // Smoothly approach expected progress
            float delta = expectedProgress - CurrentProgress;
            float maxDelta = (currentTime - LastProgressUpdateTime) / Duration;
            
            return CurrentProgress + Mathf.Clamp(delta, -maxDelta * 2f, maxDelta * 2f);
        }
    }
    
    /// <summary>
    /// Registry entry for a known off-mesh link type.
    /// Used to map link GameObjects to traversal configurations.
    /// </summary>
    [Serializable]
    public class OffMeshLinkTypeEntry
    {
        [Tooltip("Name for identification in inspector")]
        public string Name;
        
        [Tooltip("Type of traversal for this link")]
        public OffMeshLinkTraversalType TraversalType;
        
        [Tooltip("Default duration if not specified by link")]
        public float DefaultDuration = 1f;
        
        [Tooltip("Animation curve for movement")]
        public AnimationCurve MovementCurve = AnimationCurve.EaseInOut(0, 0, 1, 1);
        
        [Tooltip("Arc height for jump-type traversals")]
        public float ArcHeight = 1f;
        
        [Tooltip("Animation clip to play during traversal")]
        public AnimationClip AnimationClip;
        
        [Tooltip("Animation speed multiplier")]
        public float AnimationSpeed = 1f;
        
        [Tooltip("Whether to use root motion from animation")]
        public bool UseRootMotion = false;
    }
    
    /// <summary>
    /// ScriptableObject registry for off-mesh link types.
    /// Create via: Assets > Create > Game Creator > Network > Off-Mesh Link Registry
    /// </summary>
    [CreateAssetMenu(
        fileName = "OffMeshLinkRegistry", 
        menuName = "Game Creator/Network/Off-Mesh Link Registry")]
    public class NetworkOffMeshLinkRegistry : ScriptableObject
    {
        [Tooltip("Registered off-mesh link types")]
        public OffMeshLinkTypeEntry[] Entries;
        
        /// <summary>
        /// Find entry by traversal type.
        /// </summary>
        public OffMeshLinkTypeEntry GetEntry(OffMeshLinkTraversalType type)
        {
            if (Entries == null) return null;
            
            foreach (var entry in Entries)
            {
                if (entry.TraversalType == type)
                    return entry;
            }
            return null;
        }
        
        /// <summary>
        /// Get animation hash for a traversal type.
        /// </summary>
        public int GetAnimationHash(OffMeshLinkTraversalType type)
        {
            var entry = GetEntry(type);
            if (entry?.AnimationClip == null) return 0;
            return Animator.StringToHash(entry.AnimationClip.name);
        }
    }
}
