using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Character Role")]
    [Description("Returns the current Network Character role: None, Server, LocalClient, or RemoteClient")]

    [Category("Network/Characters/Network Character Role")]

    [Image(typeof(IconPlayer), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkCharacterRole : PropertyTypeGetString
    {
        [SerializeField]
        private PropertyGetGameObject m_Character = GetGameObjectPlayer.Create();

        public override string Get(Args args)
        {
            NetworkCharacter character = m_Character.Get<NetworkCharacter>(args);
            return character != null
                ? character.Role.ToString()
                : NetworkCharacter.NetworkRole.None.ToString();
        }

        public override string String => $"{m_Character} Network Role";
    }
}
