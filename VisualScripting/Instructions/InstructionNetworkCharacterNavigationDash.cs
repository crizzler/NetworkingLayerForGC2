using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(0, 1, 0)]

    [Title("Network Dash")]
    [Description("Moves the Character in the chosen direction and replicates the semantic dash plus its gesture animation")]

    [Category("Network/Characters/Navigation/Network Dash")]

    [Parameter("Direction", "Vector oriented towards the desired direction")]
    [Parameter("Velocity", "Velocity the Character moves throughout the whole movement")]
    [Parameter("Duration", "Defines the duration it takes to move forward at a constant velocity")]
    [Parameter("Wait to Finish", "If true this Instruction waits until the local dash is completed")]
    [Parameter("Mode", "Whether to use Cardinal Animations or a single animation")]
    [Parameter("Animation Speed", "Determines the speed coefficient applied to the animation played")]
    [Parameter("Transition In", "The time it takes to blend into the animation")]
    [Parameter("Transition Out", "The time it takes to blend out of the animation")]

    [Keywords("Network", "Dash", "Leap", "Blink", "Roll", "Flash")]
    [Image(typeof(IconCharacterDash), ColorTheme.Type.Blue)]
    [Serializable]
    public class InstructionNetworkCharacterNavigationDash : TInstructionCharacterNavigation
    {
        private const int DIRECTION_KEY = 5;

        [Serializable]
        public struct DashAnimation
        {
            public enum AnimationMode
            {
                CardinalAnimation,
                SingleAnimation
            }

            [SerializeField] private AnimationMode m_Mode;

            [SerializeField] private AnimationClip m_AnimationForward;
            [SerializeField] private AnimationClip m_AnimationBackward;
            [SerializeField] private AnimationClip m_AnimationRight;
            [SerializeField] private AnimationClip m_AnimationLeft;

            [SerializeField] private AnimationClip m_Animation;

            public AnimationMode Mode => m_Mode;

            public AnimationClip GetClip(float angle)
            {
                return m_Mode switch
                {
                    AnimationMode.CardinalAnimation => angle switch
                    {
                        <= 45f and >= -45f => m_AnimationForward,
                        < 135f and > 45f => m_AnimationLeft,
                        > -135f and < -45f => m_AnimationRight,
                        _ => m_AnimationBackward
                    },
                    AnimationMode.SingleAnimation => m_Animation,
                    _ => null
                };
            }
        }

        [SerializeField] private PropertyGetDirection m_Direction = GetDirectionCharactersMoving.Create;
        [SerializeField] private PropertyGetDecimal m_Velocity = new PropertyGetDecimal(20f);
        [SerializeField] private PropertyGetDecimal m_Duration = new PropertyGetDecimal(0.25f);

        [SerializeField] [Range(0f, 1f)] private float m_Gravity = 1f;
        [SerializeField] private bool m_WaitToFinish = true;
        [SerializeField] private DashAnimation m_DashAnimation;

        [SerializeField] private float m_AnimationSpeed = 1f;
        [SerializeField] private float m_TransitionIn = 0.1f;
        [SerializeField] private float m_TransitionOut = 0.2f;
        [SerializeField] private bool m_LogDiagnostics = false;

        public override string Title => $"Network Dash {m_Character} towards {m_Direction}";

        protected override async Task Run(Args args)
        {
            Character character = m_Character.Get<Character>(args);
            if (character == null)
            {
                Log("skipped: character reference resolved to null");
                return;
            }

            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
            if (networkCharacter != null &&
                !networkCharacter.IsOwnerInstance &&
                !networkCharacter.IsServerInstance)
            {
                Log(
                    $"skipped '{character.name}': non-authoritative instance " +
                    $"netId={networkCharacter.NetworkId} owner={networkCharacter.IsOwnerInstance} " +
                    $"server={networkCharacter.IsServerInstance}");
                return;
            }

            if (character.Busy.AreLegsBusy)
            {
                Log($"skipped '{character.name}': legs are busy");
                return;
            }

            Vector3 direction = m_Direction.Get(args);
            if (direction == Vector3.zero) direction = character.transform.forward;

            float velocity = (float)m_Velocity.Get(args);
            float duration = (float)m_Duration.Get(args);

            if (!character.Dash.CanDash())
            {
                Log($"skipped '{character.name}': Character.Dash.CanDash returned false");
                return;
            }

            Log(
                $"local dash '{character.name}' netId={networkCharacter?.NetworkId ?? 0} " +
                $"owner={networkCharacter?.IsOwnerInstance ?? false} server={networkCharacter?.IsServerInstance ?? false} " +
                $"dir={FormatVector(direction)} velocity={velocity:F2} gravity={m_Gravity:F2} " +
                $"duration={duration:F2} fade={m_TransitionOut:F2}");

            Task task = character.Dash.Execute(
                direction,
                velocity,
                m_Gravity,
                duration,
                m_TransitionOut);

            character.Busy.MakeLegsBusy();

            UnitMotionNetworkController motionController =
                networkCharacter != null ? networkCharacter.MotionController : character.Motion as UnitMotionNetworkController;

            motionController?.SubmitPredictedDash(
                direction,
                velocity,
                m_Gravity,
                duration,
                m_TransitionOut);

            if (motionController == null)
            {
                Log(
                    $"motion sync missing for '{character.name}' netId={networkCharacter?.NetworkId ?? 0}: " +
                    "NetworkCharacter.MotionController is null and Character.Motion is not UnitMotionNetworkController");
            }

            float angle = Vector3.SignedAngle(
                direction,
                character.transform.forward,
                Vector3.up);

            AnimationClip animationClip = m_DashAnimation.GetClip(angle);
            if (animationClip != null)
            {
                ConfigGesture config = new ConfigGesture(
                    0f,
                    animationClip.length,
                    m_AnimationSpeed,
                    false,
                    m_TransitionIn,
                    m_TransitionOut);

                UnitAnimimNetworkController animimController =
                    networkCharacter != null
                        ? networkCharacter.AnimimController
                        : character.GetComponent<UnitAnimimNetworkController>();

                if (animimController != null && animimController.IsInitialized)
                {
                    animimController.RegisterClip(animationClip);
                    Log(
                        $"local gesture '{character.name}' clip={animationClip.name} " +
                        $"hash={StableHashUtility.GetStableHash(animationClip)} animimReady=True");
                    _ = animimController.PlayGesture(
                        animationClip,
                        null,
                        BlendMode.Blend,
                        config,
                        true);
                }
                else
                {
                    Log(
                        $"gesture sync missing for '{character.name}' clip={animationClip.name}: " +
                        $"animimController={(animimController != null ? "present" : "null")} " +
                        $"initialized={animimController?.IsInitialized ?? false}; playing local-only fallback");
                    _ = character.Gestures.CrossFade(
                        animationClip,
                        null,
                        BlendMode.Blend,
                        config,
                        true);
                }

                if (m_DashAnimation.Mode == DashAnimation.AnimationMode.SingleAnimation)
                {
                    character.Kernel.Facing.SetLayerDirection(
                        DIRECTION_KEY,
                        direction,
                        Math.Max(animationClip.length - m_TransitionOut, 0f));
                }
            }
            else
            {
                Log($"gesture skipped for '{character.name}': no dash animation clip selected");
            }

            if (m_WaitToFinish) await task;
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkDashDebug][Instruction] {message}");
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }
    }
}
