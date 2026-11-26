using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using global::Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionReliableRpcSendTests
    {
        private const ushort Module = 65000;
        private const ushort Message = 1;
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private NetworkRunner m_Runner;

        [UnityTest]
        public IEnumerator ReliableSdkRefusal_IsReturnedToCallerForBothChannels()
        {
            yield return new EnterPlayMode();
            yield return StartSession();
            VerifyRefusal(checkContinuity: false);
        }

        [UnityTest]
        public IEnumerator ReliableSdkRefusal_DoesNotConsumeStreamSequence()
        {
            yield return new EnterPlayMode();
            yield return StartSession();
            VerifyRefusal(checkContinuity: true);
        }

        [UnityTearDown]
        public IEnumerator StopSession()
        {
            if (m_Runner != null)
            {
                var stop = m_Runner.Shutdown();
                float deadline = Time.realtimeSinceStartup + 10f;
                while (!stop.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(stop.Status, Is.EqualTo(System.Threading.Tasks.TaskStatus.RanToCompletion));
            }
        }

        private IEnumerator StartSession()
        {
            Assert.That(Application.isPlaying, Is.True);
            m_Runner = new GameObject("Reliable RPC result fixture").AddComponent<NetworkRunner>();
            var start = m_Runner.StartGame(new StartGameArgs { GameMode = GameMode.Single });
            float deadline = Time.realtimeSinceStartup + 10f;
            while (!start.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(start.IsCompleted, Is.True, "Single runner did not start");
            Assert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            Assert.That(m_Runner.IsRunning && m_Runner.IsServer, Is.True);
            Assert.That(m_Runner.HasAnyActiveConnections(), Is.False);
        }

        private void VerifyRefusal(bool checkContinuity)
        {
            var bridge = m_Runner.gameObject.AddComponent<FusionTransportBridge>();
            Assert.That(bridge.Bind(m_Runner), Is.True);
            Assert.That(bridge.TryGetLocalClientId(out uint localId), Is.True);
            ReadField<HashSet<uint>>(bridge, "m_GameplayReadyClients").Add(localId);

            var received = new List<FusionModuleMessage>();
            Assert.That(bridge.RegisterModuleHandler(Module, received.Add), Is.True);
            var sends = new List<(string Route, PlayerRef? Target, RpcSendMessageResult? Result)>();
            NetworkRunnerRpcEventDelegate observe = info =>
            {
                if (info.Runner == m_Runner && info.EventType == NetworkRunnerDebugRpcEventType.LocalCall)
                {
                    sends.Add((info.MethodBase.Name, info.TargetPlayer, info.LocalInvokeInfo?.SendMessageResult));
                }
            };

            var streams = ReadField<Dictionary<ulong, uint>>(bridge, "m_OutgoingSequences");
            var players = GetSimulationPlayers();
            var remote = PlayerRef.FromIndex(42);
            uint remoteId = checked((uint)remote.RawEncoded);
            Assert.That(remote, Is.Not.EqualTo(m_Runner.LocalPlayer));
            ulong remoteKey = ((ulong)remoteId << 8) | (byte)FusionPacketDirection.FromAuthority;
            const uint lastAccepted = 41;
            // Model an existing stream, not a dropped first packet. Do not inject a send result.
            streams[remoteKey] = lastAccepted;
            bool regular, large;
            NetworkRunner.DebugRpcEvent += observe;
            try
            {
                Assert.That(players.Add(remote), Is.True);
                Assert.That(m_Runner.IsPlayerValid(remote), Is.True);
                ReadField<HashSet<uint>>(bridge, "m_GameplayReadyClients").Add(remoteId);
                Assert.That(bridge.IsClientReady(remoteId), Is.True);
                Assert.That(bridge.SendModuleToClient(localId, Module, Message, new byte[] { 1 }), Is.True);
                regular = bridge.SendModuleToClient(remoteId, Module, Message, new byte[] { 2 });
                large = bridge.SendModuleToClient(remoteId, Module, Message, new byte[FusionProtocol.RpcPayloadLimit]);
                Assert.That(bridge.SendModuleToClient(localId, Module, Message, new byte[] { 3 }), Is.True);
            }
            finally
            {
                NetworkRunner.DebugRpcEvent -= observe;
                players.Remove(remote);
            }

            Assert.That(sends.Select(send => send.Route),
                Is.EqualTo(new[] { "RPC_FromAuthorityReliable", "RPC_FromAuthorityLarge" }),
                "Both woven remote RPCs must run; no SDK calls for manual loopback");
            foreach (var send in sends)
            {
                Assert.That(send.Target?.RawEncoded, Is.EqualTo((int)remoteId));
                Assert.That(send.Result, Is.EqualTo(RpcSendMessageResult.NoActiveConnections));
            }
            Assert.That(received.Select(packet => packet.Sequence), Is.EqualTo(new uint[] { 1, 2 }),
                "Independent local reliable stream must dispatch in order, once per send");
            Assert.That(bridge.LastSessionError, Is.Empty, "Ordinary SDK culling is not a terminal weaver fault");

            if (checkContinuity)
            {
                Assert.That(streams[remoteKey], Is.EqualTo(lastAccepted),
                    "RPC-SEQUENCE-GAP: refused attempts must not consume numbers on their shared stream");
            }
            else
            {
                Assert.That(new[] { regular, large }, Is.EqualTo(new[] { false, false }),
                    "RPC-SDK-REFUSAL: the caller must see both real SDK culls as false");
            }
        }

        private HashSet<PlayerRef> GetSimulationPlayers()
        {
            // Single has no remote sockets. Register only a player ref, without a connection,
            // so normal bridge preflight passes and the real SDK must reject at RpcBuilder.Prepare.
            // This test-only reflection is not a runtime dependency or a mock RPC result.
            var simulationProperty = typeof(NetworkRunner).GetProperty("Simulation", PrivateInstance);
            Assert.That(simulationProperty, Is.Not.Null, "Fusion test fixture needs the Single simulation");
            var simulation = simulationProperty.GetValue(m_Runner);
            var field = simulationProperty.PropertyType.GetField("_players", PrivateInstance);
            Assert.That(field, Is.Not.Null, "Fusion test fixture needs to register a disconnected player");
            return (HashSet<PlayerRef>)field.GetValue(simulation);
        }

        private static T ReadField<T>(FusionTransportBridge bridge, string name)
        {
            FieldInfo field = typeof(FusionTransportBridge).GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, name);
            return (T)field.GetValue(bridge);
        }
    }
}
