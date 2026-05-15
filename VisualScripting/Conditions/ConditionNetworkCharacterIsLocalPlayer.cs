using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Is Local Network Player")]
    [Description("Returns true if the Network Character is owned by this local peer")]

    [Category("Network/Characters/Is Local Network Player")]

    [Keywords("Network", "Character", "Player", "Owner", "Local")]
    [Image(typeof(IconBust), ColorTheme.Type.Green)]

    [Serializable]
    public class ConditionNetworkCharacterIsLocalPlayer : Condition
    {
        [SerializeField]
        private PropertyGetGameObject m_Character = GetGameObjectSelf.Create();

        protected override string Summary => $"is Local Network Player {this.m_Character}";

        protected override bool Run(Args args)
        {
            NetworkCharacter character = this.m_Character.Get<NetworkCharacter>(args);
            return character != null && character.IsLocalPlayer;
        }
    }
}
