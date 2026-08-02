using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Lifecycle Player")]
    [Description("Returns the player from the latest local-player-ready or local-player-lost event")]
    [Category("Network/Lifecycle/Last Event Player")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetGameObjectNetworkLifecyclePlayer : PropertyTypeGetGameObject
    {
        public override GameObject Get(Args args)
        {
            return NetworkLifecycleEvents.LastLocalPlayer;
        }

        public override GameObject Get(GameObject gameObject)
        {
            return NetworkLifecycleEvents.LastLocalPlayer;
        }

        public override string String => "Network Event Player";
    }
}
