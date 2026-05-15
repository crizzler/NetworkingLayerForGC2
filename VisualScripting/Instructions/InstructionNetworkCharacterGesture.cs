using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(0, 1, 0)]

    [Title("Network Play Gesture")]
    [Description("Plays a Game Creator 2 Gesture on a Character and replicates it to remote clients")]

    [Category("Network/Characters/Animation/Network Play Gesture")]

    [Parameter("Character", "The character that plays the animation")]
    [Parameter("Animation Clip", "The Animation Clip that is played")]
    [Parameter(
        "Avatar Mask",
        "(Optional) Allows to play the animation on specific body parts of the Character"
    )]
    [Parameter(
        "Blend Mode",
        "Additively adds the new animation on top of the rest or overrides any lower layer animations"
    )]
    [Parameter("Delay", "Amount of seconds to wait before the animation starts to play")]
    [Parameter("Use Animation Length", "If true the gesture duration uses the Animation Clip length")]
    [Parameter("Duration", "Total seconds the gesture remains active when Use Animation Length is false")]
    [Parameter("Speed", "Speed coefficient at which the animation plays. 1 means normal speed")]
    [Parameter("Transition In", "The amount of seconds the animation takes to blend in")]
    [Parameter("Transition Out", "The amount of seconds the animation takes to blend out")]
    [Parameter("Stop Previous Gestures", "If true any current gesture blends out before this gesture plays")]
    [Parameter("Wait To Complete", "If true this Instruction waits until the animation is complete")]

    [Keywords("Network", "Characters", "Animation", "Animate", "Gesture", "Play")]
    [Image(typeof(IconCharacterGesture), ColorTheme.Type.Blue)]

    [Serializable]
    public class InstructionNetworkCharacterGesture : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Character = GetGameObjectPlayer.Create();

        [Space]
        [SerializeField] private PropertyGetAnimation m_AnimationClip = GetAnimationInstance.Create;
        [SerializeField] private AvatarMask m_AvatarMask = null;
        [SerializeField] private BlendMode m_BlendMode = BlendMode.Blend;

        [SerializeField] private PropertyGetDecimal m_Delay = GetDecimalConstantZero.Create;
        [SerializeField] private bool m_UseAnimationLength = false;
        [SerializeField] private PropertyGetDecimal m_Duration = new PropertyGetDecimal(1f);
        [SerializeField] private PropertyGetDecimal m_Speed = GetDecimalConstantOne.Create;
        [SerializeField] private bool m_UseRootMotion = false;
        [SerializeField] private PropertyGetDecimal m_TransitionIn = new PropertyGetDecimal(0.1f);
        [SerializeField] private PropertyGetDecimal m_TransitionOut = new PropertyGetDecimal(0.1f);

        [Space]
        [SerializeField] private bool m_StopPreviousGestures = false;
        [SerializeField] private bool m_WaitToComplete = true;
        [SerializeField] private bool m_PlayLocalFallback = true;
        [SerializeField] private bool m_LogDiagnostics = false;

        public override string Title => $"Network Gesture {m_AnimationClip} on {m_Character}";

        protected override async Task Run(Args args)
        {
            AnimationClip animationClip = m_AnimationClip.Get(args);
            if (animationClip == null) return;

            Character character = m_Character.Get<Character>(args);
            if (character == null) return;

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

            float transitionIn = (float)m_TransitionIn.Get(args);
            float transitionOut = (float)m_TransitionOut.Get(args);
            float duration = m_UseAnimationLength
                ? animationClip.length
                : Mathf.Max(0f, (float)m_Duration.Get(args));

            ConfigGesture configuration = new ConfigGesture(
                (float)m_Delay.Get(args),
                duration,
                (float)m_Speed.Get(args),
                m_UseRootMotion,
                transitionIn,
                transitionOut);

            Log(
                $"requested '{character.name}' clip={animationClip.name} " +
                $"hash={StableHashUtility.GetStableHash(animationClip)} clipLength={animationClip.length:F3} " +
                $"blend={m_BlendMode} stopPrevious={m_StopPreviousGestures} wait={m_WaitToComplete} " +
                $"durationSource={(m_UseAnimationLength ? "clipLength" : "explicit")} " +
                $"delay={configuration.DelayIn:F3} duration={configuration.Duration:F3} " +
                $"speed={configuration.Speed:F3} rootMotion={configuration.RootMotion} " +
                $"transitionIn={configuration.TransitionIn:F3} transitionOut={configuration.TransitionOut:F3} " +
                $"networkCharacter={(networkCharacter != null)} owner={networkCharacter?.IsOwnerInstance ?? false} " +
                $"server={networkCharacter?.IsServerInstance ?? false}");

            if (configuration.Duration <= configuration.TransitionIn + configuration.TransitionOut)
            {
                Log(
                    $"warning '{character.name}' clip={animationClip.name}: duration={configuration.Duration:F3}s " +
                    $"is not longer than transitionIn+transitionOut=" +
                    $"{configuration.TransitionIn + configuration.TransitionOut:F3}s. " +
                    "This usually appears as an eye-blink gesture; use an explicit longer duration for pose clips.");
            }

            UnitAnimimNetworkController animimController =
                networkCharacter != null
                    ? networkCharacter.AnimimController
                    : character.GetComponent<UnitAnimimNetworkController>();

            Task gestureTask = null;
            if (animimController != null &&
                animimController.IsInitialized &&
                animimController.IsSyncEnabled)
            {
                animimController.RegisterClip(animationClip);
                Log($"playing network gesture '{character.name}' clip={animationClip.name}");
                gestureTask = animimController.PlayGesture(
                    animationClip,
                    m_AvatarMask,
                    m_BlendMode,
                    configuration,
                    m_StopPreviousGestures);
            }
            else if (m_PlayLocalFallback)
            {
                Log(
                    $"playing local fallback '{character.name}' clip={animationClip.name} " +
                    $"controller={(animimController != null ? "present" : "null")} " +
                    $"initialized={animimController?.IsInitialized ?? false} " +
                    $"sync={animimController?.IsSyncEnabled ?? false}");
                gestureTask = character.Gestures.CrossFade(
                    animationClip,
                    m_AvatarMask,
                    m_BlendMode,
                    configuration,
                    m_StopPreviousGestures);
            }
            else
            {
                Log(
                    $"skipped playback '{character.name}' clip={animationClip.name}: " +
                    $"controller={(animimController != null ? "present" : "null")} " +
                    $"initialized={animimController?.IsInitialized ?? false} " +
                    $"sync={animimController?.IsSyncEnabled ?? false} fallback={m_PlayLocalFallback}");
            }

            if (m_WaitToComplete && gestureTask != null)
            {
                float startedRealtime = UnityEngine.Time.realtimeSinceStartup;
                await gestureTask;
                Log(
                    $"gesture task completed '{character.name}' clip={animationClip.name} " +
                    $"wallDuration={UnityEngine.Time.realtimeSinceStartup - startedRealtime:F3}s " +
                    $"gesturesPlaying={character.Gestures.IsPlaying} weight={character.Gestures.CurrentWeight:F3}");
            }
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkGestureInstruction] {message}");
        }
    }
}
