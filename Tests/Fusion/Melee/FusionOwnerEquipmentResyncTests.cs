using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Melee;
using Arawn.GameCreator2.Networking.Melee.Transport.Fusion;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using global::Fusion;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionOwnerEquipmentResyncTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const ushort WeaponStateMessage = 14;
        private const ushort AfterMeleeSnapshot = 65000;
        private readonly List<UnityEngine.Object> m_Objects = new();
        private NetworkRunner m_Runner;

        [UnityTest]
        public IEnumerator SameEpochResync_RepublishesEquip()
        {
            yield return new EnterPlayMode();
            yield return ExerciseResync(unequip: false);
        }

        [UnityTest]
        public IEnumerator SameEpochResync_RepublishesExplicitUnequip()
        {
            yield return new EnterPlayMode();
            yield return ExerciseResync(unequip: true);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!Application.isPlaying) yield break;
            if (m_Runner != null && m_Runner.IsRunning && !m_Runner.IsShutdown)
            {
                Task shutdown = m_Runner.Shutdown(destroyGameObject: false);
                yield return Await(shutdown);
            }
            for (int i = m_Objects.Count - 1; i >= 0; i--)
                if (m_Objects[i] != null) UnityEngine.Object.Destroy(m_Objects[i]);
            m_Objects.Clear();
            yield return null;
            yield return new ExitPlayMode();
        }

        private IEnumerator ExerciseResync(bool unequip)
        {
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(NetworkTransportBridge.Active, Is.Null, "Fixture requires an empty gameplay scene.");

            var session = Track(new GameObject("Owner resync offline session"));
            m_Runner = session.AddComponent<NetworkRunner>();
            // Single is Fusion's offline host/owner mode; no Photon service or peers.
            var start = m_Runner.StartGame(new StartGameArgs { GameMode = GameMode.Single });
            yield return Await(start);
            Assert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());

            var transport = session.AddComponent<FusionTransportBridge>();
            Assert.That(transport.Bind(m_Runner), Is.True);
            Assert.That(transport.IsServer && transport.IsClient, Is.True);
            Assert.That(transport.TryGetLocalClientId(out uint clientId), Is.True);
            var registry = session.AddComponent<FusionAuthoritySpawnRegistry>();
            registry.Configure(transport);
            var manager = Track(new GameObject("Resync Melee manager")).AddComponent<NetworkMeleeManager>();

            var actor = Track(new GameObject("Resync authenticated owner"));
            actor.SetActive(false);
            var character = actor.AddComponent<Character>();
            var model = new GameObject("Model");
            model.transform.SetParent(actor.transform, false);
            character.Animim.Animator = model.AddComponent<Animator>();
            // This equipment-only actor has no humanoid rig for GC2's default footstep probes.
            var serializedCharacter = new SerializedObject(character);
            serializedCharacter.FindProperty("m_Footsteps.m_FootstepDetector").managedReferenceValue = null;
            serializedCharacter.ApplyModifiedPropertiesWithoutUndo();
            var networkCharacter = actor.AddComponent<NetworkCharacter>();
            var controller = actor.AddComponent<NetworkMeleeController>();
            var networkObject = actor.AddComponent<NetworkObject>();
            var identity = actor.AddComponent<FusionNetworkIdentity>();
            actor.SetActive(true);
            new NetworkObjectBaker().Bake(actor);
            // Register a scene object so the real authority registry can admit it.
            m_Runner.RegisterSceneObjects(SceneRef.FromIndex(0), new[] { networkObject });
            Assert.That(identity.TransportAdmitted, Is.True, "Actor was not admitted by the authority registry.");
            Assert.That(identity.TryAssignLogicalOwner(m_Runner.LocalPlayer), Is.True);
            uint actorId = identity.NetworkId;
            networkCharacter.InitializeNetworkRole(true, true, true, true);
            Assert.That(transport.TryVerifyActorOwnership(clientId, actorId, out _), Is.True);

            var weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            int swordHash = weapon.Id.Hash;
            Assert.That(swordHash, Is.Not.Zero);
            var args = new Args(actor);
            if (unequip) yield return Await(character.Combat.Equip(weapon, null, args));
            int beforeHash = unequip ? swordHash : 0;
            int expectedHash = unequip ? 0 : swordHash;

            var melee = session.AddComponent<FusionMeleeTransportBridge>();
            melee.Configure(transport);
            foreach (ushort module in new[] { FusionModuleIds.Core, FusionModuleIds.Variables, FusionModuleIds.AnimationMotion })
                RegisterEmptyProducer(transport, new EmptyProducer(module));
            var window = new EmptyProducer(AfterMeleeSnapshot);
            RegisterEmptyProducer(transport, window);

            var ownerPackets = new List<FusionModuleMessage>();
            var handlers = Get<Dictionary<ushort, Action<FusionModuleMessage>>>(transport, "m_ModuleHandlers");
            Action<FusionModuleMessage> realHandler = handlers[FusionMeleeTransportBridge.ModuleId];
            handlers[FusionMeleeTransportBridge.ModuleId] = message =>
            {
                if (!message.FromAuthority && message.MessageType == WeaponStateMessage)
                    ownerPackets.Add(message); // After the real authority admission gate.
                realHandler(message);
            };
            var completedTokens = new List<uint>();
            transport.ClientSnapshotAcknowledged += id =>
            {
                if (id == clientId) completedTokens.Add(Get<uint>(transport, "m_NextSnapshotToken"));
            };

            uint epoch = transport.AuthorityEpoch;
            transport.NotifyLocalSceneReady();
            transport.NotifyLocalGameplayReady();
            Assert.That(transport.IsLocalGameplayReady && transport.IsClientReady(clientId), Is.True);
            Assert.That(completedTokens.Count, Is.EqualTo(1));
            uint initialToken = completedTokens[0];
            float earliestResync = Time.unscaledTime + FusionProtocol.ReorderTimeoutSeconds;
            yield return null;
            yield return null;
            Assert.That(ownerPackets.Count, Is.EqualTo(1), "Initial acknowledged snapshot must publish exactly once.");
            AssertOwnerPacket(ownerPackets[0], clientId, actorId, epoch, beforeHash);
            Assert.That(AuthorityHash(manager, actorId), Is.EqualTo(beforeHash));

            // Replay the same ACK, not a fresh snapshot. It must not re-arm equipment.
            Invoke(transport, "CompleteClientSnapshot", clientId, initialToken);
            while (Time.unscaledTime <= earliestResync) yield return null;
            Assert.That(completedTokens.Count, Is.EqualTo(1));
            Assert.That(ownerPackets.Count, Is.EqualTo(1), "Duplicate ACK or idle ticks republished equipment.");

            var localChanges = new List<int>();
            bool changingEquipment = false;
            controller.OnWeaponStateChangedWithSender += (_, state) =>
            {
                if (changingEquipment) localChanges.Add(state.WeaponHash);
            };
            bool windowReached = false;
            bool locallyReady = false;
            bool authorityReady = true;
            uint inFlightToken = 0;
            int hashDuringWindow = 0;
            int packetsDuringWindow = 0;
            window.Produce = client =>
            {
                windowReached = true;
                locallyReady = transport.IsLocalGameplayReady;
                authorityReady = transport.IsClientReady(client);
                inFlightToken = Get<Dictionary<uint, uint>>(transport, "m_PendingSnapshotTokens")[client];
                // The blank weapon has no authored instructions; finish in this same snapshot stack.
                changingEquipment = true;
                try
                {
                    Task change = unequip
                        ? character.Combat.Unequip(weapon, args)
                        : character.Combat.Equip(weapon, null, args);
                    if (!change.IsCompleted) throw new InvalidOperationException("Fixture equip yielded.");
                    change.GetAwaiter().GetResult();
                }
                finally
                {
                    changingEquipment = false;
                }
                hashDuringWindow = AuthorityHash(manager, actorId);
                packetsDuringWindow = ownerPackets.Count;
            };
            // Trigger the actual authority-side resync, without waiting for a network gap.
            Invoke(transport, "RequestResync", clientId, FusionPacketDirection.ToAuthority);
            window.Produce = null;
            Assert.That(windowReached && !authorityReady, Is.True,
                "Did not enter a forced snapshot with authority-side gameplay readiness suspended.");
            Assert.That(inFlightToken, Is.Not.Zero.And.Not.EqualTo(initialToken));
            Assert.That(localChanges, Is.EqualTo(new[] { expectedHash }), "No exact local equipment change inside resync.");
            Assert.That(packetsDuringWindow, Is.EqualTo(1), "The unready authority dispatched the in-flight owner change.");
            Assert.That(hashDuringWindow, Is.EqualTo(beforeHash));
            Assert.That(completedTokens, Is.EqualTo(new[] { initialToken, inFlightToken }));
            Assert.That(transport.IsLocalGameplayReady && transport.IsClientReady(clientId), Is.True);
            Assert.That(transport.AuthorityEpoch, Is.EqualTo(epoch), "Re-admission must stay in the same epoch.");

            yield return null;
            yield return null;
            int actualHash = AuthorityHash(manager, actorId);
            Debug.Log($"[OwnerEquipmentResync] case={(unequip ? "unequip" : "equip")} actor={actorId} client={clientId} " +
                $"epoch={epoch} tokens={initialToken}->{inFlightToken} inFlightLocalReady={locallyReady} " +
                $"inFlightAuthorityReady={authorityReady} inFlightOwnerPackets={packetsDuringWindow} " +
                $"postAckOwnerPackets={ownerPackets.Count} baselineHash={beforeHash} localChange={expectedHash} " +
                $"postAckAuthorityHash={actualHash}");
            Assert.That((ownerPackets.Count - 1, actualHash), Is.EqualTo((1, expectedHash)),
                "RESYNC-EQUIPMENT: a distinct same-epoch ACK must republish the current owner weapon exactly once (zero means explicit unequip).");
            AssertOwnerPacket(ownerPackets[1], clientId, actorId, epoch, expectedHash);
        }

        private static int AuthorityHash(NetworkMeleeManager manager, uint actorId)
        {
            var snapshot = manager.CaptureCharacterSnapshots().Single(s => s.CharacterNetworkId == actorId);
            if (!snapshot.HasWeaponState) throw new InvalidOperationException("No authoritative equipment state.");
            return snapshot.WeaponState.WeaponHash;
        }

        private static void AssertOwnerPacket(FusionModuleMessage packet, uint clientId, uint actorId, uint epoch, int hash)
        {
            var reader = new FusionValueReader(packet.Payload);
            uint receivedActor = 0;
            NetworkMeleeWeaponState state = default;
            reader.Read(ref receivedActor);
            reader.Read(ref state);
            Assert.That(reader.End, Is.True);
            Assert.That(packet.Sequence, Is.Not.Zero, "Equipment must use the reliable ordered stream.");
            Assert.That((packet.SenderClientId, receivedActor, packet.AuthorityEpoch, state.WeaponHash),
                Is.EqualTo((clientId, actorId, epoch, hash)));
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            m_Objects.Add(obj);
            return obj;
        }

        private static IEnumerator Await(Task task)
        {
            float deadline = Time.realtimeSinceStartup + 15f;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(task.IsCompleted, Is.True, "Timed out waiting for offline test setup/teardown.");
            task.GetAwaiter().GetResult();
        }

        private static T Get<T>(FusionTransportBridge transport, string field) =>
            (T)(typeof(FusionTransportBridge).GetField(field, PrivateInstance)
                ?? throw new MissingFieldException(typeof(FusionTransportBridge).Name, field)).GetValue(transport);

        private static void Invoke(FusionTransportBridge transport, string method, params object[] args) =>
            (typeof(FusionTransportBridge).GetMethod(method, PrivateInstance)
                ?? throw new MissingMethodException(typeof(FusionTransportBridge).Name, method)).Invoke(transport, args);

        private static void RegisterEmptyProducer(FusionTransportBridge transport, EmptyProducer producer)
        {
            Assert.That(transport.RegisterModuleHandler(producer.FullSnapshotModuleId, _ => { }), Is.True);
            Assert.That(transport.RegisterFullSnapshotProducer(producer), Is.True);
        }

        // Only unrelated snapshot contributors are empty; the real Melee producer and handlers run.
        private sealed class EmptyProducer : IFusionFullSnapshotProducer
        {
            public EmptyProducer(ushort moduleId) => FullSnapshotModuleId = moduleId;
            public ushort FullSnapshotModuleId { get; }
            public string FullSnapshotProducerName => "Resync test fixture";
            public Action<uint> Produce;

            public FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context)
            {
                Produce?.Invoke(context.ClientId);
                return context.Complete();
            }
        }
    }
}
