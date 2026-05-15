using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(0, 1, 0)]

    [Title("Network Enter State")]
    [Description("Makes a Character start a Game Creator 2 animation State and replicates it to remote clients")]

    [Category("Network/Characters/Animation/Network Enter State")]

    [Parameter("Character", "The character that plays the animation state")]
    [Parameter("State", "The animation data necessary to play a state")]
    [Parameter("Layer", "Slot number in which the animation state is allocated")]
    [Parameter(
        "Blend Mode",
        "Additively adds the new animation on top of the rest or overrides any lower layer animations"
    )]
    [Parameter("Delay", "Amount of seconds to wait before the animation starts to play")]
    [Parameter("Speed", "Speed coefficient at which the animation plays")]
    [Parameter("Weight", "The opacity of the animation that plays. Between 0 and 1")]
    [Parameter("Transition", "The amount of seconds the animation takes to blend in")]
    [Parameter("Use Duration", "If true the state automatically exits after Duration seconds")]
    [Parameter("Duration", "Total seconds the state remains active when Use Duration is true")]
    [Parameter("Wait To Complete", "If true this Instruction waits until the state completes")]

    [Keywords("Network", "Characters", "Animation", "Animate", "State", "Play")]
    [Image(typeof(IconCharacterState), ColorTheme.Type.Blue)]

    [Serializable]
    public class InstructionNetworkCharacterEnterState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Character = GetGameObjectPlayer.Create();

        [Space]
        [SerializeField] private StateData m_State = new StateData(StateData.StateType.State);
        [SerializeField] private PropertyGetInteger m_Layer = new PropertyGetInteger(1);
        [SerializeField] private BlendMode m_BlendMode = BlendMode.Blend;

        [Space]
        [SerializeField] private float m_Delay = 0f;
        [SerializeField] private float m_Speed = 1f;
        [SerializeField] [Range(0f, 1f)] private float m_Weight = 1f;
        [SerializeField] private float m_Transition = 0.1f;
        [SerializeField] private bool m_RootMotion = false;

        [Space]
        [SerializeField] private bool m_UseDuration = false;
        [SerializeField] private PropertyGetDecimal m_Duration = new PropertyGetDecimal(1f);
        [SerializeField] private bool m_WaitToComplete = false;
        [SerializeField] private bool m_PlayLocalFallback = true;
        [SerializeField] private bool m_LogDiagnostics = false;

        public override string Title => $"Network State {m_State} on {m_Character} in Layer {m_Layer}";

        protected override async Task Run(Args args)
        {
            Character character = m_Character.Get<Character>(args);
            if (character == null) return;

            if (!m_State.IsValid(character))
            {
                Log($"skipped: state data '{m_State}' is not valid for '{character.name}'");
                return;
            }

            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
            if (networkCharacter != null &&
                !networkCharacter.IsOwnerInstance &&
                !networkCharacter.IsServerInstance)
            {
                Log(
                    $"skipped '{character.name}': non-authoritative instance " +
                    $"netId={networkCharacter.NetworkId}");
                return;
            }

            int layer = (int)m_Layer.Get(args);
            ConfigState configuration = new ConfigState(
                m_Delay,
                m_Speed,
                m_Weight,
                m_Transition,
                0f)
            {
                RootMotion = m_RootMotion
            };

            if (m_UseDuration)
            {
                configuration.Duration = Mathf.Max(0f, (float)m_Duration.Get(args));
            }

            UnitAnimimNetworkController animimController =
                networkCharacter != null
                    ? networkCharacter.AnimimController
                    : character.GetComponent<UnitAnimimNetworkController>();

            Log(
                $"requested '{character.name}' state={m_State} type={m_State.Type} layer={layer} " +
                $"blend={m_BlendMode} delay={configuration.DelayIn:F3} duration={configuration.Duration:F3} " +
                $"speed={configuration.Speed:F3} weight={configuration.Weight:F3} rootMotion={configuration.RootMotion} " +
                $"transition={configuration.TransitionIn:F3} networkCharacter={(networkCharacter != null)} " +
                $"owner={networkCharacter?.IsOwnerInstance ?? false} server={networkCharacter?.IsServerInstance ?? false}");

            Task stateTask = null;
            if (animimController != null &&
                animimController.IsInitialized &&
                animimController.IsSyncEnabled)
            {
                stateTask = PlayNetworkState(args, character, animimController, layer, configuration);
            }
            else if (m_PlayLocalFallback)
            {
                Log(
                    $"playing local fallback '{character.name}' state={m_State} " +
                    $"controller={(animimController != null ? "present" : "null")} " +
                    $"initialized={animimController?.IsInitialized ?? false} " +
                    $"sync={animimController?.IsSyncEnabled ?? false}");

                stateTask = character.States.SetState(m_State, layer, m_BlendMode, configuration);
            }
            else
            {
                Log(
                    $"skipped playback '{character.name}' state={m_State}: " +
                    $"controller={(animimController != null ? "present" : "null")} " +
                    $"initialized={animimController?.IsInitialized ?? false} " +
                    $"sync={animimController?.IsSyncEnabled ?? false} fallback={m_PlayLocalFallback}");
            }

            if (m_WaitToComplete && stateTask != null)
            {
                float startedRealtime = UnityEngine.Time.realtimeSinceStartup;
                await stateTask;
                Log(
                    $"state task completed '{character.name}' state={m_State} " +
                    $"wallDuration={UnityEngine.Time.realtimeSinceStartup - startedRealtime:F3}s");
            }
        }

        private Task PlayNetworkState(
            Args args,
            Character character,
            UnitAnimimNetworkController animimController,
            int layer,
            ConfigState configuration)
        {
            switch (m_State.Type)
            {
                case StateData.StateType.AnimationClip:
                    AnimationClip clip = m_State.GetAnimationClip(args);
                    if (clip == null) return null;

                    animimController.RegisterClip(clip);
                    Log(
                        $"playing network clip state '{character.name}' clip={clip.name} " +
                        $"hash={StableHashUtility.GetStableHash(clip)} clipLength={clip.length:F3}");
                    return animimController.SetState(
                        clip,
                        m_State.AvatarMask,
                        layer,
                        m_BlendMode,
                        configuration);

                case StateData.StateType.RuntimeController:
                    RuntimeAnimatorController controller = m_State.RuntimeController;
                    if (controller == null) return null;

                    animimController.RegisterRuntimeController(controller);
                    Log(
                        $"playing network controller state '{character.name}' controller={controller.name} " +
                        $"hash={StableHashUtility.GetStableHash(controller)} clips={controller.animationClips?.Length ?? 0}");
                    return animimController.SetState(
                        controller,
                        m_State.AvatarMask,
                        layer,
                        m_BlendMode,
                        configuration);

                case StateData.StateType.State:
                    State state = m_State.State;
                    if (state == null) return null;

                    animimController.RegisterState(state);
                    Log(
                        $"playing network state asset '{character.name}' state={state.name} " +
                        $"hash={StableHashUtility.GetStableHash(state)} " +
                        $"entryClip={(state.HasEntryClip ? state.EntryClip.name : "none")} " +
                        $"exitClip={(state.HasExitClip ? state.ExitClip.name : "none")}");
                    return animimController.SetState(
                        state,
                        layer,
                        m_BlendMode,
                        configuration);

                default:
                    return null;
            }
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkStateInstruction] {message}");
        }
    }
}
