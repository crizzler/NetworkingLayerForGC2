using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Characters.Animim;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network controller for synchronizing GC2's Playable Graph animation system.
    /// 
    /// Design Philosophy:
    /// - Animations are cosmetic, not gameplay-critical, so we use owner-authority (not server)
    /// - Local player captures animation commands and broadcasts to remotes
    /// - Remote players receive commands and apply animations locally
    /// - No server validation needed (animation cheating doesn't affect gameplay physics)
    /// 
    /// This controller wraps GC2's States and Gestures systems, intercepting
    /// animation calls to broadcast them over the network.
    /// </summary>
    public class UnitAnimimNetworkController : MonoBehaviour
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [Header("Configuration")]
        [SerializeField] private NetworkAnimationRegistry m_AnimationRegistry;
        
        [Header("Sync Settings")]
        [Tooltip("Rate limit for state sync (commands per second)")]
        [SerializeField] private float m_MaxStateRate = 10f;
        
        [Tooltip("Rate limit for gesture sync (commands per second)")]
        [SerializeField] private float m_MaxGestureRate = 20f;
        
        [Tooltip("Enable animation sync (disable for NPCs that don't need remote sync)")]
        [SerializeField] private bool m_EnableSync = true;
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        private Character m_Character;
        private bool m_IsLocalPlayer;
        private bool m_IsInitialized;
        
        // Rate limiting
        private float m_LastStateTime;
        private float m_LastGestureTime;
        
        // Command queues for batching
        private Queue<NetworkStateCommand> m_PendingStateCommands;
        private Queue<NetworkGestureCommand> m_PendingGestureCommands;
        private Queue<NetworkStopStateCommand> m_PendingStopStateCommands;
        private Queue<NetworkStopGestureCommand> m_PendingStopGestureCommands;
        
        // Animation lookup for remotes (maps hash to clip)
        private Dictionary<int, AnimationClip> m_ClipCache;
        private Dictionary<int, State> m_StateCache;
        private Dictionary<int, RuntimeAnimatorController> m_ControllerCache;
        
        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Raised when a state command should be sent to the network.
        /// Subscribe to this in your network implementation.
        /// </summary>
        public event Action<NetworkStateCommand> OnStateCommandReady;
        
        /// <summary>
        /// Raised when a gesture command should be sent to the network.
        /// </summary>
        public event Action<NetworkGestureCommand> OnGestureCommandReady;
        
        /// <summary>
        /// Raised when a stop state command should be sent.
        /// </summary>
        public event Action<NetworkStopStateCommand> OnStopStateCommandReady;
        
        /// <summary>
        /// Raised when a stop gesture command should be sent.
        /// </summary>
        public event Action<NetworkStopGestureCommand> OnStopGestureCommandReady;
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        public Character Character => m_Character;
        public bool IsLocalPlayer => m_IsLocalPlayer;
        public bool IsInitialized => m_IsInitialized;
        public NetworkAnimationRegistry Registry => m_AnimationRegistry;
        
        public void SetSyncEnabled(bool enabled)
        {
            m_EnableSync = enabled;
        }
        
        public void SetRateLimits(float stateRate, float gestureRate)
        {
            m_MaxStateRate = Mathf.Max(1f, stateRate);
            m_MaxGestureRate = Mathf.Max(1f, gestureRate);
        }
        
        // INITIALIZATION: ------------------------------------------------------------------------
        
        public void Initialize(Character character, bool isLocalPlayer)
        {
            if (m_IsInitialized) return;
            
            m_Character = character;
            m_IsLocalPlayer = isLocalPlayer;
            
            m_PendingStateCommands = new Queue<NetworkStateCommand>();
            m_PendingGestureCommands = new Queue<NetworkGestureCommand>();
            m_PendingStopStateCommands = new Queue<NetworkStopStateCommand>();
            m_PendingStopGestureCommands = new Queue<NetworkStopGestureCommand>();
            
            m_ClipCache = new Dictionary<int, AnimationClip>();
            m_StateCache = new Dictionary<int, State>();
            m_ControllerCache = new Dictionary<int, RuntimeAnimatorController>();
            
            m_IsInitialized = true;
        }
        
        // PUBLIC API - LOCAL PLAYER: -------------------------------------------------------------
        // Call these methods instead of directly calling Character.States/Gestures
        // They will apply locally AND broadcast to network
        
        /// <summary>
        /// Set an animation state on a layer and broadcast to network.
        /// Use this instead of Character.States.SetState() for networked animations.
        /// </summary>
        public async Task SetState(
            AnimationClip clip,
            AvatarMask mask,
            int layer,
            BlendMode blendMode,
            ConfigState config)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            
            try
            {
                // Apply locally
                if (m_Character?.States != null)
                {
                    await m_Character.States.SetState(clip, mask, layer, blendMode, config);
                }
                
                // Broadcast if local player
                if (m_IsLocalPlayer && CanSendState())
                {
                    int clipHash = StableHashUtility.GetStableHash(clip);
                    var command = NetworkStateCommand.Create(
                        clipHash,
                        NetworkStateType.AnimationClip,
                        layer,
                        blendMode,
                        config
                    );
                    
                    OnStateCommandReady?.Invoke(command);
                    m_LastStateTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAnimim] SetState(clip) failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Set a State asset on a layer and broadcast to network.
        /// </summary>
        public async Task SetState(
            State state,
            int layer,
            BlendMode blendMode,
            ConfigState config)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            
            try
            {
                // Apply locally
                if (m_Character?.States != null)
                {
                    await m_Character.States.SetState(state, layer, blendMode, config);
                }
                
                // Broadcast if local player
                if (m_IsLocalPlayer && CanSendState())
                {
                    int stateHash = StableHashUtility.GetStableHash(state);
                    var command = NetworkStateCommand.Create(
                        stateHash,
                        NetworkStateType.StateAsset,
                        layer,
                        blendMode,
                        config
                    );
                    
                    OnStateCommandReady?.Invoke(command);
                    m_LastStateTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAnimim] SetState(asset) failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Set a runtime animator controller on a layer and broadcast to network.
        /// </summary>
        public async Task SetState(
            RuntimeAnimatorController controller,
            AvatarMask mask,
            int layer,
            BlendMode blendMode,
            ConfigState config)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            if (controller == null) return;

            try
            {
                if (m_Character?.States != null)
                {
                    await m_Character.States.SetState(controller, mask, layer, blendMode, config);
                }

                if (m_IsLocalPlayer && CanSendState())
                {
                    int controllerHash = StableHashUtility.GetStableHash(controller);
                    var command = NetworkStateCommand.Create(
                        controllerHash,
                        NetworkStateType.RuntimeController,
                        layer,
                        blendMode,
                        config
                    );

                    OnStateCommandReady?.Invoke(command);
                    m_LastStateTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAnimim] SetState(controller) failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Stop a state layer and broadcast to network.
        /// </summary>
        public void StopState(int layer, float delay, float transitionOut)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            
            // Apply locally
            m_Character?.States?.Stop(layer, delay, transitionOut);
            
            // Broadcast if local player
            if (m_IsLocalPlayer)
            {
                var command = NetworkStopStateCommand.Create(layer, delay, transitionOut);
                OnStopStateCommandReady?.Invoke(command);
            }
        }
        
        /// <summary>
        /// Play a gesture animation and broadcast to network.
        /// Use this instead of Character.Gestures.CrossFade() for networked animations.
        /// </summary>
        public async Task PlayGesture(
            AnimationClip clip,
            AvatarMask mask,
            BlendMode blendMode,
            ConfigGesture config,
            bool stopPreviousGestures = true)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            
            try
            {
                // Apply locally
                if (m_Character?.Gestures != null)
                {
                    await m_Character.Gestures.CrossFade(clip, mask, blendMode, config, stopPreviousGestures);
                }
                
                // Broadcast if local player
                if (m_IsLocalPlayer && CanSendGesture())
                {
                    int clipHash = StableHashUtility.GetStableHash(clip);
                    var command = NetworkGestureCommand.Create(
                        clipHash,
                        blendMode,
                        config,
                        stopPreviousGestures
                    );
                    
                    OnGestureCommandReady?.Invoke(command);
                    m_LastGestureTime = Time.time;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAnimim] PlayGesture failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Stop all gestures and broadcast to network.
        /// </summary>
        public void StopGestures(float delay, float transitionOut)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            
            // Apply locally
            m_Character?.Gestures?.Stop(delay, transitionOut);
            
            // Broadcast if local player
            if (m_IsLocalPlayer)
            {
                var command = NetworkStopGestureCommand.Create(0, delay, transitionOut);
                OnStopGestureCommandReady?.Invoke(command);
            }
        }
        
        /// <summary>
        /// Stop a specific gesture and broadcast to network.
        /// </summary>
        public void StopGesture(AnimationClip clip, float delay, float transitionOut)
        {
            if (!m_EnableSync || !m_IsInitialized) return;
            
            // Apply locally
            m_Character?.Gestures?.Stop(clip, delay, transitionOut);
            
            // Broadcast if local player
            if (m_IsLocalPlayer)
            {
                int clipHash = StableHashUtility.GetStableHash(clip);
                var command = NetworkStopGestureCommand.Create(clipHash, delay, transitionOut);
                OnStopGestureCommandReady?.Invoke(command);
            }
        }
        
        // PUBLIC API - REMOTE PLAYERS: -----------------------------------------------------------
        // Network implementation calls these to apply received commands
        
        /// <summary>
        /// Apply a received state command from a remote player.
        /// Call this from your network receive handler.
        /// </summary>
        public async Task ApplyStateCommand(NetworkStateCommand command)
        {
            if (!m_EnableSync || !m_IsInitialized || m_IsLocalPlayer) return;
            if (m_Character?.States == null) return;
            
            try
            {
                var config = command.ToConfigState();
                
                switch (command.StateType)
                {
                    case NetworkStateType.AnimationClip:
                        if (TryGetClip(command.AnimationId, out var clip))
                        {
                            await m_Character.States.SetState(
                                clip, null, command.Layer, command.BlendMode, config
                            );
                        }
                        break;
                        
                    case NetworkStateType.StateAsset:
                        if (TryGetState(command.AnimationId, out var state))
                        {
                            await m_Character.States.SetState(
                                state, command.Layer, command.BlendMode, config
                            );
                        }
                        break;
                        
                    case NetworkStateType.RuntimeController:
                        if (TryGetRuntimeController(command.AnimationId, out var controller))
                        {
                            await m_Character.States.SetState(
                                controller, null, command.Layer, command.BlendMode, config
                            );
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAnimim] ApplyStateCommand failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Apply a received gesture command from a remote player.
        /// </summary>
        public async Task ApplyGestureCommand(NetworkGestureCommand command)
        {
            if (!m_EnableSync || !m_IsInitialized || m_IsLocalPlayer) return;
            if (m_Character?.Gestures == null) return;
            
            try
            {
                if (TryGetClip(command.ClipHash, out var clip))
                {
                    var config = command.ToConfigGesture();
                    await m_Character.Gestures.CrossFade(
                        clip, null, command.BlendMode, config, command.StopPreviousGestures
                    );
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAnimim] ApplyGestureCommand failed: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Apply a received stop state command.
        /// </summary>
        public void ApplyStopStateCommand(NetworkStopStateCommand command)
        {
            if (!m_EnableSync || !m_IsInitialized || m_IsLocalPlayer) return;
            m_Character?.States?.Stop(command.Layer, command.GetDelay(), command.GetTransitionOut());
        }
        
        /// <summary>
        /// Apply a received stop gesture command.
        /// </summary>
        public void ApplyStopGestureCommand(NetworkStopGestureCommand command)
        {
            if (!m_EnableSync || !m_IsInitialized || m_IsLocalPlayer) return;
            
            if (command.ClipHash == 0)
            {
                m_Character?.Gestures?.Stop(command.GetDelay(), command.GetTransitionOut());
            }
            else if (TryGetClip(command.ClipHash, out var clip))
            {
                m_Character?.Gestures?.Stop(clip, command.GetDelay(), command.GetTransitionOut());
            }
        }
        
        // CACHE MANAGEMENT: ----------------------------------------------------------------------
        
        /// <summary>
        /// Register an animation clip so remotes can look it up by hash.
        /// Call this during initialization for all networked animations.
        /// </summary>
        public void RegisterClip(AnimationClip clip)
        {
            if (clip == null) return;
            int hash = StableHashUtility.GetStableHash(clip);
            m_ClipCache[hash] = clip;
        }
        
        /// <summary>
        /// Register a State asset so remotes can look it up by hash.
        /// </summary>
        public void RegisterState(State state)
        {
            if (state == null) return;
            int hash = StableHashUtility.GetStableHash(state);
            m_StateCache[hash] = state;
        }

        /// <summary>
        /// Register a RuntimeAnimatorController so remotes can look it up by hash.
        /// </summary>
        public void RegisterRuntimeController(RuntimeAnimatorController controller)
        {
            if (controller == null) return;
            int hash = StableHashUtility.GetStableHash(controller);
            m_ControllerCache[hash] = controller;
        }
        
        /// <summary>
        /// Register multiple clips at once.
        /// </summary>
        public void RegisterClips(IEnumerable<AnimationClip> clips)
        {
            foreach (var clip in clips)
            {
                RegisterClip(clip);
            }
        }
        
        /// <summary>
        /// Register multiple states at once.
        /// </summary>
        public void RegisterStates(IEnumerable<State> states)
        {
            foreach (var state in states)
            {
                RegisterState(state);
            }
        }

        /// <summary>
        /// Register multiple RuntimeAnimatorController assets at once.
        /// </summary>
        public void RegisterRuntimeControllers(IEnumerable<RuntimeAnimatorController> controllers)
        {
            foreach (var controller in controllers)
            {
                RegisterRuntimeController(controller);
            }
        }
        
        // PRIVATE METHODS: -----------------------------------------------------------------------
        
        private bool CanSendState()
        {
            return Time.time - m_LastStateTime >= 1f / m_MaxStateRate;
        }
        
        private bool CanSendGesture()
        {
            return Time.time - m_LastGestureTime >= 1f / m_MaxGestureRate;
        }
        
        private bool TryGetClip(int hash, out AnimationClip clip)
        {
            // Try local cache first
            if (m_ClipCache.TryGetValue(hash, out clip))
            {
                return clip != null;
            }
            
            // Try registry
            if (m_AnimationRegistry != null && 
                m_AnimationRegistry.TryGetEntry(hash, out var entry))
            {
                clip = entry.Clip;
                if (clip != null)
                {
                    m_ClipCache[hash] = clip;
                    return true;
                }
            }
            
            clip = null;
            return false;
        }
        
        private bool TryGetState(int hash, out State state)
        {
            // Try local cache first
            if (m_StateCache.TryGetValue(hash, out state))
            {
                return state != null;
            }
            
            // Try registry
            if (m_AnimationRegistry != null && 
                m_AnimationRegistry.TryGetEntry(hash, out var entry))
            {
                state = entry.StateAsset;
                if (state != null)
                {
                    m_StateCache[hash] = state;
                    return true;
                }
            }
            
            state = null;
            return false;
        }

        private bool TryGetRuntimeController(int hash, out RuntimeAnimatorController controller)
        {
            if (m_ControllerCache.TryGetValue(hash, out controller))
            {
                return controller != null;
            }

            if (m_AnimationRegistry != null &&
                m_AnimationRegistry.TryGetEntry(hash, out var entry))
            {
                controller = entry.Controller;
                if (controller != null)
                {
                    m_ControllerCache[hash] = controller;
                    return true;
                }
            }

            controller = null;
            return false;
        }
    }
}
