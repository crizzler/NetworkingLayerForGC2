using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Character ID")]
    [Description("Returns the stable network identifier of a Network Character, or 0 if it is not initialized")]

    [Category("Network/Characters/Network Character ID")]

    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetDecimalNetworkCharacterId : PropertyTypeGetDecimal
    {
        [SerializeField]
        private PropertyGetGameObject m_Character = GetGameObjectPlayer.Create();

        public override double Get(Args args)
        {
            NetworkCharacter character = m_Character.Get<NetworkCharacter>(args);
            return character != null ? character.NetworkId : 0d;
        }

        public override string String => $"{m_Character} Network ID";
    }
}
