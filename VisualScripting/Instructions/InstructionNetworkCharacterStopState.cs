using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(0, 1, 0)]

    [Title("Network Stop State")]
    [Description("Stops a Game Creator 2 animation State layer and replicates the stop to remote clients")]

    [Category("Network/Characters/Animation/Network Stop State")]

    [Parameter("Character", "The character that plays animation States")]
    [Parameter("Layer", "Slot number in which the animation state is allocated")]
    [Parameter("Delay", "Amount of seconds to wait before the animation starts to blend out")]
    [Parameter("Transition", "The amount of seconds the animation takes to blend out")]

    [Keywords("Network", "Characters", "Animation", "Animate", "State", "Stop")]
    [Image(typeof(IconCharacterState), ColorTheme.Type.Blue, typeof(OverlayCross))]

    [Serializable]
    public class InstructionNetworkCharacterStopState : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Character = GetGameObjectPlayer.Create();

        [Space]
        [SerializeField] private PropertyGetInteger m_Layer = new PropertyGetInteger(1);
        [SerializeField] private PropertyGetDecimal m_Delay = GetDecimalConstantZero.Create;
        [SerializeField] private PropertyGetDecimal m_Transition = new PropertyGetDecimal(0.1f);

        [Space]
        [SerializeField] private bool m_PlayLocalFallback = true;
        [SerializeField] private bool m_LogDiagnostics = false;

        public override string Title => $"Network Stop state on {m_Character} in Layer {m_Layer}";

        protected override Task Run(Args args)
        {
            Character character = m_Character.Get<Character>(args);
            if (character == null) return DefaultResult;

            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
            if (networkCharacter != null &&
                !networkCharacter.IsOwnerInstance &&
                !networkCharacter.IsServerInstance)
            {
                Log(
                    $"skipped '{character.name}': non-authoritative instance " +
                    $"netId={networkCharacter.NetworkId}");
                return DefaultResult;
            }

            int layer = (int)m_Layer.Get(args);
            float delay = (float)m_Delay.Get(args);
            float transition = (float)m_Transition.Get(args);

            Log(
                $"requested '{character.name}' layer={layer} delay={delay:F3} transition={transition:F3} " +
                $"networkCharacter={(networkCharacter != null)} owner={networkCharacter?.IsOwnerInstance ?? false} " +
                $"server={networkCharacter?.IsServerInstance ?? false}");

            UnitAnimimNetworkController animimController =
                networkCharacter != null
                    ? networkCharacter.AnimimController
                    : character.GetComponent<UnitAnimimNetworkController>();

            if (animimController != null &&
                animimController.IsInitialized &&
                animimController.IsSyncEnabled)
            {
                Log($"stopping network state '{character.name}' layer={layer}");
                animimController.StopState(layer, delay, transition);
            }
            else if (m_PlayLocalFallback)
            {
                Log(
                    $"stopping local fallback '{character.name}' layer={layer} " +
                    $"controller={(animimController != null ? "present" : "null")} " +
                    $"initialized={animimController?.IsInitialized ?? false} " +
                    $"sync={animimController?.IsSyncEnabled ?? false}");
                character.States.Stop(layer, delay, transition);
            }
            else
            {
                Log(
                    $"skipped stop '{character.name}' layer={layer}: " +
                    $"controller={(animimController != null ? "present" : "null")} " +
                    $"initialized={animimController?.IsInitialized ?? false} " +
                    $"sync={animimController?.IsSyncEnabled ?? false} fallback={m_PlayLocalFallback}");
            }

            return DefaultResult;
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkStateInstruction] {message}");
        }
    }
}
