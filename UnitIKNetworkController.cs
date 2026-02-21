using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Characters.IK;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network controller for synchronizing GC2's IK Rig systems.
    /// 
    /// Design Philosophy:
    /// - Most IK rigs are LOCAL-ONLY (feet plant, breathing, twitching, lean)
    ///   because they either:
    ///   a) Derive from already-synced movement data (lean)
    ///   b) Use local physics (feet plant raycasts)
    ///   c) Are cosmetic randomness (breathing, twitching)
    /// 
    /// - Only TARGET-BASED IK needs sync:
    ///   * RigLookTo: Where the character is looking
    ///   * RigAimTowards: Where the character is aiming
    /// 
    /// This controller captures IK state from the local player and broadcasts
    /// it efficiently to remote players, who apply it to their local IK systems.
    /// </summary>
    public class UnitIKNetworkController : MonoBehaviour
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [Header("Configuration")]
        [SerializeField] private NetworkIKConfig m_Config = NetworkIKConfig.Default;
        
        [Header("Sync Options")]
        [SerializeField] private bool m_SyncLookTo = true;
        [SerializeField] private bool m_SyncAim = true;
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        private Character m_Character;
        private bool m_IsLocalPlayer;
        private bool m_IsInitialized;
        
        // IK Rig references
        private RigLookTo m_RigLookTo;
        private RigAimTowards m_RigAim;
        
        // Remote interpolation
        private NetworkLookToState m_CurrentLookTo;
        private NetworkLookToState m_TargetLookTo;
        private NetworkAimState m_CurrentAim;
        private NetworkAimState m_TargetAim;
        private float m_InterpolationT;
        
        // Delta compression
        private NetworkIKState m_LastSentState;
        private float m_LastSendTime;
        
        // Network look target for remotes
        private NetworkLookTarget m_NetworkLookTarget;
        
        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Raised when IK state should be sent to the network.
        /// Subscribe to this in your network implementation.
        /// </summary>
        public event Action<NetworkIKState> OnIKStateReady;
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        public Character Character => m_Character;
        public bool IsLocalPlayer => m_IsLocalPlayer;
        public bool IsInitialized => m_IsInitialized;
        
        public bool SyncLookTo
        {
            get => m_SyncLookTo;
            set => m_SyncLookTo = value;
        }
        
        public bool SyncAim
        {
            get => m_SyncAim;
            set => m_SyncAim = value;
        }
        
        // INITIALIZATION: ------------------------------------------------------------------------
        
        public void Initialize(Character character, bool isLocalPlayer)
        {
            if (m_IsInitialized) return;
            
            m_Character = character;
            m_IsLocalPlayer = isLocalPlayer;
            
            // Find IK rigs
            FindIKRigs();
            
            // Setup network look target for remotes
            if (!isLocalPlayer)
            {
                SetupNetworkLookTarget();
            }
            
            m_LastSentState = NetworkIKState.CreateEmpty();
            m_IsInitialized = true;
        }
        
        private void FindIKRigs()
        {
            if (m_Character?.Animim?.Animator == null) return;
            
            // Get IK rig layers from character
            // GC2 stores rigs in the IUnitAnimim.Rigs property
            var animim = m_Character.Animim;
            if (animim == null) return;
            
            // Access rigs through reflection to get the RigLayers
            var rigsField = animim.GetType().GetField("m_Rigs", 
                System.Reflection.BindingFlags.NonPublic | 
                System.Reflection.BindingFlags.Instance);
            
            if (rigsField?.GetValue(animim) is RigLayers rigLayers)
            {
                // Use GetRig<T>() to find specific rig types
                m_RigLookTo = rigLayers.GetRig<RigLookTo>();
                m_RigAim = rigLayers.GetRig<RigAimTowards>();
            }
        }
        
        private void SetupNetworkLookTarget()
        {
            // Create a network-controlled look target for remote characters
            m_NetworkLookTarget = new NetworkLookTarget();
        }
        
        // UNITY CALLBACKS: -----------------------------------------------------------------------
        
        private void Update()
        {
            if (!m_IsInitialized) return;
            
            if (m_IsLocalPlayer)
            {
                UpdateLocalPlayer();
            }
            else
            {
                UpdateRemotePlayer();
            }
        }
        
        // LOCAL PLAYER: --------------------------------------------------------------------------
        
        private void UpdateLocalPlayer()
        {
            // Check if it's time to send
            if (Time.time - m_LastSendTime < 1f / m_Config.SendRate) return;
            
            // Capture current IK state
            var state = CaptureIKState();
            
            // Delta compression - only send if changed significantly
            if (m_Config.DeltaCompression && !HasSignificantChange(state))
            {
                return;
            }
            
            // Send state
            OnIKStateReady?.Invoke(state);
            m_LastSentState = state;
            m_LastSendTime = Time.time;
        }
        
        private NetworkIKState CaptureIKState()
        {
            var state = NetworkIKState.CreateEmpty();
            
            // Capture LookTo state
            if (m_SyncLookTo && m_RigLookTo != null && m_RigLookTo.IsActive)
            {
                var lookTarget = m_RigLookTo.LookToTarget;
                if (lookTarget != null && lookTarget.Exists)
                {
                    state.HasLookTo = true;
                    state.LookTo = NetworkLookToState.Create(
                        lookTarget.Position,
                        m_Character.transform.position,
                        1f, // Weight is managed internally by GC2
                        lookTarget.Layer
                    );
                }
            }
            
            // Capture Aim state
            if (m_SyncAim && m_RigAim != null && m_RigAim.IsActive)
            {
                // RigAimTowards tracks pitch from a source transform
                // We need to capture the current bone rotation
                var animator = m_Character.Animim?.Animator;
                if (animator != null)
                {
                    // Get the local rotation of the aimed bone relative to character
                    Transform chest = animator.GetBoneTransform(HumanBodyBones.Chest);
                    if (chest != null)
                    {
                        Vector3 localEuler = m_Character.transform.InverseTransformDirection(
                            chest.forward
                        );
                        float pitch = -Mathf.Asin(localEuler.y) * Mathf.Rad2Deg;
                        float yaw = Mathf.Atan2(localEuler.x, localEuler.z) * Mathf.Rad2Deg;
                        
                        state.HasAim = true;
                        state.Aim = NetworkAimState.Create(pitch, yaw, 1f);
                    }
                }
            }
            
            return state;
        }
        
        private bool HasSignificantChange(NetworkIKState newState)
        {
            // Check LookTo changes
            if (newState.HasLookTo != m_LastSentState.HasLookTo) return true;
            
            if (newState.HasLookTo && m_LastSentState.HasLookTo)
            {
                var oldPos = m_LastSentState.LookTo.GetTargetPosition(m_Character.transform.position);
                var newPos = newState.LookTo.GetTargetPosition(m_Character.transform.position);
                
                if (Vector3.Distance(oldPos, newPos) > m_Config.PositionThreshold)
                    return true;
            }
            
            // Check Aim changes
            if (newState.HasAim != m_LastSentState.HasAim) return true;
            
            if (newState.HasAim && m_LastSentState.HasAim)
            {
                float pitchDiff = Mathf.Abs(newState.Aim.GetPitch() - m_LastSentState.Aim.GetPitch());
                float yawDiff = Mathf.Abs(newState.Aim.GetYaw() - m_LastSentState.Aim.GetYaw());
                
                if (pitchDiff > m_Config.AngleThreshold || yawDiff > m_Config.AngleThreshold)
                    return true;
            }
            
            return false;
        }
        
        // REMOTE PLAYER: -------------------------------------------------------------------------
        
        private void UpdateRemotePlayer()
        {
            // Interpolate towards target state
            if (m_Config.InterpolationTime > 0)
            {
                m_InterpolationT += Time.deltaTime / m_Config.InterpolationTime;
                m_InterpolationT = Mathf.Clamp01(m_InterpolationT);
            }
            else
            {
                m_InterpolationT = 1f;
            }
            
            // Update network look target position for LookTo rig
            if (m_NetworkLookTarget != null && m_TargetLookTo.HasTarget)
            {
                Vector3 targetPos = m_TargetLookTo.GetTargetPosition(m_Character.transform.position);
                
                if (m_InterpolationT < 1f && m_CurrentLookTo.HasTarget)
                {
                    Vector3 currentPos = m_CurrentLookTo.GetTargetPosition(m_Character.transform.position);
                    targetPos = Vector3.Lerp(currentPos, targetPos, m_InterpolationT);
                }
                
                m_NetworkLookTarget.Position = targetPos;
            }
        }
        
        /// <summary>
        /// Apply received IK state from network.
        /// Call this from your network receive handler.
        /// </summary>
        public void ApplyIKState(NetworkIKState state)
        {
            if (!m_IsInitialized || m_IsLocalPlayer) return;
            
            // Start new interpolation
            m_CurrentLookTo = m_TargetLookTo;
            m_TargetLookTo = state.LookTo;
            
            m_CurrentAim = m_TargetAim;
            m_TargetAim = state.Aim;
            
            m_InterpolationT = 0f;
            
            // Update LookTo target
            if (state.HasLookTo && m_RigLookTo != null)
            {
                if (m_NetworkLookTarget != null)
                {
                    m_NetworkLookTarget.Layer = state.LookTo.Layer;
                    m_NetworkLookTarget.Exists = state.LookTo.HasTarget;
                    
                    if (!state.LookTo.HasTarget)
                    {
                        m_RigLookTo.RemoveTarget(m_NetworkLookTarget);
                    }
                    else
                    {
                        m_RigLookTo.SetTarget(m_NetworkLookTarget);
                    }
                }
            }
            else if (m_RigLookTo != null && m_NetworkLookTarget != null)
            {
                m_RigLookTo.RemoveTarget(m_NetworkLookTarget);
            }
        }
        
        // PUBLIC API: ----------------------------------------------------------------------------
        
        /// <summary>
        /// Manually set look target for local player and broadcast.
        /// Use this for programmatic look control with network sync.
        /// </summary>
        public void SetLookTarget(Vector3 worldPosition, int layer = 0)
        {
            if (!m_IsInitialized || !m_IsLocalPlayer) return;
            
            // The actual look target should be set via GC2's normal API
            // This method is for when you want to force a network update
            var state = new NetworkIKState
            {
                HasLookTo = true,
                LookTo = NetworkLookToState.Create(
                    worldPosition,
                    m_Character.transform.position,
                    1f,
                    layer
                )
            };
            
            OnIKStateReady?.Invoke(state);
            m_LastSentState = state;
            m_LastSendTime = Time.time;
        }
        
        /// <summary>
        /// Clear all look targets and broadcast.
        /// </summary>
        public void ClearLookTarget()
        {
            if (!m_IsInitialized || !m_IsLocalPlayer) return;
            
            var state = NetworkIKState.CreateEmpty();
            OnIKStateReady?.Invoke(state);
            m_LastSentState = state;
            m_LastSendTime = Time.time;
        }
        
        // CLEANUP: -------------------------------------------------------------------------------
        
        private void OnDestroy()
        {
            if (m_RigLookTo != null && m_NetworkLookTarget != null)
            {
                m_RigLookTo.RemoveTarget(m_NetworkLookTarget);
            }
        }
    }
    
    /// <summary>
    /// Network-controlled look target that implements ILookTo.
    /// Used to inject network-received look positions into GC2's RigLookTo system.
    /// </summary>
    internal class NetworkLookTarget : ILookTo
    {
        public int Layer { get; set; }
        public bool Exists { get; set; }
        public Vector3 Position { get; set; }
        public GameObject Target => null; // We use position-based targeting
        
        public NetworkLookTarget()
        {
            Layer = 0;
            Exists = false;
            Position = Vector3.zero;
        }
    }
}
