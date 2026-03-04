using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Returns true if the character on Self is the local owner (local player).
    /// </summary>
    [Title("Is Network Owner")]
    [Description("Returns true if the character on Self is owned by the local client")]

    [Category("Network/General/Is Network Owner")]

    [Keywords("Network", "Owner", "Local", "Player", "Authority")]

    [Image(typeof(IconPlayer), ColorTheme.Type.Blue)]
    [Serializable]
    public class ConditionIsNetworkOwner : Condition
    {
        // PROPERTIES: ----------------------------------------------------------------------------

        protected override string Summary => "is Network Owner";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override bool Run(Args args)
        {
            if (args.Self == null) return false;

            var netChar = args.Self.Get<NetworkCharacter>();
            if (netChar != null)
                return netChar.IsLocalPlayer;

            // Fallback: check Netcode directly
#if UNITY_NETCODE
            var netObj = args.Self.GetComponent<Unity.Netcode.NetworkObject>();
            if (netObj != null)
                return netObj.IsOwner;
#endif

            return false;
        }
    }
}
