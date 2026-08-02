using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Local Network Player")]
    [Description("Returns the locally owned Network Character registered as Game Creator's Player")]

    [Category("Network/Characters/Local Network Player")]

    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetGameObjectLocalNetworkPlayer : PropertyTypeGetGameObject
    {
        public override GameObject Get(Args args)
        {
            return Resolve();
        }

        public override GameObject Get(GameObject gameObject)
        {
            return Resolve();
        }

        public static PropertyGetGameObject Create()
        {
            return new PropertyGetGameObject(new GetGameObjectLocalNetworkPlayer());
        }

        public override string String => "Local Network Player";

        private static GameObject Resolve()
        {
            return NetworkTransportBridge.HasActive &&
                   NetworkTransportBridge.Active.TryGetLocalPlayer(out GameObject player)
                ? player
                : null;
        }
    }
}
