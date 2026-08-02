using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Owner Client ID")]
    [Description("Returns the client ID that owns a Network Character, or -1 when no owner is available")]

    [Category("Network/Characters/Network Owner Client ID")]

    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalNetworkOwnerClientId : PropertyTypeGetDecimal
    {
        private const double NoOwner = -1d;

        [SerializeField]
        private PropertyGetGameObject m_Character = GetGameObjectPlayer.Create();

        public override double Get(Args args)
        {
            NetworkCharacter character = m_Character.Get<NetworkCharacter>(args);
            if (character == null || character.NetworkId == 0 ||
                !NetworkTransportBridge.HasActive)
            {
                return NoOwner;
            }

            return NetworkTransportBridge.Active.TryGetCharacterOwner(
                character.NetworkId, out uint ownerClientId)
                ? ownerClientId
                : NoOwner;
        }

        public override string String => $"{m_Character} Owner Client ID";
    }
}
