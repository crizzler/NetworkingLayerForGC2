using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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
        private const int MAX_AUTO_DISCOVERY_DEPTH = 8;
        private const int MAX_AUTO_DISCOVERED_CLIPS = 256;
        private const int MAX_GLOBAL_AUTO_DISCOVERED_CLIPS = 1024;

        private static readonly Dictionary<int, AnimationClip> s_GlobalClipCache = new(1024);
        private static readonly Dictionary<int, State> s_GlobalStateCache = new(256);
        private static readonly Dictionary<int, RuntimeAnimatorController> s_GlobalControllerCache = new(256);
        private static bool s_HasDiscoveredGlobalClips;
        private static FieldInfo s_StatesLayersField;
        private static bool s_TriedResolveStatesLayersField;

        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [Header("Configuration")]
        [SerializeField] private NetworkAnimationRegistry m_AnimationRegistry;

        [Tooltip("Animation clips to pre-register at startup so remote peers can resolve " +
                 "broadcast hashes (e.g., dash gesture clips referenced by a Network Dash " +
                 "instruction). The same prefab is instantiated on every peer, so dropping " +
                 "the clip here guarantees every peer can play the gesture.")]
        [SerializeField] private AnimationClip[] m_PreRegisteredClips;
        
        [Header("Sync Settings")]
        [Tooltip("Rate limit for state sync (commands per second)")]
        [SerializeField] private float m_MaxStateRate = 10f;
        
        [Tooltip("Rate limit for gesture sync (commands per second)")]
        [SerializeField] private float m_MaxGestureRate = 20f;
        
        [Tooltip("Enable animation sync (disable for NPCs that don't need remote sync)")]
        [SerializeField] private bool m_EnableSync = true;

        [SerializeField] private bool m_LogDashDiagnostics = false;
        [SerializeField] private bool m_LogStateDiagnostics = false;
        [SerializeField] private bool m_LogGestureDiagnostics = false;
        [SerializeField] private float m_StateDiagnosticsSampleInterval = 0.25f;
        [SerializeField] private float m_GestureDiagnosticsSampleInterval = 0.25f;
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        private Character m_Character;
        private bool m_IsLocalPlayer;
        private bool m_IsInitialized;
        private bool m_HasObservedGestureState;
        private bool m_LastObservedGesturePlaying;
        private float m_LastObservedGestureChangeRealtime;
        private float m_LastObservedGestureSampleRealtime;
        private bool m_HasObservedAnimimState;
        private int m_LastObservedStateCount;
        private float m_LastObservedStateChangeRealtime;
        private float m_LastObservedStateSampleRealtime;
        
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
        private bool m_HasAutoDiscoveredClips;
        
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
        public bool IsSyncEnabled => m_EnableSync;
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

            if (m_PreRegisteredClips != null)
            {
                for (int i = 0; i < m_PreRegisteredClips.Length; i++)
                {
                    AnimationClip clip = m_PreRegisteredClips[i];
                    if (clip == null) continue;
                    RegisterClipIfNew(clip);
                }
            }

            DiscoverAnimationClipsFromCharacter();
	            
            m_IsInitialized = true;
            LogDash($"initialized character={character.name} local={m_IsLocalPlayer} sync={m_EnableSync}");
        }

        private void Update()
        {
            ObserveAnimimState();
            ObserveGestureState();
        }

        /// <summary>
        /// Register additional clips after Initialize. Used by NetworkCharacter to forward
        /// its own Pre Registered Animation Clips list (which is authored on the prefab even
        /// when this controller is auto-added at runtime).
        /// </summary>
        public void RegisterClips(AnimationClip[] clips)
        {
            if (clips == null || clips.Length == 0) return;
            if (m_ClipCache == null) m_ClipCache = new Dictionary<int, AnimationClip>();
            for (int i = 0; i < clips.Length; i++)
            {
                AnimationClip clip = clips[i];
                if (clip == null) continue;
                RegisterClipIfNew(clip);
            }
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
            if (clip == null)
            {
                LogStateDiagnostics("SetState(clip) skipped: clip is null");
                return;
            }
            
            try
            {
                RegisterClip(clip);
                LogStateDiagnostics(
                    $"SetState(clip) requested local={m_IsLocalPlayer} clip={clip.name} " +
                    $"hash={StableHashUtility.GetStableHash(clip)} clipLength={clip.length:F3} " +
                    $"layer={layer} blend={blendMode} mask={(mask != null ? mask.name : "none")} " +
                    $"{FormatConfig(config)} {FormatCharacterTime()}");
                LogHigherStateLayerWarning(layer, $"SetState(clip) clip={clip.name}");

                // Broadcast before applying. State assets can remain active indefinitely,
                // so awaiting the local SetState first can delay the network command until
                // the state exits.
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

                    LogStateDiagnostics($"SetState(clip) sending {FormatCommand(command)}");
                    OnStateCommandReady?.Invoke(command);
                    m_LastStateTime = Time.time;
                }
                else if (m_IsLocalPlayer)
                {
                    LogStateDiagnostics(
                        $"SetState(clip) not sent due to state rate limit clip={clip.name} " +
                        $"elapsed={Time.time - m_LastStateTime:F3} maxRate={m_MaxStateRate:F1}");
                }

                // Apply locally
                if (m_Character?.States != null)
                {
                    await m_Character.States.SetState(clip, mask, layer, blendMode, config);
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
            if (state == null)
            {
                LogStateDiagnostics("SetState(asset) skipped: state is null");
                return;
            }
            
            try
            {
                RegisterState(state);
                LogStateDiagnostics(
                    $"SetState(asset) requested local={m_IsLocalPlayer} state={state.name} " +
                    $"hash={StableHashUtility.GetStableHash(state)} layer={layer} blend={blendMode} " +
                    $"entryClip={(state.HasEntryClip ? state.EntryClip.name : "none")} " +
                    $"exitClip={(state.HasExitClip ? state.ExitClip.name : "none")} " +
                    $"{FormatConfig(config)} {FormatCharacterTime()}");
                LogHigherStateLayerWarning(layer, $"SetState(asset) state={state.name}");

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

                    LogStateDiagnostics($"SetState(asset) sending {FormatCommand(command)}");
                    OnStateCommandReady?.Invoke(command);
                    m_LastStateTime = Time.time;
                }
                else if (m_IsLocalPlayer)
                {
                    LogStateDiagnostics(
                        $"SetState(asset) not sent due to state rate limit state={state.name} " +
                        $"elapsed={Time.time - m_LastStateTime:F3} maxRate={m_MaxStateRate:F1}");
                }

                // Apply locally
                if (m_Character?.States != null)
                {
                    await m_Character.States.SetState(state, layer, blendMode, config);
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
                RegisterRuntimeController(controller);
                LogStateDiagnostics(
                    $"SetState(controller) requested local={m_IsLocalPlayer} controller={controller.name} " +
                    $"hash={StableHashUtility.GetStableHash(controller)} layer={layer} blend={blendMode} " +
                    $"mask={(mask != null ? mask.name : "none")} clips={controller.animationClips?.Length ?? 0} " +
                    $"{FormatConfig(config)} {FormatCharacterTime()}");
                LogHigherStateLayerWarning(layer, $"SetState(controller) controller={controller.name}");

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

                    LogStateDiagnostics($"SetState(controller) sending {FormatCommand(command)}");
                    OnStateCommandReady?.Invoke(command);
                    m_LastStateTime = Time.time;
                }
                else if (m_IsLocalPlayer)
                {
                    LogStateDiagnostics(
                        $"SetState(controller) not sent due to state rate limit controller={controller.name} " +
                        $"elapsed={Time.time - m_LastStateTime:F3} maxRate={m_MaxStateRate:F1}");
                }

                if (m_Character?.States != null)
                {
                    await m_Character.States.SetState(controller, mask, layer, blendMode, config);
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

            LogStateDiagnostics(
                $"StopState requested local={m_IsLocalPlayer} layer={layer} delay={delay:F3} " +
                $"transitionOut={transitionOut:F3} {FormatCharacterTime()}");
            
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
            if (!m_EnableSync)
            {
                LogDash($"PlayGesture skipped: sync disabled clip={clip?.name ?? "null"}");
                return;
            }

            if (!m_IsInitialized)
            {
                LogDash($"PlayGesture skipped: controller is not initialized clip={clip?.name ?? "null"}");
                return;
            }

            if (clip == null)
            {
                LogDash("PlayGesture skipped: clip is null");
                return;
            }
            
            try
            {
                LogGestureDiagnostics(
                    $"PlayGesture requested local={m_IsLocalPlayer} clip={clip.name} " +
                    $"hash={StableHashUtility.GetStableHash(clip)} clipLength={clip.length:F3} " +
                    $"blend={blendMode} stopPrevious={stopPreviousGestures} mask={(mask != null ? mask.name : "none")} " +
                    $"{FormatConfig(config)} {FormatCharacterTime()}");

                // Broadcast FIRST so the receiver starts the animation in lock-step with
                // any concurrent motion impulse (e.g., a Network Dash). Awaiting the local
                // CrossFade before broadcasting causes the gesture command to be sent only
                // after the local clip has finished playing -- on remotes that delays the
                // animation until well after the velocity push has already completed.
                if (m_IsLocalPlayer && CanSendGesture())
                {
                    int clipHash = StableHashUtility.GetStableHash(clip);
                    var command = NetworkGestureCommand.Create(
                        clipHash,
                        blendMode,
                        config,
                        stopPreviousGestures
                    );

                    LogGestureDiagnostics(
                        $"PlayGesture packed command clip={clip.name} hash={clipHash} " +
                        $"{FormatCommand(command)}");

                    if (OnGestureCommandReady == null)
                    {
                        LogDash(
                            $"PlayGesture cannot send: OnGestureCommandReady has no listeners " +
                            $"clip={clip.name} hash={clipHash}");
                    }

                    LogDash(
                        $"PlayGesture sending clip={clip.name} hash={clipHash} " +
                        $"duration={config.Duration:F2} speed={config.Speed:F2} " +
                        $"transitionIn={config.TransitionIn:F2} transitionOut={config.TransitionOut:F2}");
                    OnGestureCommandReady?.Invoke(command);
                    m_LastGestureTime = Time.time;
                }
                else if (m_IsLocalPlayer)
                {
                    LogDash(
                        $"PlayGesture not sent due to gesture rate limit clip={clip?.name ?? "null"} " +
                        $"elapsed={Time.time - m_LastGestureTime:F3} maxRate={m_MaxGestureRate:F1}");
                }
                else
                {
                    LogDash($"PlayGesture local apply only: controller is remote clip={clip?.name ?? "null"}");
                }

                // Apply locally after the broadcast so the wire send happens in the same
                // frame as the local impulse / animation start.
                if (m_Character?.Gestures != null)
                {
                    float startedRealtime = Time.realtimeSinceStartup;
                    await m_Character.Gestures.CrossFade(clip, mask, blendMode, config, stopPreviousGestures);
                    LogGestureDiagnostics(
                        $"PlayGesture local CrossFade completed clip={clip.name} " +
                        $"wallDuration={Time.realtimeSinceStartup - startedRealtime:F3}s " +
                        $"gesturesPlaying={m_Character.Gestures.IsPlaying} " +
                        $"weight={m_Character.Gestures.CurrentWeight:F3} {FormatCharacterTime()}");
                }
                else
                {
                    LogGestureDiagnostics("PlayGesture local CrossFade skipped: Character.Gestures is null");
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

            LogGestureDiagnostics(
                $"StopGestures requested local={m_IsLocalPlayer} delay={delay:F3} " +
                $"transitionOut={transitionOut:F3} gesturesPlaying={m_Character?.Gestures?.IsPlaying ?? false} " +
                $"weight={m_Character?.Gestures?.CurrentWeight ?? 0f:F3} {FormatCharacterTime()}");
            
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

            LogGestureDiagnostics(
                $"StopGesture requested local={m_IsLocalPlayer} clip={clip?.name ?? "null"} " +
                $"hash={(clip != null ? StableHashUtility.GetStableHash(clip) : 0)} " +
                $"delay={delay:F3} transitionOut={transitionOut:F3} " +
                $"gesturesPlaying={m_Character?.Gestures?.IsPlaying ?? false} " +
                $"weight={m_Character?.Gestures?.CurrentWeight ?? 0f:F3} {FormatCharacterTime()}");
            
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
            if (!m_EnableSync)
            {
                LogStateDiagnostics($"ApplyState skipped: sync disabled {FormatCommand(command)}");
                return;
            }

            if (!m_IsInitialized)
            {
                LogStateDiagnostics($"ApplyState skipped: controller is not initialized {FormatCommand(command)}");
                return;
            }

            if (m_IsLocalPlayer)
            {
                LogStateDiagnostics($"ApplyState skipped: local player controller {FormatCommand(command)}");
                return;
            }

            if (m_Character?.States == null)
            {
                LogStateDiagnostics($"ApplyState skipped: Character.States is null {FormatCommand(command)}");
                return;
            }
            
            try
            {
                var config = command.ToConfigState();
                LogStateDiagnostics($"ApplyState received {FormatCommand(command)} {FormatCharacterTime()}");
                
                switch (command.StateType)
                {
                    case NetworkStateType.AnimationClip:
                        if (TryGetClip(command.AnimationId, out var clip))
                        {
                            LogStateDiagnostics(
                                $"ApplyState resolved clip={clip.name} hash={command.AnimationId} " +
                                $"clipLength={clip.length:F3}");
                            await m_Character.States.SetState(
                                clip, null, command.Layer, command.BlendMode, config
                            );
                        }
                        else
                        {
                            LogStateDiagnostics(
                                $"ApplyState failed clip lookup hash={command.AnimationId} " +
                                $"registry={(m_AnimationRegistry != null ? m_AnimationRegistry.name : "null")} " +
                                $"localCache={m_ClipCache.Count} globalCache={s_GlobalClipCache.Count}");
                        }
                        break;
                        
                    case NetworkStateType.StateAsset:
                        if (TryGetState(command.AnimationId, out var state))
                        {
                            LogStateDiagnostics(
                                $"ApplyState resolved state={state.name} hash={command.AnimationId} " +
                                $"entryClip={(state.HasEntryClip ? state.EntryClip.name : "none")} " +
                                $"exitClip={(state.HasExitClip ? state.ExitClip.name : "none")}");
                            await m_Character.States.SetState(
                                state, command.Layer, command.BlendMode, config
                            );
                        }
                        else
                        {
                            LogStateDiagnostics(
                                $"ApplyState failed state lookup hash={command.AnimationId} " +
                                $"registry={(m_AnimationRegistry != null ? m_AnimationRegistry.name : "null")} " +
                                $"localCache={m_StateCache.Count} globalCache={s_GlobalStateCache.Count}");
                        }
                        break;
                        
                    case NetworkStateType.RuntimeController:
                        if (TryGetRuntimeController(command.AnimationId, out var controller))
                        {
                            LogStateDiagnostics(
                                $"ApplyState resolved controller={controller.name} hash={command.AnimationId} " +
                                $"clips={controller.animationClips?.Length ?? 0}");
                            await m_Character.States.SetState(
                                controller, null, command.Layer, command.BlendMode, config
                            );
                        }
                        else
                        {
                            LogStateDiagnostics(
                                $"ApplyState failed controller lookup hash={command.AnimationId} " +
                                $"registry={(m_AnimationRegistry != null ? m_AnimationRegistry.name : "null")} " +
                                $"localCache={m_ControllerCache.Count} globalCache={s_GlobalControllerCache.Count}");
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
            if (!m_EnableSync)
            {
                LogDash($"ApplyGesture skipped: sync disabled clipHash={command.ClipHash}");
                return;
            }

            if (!m_IsInitialized)
            {
                LogDash($"ApplyGesture skipped: controller is not initialized clipHash={command.ClipHash}");
                return;
            }

            if (m_IsLocalPlayer)
            {
                LogDash($"ApplyGesture skipped: local player controller clipHash={command.ClipHash}");
                return;
            }

            if (m_Character?.Gestures == null)
            {
                LogDash($"ApplyGesture skipped: Character.Gestures is null clipHash={command.ClipHash}");
                return;
            }
            
            try
            {
                LogGestureDiagnostics(
                    $"ApplyGesture received command local={m_IsLocalPlayer} {FormatCommand(command)} " +
                    $"{FormatCharacterTime()}");

                if (TryGetClip(command.ClipHash, out var clip))
                {
                    var config = command.ToConfigGesture();
                    LogDash(
                        $"ApplyGesture playing clip={clip.name} hash={command.ClipHash} " +
                        $"duration={config.Duration:F2} speed={config.Speed:F2} " +
                        $"transitionIn={config.TransitionIn:F2} transitionOut={config.TransitionOut:F2}");
                    LogGestureDiagnostics(
                        $"ApplyGesture resolved clip={clip.name} hash={command.ClipHash} " +
                        $"clipLength={clip.length:F3} blend={command.BlendMode} " +
                        $"stopPrevious={command.StopPreviousGestures} {FormatConfig(config)}");
                    float startedRealtime = Time.realtimeSinceStartup;
                    await m_Character.Gestures.CrossFade(
                        clip, null, command.BlendMode, config, command.StopPreviousGestures
                    );
                    LogGestureDiagnostics(
                        $"ApplyGesture remote CrossFade completed clip={clip.name} " +
                        $"wallDuration={Time.realtimeSinceStartup - startedRealtime:F3}s " +
                        $"gesturesPlaying={m_Character.Gestures.IsPlaying} " +
                        $"weight={m_Character.Gestures.CurrentWeight:F3} {FormatCharacterTime()}");
                }
                else
                {
                    LogDash(
                        $"ApplyGesture failed: clip hash {command.ClipHash} not found. " +
                        $"registry={(m_AnimationRegistry != null ? m_AnimationRegistry.name : "null")} " +
                        $"localCache={m_ClipCache.Count}");
                    LogGestureDiagnostics(
                        $"ApplyGesture failed clip lookup hash={command.ClipHash} " +
                        $"registry={(m_AnimationRegistry != null ? m_AnimationRegistry.name : "null")} " +
                        $"localCache={m_ClipCache.Count} globalCache={s_GlobalClipCache.Count}");
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
            LogStateDiagnostics(
                $"ApplyStopState received layer={command.Layer} delay={command.GetDelay():F3} " +
                $"transitionOut={command.GetTransitionOut():F3} {FormatCharacterTime()}");
            m_Character?.States?.Stop(command.Layer, command.GetDelay(), command.GetTransitionOut());
        }
        
        /// <summary>
        /// Apply a received stop gesture command.
        /// </summary>
        public void ApplyStopGestureCommand(NetworkStopGestureCommand command)
        {
            if (!m_EnableSync || !m_IsInitialized || m_IsLocalPlayer) return;

            LogGestureDiagnostics(
                $"ApplyStopGesture received clipHash={command.ClipHash} " +
                $"delay={command.GetDelay():F3} transitionOut={command.GetTransitionOut():F3} " +
                $"gesturesPlaying={m_Character?.Gestures?.IsPlaying ?? false} " +
                $"weight={m_Character?.Gestures?.CurrentWeight ?? 0f:F3} {FormatCharacterTime()}");
            
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
            RegisterClipIfNew(clip);
        }
        
        /// <summary>
        /// Register a State asset so remotes can look it up by hash.
        /// </summary>
        public void RegisterState(State state)
        {
            if (state == null) return;
            int hash = StableHashUtility.GetStableHash(state);
            m_StateCache[hash] = state;
            s_GlobalStateCache[hash] = state;
        }

        /// <summary>
        /// Register a RuntimeAnimatorController so remotes can look it up by hash.
        /// </summary>
        public void RegisterRuntimeController(RuntimeAnimatorController controller)
        {
            if (controller == null) return;
            int hash = StableHashUtility.GetStableHash(controller);
            m_ControllerCache[hash] = controller;
            s_GlobalControllerCache[hash] = controller;
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

            if (TryGetGlobalClip(hash, out clip))
            {
                m_ClipCache[hash] = clip;
                return true;
            }
	            
            clip = null;
            return false;
        }

        private static bool TryGetGlobalClip(int hash, out AnimationClip clip)
        {
            EnsureGlobalAnimationClipsDiscovered();
            return s_GlobalClipCache.TryGetValue(hash, out clip) && clip != null;
        }

        private static void EnsureGlobalAnimationClipsDiscovered()
        {
            if (s_HasDiscoveredGlobalClips) return;

            s_HasDiscoveredGlobalClips = true;
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            int registered = 0;

            Component[] components = FindObjectsByType<Component>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);

            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;

                RegisterGlobalAnimationClipsFromObject(component, 0, visited, ref registered);
                if (registered >= MAX_GLOBAL_AUTO_DISCOVERED_CLIPS) break;
            }
        }

        private void DiscoverAnimationClipsFromCharacter()
        {
            if (m_HasAutoDiscoveredClips || m_Character == null) return;

            m_HasAutoDiscoveredClips = true;
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            int registered = 0;

            Component[] components = m_Character.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null) continue;

                RegisterAnimationClipsFromObject(component, 0, visited, ref registered);
                if (registered >= MAX_AUTO_DISCOVERED_CLIPS) break;
            }

            if (registered > 0)
            {
                LogDash($"auto-registered {registered} animation clip(s) from serialized character data");
            }
        }

        private void RegisterAnimationClipsFromObject(
            object value,
            int depth,
            HashSet<object> visited,
            ref int registered)
        {
            if (value == null) return;
            if (depth > MAX_AUTO_DISCOVERY_DEPTH) return;
            if (registered >= MAX_AUTO_DISCOVERED_CLIPS) return;

            if (value is AnimationClip clip)
            {
                if (RegisterClipIfNew(clip)) registered++;
                return;
            }

            Type type = value.GetType();
            if (IsTerminalType(type)) return;
            if (IsUnsupportedTraversalType(type)) return;

            if (value is Component component &&
                component.GetType().Assembly == typeof(Component).Assembly)
            {
                return;
            }

            if (value is GameObject)
            {
                return;
            }

            if (value is UnityEngine.Object &&
                value is not Component)
            {
                return;
            }

            if (!type.IsValueType && !visited.Add(value)) return;

            if (value is IEnumerable enumerable && value is not string)
            {
                IEnumerator enumerator = null;
                try
                {
                    enumerator = enumerable.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        RegisterAnimationClipsFromObject(enumerator.Current, depth + 1, visited, ref registered);
                        if (registered >= MAX_AUTO_DISCOVERED_CLIPS) return;
                    }
                }
                catch
                {
                    // Serialized component discovery is best-effort. Some Unity native
                    // containers expose IEnumerable but throw from GetEnumerator().
                }
                finally
                {
                    (enumerator as IDisposable)?.Dispose();
                }

                return;
            }

            for (Type current = type;
                 current != null &&
                 current != typeof(object) &&
                 current != typeof(UnityEngine.Object) &&
                 current != typeof(Component) &&
                 current != typeof(Behaviour) &&
                 current != typeof(MonoBehaviour);
                 current = current.BaseType)
            {
                FieldInfo[] fields = current.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (ShouldSkipField(field)) continue;

                    object fieldValue;
                    try
                    {
                        fieldValue = field.GetValue(value);
                    }
                    catch
                    {
                        continue;
                    }

                    RegisterAnimationClipsFromObject(fieldValue, depth + 1, visited, ref registered);
                    if (registered >= MAX_AUTO_DISCOVERED_CLIPS) return;
                }
            }
        }

        private bool RegisterClipIfNew(AnimationClip clip)
        {
            if (clip == null) return false;
            m_ClipCache ??= new Dictionary<int, AnimationClip>();

            int hash = StableHashUtility.GetStableHash(clip);
            bool isNew = !m_ClipCache.TryGetValue(hash, out AnimationClip existing) || existing == null;
            m_ClipCache[hash] = clip;
            s_GlobalClipCache[hash] = clip;
            return isNew;
        }

        private static void RegisterGlobalAnimationClipsFromObject(
            object value,
            int depth,
            HashSet<object> visited,
            ref int registered)
        {
            if (value == null) return;
            if (depth > MAX_AUTO_DISCOVERY_DEPTH) return;
            if (registered >= MAX_GLOBAL_AUTO_DISCOVERED_CLIPS) return;

            if (value is AnimationClip clip)
            {
                if (RegisterGlobalClipIfNew(clip)) registered++;
                return;
            }

            if (value is State state)
            {
                if (RegisterGlobalStateIfNew(state)) registered++;
                return;
            }

            if (value is RuntimeAnimatorController controller)
            {
                if (RegisterGlobalRuntimeControllerIfNew(controller)) registered++;
                return;
            }

            Type type = value.GetType();
            if (IsTerminalType(type)) return;
            if (IsUnsupportedTraversalType(type)) return;

            if (value is Component component &&
                component.GetType().Assembly == typeof(Component).Assembly)
            {
                return;
            }

            if (value is GameObject)
            {
                return;
            }

            if (value is UnityEngine.Object &&
                value is not Component)
            {
                return;
            }

            if (!type.IsValueType && !visited.Add(value)) return;

            if (value is IEnumerable enumerable && value is not string)
            {
                IEnumerator enumerator = null;
                try
                {
                    enumerator = enumerable.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        RegisterGlobalAnimationClipsFromObject(
                            enumerator.Current,
                            depth + 1,
                            visited,
                            ref registered);

                        if (registered >= MAX_GLOBAL_AUTO_DISCOVERED_CLIPS) return;
                    }
                }
                catch
                {
                    // Serialized scene discovery is best-effort. Some Unity native
                    // containers expose IEnumerable but throw from GetEnumerator().
                }
                finally
                {
                    (enumerator as IDisposable)?.Dispose();
                }

                return;
            }

            for (Type current = type;
                 current != null &&
                 current != typeof(object) &&
                 current != typeof(UnityEngine.Object) &&
                 current != typeof(Component) &&
                 current != typeof(Behaviour) &&
                 current != typeof(MonoBehaviour);
                 current = current.BaseType)
            {
                FieldInfo[] fields = current.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (ShouldSkipField(field)) continue;

                    object fieldValue;
                    try
                    {
                        fieldValue = field.GetValue(value);
                    }
                    catch
                    {
                        continue;
                    }

                    RegisterGlobalAnimationClipsFromObject(fieldValue, depth + 1, visited, ref registered);
                    if (registered >= MAX_GLOBAL_AUTO_DISCOVERED_CLIPS) return;
                }
            }
        }

        private static bool RegisterGlobalClipIfNew(AnimationClip clip)
        {
            if (clip == null) return false;

            int hash = StableHashUtility.GetStableHash(clip);
            bool isNew = !s_GlobalClipCache.TryGetValue(hash, out AnimationClip existing) || existing == null;
            s_GlobalClipCache[hash] = clip;
            return isNew;
        }

        private static bool RegisterGlobalStateIfNew(State state)
        {
            if (state == null) return false;

            int hash = StableHashUtility.GetStableHash(state);
            bool isNew = !s_GlobalStateCache.TryGetValue(hash, out State existing) || existing == null;
            s_GlobalStateCache[hash] = state;
            return isNew;
        }

        private static bool RegisterGlobalRuntimeControllerIfNew(RuntimeAnimatorController controller)
        {
            if (controller == null) return false;

            int hash = StableHashUtility.GetStableHash(controller);
            bool isNew = !s_GlobalControllerCache.TryGetValue(hash, out RuntimeAnimatorController existing) ||
                         existing == null;
            s_GlobalControllerCache[hash] = controller;
            return isNew;
        }

        private static bool ShouldSkipField(FieldInfo field)
        {
            if (field == null) return true;
            if (field.IsStatic || field.IsLiteral) return true;
            if (field.IsNotSerialized) return true;

            Type fieldType = field.FieldType;
            return typeof(Delegate).IsAssignableFrom(fieldType) ||
                   IsUnsupportedTraversalType(fieldType) ||
                   fieldType == typeof(IntPtr) ||
                   fieldType == typeof(UIntPtr);
        }

        private static bool IsTerminalType(Type type)
        {
            return type == null ||
                   type.IsPrimitive ||
                   type.IsEnum ||
                   type == typeof(string) ||
                   type == typeof(decimal) ||
                   type == typeof(Type) ||
                   type == typeof(IntPtr) ||
                   type == typeof(UIntPtr);
        }

        private static bool IsUnsupportedTraversalType(Type type)
        {
            if (type == null) return true;

            string typeNamespace = type.Namespace ?? string.Empty;
            if (typeNamespace == "Unity.Collections" ||
                typeNamespace.StartsWith("Unity.Collections.", StringComparison.Ordinal))
            {
                return true;
            }

            string assemblyName = type.Assembly.GetName().Name;
            return assemblyName.StartsWith("Unity.Collections", StringComparison.Ordinal);
        }

        private void ObserveAnimimState()
        {
            if (!m_LogStateDiagnostics || !m_IsInitialized || m_Character?.States == null)
            {
                return;
            }

            if (!TryGetStateLayerSnapshot(m_Character.States, out int activeCount, out string summary))
            {
                return;
            }

            float now = Time.realtimeSinceStartup;

            if (!m_HasObservedAnimimState)
            {
                m_HasObservedAnimimState = true;
                m_LastObservedStateCount = activeCount;
                m_LastObservedStateChangeRealtime = now;
                m_LastObservedStateSampleRealtime = now;
                return;
            }

            if (activeCount != m_LastObservedStateCount)
            {
                float previousStateDuration = now - m_LastObservedStateChangeRealtime;
                LogStateDiagnostics(
                    $"observed GC2 States activeCount {m_LastObservedStateCount}->{activeCount} " +
                    $"previousCountDuration={previousStateDuration:F3}s {summary} {FormatCharacterTime()}");

                m_LastObservedStateCount = activeCount;
                m_LastObservedStateChangeRealtime = now;
                m_LastObservedStateSampleRealtime = now;
                return;
            }

            if (activeCount == 0) return;
            if (now - m_LastObservedStateSampleRealtime < Mathf.Max(0.05f, m_StateDiagnosticsSampleInterval))
            {
                return;
            }

            m_LastObservedStateSampleRealtime = now;
            LogStateDiagnostics(
                $"observed GC2 state sample activeCount={activeCount} " +
                $"activeFor={now - m_LastObservedStateChangeRealtime:F3}s {summary} {FormatCharacterTime()}");
        }

        private void LogHigherStateLayerWarning(int requestedLayer, string context)
        {
            if (!m_LogStateDiagnostics) return;
            if (!TryGetHigherStateLayerSummary(requestedLayer, out string higherLayers)) return;

            LogStateDiagnostics(
                $"{context} requested on layer={requestedLayer}, but higher GC2 state layers are active: " +
                $"{higherLayers}. Higher blend layers can visually override this state after entry gestures finish. " +
                "Use a higher state layer or stop the higher state layer when this state should own the full body.");
        }

        private bool TryGetHigherStateLayerSummary(int requestedLayer, out string summary)
        {
            summary = string.Empty;
            if (m_Character?.States == null) return false;

            FieldInfo field = GetStatesLayersField();
            if (field == null) return false;

            if (field.GetValue(m_Character.States) is not SortedList<int, List<StatePlayableBehaviour>> layers)
            {
                return false;
            }

            StringBuilder builder = null;

            foreach (KeyValuePair<int, List<StatePlayableBehaviour>> entry in layers)
            {
                if (entry.Key <= requestedLayer) continue;

                List<StatePlayableBehaviour> behaviours = entry.Value;
                int count = behaviours?.Count ?? 0;
                if (count <= 0 || behaviours == null) continue;

                StatePlayableBehaviour latest = behaviours[count - 1];
                builder ??= new StringBuilder();
                if (builder.Length > 0) builder.Append("; ");

                builder
                    .Append("layer=").Append(entry.Key)
                    .Append(" latest=")
                    .Append(latest.State != null ? latest.State.name : "clip/controller")
                    .Append(" exiting=").Append(latest.IsExiting)
                    .Append(" complete=").Append(latest.IsComplete)
                    .Append(" weight=").Append(latest.CurrentWeight.ToString("F3"));
            }

            if (builder == null) return false;

            summary = builder.ToString();
            return true;
        }

        private static bool TryGetStateLayerSnapshot(
            StatesOutput states,
            out int activeCount,
            out string summary)
        {
            activeCount = 0;
            summary = "layers=unavailable";
            if (states == null) return false;

            FieldInfo field = GetStatesLayersField();
            if (field == null) return false;

            if (field.GetValue(states) is not SortedList<int, List<StatePlayableBehaviour>> layers)
            {
                return false;
            }

            var builder = new StringBuilder("layers=[");
            bool firstLayer = true;

            foreach (KeyValuePair<int, List<StatePlayableBehaviour>> entry in layers)
            {
                List<StatePlayableBehaviour> behaviours = entry.Value;
                int layerCount = behaviours?.Count ?? 0;
                activeCount += layerCount;

                if (!firstLayer) builder.Append("; ");
                firstLayer = false;
                builder.Append("layer=").Append(entry.Key).Append(" count=").Append(layerCount);

                if (layerCount <= 0 || behaviours == null) continue;

                StatePlayableBehaviour latest = behaviours[layerCount - 1];
                builder
                    .Append(" latest=")
                    .Append(latest.State != null ? latest.State.name : "clip/controller")
                    .Append(" exiting=").Append(latest.IsExiting)
                    .Append(" complete=").Append(latest.IsComplete)
                    .Append(" weight=").Append(latest.CurrentWeight.ToString("F3"));
            }

            builder.Append(']');
            summary = builder.ToString();
            return true;
        }

        private static FieldInfo GetStatesLayersField()
        {
            if (s_TriedResolveStatesLayersField) return s_StatesLayersField;

            s_TriedResolveStatesLayersField = true;
            s_StatesLayersField = typeof(StatesOutput).GetField(
                "m_Layers",
                BindingFlags.Instance | BindingFlags.NonPublic);

            return s_StatesLayersField;
        }

        private void ObserveGestureState()
        {
            if (!m_LogGestureDiagnostics || !m_IsInitialized || m_Character?.Gestures == null)
            {
                return;
            }

            bool isPlaying = m_Character.Gestures.IsPlaying;
            float weight = m_Character.Gestures.CurrentWeight;
            float now = Time.realtimeSinceStartup;

            if (!m_HasObservedGestureState)
            {
                m_HasObservedGestureState = true;
                m_LastObservedGesturePlaying = isPlaying;
                m_LastObservedGestureChangeRealtime = now;
                m_LastObservedGestureSampleRealtime = now;
                return;
            }

            if (isPlaying != m_LastObservedGesturePlaying)
            {
                float previousStateDuration = now - m_LastObservedGestureChangeRealtime;
                LogGestureDiagnostics(
                    $"observed GC2 Gestures.IsPlaying {m_LastObservedGesturePlaying}->{isPlaying} " +
                    $"previousStateDuration={previousStateDuration:F3}s weight={weight:F3} " +
                    $"{FormatCharacterTime()}");

                m_LastObservedGesturePlaying = isPlaying;
                m_LastObservedGestureChangeRealtime = now;
                m_LastObservedGestureSampleRealtime = now;
                return;
            }

            if (!isPlaying) return;
            if (now - m_LastObservedGestureSampleRealtime < Mathf.Max(0.05f, m_GestureDiagnosticsSampleInterval))
            {
                return;
            }

            m_LastObservedGestureSampleRealtime = now;
            LogGestureDiagnostics(
                $"observed GC2 gesture sample playing=True " +
                $"activeFor={now - m_LastObservedGestureChangeRealtime:F3}s weight={weight:F3} " +
                $"{FormatCharacterTime()}");
        }

        private string FormatCharacterTime()
        {
            if (m_Character == null)
            {
                return "characterTime=null";
            }

            return
                $"unityTime={Time.time:F3} realtime={Time.realtimeSinceStartup:F3} " +
                $"characterTime={m_Character.Time.Time:F3} characterDelta={m_Character.Time.DeltaTime:F4}";
        }

        private static string FormatConfig(ConfigGesture config)
        {
            return
                $"config(delay={config.DelayIn:F3}, duration={config.Duration:F3}, " +
                $"speed={config.Speed:F3}, rootMotion={config.RootMotion}, " +
                $"transitionIn={config.TransitionIn:F3}, transitionOut={config.TransitionOut:F3})";
        }

        private static string FormatConfig(ConfigState config)
        {
            return
                $"config(delay={config.DelayIn:F3}, duration={config.Duration:F3}, " +
                $"speed={config.Speed:F3}, weight={config.Weight:F3}, rootMotion={config.RootMotion}, " +
                $"transitionIn={config.TransitionIn:F3}, transitionOut={config.TransitionOut:F3})";
        }

        private static string FormatCommand(NetworkStateCommand command)
        {
            ConfigState config = command.ToConfigState();
            return
                $"command(flags=0x{command.Flags:X2}, animationId={command.AnimationId}, " +
                $"type={command.StateType}, layer={command.Layer}, blend={command.BlendMode}, " +
                $"rootMotion={command.RootMotion}, rawDelay={command.DelayIn}, " +
                $"rawDuration={command.Duration}, rawSpeed={command.Speed}, rawWeight={command.Weight}, " +
                $"rawTransitionIn={command.TransitionIn}, rawTransitionOut={command.TransitionOut}, " +
                $"{FormatConfig(config)})";
        }

        private static string FormatCommand(NetworkGestureCommand command)
        {
            ConfigGesture config = command.ToConfigGesture();
            return
                $"command(flags=0x{command.Flags:X2}, clipHash={command.ClipHash}, " +
                $"blend={command.BlendMode}, rootMotion={command.RootMotion}, " +
                $"stopPrevious={command.StopPreviousGestures}, " +
                $"rawDelay={command.DelayIn}, rawDuration={command.Duration}, rawSpeed={command.Speed}, " +
                $"rawTransitionIn={command.TransitionIn}, rawTransitionOut={command.TransitionOut}, " +
                $"{FormatConfig(config)})";
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        private void LogDash(string message)
        {
            if (!m_LogDashDiagnostics) return;
            string characterName = m_Character != null ? m_Character.name : name;
            Debug.Log($"[NetworkDashDebug][AnimimController] {characterName}: {message}", this);
        }

        private void LogGestureDiagnostics(string message)
        {
            if (!m_LogGestureDiagnostics) return;
            string characterName = m_Character != null ? m_Character.name : name;
            Debug.Log($"[NetworkGestureDebug][AnimimController] {characterName}: {message}", this);
        }

        private void LogStateDiagnostics(string message)
        {
            if (!m_LogStateDiagnostics) return;
            string characterName = m_Character != null ? m_Character.name : name;
            Debug.Log($"[NetworkStateDebug][AnimimController] {characterName}: {message}", this);
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

            EnsureGlobalAnimationClipsDiscovered();
            if (s_GlobalStateCache.TryGetValue(hash, out state) && state != null)
            {
                m_StateCache[hash] = state;
                return true;
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

            EnsureGlobalAnimationClipsDiscovered();
            if (s_GlobalControllerCache.TryGetValue(hash, out controller) && controller != null)
            {
                m_ControllerCache[hash] = controller;
                return true;
            }

            controller = null;
            return false;
        }
    }
}
