using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Is Remote Network Player")]
    [Description("Returns true if the selected Network Character represents a remote player")]

    [Category("Network/Characters/Is Remote Network Player")]

    [Keywords("Network", "Character", "Player", "Remote", "Client")]
    [Image(typeof(IconBust), ColorTheme.Type.Blue)]

    [Serializable]
    public sealed class ConditionNetworkCharacterIsRemotePlayer : Condition
    {
        [SerializeField]
        private PropertyGetGameObject m_Character = GetGameObjectSelf.Create();

        protected override string Summary => $"is Remote Network Player {m_Character}";

        protected override bool Run(Args args)
        {
            NetworkCharacter character = m_Character.Get<NetworkCharacter>(args);
            return character != null && character.IsRemotePlayer;
        }
    }
}
