using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Runner-level RPC endpoint and Shared logical-owner tick pump. Static transport RPCs
    /// deliberately avoid coupling delivery to the lifetime or State Authority of a gameplay
    /// object. Source and direction are validated again by
    /// <see cref="FusionTransportBridge"/>.
    /// </summary>
    public sealed class FusionRpcRouter : SimulationBehaviour
    {
        private string m_SharedOwnerResolveFailure = string.Empty;
        private float m_NextSharedOwnerResolveDiagnosticAt;
        private readonly List<NetworkObject> m_SharedObjectScratch = new();
        private NetworkObject m_CachedSharedLogicalOwnerObject;
        private bool m_SharedLogicalOwnerFallbackDiagnosticIssued;

        /// <summary>
        /// Fusion 2 does not execute NetworkBehaviour simulation callbacks for Shared proxies.
        /// GC2 deliberately keeps player State Authority on the Shared master, so the runner's
        /// global simulation callback advances the local logical-owner proxy once per Fusion tick.
        /// </summary>
        public override void FixedUpdateNetwork()
        {
            if (Runner == null || !Runner.IsForward) return;

            if (!TryResolveSharedLogicalOwnerProxy(
                    out NetworkObject playerObject,
                    out FusionNativeNetworkCharacterMotor motor,
                    out string failure))
            {
                ReportSharedOwnerResolveFailure(failure);
                return;
            }

            ClearSharedOwnerResolveFailure();
            motor.SimulateSharedLogicalOwnerProxyTick(
                Runner.Tick.Raw,
                restorePredictedPose: true);
        }

        public override void Render()
        {
            if (!TryResolveSharedLogicalOwnerProxy(
                    out NetworkObject playerObject,
                    out FusionNativeNetworkCharacterMotor motor,
                    out _))
            {
                return;
            }

            // When a future Fusion version elects to simulate the proxy, its own Render callback
            // is already active and remains the sole presentation writer.
            if (!playerObject.IsInSimulation)
            {
                motor.RenderSharedLogicalOwnerProxy();
            }
        }

        private bool TryResolveSharedLogicalOwnerProxy(
            out NetworkObject playerObject,
            out FusionNativeNetworkCharacterMotor motor,
            out string failure)
        {
            playerObject = null;
            motor = null;
            failure = string.Empty;

            if (Runner == null || !Runner.IsRunning || Runner.GameMode != GameMode.Shared)
            {
                return false;
            }

            PlayerRef localPlayer = Runner.LocalPlayer;
            if (!localPlayer.IsRealPlayer)
            {
                failure = "Runner.LocalPlayer is not assigned";
                return false;
            }

            bool hasPlayerObject =
                Runner.TryGetPlayerObject(localPlayer, out playerObject) &&
                IsSharedLogicalOwnerObject(playerObject, localPlayer);
            if (!hasPlayerObject)
            {
                playerObject = ResolveSharedLogicalOwnerObject(localPlayer);
                if (playerObject == null)
                {
                    failure = $"no confirmed PlayerObject mapping or admitted logical-owner " +
                              $"character for {localPlayer}";
                    return false;
                }

                if (!m_SharedLogicalOwnerFallbackDiagnosticIssued)
                {
                    m_SharedLogicalOwnerFallbackDiagnosticIssued = true;
                    Debug.Log(
                        $"[FusionSharedInput] Resolved {localPlayer}'s Shared character " +
                        $"through LogicalOwner fallback; object='{playerObject.name}' " +
                        $"networkId={playerObject.Id.Raw}. Fusion PlayerObject mapping was " +
                        "absent or not admitted.",
                        this);
                }
            }

            // The Shared master owns its own NetworkObject and receives the regular behaviour
            // callback. Only a master-owned logical-owner proxy needs this runner-level pump.
            if (playerObject.HasStateAuthority)
            {
                return false;
            }

            FusionNetworkIdentity identity =
                playerObject.GetComponent<FusionNetworkIdentity>();
            if (identity == null || !identity.IsSpawned ||
                !identity.IsOwnedBy(localPlayer))
            {
                failure = identity == null
                    ? $"player object '{playerObject.name}' has no FusionNetworkIdentity"
                    : $"player object '{playerObject.name}' logical owner is " +
                      $"{identity.LogicalOwner}, expected {localPlayer}";
                return false;
            }

            motor = playerObject.GetComponent<FusionNativeNetworkCharacterMotor>();
            if (motor == null)
            {
                failure = $"player object '{playerObject.name}' has no Fusion native motor";
                return false;
            }

            return true;
        }

        private NetworkObject ResolveSharedLogicalOwnerObject(PlayerRef localPlayer)
        {
            if (IsSharedLogicalOwnerObject(
                    m_CachedSharedLogicalOwnerObject,
                    localPlayer))
            {
                return m_CachedSharedLogicalOwnerObject;
            }

            m_CachedSharedLogicalOwnerObject = null;
            m_SharedObjectScratch.Clear();
            Runner.GetAllNetworkObjects(m_SharedObjectScratch);
            for (int i = 0; i < m_SharedObjectScratch.Count; i++)
            {
                NetworkObject candidate = m_SharedObjectScratch[i];
                if (!IsSharedLogicalOwnerObject(candidate, localPlayer)) continue;
                m_CachedSharedLogicalOwnerObject = candidate;
                return candidate;
            }

            return null;
        }

        private bool IsSharedLogicalOwnerObject(
            NetworkObject candidate,
            PlayerRef localPlayer)
        {
            if (candidate == null || !candidate.IsValid) return false;
            FusionNetworkIdentity identity =
                candidate.GetComponent<FusionNetworkIdentity>();
            return identity != null && identity.Runner == Runner &&
                   identity.IsSpawned && identity.TransportAdmitted &&
                   identity.IsOwnedBy(localPlayer) &&
                   candidate.GetComponent<FusionNativeNetworkCharacterMotor>() != null;
        }

        private void ReportSharedOwnerResolveFailure(string failure)
        {
            if (string.IsNullOrEmpty(failure))
            {
                ClearSharedOwnerResolveFailure();
                return;
            }

            float now = Time.unscaledTime;
            if (!string.Equals(m_SharedOwnerResolveFailure, failure,
                    System.StringComparison.Ordinal))
            {
                m_SharedOwnerResolveFailure = failure;
                m_NextSharedOwnerResolveDiagnosticAt = now + 1f;
                return;
            }

            if (now < m_NextSharedOwnerResolveDiagnosticAt) return;
            m_NextSharedOwnerResolveDiagnosticAt = now + 2f;
            Debug.LogWarning(
                $"[FusionSharedInput] Shared owner tick pump is waiting: {failure}. " +
                $"runner='{Runner?.name}' tick={Runner?.Tick.Raw}",
                this);
        }

        private void ClearSharedOwnerResolveFailure()
        {
            m_SharedOwnerResolveFailure = string.Empty;
            m_NextSharedOwnerResolveDiagnosticAt = 0f;
        }

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
