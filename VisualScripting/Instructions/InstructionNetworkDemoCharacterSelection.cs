using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkDemoCharacterSelectionAction
    {
        SelectCharacter = 0,
        StartHost = 1,
        JoinServer = 2,
        StopSession = 3
    }

    public interface INetworkDemoCharacterSelectionTarget
    {
        void SelectCharacter(int index);
        void StartHost();
        void JoinServer();
        void StopSession();
    }

    [Version(0, 1, 0)]

    [Title("Network Demo Character Selection")]
    [Description("Runs one action on a network demo character selection controller")]

    [Category("Network/Demo/Character Selection")]

    [Parameter("Target", "Game Object with a component that implements the network demo character selection target")]
    [Parameter("Action", "Selection screen action to run")]
    [Parameter("Character Index", "Zero-based character slot used by Select Character")]

    [Keywords("Network", "Demo", "Character", "Selection", "PurrNet", "Host", "Join")]
    [Image(typeof(IconCharacter), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkDemoCharacterSelection : Instruction
    {
        [SerializeField] private GameObject m_Target;
        [SerializeField] private NetworkDemoCharacterSelectionAction m_Action;
        [SerializeField] private int m_CharacterIndex;

        public InstructionNetworkDemoCharacterSelection()
        {
        }

        public InstructionNetworkDemoCharacterSelection(
            GameObject target,
            NetworkDemoCharacterSelectionAction action,
            int characterIndex = 0)
        {
            m_Target = target;
            m_Action = action;
            m_CharacterIndex = characterIndex;
        }

        public override string Title
        {
            get
            {
                return m_Action == NetworkDemoCharacterSelectionAction.SelectCharacter
                    ? $"Select Demo Character {m_CharacterIndex + 1}"
                    : $"Demo Character Selection: {m_Action}";
            }
        }

        protected override Task Run(Args args)
        {
            INetworkDemoCharacterSelectionTarget target = ResolveTarget();
            if (target == null)
            {
                Debug.LogWarning(
                    "[InstructionNetworkDemoCharacterSelection] No selection target found.",
                    m_Target);
                return DefaultResult;
            }

            switch (m_Action)
            {
                case NetworkDemoCharacterSelectionAction.SelectCharacter:
                    target.SelectCharacter(m_CharacterIndex);
                    break;

                case NetworkDemoCharacterSelectionAction.StartHost:
                    target.StartHost();
                    break;

                case NetworkDemoCharacterSelectionAction.JoinServer:
                    target.JoinServer();
                    break;

                case NetworkDemoCharacterSelectionAction.StopSession:
                    target.StopSession();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            return DefaultResult;
        }

        private INetworkDemoCharacterSelectionTarget ResolveTarget()
        {
            if (m_Target == null) return null;

            MonoBehaviour[] components = m_Target.GetComponents<MonoBehaviour>();
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] is INetworkDemoCharacterSelectionTarget target)
                {
                    return target;
                }
            }

            return null;
        }
    }
}
