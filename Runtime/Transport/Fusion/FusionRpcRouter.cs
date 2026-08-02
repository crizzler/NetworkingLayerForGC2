using Fusion;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Static RPC endpoint for the transport. Static RPCs deliberately avoid coupling
    /// transport delivery to the lifetime or State Authority of a gameplay object.
    /// Source and direction are validated again by <see cref="FusionTransportBridge"/>.
    /// </summary>
    public sealed class FusionRpcRouter : SimulationBehaviour
    {
        [Rpc(
            InvokeLocal = false,
            TickAligned = false,
            Channel = RpcChannel.Unreliable,
            HostMode = RpcHostMode.SourceIsHostPlayer)]
        internal static void RPC_ToAuthorityUnreliable(
            NetworkRunner runner,
            [RpcTarget] PlayerRef target,
            byte[] packet,
            RpcInfo info = default)
        {
            FusionTransportBridge.RouteRpc(
                runner,
                packet,
                info,
                FusionPacketDirection.ToAuthority,
                reliable: false,
                largeData: false);
        }

        [Rpc(
            InvokeLocal = false,
            TickAligned = false,
            Channel = RpcChannel.Reliable,
            HostMode = RpcHostMode.SourceIsHostPlayer)]
        internal static void RPC_ToAuthorityReliable(
            NetworkRunner runner,
            [RpcTarget] PlayerRef target,
            byte[] packet,
            RpcInfo info = default)
        {
            FusionTransportBridge.RouteRpc(
                runner,
                packet,
                info,
                FusionPacketDirection.ToAuthority,
                reliable: true,
                largeData: false);
        }

        [Rpc(
            InvokeLocal = false,
            TickAligned = false,
            Channel = RpcChannel.ReliableLargeData,
            HostMode = RpcHostMode.SourceIsHostPlayer)]
        internal static void RPC_ToAuthorityLarge(
            NetworkRunner runner,
            [RpcTarget] PlayerRef target,
            byte[] packet,
            RpcInfo info = default)
        {
            FusionTransportBridge.RouteRpc(
                runner,
                packet,
                info,
                FusionPacketDirection.ToAuthority,
                reliable: true,
                largeData: true);
        }

        [Rpc(
            InvokeLocal = false,
            TickAligned = false,
            Channel = RpcChannel.Unreliable,
            HostMode = RpcHostMode.SourceIsServer)]
        internal static void RPC_FromAuthorityUnreliable(
            NetworkRunner runner,
            [RpcTarget] PlayerRef target,
            byte[] packet,
            RpcInfo info = default)
        {
            FusionTransportBridge.RouteRpc(
                runner,
                packet,
                info,
                FusionPacketDirection.FromAuthority,
                reliable: false,
                largeData: false);
        }

        [Rpc(
            InvokeLocal = false,
            TickAligned = false,
            Channel = RpcChannel.Reliable,
            HostMode = RpcHostMode.SourceIsServer)]
        internal static void RPC_FromAuthorityReliable(
            NetworkRunner runner,
            [RpcTarget] PlayerRef target,
            byte[] packet,
            RpcInfo info = default)
        {
            FusionTransportBridge.RouteRpc(
                runner,
                packet,
                info,
                FusionPacketDirection.FromAuthority,
                reliable: true,
                largeData: false);
        }

        [Rpc(
            InvokeLocal = false,
            TickAligned = false,
            Channel = RpcChannel.ReliableLargeData,
            HostMode = RpcHostMode.SourceIsServer)]
        internal static void RPC_FromAuthorityLarge(
            NetworkRunner runner,
            [RpcTarget] PlayerRef target,
            byte[] packet,
            RpcInfo info = default)
        {
            FusionTransportBridge.RouteRpc(
                runner,
                packet,
                info,
                FusionPacketDirection.FromAuthority,
                reliable: true,
                largeData: true);
        }

        internal static void SendToAuthority(
            NetworkRunner runner,
            PlayerRef target,
            byte[] packet,
            bool reliable)
        {
            if (reliable)
            {
                if (packet.Length <= FusionProtocol.RpcPayloadLimit)
                {
                    RPC_ToAuthorityReliable(runner, target, packet);
                }
                else
                {
                    RPC_ToAuthorityLarge(runner, target, packet);
                }
            }
            else
            {
                RPC_ToAuthorityUnreliable(runner, target, packet);
            }
        }

        internal static void SendFromAuthority(
            NetworkRunner runner,
            PlayerRef target,
            byte[] packet,
            bool reliable)
        {
            if (reliable)
            {
                if (packet.Length <= FusionProtocol.RpcPayloadLimit)
                {
                    RPC_FromAuthorityReliable(runner, target, packet);
                }
                else
                {
                    RPC_FromAuthorityLarge(runner, target, packet);
                }
            }
            else
            {
                RPC_FromAuthorityUnreliable(runner, target, packet);
            }
        }
    }
}
