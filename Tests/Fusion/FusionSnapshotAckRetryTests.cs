using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using global::Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionSnapshotAckRetryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ushort ProbeModule = 65000;
        private const ushort SnapshotCompleteMessage = 7;
        private NetworkRunner m_Runner;

        [UnityTest]
        public IEnumerator InitialAdmission_RetriesRetainedSnapshotAck()
        {
            yield return new EnterPlayMode();
            yield return ExerciseRetry(resync: false);
        }

        [UnityTest]
        public IEnumerator SameEpochResync_RetriesRetainedSnapshotAck()
        {
            yield return new EnterPlayMode();
            yield return ExerciseRetry(resync: true);
        }

        [UnityTest]
        public IEnumerator ReplacementSnapshot_DiscardsOlderRefusedAck()
        {
            yield return new EnterPlayMode();
            yield return ExerciseReplacement();
        }

        [UnityTest]
        public IEnumerator SceneResetInsideLoopbackAck_DoesNotRestoreOldReadiness()
        {
            yield return new EnterPlayMode();
            yield return ExerciseReentrantReset();
        }

        [UnityTearDown]
        public IEnumerator StopSession()
        {
            if (!Application.isPlaying) yield break;
            if (m_Runner != null && m_Runner.IsRunning && !m_Runner.IsShutdown)
                yield return Await(m_Runner.Shutdown());
            yield return new ExitPlayMode();
        }

        private IEnumerator ExerciseRetry(bool resync)
        {
            yield return StartSession();
            var session = new Admission(m_Runner);
            if (resync)
            {
                session.Bridge.NotifyLocalGameplayReady();
                Assert.That(session.Acks, Has.Count.EqualTo(1));
                Assert.That(session.Bridge.IsLocalGameplayReady && session.Bridge.IsClientReady(session.ClientId), Is.True);
                yield return WaitForResyncWindow();
            }

            uint epoch = session.Bridge.AuthorityEpoch;
            ulong revision = session.Bridge.LocalGameplaySnapshotRevision;
            int earlierAcks = session.Acks.Count;
            session.Probe.RefuseAck = true;
            if (resync) session.RequestResync();
            else session.Bridge.NotifyLocalGameplayReady();
            uint token = session.PendingToken;
            Assert.That(token, Is.Not.Zero);
            if (resync) Assert.That(token, Is.Not.EqualTo(session.Acks[0]));
            Assert.That(session.Bridge.IsClientReady(session.ClientId), Is.False);
            Assert.That(session.Acks, Has.Count.EqualTo(earlierAcks));
            Assert.That(session.Bridge.LocalGameplaySnapshotRevision, Is.EqualTo(revision),
                "A locally refused ACK must not publish a new readiness revision.");
            int produced = session.Probe.Produced;
            session.ReleaseSendGuard();

            // No replayed marker, explicit resync, or direct retry call: only real Update frames.
            float deadline = Time.unscaledTime + 3f;
            while (!session.Bridge.IsClientReady(session.ClientId) && Time.unscaledTime < deadline)
                yield return null;
            Assert.That(session.Bridge.IsClientReady(session.ClientId), Is.True,
                $"RETAINED-SNAPSHOT-ACK: {(resync ? "same-epoch resync" : "initial admission")} " +
                $"did not retry pending token {token} after the reliable send guard cleared.");
            Assert.That(session.Acks, Has.Count.EqualTo(earlierAcks + 1));
            Assert.That(session.Acks[earlierAcks], Is.EqualTo(token));
            Assert.That(session.Probe.Produced, Is.EqualTo(produced), "Retry must not rebuild the snapshot.");
            Assert.That(session.Bridge.AuthorityEpoch, Is.EqualTo(epoch));
            Assert.That(session.Bridge.LocalGameplaySnapshotRevision, Is.EqualTo(revision + 1));

            uint sequence = session.LastClientSequence;
            session.ReplayMarker(token);
            yield return null;
            Assert.That(session.LastClientSequence, Is.EqualTo(sequence), "Duplicate completion sent another ACK.");
            Assert.That(session.Acks, Has.Count.EqualTo(earlierAcks + 1));
            Assert.That(session.Bridge.LocalGameplaySnapshotRevision, Is.EqualTo(revision + 1));
        }

        private IEnumerator ExerciseReplacement()
        {
            yield return StartSession();
            var session = new Admission(m_Runner);
            session.Probe.RefuseAck = true;
            session.Bridge.NotifyLocalGameplayReady();
            uint original = session.PendingToken;
            Assert.That(original, Is.Not.Zero);
            yield return WaitForResyncWindow();
            Assert.That(session.Acks, Is.Empty);
            session.RequestResync();
            uint replacement = session.PendingToken;
            Assert.That(replacement, Is.Not.Zero.And.Not.EqualTo(original));
            // A late duplicate of the old token must not displace the new pending token.
            session.ReplayMarker(original);
            session.ReleaseSendGuard();
            float deadline = Time.unscaledTime + 3f;
            while (session.Acks.Count == 0 && Time.unscaledTime < deadline) yield return null;
            Assert.That(session.Acks, Is.EqualTo(new[] { replacement }));
            Assert.That(session.Bridge.LocalGameplaySnapshotRevision, Is.EqualTo(1UL));
            uint sequence = session.LastClientSequence;
            session.ReplayMarker(original);
            Assert.That(session.LastClientSequence, Is.EqualTo(sequence));
            Assert.That(session.Bridge.LocalGameplaySnapshotRevision, Is.EqualTo(1UL));
        }

        private IEnumerator ExerciseReentrantReset()
        {
            yield return StartSession();
            var session = new Admission(m_Runner);
            session.Bridge.NotifyLocalGameplayReady();
            Assert.That(session.Acks, Has.Count.EqualTo(1));
            yield return WaitForResyncWindow();

            Action<uint> reset = _ => session.Bridge.OnSceneLoadStart(m_Runner);
            session.Bridge.ClientSnapshotAcknowledged += reset;
            session.RequestResync();
            session.Bridge.ClientSnapshotAcknowledged -= reset;
            Assert.That(session.Acks, Has.Count.EqualTo(2));
            Assert.That(session.Bridge.IsClientReady(session.ClientId), Is.False);
            session.Bridge.NotifyLocalSceneReady();
            session.Probe.RefuseAck = true;
            session.Bridge.NotifyLocalGameplayReady();
            Assert.That(session.Bridge.LocalGameplaySnapshotRevision, Is.Zero,
                "RESET-SNAPSHOT-ACK: The synchronous ACK callback reset the scene; the old send must not restore readiness.");
            Assert.That(session.PendingToken, Is.Not.Zero.And.Not.EqualTo(session.Acks[1]));
        }

        private IEnumerator StartSession()
        {
            Assert.That(Application.isPlaying, Is.True);
            m_Runner = new GameObject("Snapshot ACK retry offline session").AddComponent<NetworkRunner>();
            var start = m_Runner.StartGame(new StartGameArgs { GameMode = GameMode.Single });
            yield return Await(start);
            Assert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            Assert.That(m_Runner.HasAnyActiveConnections(), Is.False);
        }

        private static IEnumerator Await(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Offline Fusion setup or shutdown timed out.");
            task.GetAwaiter().GetResult();
        }

        private static IEnumerator WaitForResyncWindow()
        {
            float end = Time.unscaledTime + FusionProtocol.ReorderTimeoutSeconds;
            while (Time.unscaledTime <= end) yield return null;
        }

        private sealed class Admission
        {
            public readonly FusionTransportBridge Bridge;
            public readonly uint ClientId;
            public readonly List<uint> Acks = new();
            public readonly EmptyProducer Probe;
            private readonly HashSet<ulong> m_SendsInProgress;
            // Single loopback goes ToAuthority without a targeted Shared master PlayerRef.
            private readonly ulong m_ClientStream = ((ulong)uint.MaxValue << 8) | (byte)FusionPacketDirection.ToAuthority;

            public Admission(NetworkRunner runner)
            {
                Bridge = runner.gameObject.AddComponent<FusionTransportBridge>();
                Assert.That(Bridge.Bind(runner), Is.True);
                Assert.That(Bridge.IsServer && Bridge.IsClient, Is.True);
                Assert.That(Bridge.TryGetLocalClientId(out ClientId), Is.True);
                m_SendsInProgress = Field<HashSet<ulong>>("m_ReliableRpcSendsInProgress");
                foreach (ushort module in new[] { FusionModuleIds.Core, FusionModuleIds.Variables, FusionModuleIds.AnimationMotion })
                    Register(new EmptyProducer(module));
                Probe = new EmptyProducer(ProbeModule) { BeforeComplete = () => m_SendsInProgress.Add(m_ClientStream) };
                Register(Probe);
                Bridge.ClientSnapshotAcknowledged += id =>
                {
                    if (id == ClientId) Acks.Add(Field<uint>("m_NextSnapshotToken"));
                };
                Bridge.NotifyLocalSceneReady();
                Assert.That(Bridge.IsClientSceneReady(ClientId), Is.True);
            }

            public uint PendingToken => Field<Dictionary<uint, uint>>("m_PendingSnapshotTokens")[ClientId];
            public uint LastClientSequence => Field<Dictionary<ulong, uint>>("m_OutgoingSequences")[m_ClientStream];

            public void RequestResync() => Invoke("RequestResync", ClientId, FusionPacketDirection.ToAuthority);

            public void ReleaseSendGuard()
            {
                Assert.That(m_SendsInProgress.Remove(m_ClientStream), Is.True,
                    "The producer must reserve the actual reliable ACK stream before SnapshotComplete.");
                Probe.RefuseAck = false;
            }

            public void ReplayMarker(uint token)
            {
                var writer = new FusionPacketWriter(4);
                writer.WriteUInt32(token);
                Assert.That(Invoke("SendToClientInternal", ClientId, FusionProtocol.TransportModuleId,
                    SnapshotCompleteMessage, writer.ToArray(), true), Is.True);
            }

            private T Field<T>(string name) => (T)typeof(FusionTransportBridge).GetField(name, PrivateInstance).GetValue(Bridge);
            private object Invoke(string name, params object[] args) =>
                typeof(FusionTransportBridge).GetMethod(name, PrivateInstance).Invoke(Bridge, args);

            private void Register(EmptyProducer producer)
            {
                Assert.That(Bridge.RegisterModuleHandler(producer.FullSnapshotModuleId, _ => { }), Is.True);
                Assert.That(Bridge.RegisterFullSnapshotProducer(producer), Is.True);
            }
        }

        private sealed class EmptyProducer : IFusionFullSnapshotProducer
        {
            public EmptyProducer(ushort module) => FullSnapshotModuleId = module;
            public ushort FullSnapshotModuleId { get; }
            public string FullSnapshotProducerName => "Snapshot ACK test fixture";
            public Action BeforeComplete;
            public bool RefuseAck;
            public int Produced;

            public FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context)
            {
                Produced++;
                // Simulate contention at the actual send boundary. Do not replace the ACK
                // handler, force its return value, or bypass authority's token checks.
                if (RefuseAck) BeforeComplete.Invoke();
                return context.Complete();
            }
        }
    }
}
