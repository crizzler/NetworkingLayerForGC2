using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Player Count")]
    [Category("Network/Fusion/Session/Player Count")]
    [Description("The number of players currently reported by the active Fusion session")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Player", "Count")]
    [Image(typeof(IconNumber), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalFusionPlayerCount : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return FusionVisualScriptingSupport.TryGetActiveSession(
                args.Self,
                out FusionSessionSnapshot session)
                ? session.PlayerCount
                : 0d;
        }

        public override string String => "Fusion Player Count";
    }

    [Title("Fusion Maximum Players")]
    [Category("Network/Fusion/Session/Maximum Players")]
    [Description("The maximum player count reported by the active Fusion session")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Player", "Maximum", "Count")]
    [Image(typeof(IconNumber), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalFusionMaximumPlayers : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return FusionVisualScriptingSupport.TryGetActiveSession(
                args.Self,
                out FusionSessionSnapshot session)
                ? session.MaxPlayers
                : 0d;
        }

        public override string String => "Fusion Maximum Players";
    }

    [Title("Fusion Local Client ID")]
    [Category("Network/Fusion/Player/Local Client ID")]
    [Description("The local player's GC2-compatible Fusion client ID, or -1 when unavailable")]
    [Keywords("Network", "Fusion", "Photon", "Player", "Local", "Client", "ID")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalFusionLocalClientId : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.TryGetLocalClientId(out uint clientId)
                ? clientId
                : -1d;
        }

        public override string String => "Fusion Local Client ID";
    }

    [Title("Fusion Authority Epoch")]
    [Category("Network/Fusion/Authority/Authority Epoch")]
    [Description("The current Fusion logical-authority generation counter")]
    [Keywords("Network", "Fusion", "Photon", "Authority", "Master", "Epoch", "Generation")]
    [Image(typeof(IconCrown), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetDecimalFusionAuthorityEpoch : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                args.Self,
                out FusionTransportBridge bridge)
                ? bridge.AuthorityEpoch
                : 0d;
        }

        public override string String => "Fusion Authority Epoch";
    }

    [Title("Fusion Network ID")]
    [Category("Network/Fusion/Object/Network ID")]
    [Description("The target identity's Fusion network ID, or 0 when unavailable")]
    [Keywords("Network", "Fusion", "Photon", "Object", "Identity", "Network", "ID")]
    [Image(typeof(IconID), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetDecimalFusionNetworkId : PropertyTypeGetDecimal
    {
        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        public override double Get(Args args)
        {
            FusionNetworkIdentity identity =
                FusionVisualScriptingSupport.ResolveIdentity(m_Target, args);
            return identity != null ? identity.NetworkId : 0d;
        }

        public override string String => $"Fusion Network ID of {m_Target}";
    }

    [Title("Fusion Logical Owner Client ID")]
    [Category("Network/Fusion/Object/Logical Owner Client ID")]
    [Description("The target identity's logical gameplay owner client ID, or -1 when unavailable")]
    [Keywords("Network", "Fusion", "Photon", "Object", "Logical", "Owner", "Client", "ID")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalFusionLogicalOwnerClientId : PropertyTypeGetDecimal
    {
        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        public override double Get(Args args)
        {
            FusionNetworkIdentity identity =
                FusionVisualScriptingSupport.ResolveIdentity(m_Target, args);
            return identity != null && identity.TryGetLogicalOwnerClientId(out uint clientId)
                ? clientId
                : -1d;
        }

        public override string String => $"Fusion Logical Owner of {m_Target}";
    }

    [Title("Fusion Round Trip Time")]
    [Category("Network/Fusion/Player/Round Trip Time")]
    [Description("The local player's Photon Fusion round-trip time in milliseconds, or -1 when unavailable")]
    [Keywords("Network", "Fusion", "Photon", "Player", "RTT", "Ping", "Latency", "Milliseconds")]
    [Image(typeof(IconClock), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class GetDecimalFusionRoundTripTime : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            if (!FusionVisualScriptingSupport.TryResolveBridge(
                    args.Self,
                    out FusionTransportBridge bridge) ||
                !bridge.TryGetLocalClientId(out uint clientId) ||
                !bridge.TryGetPlayerRtt(clientId, out double seconds))
            {
                return -1d;
            }

            return seconds * 1000d;
        }

        public override string String => "Fusion Round Trip Time (ms)";
    }

    [Title("Fusion Last Observed Master Client ID")]
    [Category("Network/Fusion/Authority/Last Master Client ID")]
    [Description("The Master Client ID carried by the latest Fusion authority observation, or -1 when unavailable")]
    [Keywords("Network", "Fusion", "Photon", "Authority", "Master", "Client", "ID", "Payload")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetDecimalFusionLastObservedMasterClientId : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            if (!FusionVisualScriptingSupport.TryResolveBridge(
                    args.Self,
                    out FusionTransportBridge bridge) ||
                !bridge.HasLastAuthorityObservation)
            {
                return -1d;
            }

            uint clientId = bridge.LastAuthorityObservation.MasterClientId;
            return clientId != uint.MaxValue ? clientId : -1d;
        }

        public override string String => "Fusion Last Observed Master Client ID";
    }

    [Title("Fusion Last Local Scene Build Index")]
    [Category("Network/Fusion/Scene/Last Local Scene Build Index")]
    [Description("The build index carried by the latest Fusion local-scene observation, or -1 when unavailable")]
    [Keywords("Network", "Fusion", "Photon", "Scene", "Build", "Index", "Ready", "Payload")]
    [Image(typeof(IconUnity), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetDecimalFusionLastLocalSceneBuildIndex : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.HasLastLocalSceneObservation
                ? bridge.LastLocalSceneObservation.SceneBuildIndex
                : -1d;
        }

        public override string String => "Fusion Last Local Scene Build Index";
    }
}
