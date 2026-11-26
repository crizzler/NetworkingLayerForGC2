using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
    public sealed class FusionEquipmentSendRefusalTests
    {
        private const ushort WeaponState = 14;
        private const ushort Reaction = 13;
        private const uint Recipient = 301;
        private const uint OtherRecipient = 302;
        private NetworkRunner m_Runner;
        private FusionTransportBridge m_Transport;
        private FusionMeleeTransportBridge m_Melee;
        private NetworkMeleeManager m_Manager;
        private Character m_Character;
        private MeleeWeapon m_Weapon;
        private uint m_ActorId;
        private uint m_ClientId;
        private readonly List<UnityEngine.Object> m_Objects = new();

        [UnityTest]
        public IEnumerator SameFrameFallback_FollowsAcceptedEquipment()
        {
            yield return new EnterPlayMode();
            yield return ExerciseFallbackOrdering(refuse: false);
        }

        [UnityTest]
        public IEnumerator RefusedEquipment_HoldsOnlyDependentFallbackUntilAccepted()
        {
            yield return new EnterPlayMode();
            yield return ExerciseFallbackOrdering(refuse: true);
        }

        private IEnumerator ExerciseFallbackOrdering(bool refuse)
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;

            var observed = ObserveEquipmentAndReactions();
            int attempts = 0;
            InstallSendProbe((client, payload) =>
            {
                if (client != m_ClientId) return null;
                AssertEquipment(payload, m_Weapon.Id.Hash);
                return ++attempts == 1 && refuse ? false : null;
            });
            PublishLoopbackEquipment(m_Weapon.Id.Hash);
            Assert.That(attempts, Is.Zero, "Live equipment fan-out must stay outside the current call stack.");
            EmitReaction(m_ActorId, sequence: 2, hash: 1234);
            EmitReaction(m_ActorId, sequence: 3, hash: 0);
            EmitReaction(m_ActorId + 1, sequence: 4, hash: 0);
            Assert.That(observed.Contains("reaction:2"), Is.True, "Explicit reactions must not wait for equipment.");
            Assert.That(observed.Contains("reaction:4"), Is.True, "A different actor must not wait for this equipment.");
            Assert.That(observed.Contains("reaction:3"), Is.False,
                "FALLBACK-OVERTAKE: a same-frame fallback reached loopback before its deferred equipment.");

            if (refuse)
            {
                yield return WaitUntil(() => attempts != 0, 2f);
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(observed.Contains("reaction:3"), Is.False, "The first equipment attempt was refused.");
            }
            yield return WaitUntil(() => observed.Contains("reaction:3"), 2f);
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(attempts, Is.EqualTo(refuse ? 2 : 1));
            Assert.That(observed, Is.EqualTo(new[] { "reaction:2", "reaction:4", $"equipment:{m_Weapon.Id.Hash}", "reaction:3" }));
        }

        [UnityTest]
        public IEnumerator DelayedFallback_DoesNotReplaceNewerNativeReactionAcrossSequenceWrap()
        {
            yield return new EnterPlayMode();
            yield return ExerciseNativeFallback(supersede: true);
        }

        [UnityTest]
        public IEnumerator DelayedFallback_StillPresentsAfterEquipmentWhenNotSuperseded()
        {
            yield return new EnterPlayMode();
            yield return ExerciseNativeFallback(supersede: false);
        }

        [UnityTest]
        public IEnumerator NewerFallback_SupersedesOnlyInItsReadyAudience()
        {
            yield return new EnterPlayMode();
            yield return ExerciseFallbackAudience();
        }

        private IEnumerator ExerciseFallbackAudience()
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;
            AddReadyRecipient(Recipient);

            var observed = ObserveEquipmentAndReactions();
            InstallSendProbe((client, _) => client == Recipient ? true : null);
            PublishLoopbackEquipment(m_Weapon.Id.Hash);
            EmitReaction(m_ActorId, sequence: uint.MaxValue, hash: 0);

            // The synthetic recipient is outside the newer event's ready audience,
            // then resumes before the next Update. Its older fallback stays eligible.
            var ready = ReadField<HashSet<uint>>(m_Transport, "m_GameplayReadyClients");
            Assert.That(ready.Remove(Recipient), Is.True);
            EmitReaction(m_ActorId, sequence: 1, hash: 0);
            Assert.That(ready.Add(Recipient), Is.True);
            Assert.That(observed, Is.Empty, "Even the newest fallback must wait for equipment.");

            // Only the synthetic remote's old item and the real loopback recipient's
            // new item should remain. Do not claim remote delivery to a synthetic ID.
            var deferred = ReadField<ICollection>(m_Melee, "m_DeferredFallbacks");
            Assert.That(deferred.Count, Is.EqualTo(2), "FALLBACK-AUDIENCE: retire only the older item in the newer reaction's audience.");
            var sequencesByClient = deferred.Cast<object>().ToDictionary(
                item => (uint)item.GetType().GetField("ClientId").GetValue(item),
                item => ((NetworkReactionBroadcast)item.GetType().GetField("Reaction").GetValue(item)).Sequence);
            Assert.That(sequencesByClient[m_ClientId], Is.EqualTo(1));
            Assert.That(sequencesByClient[Recipient], Is.EqualTo(uint.MaxValue));
            yield return WaitUntil(() => observed.Contains("reaction:1"), 2f);
            Assert.That(observed, Is.EqualTo(new[] { $"equipment:{m_Weapon.Id.Hash}", "reaction:1" }));
        }

        private IEnumerator ExerciseNativeFallback(bool supersede)
        {
            // The real offline Fusion loopback feeds a presentation-only Melee replica.
            yield return StartSession(presentReplica: true);
            AdmitLocalClient();
            yield return null;
            yield return null;

            var controller = m_Character.GetComponent<NetworkMeleeController>();
            Assert.That(controller.IsServer || controller.IsLocalClient, Is.False);
            var stance = m_Character.Combat.RequestStance<MeleeStance>();
            Assert.That(stance.CurrentPhase, Is.EqualTo(MeleePhase.None));

            var marker = new GameObject("Reaction pose").transform;
            marker.SetParent(m_Character.Animim.Animator.transform, false);
            const float fallbackPose = -1f;
            const float explicitPose = 1f;
            var fallback = CreatePoseReaction(marker.name, "Fallback clip", fallbackPose);
            var explicitReaction = CreatePoseReaction(marker.name, "Explicit clip", explicitPose);
            typeof(TWeapon).GetField("m_HitReaction", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(m_Weapon, fallback);
            NetworkMeleeManager.RegisterMeleeWeapon(m_Weapon);
            int hash = NetworkMeleeManager.GetReactionHash(explicitReaction);
            Assert.That(hash, Is.Not.Zero);

            int attempts = 0;
            bool allowEquipment = false;
            InstallSendProbe((client, payload) =>
            {
                if (client != m_ClientId) return null;
                AssertEquipment(payload, m_Weapon.Id.Hash);
                attempts++;
                return allowEquipment ? null : false;
            });
            var received = new List<uint>();
            controller.OnReactionReceived += value => received.Add(value.Sequence);
            // Start at authority fan-out, after owner handling. The same manager in
            // offline mode would otherwise apply the state on the replica as authority
            // before this test's send refusal could take effect.
            typeof(FusionMeleeTransportBridge).GetMethod("BroadcastWeaponState", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(m_Melee, new object[] { m_ActorId, new NetworkMeleeWeaponState { WeaponHash = m_Weapon.Id.Hash } });
            EmitReaction(m_ActorId, sequence: uint.MaxValue, hash: 0);
            yield return WaitUntil(() => attempts > 0, 2f);
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(received, Is.Empty, "The refused equipment must hold its fallback.");
            Assert.That(m_Character.Combat.IsEquipped(m_Weapon), Is.False);
            Assert.That(marker.localPosition.x, Is.Zero);

            EmitReaction(supersede ? m_ActorId : m_ActorId + 1, sequence: 1, hash: hash);
            if (supersede)
            {
                Assert.That(received, Is.EqualTo(new uint[] { 1 }), "Explicit reaction delivery must remain immediate.");
                yield return WaitUntil(() => marker.localPosition.x > 0.9f, 0.5f);
                Assert.That(marker.localPosition.x, Is.GreaterThan(0.9f), "The newer GC2 clip must animate before equipment recovers.");
                Assert.That(stance.CurrentPhase, Is.EqualTo(MeleePhase.Reaction));
            }

            allowEquipment = true;
            yield return WaitUntil(() => m_Character.Combat.IsEquipped(m_Weapon), 1f);
            Assert.That(attempts, Is.GreaterThan(1));
            Assert.That(m_Character.Combat.IsEquipped(m_Weapon), Is.True, "The retry must actually apply on the receiver.");
            if (supersede)
            {
                float deadline = Time.realtimeSinceStartup + 0.25f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    Assert.That(marker.localPosition.x, Is.GreaterThan(0.9f),
                        "STALE-FALLBACK-POSE: retry replaced the newer GC2 clip with the delayed weapon fallback.");
                }
                Assert.That(received, Is.EqualTo(new uint[] { 1 }));
            }
            else
            {
                yield return WaitUntil(() => marker.localPosition.x < -0.9f, 0.5f);
                Assert.That(marker.localPosition.x, Is.LessThan(-0.9f), "An unsuperseded fallback must animate after equipment arrives.");
                Assert.That(stance.CurrentPhase, Is.EqualTo(MeleePhase.Reaction));
                Assert.That(received, Is.EqualTo(new[] { uint.MaxValue }));
            }
        }

        private MeleeReaction CreatePoseReaction(string bindingPath, string name, float pose)
        {
            var clip = Track(new AnimationClip { name = name });
            AnimationUtility.SetEditorCurve(clip,
                EditorCurveBinding.FloatCurve(bindingPath, typeof(Transform), "m_LocalPosition.x"),
                AnimationCurve.Constant(0f, 3f, pose));
            var reaction = Track(ScriptableObject.CreateInstance<MeleeReaction>());
            reaction.name = name;
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            var item = new ReactionItem();
            var animations = (ReactionAnimations)typeof(ReactionItem).GetField("m_Animations", fields).GetValue(item);
            typeof(ReactionAnimations).GetField("m_Animations", fields).SetValue(animations, new[] { clip });
            typeof(Reaction).GetField("m_ReactionList", fields).SetValue(reaction, new ReactionList(item));
            typeof(Reaction).GetField("m_TransitionIn", fields).SetValue(reaction, 0f);
            typeof(Reaction).GetField("m_UseRootMotion", fields).SetValue(reaction, false);
            return reaction;
        }

        [UnityTest]
        public IEnumerator TerminalEquipmentRefusal_DropsDependentFallbackUntilNewEquipment()
        {
            yield return new EnterPlayMode();
            yield return ExerciseTerminalFallback();
        }

        private IEnumerator ExerciseTerminalFallback()
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;

            var observed = ObserveEquipmentAndReactions();
            int attempts = 0;
            InstallSendProbe((client, _) =>
                client != m_ClientId ? null : ++attempts <= 4 ? false : null);
            LogAssert.Expect(LogType.Warning, new Regex(@"\[FusionMeleeTransportBridge\].*equipment.*abandoned"));
            PublishLoopbackEquipment(m_Weapon.Id.Hash);
            EmitReaction(m_ActorId, sequence: 1, hash: 0);
            yield return WaitUntil(() => attempts >= 4, 2f);
            Assert.That(attempts, Is.EqualTo(4));
            EmitReaction(m_ActorId, sequence: 2, hash: 0);
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.That(attempts, Is.EqualTo(4), "Do not keep retrying a permanently refused update.");
            Assert.That(observed, Is.Empty, "FALLBACK-TERMINAL: do not play a fallback against stale equipment.");

            PublishLoopbackEquipment(0);
            EmitReaction(m_ActorId, sequence: 3, hash: 0);
            yield return WaitUntil(() => observed.Contains("reaction:3"), 2f);
            Assert.That(observed, Is.EqualTo(new[] { "equipment:0", "reaction:3" }),
                "A fresh explicit unequip lifts the barrier without replaying dropped reactions.");
        }

        private List<string> ObserveEquipmentAndReactions()
        {
            var observed = new List<string>();
            var handlers = ReadField<Dictionary<ushort, Action<FusionModuleMessage>>>(m_Transport, "m_ModuleHandlers");
            var original = handlers[FusionMeleeTransportBridge.ModuleId];
            handlers[FusionMeleeTransportBridge.ModuleId] = message =>
            {
                if (message.FromAuthority && message.MessageType == Reaction)
                {
                    // Measure real ordered transport delivery; native playback is covered by the shared Melee tests.
                    var reader = new FusionValueReader(message.Payload);
                    NetworkReactionBroadcast reaction = default;
                    reader.Read(ref reaction);
                    Assert.That(reader.End, Is.True);
                    observed.Add($"reaction:{reaction.Sequence}");
                    return;
                }
                if (message.FromAuthority && message.MessageType == WeaponState)
                    observed.Add($"equipment:{ReadEquipment(message.Payload.ToArray())}");
                original(message);
            };
            return observed;
        }

        private void PublishLoopbackEquipment(int hash)
        {
            var writer = new FusionValueWriter();
            writer.Write(m_ActorId);
            writer.Write(new NetworkMeleeWeaponState { WeaponHash = hash });
            Assert.That(m_Transport.SendModuleToAuthority(FusionMeleeTransportBridge.ModuleId,
                WeaponState, writer.ToArray(), reliable: true), Is.True);
            Assert.That(m_Manager.CaptureCharacterSnapshots().Single(s => s.CharacterNetworkId == m_ActorId)
                .WeaponState.WeaponHash, Is.EqualTo(hash));
        }

        private void EmitReaction(uint actorId, uint sequence, int hash) =>
            m_Manager.BroadcastReactionToAllClients(new NetworkReactionBroadcast
            {
                CharacterNetworkId = actorId,
                Sequence = sequence,
                ReactionHash = hash,
                PlaybackKind = NetworkReactionPlaybackKind.Stance
            });

        [UnityTest]
        public IEnumerator PostAckOwnerRefusal_RetriesWithoutAnotherAckOrEquip()
        {
            yield return new EnterPlayMode();
            yield return ExerciseOwnerRefusal();
        }

        private IEnumerator ExerciseOwnerRefusal()
        {
            yield return StartSession();
            yield return Await(m_Character.Combat.Equip(m_Weapon, null, new Args(m_Character.gameObject)));
            int attempts = 0;
            int accepted = 0;
            InstallSendProbe((client, payload) =>
            {
                if (client.HasValue) return null;
                AssertEquipment(payload, m_Weapon.Id.Hash);
                if (++attempts == 1) return false;
                ++accepted;
                return null; // Forward accepted owner sends through real ordered admission and Melee handling.
            });
            AdmitLocalClient();
            uint epoch = m_Transport.AuthorityEpoch;
            ulong revision = m_Transport.LocalGameplaySnapshotRevision;
            yield return WaitUntil(() => attempts != 0, 1f);
            Assert.That(attempts, Is.EqualTo(1), "The post-ACK send must first be refused.");
            Assert.That(accepted, Is.Zero);
            yield return WaitUntil(() => accepted != 0, 2f);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(m_Transport.LocalGameplaySnapshotRevision, Is.EqualTo(revision));
            Assert.That(m_Transport.AuthorityEpoch, Is.EqualTo(epoch));
            Assert.That((attempts, accepted), Is.EqualTo((2, 1)),
                "OWNER-REFUSAL: an unchanged acknowledged revision must recover one refused send exactly once.");
            Assert.That(m_Manager.CaptureCharacterSnapshots().Single(s => s.CharacterNetworkId == m_ActorId)
                .WeaponState.WeaponHash, Is.EqualTo(m_Weapon.Id.Hash));
        }

        [UnityTest]
        public IEnumerator CatchUpAndFanOutRefusal_RetryOnlyLatestForTheFailedRecipient()
        {
            yield return new EnterPlayMode();
            yield return ExerciseCatchUpAndFanOut();
        }

        private IEnumerator ExerciseCatchUpAndFanOut()
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;
            AddReadyRecipient(Recipient);
            AddReadyRecipient(OtherRecipient);
            var refused = new List<int>();
            var recovered = new List<int>();
            var other = new List<int>();
            bool refuse = true;
            InstallSendProbe((client, payload) =>
            {
                if (client == Recipient)
                {
                    (refuse ? refused : recovered).Add(ReadEquipment(payload));
                    return !refuse;
                }
                if (client == OtherRecipient)
                {
                    other.Add(ReadEquipment(payload));
                    return true;
                }
                return null;
            });
            m_Manager.RecordAuthoritativeWeaponState(m_ActorId,
                new NetworkMeleeWeaponState { WeaponHash = m_Weapon.Id.Hash });
            // Remote delivery is replaced only at the equipment send boundary. Invoke the
            // real ACK subscriber for this synthetic ready recipient; admission of the owner is real.
            ReadField<Action<uint>>(m_Transport, "ClientSnapshotAcknowledged")(Recipient);
            Assert.That(refused, Is.EqualTo(new[] { m_Weapon.Id.Hash }));
            var writer = new FusionValueWriter();
            writer.Write(m_ActorId);
            writer.Write(new NetworkMeleeWeaponState { WeaponHash = 0 });
            Assert.That(m_Transport.SendModuleToAuthority(FusionMeleeTransportBridge.ModuleId,
                WeaponState, writer.ToArray(), reliable: true), Is.True);
            yield return WaitUntil(() => other.Count != 0, 2f);
            Assert.That(other, Is.EqualTo(new[] { 0 }), "The other ready recipient must receive the live change.");
            refuse = false;
            yield return WaitUntil(() => recovered.Count != 0, 2f);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(recovered, Is.EqualTo(new[] { 0 }),
                "FANOUT-REFUSAL: send only the latest state (explicit unequip) to the refused recipient.");
            Assert.That(other, Is.EqualTo(new[] { 0 }), "Never retry a recipient that already accepted the update.");
        }

        [UnityTest]
        public IEnumerator LiveFanOut_DoesNotInvalidateAnActiveSnapshotProducer()
        {
            yield return new EnterPlayMode();
            yield return ExerciseLiveFanOutDuringSnapshot();
        }

        private IEnumerator ExerciseLiveFanOutDuringSnapshot()
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;
            AddReadyRecipient(Recipient);

            // Model a synchronous producer while another recipient is already ready.
            // Leave the real send path in place until the producer unwinds.
            var producer = new EmptyProducer(FusionModuleIds.Core);
            var context = (FusionFullSnapshotContext)Activator.CreateInstance(typeof(FusionFullSnapshotContext),
                BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { m_Transport, producer, producer.FullSnapshotModuleId, OtherRecipient }, null);
            var contextField = Field(m_Transport, "m_ActiveSnapshotContext");
            Assert.That(contextField.GetValue(m_Transport), Is.Null);
            contextField.SetValue(m_Transport, context);
            try
            {
                var writer = new FusionValueWriter();
                writer.Write(m_ActorId);
                writer.Write(new NetworkMeleeWeaponState { WeaponHash = m_Weapon.Id.Hash });
                Assert.That(m_Transport.SendModuleToAuthority(FusionMeleeTransportBridge.ModuleId,
                    WeaponState, writer.ToArray(), reliable: true), Is.True);
                FusionFullSnapshotResult result = context.Complete();
                Assert.That(result.IsComplete && result.PacketsEnqueued == 0, Is.True,
                    "SNAPSHOT-ISOLATION: live equipment must not count as snapshot producer output: " + result.FailureReason);
            }
            finally
            {
                contextField.SetValue(m_Transport, null);
            }

            int attempts = 0;
            InstallSendProbe((client, payload) =>
            {
                if (client != Recipient) return null;
                AssertEquipment(payload, m_Weapon.Id.Hash);
                return ++attempts > 1;
            });
            yield return WaitUntil(() => attempts >= 2, 2f);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(attempts, Is.EqualTo(2), "A live-only refusal must retry after the producer exits.");
        }

        [UnityTest]
        public IEnumerator PermanentTargetRefusal_StopsAndReportsOnce()
        {
            yield return new EnterPlayMode();
            yield return ExercisePermanentRefusal();
        }

        private IEnumerator ExercisePermanentRefusal()
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;
            AddReadyRecipient(Recipient);
            int attempts = 0;
            InstallSendProbe((client, payload) =>
            {
                if (client != Recipient) return null;
                AssertEquipment(payload, 0);
                ++attempts;
                return false;
            });
            LogAssert.Expect(LogType.Warning, new Regex(@"\[FusionMeleeTransportBridge\].*equipment.*abandoned.*client=301"));
            ReadField<Action<uint>>(m_Transport, "ClientSnapshotAcknowledged")(Recipient);
            Assert.That(attempts, Is.EqualTo(1));
            yield return new WaitForSecondsRealtime(1.75f);
            Assert.That(attempts, Is.InRange(2, 5), "TERMINAL-REFUSAL: retry attempts must be bounded.");
            int terminalAttempts = attempts;
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.That(attempts, Is.EqualTo(terminalAttempts));
        }

        [UnityTest]
        public IEnumerator PendingCatchUp_DoesNotCrossRecipientDeparture()
        {
            yield return new EnterPlayMode();
            yield return ExerciseRecipientDeparture();
        }

        private IEnumerator ExerciseRecipientDeparture()
        {
            yield return StartSession();
            AdmitLocalClient();
            yield return null;
            yield return null;
            AddReadyRecipient(Recipient);
            int attempts = 0;
            InstallSendProbe((client, _) =>
            {
                if (client != Recipient) return null;
                ++attempts;
                return false;
            });
            ReadField<Action<uint>>(m_Transport, "ClientSnapshotAcknowledged")(Recipient);
            Assert.That(attempts, Is.EqualTo(1));
            m_Transport.OnPlayerLeft(m_Runner, PlayerRef.FromRaw((int)Recipient));
            // Reuse the ID before the next Update: polling readiness alone cannot detect this departure.
            AddReadyRecipient(Recipient);
            yield return new WaitForSecondsRealtime(0.75f);
            Assert.That(attempts, Is.EqualTo(1), "A new occupant must not inherit the previous recipient's retry.");
        }

        private IEnumerator StartSession(bool presentReplica = false)
        {
            Assert.That(NetworkTransportBridge.Active, Is.Null, "The test requires an empty gameplay scene.");
            var session = Track(new GameObject("Equipment refusal test session"));
            m_Runner = session.AddComponent<NetworkRunner>();
            var start = m_Runner.StartGame(new StartGameArgs { GameMode = GameMode.Single });
            yield return Await(start);
            Assert.That(start.Result.Ok, Is.True, start.Result.ShutdownReason.ToString());
            m_Transport = session.AddComponent<FusionTransportBridge>();
            Assert.That(m_Transport.Bind(m_Runner), Is.True);
            Assert.That(m_Transport.TryGetLocalClientId(out m_ClientId), Is.True);
            session.AddComponent<FusionAuthoritySpawnRegistry>().Configure(m_Transport);
            m_Manager = Track(new GameObject("Equipment refusal Melee manager")).AddComponent<NetworkMeleeManager>();
            var actor = Track(new GameObject("Equipment refusal owner"));
            actor.SetActive(false);
            m_Character = actor.AddComponent<Character>();
            var model = new GameObject("Model");
            model.transform.SetParent(actor.transform, false);
            m_Character.Animim.Animator = model.AddComponent<Animator>();
            var serializedCharacter = new SerializedObject(m_Character);
            serializedCharacter.FindProperty("m_Footsteps.m_FootstepDetector").managedReferenceValue = null;
            serializedCharacter.ApplyModifiedPropertiesWithoutUndo();
            var networkCharacter = actor.AddComponent<NetworkCharacter>();
            actor.AddComponent<NetworkMeleeController>();
            var networkObject = actor.AddComponent<NetworkObject>();
            var identity = actor.AddComponent<FusionNetworkIdentity>();
            actor.SetActive(true);
            new NetworkObjectBaker().Bake(actor);
            m_Runner.RegisterSceneObjects(SceneRef.FromIndex(0), new[] { networkObject });
            Assert.That(identity.TransportAdmitted, Is.True);
            Assert.That(identity.TryAssignLogicalOwner(m_Runner.LocalPlayer), Is.True);
            m_ActorId = identity.NetworkId;
            networkCharacter.InitializeNetworkRole(!presentReplica, !presentReplica, !presentReplica, true);
            Assert.That(m_Transport.TryVerifyActorOwnership(m_ClientId, m_ActorId, out _), Is.True);
            m_Weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Assert.That(m_Weapon.Id.Hash, Is.Not.Zero);
            m_Melee = session.AddComponent<FusionMeleeTransportBridge>();
            m_Melee.Configure(m_Transport);
            foreach (ushort module in new[] { FusionModuleIds.Core, FusionModuleIds.Variables, FusionModuleIds.AnimationMotion })
            {
                Assert.That(m_Transport.RegisterModuleHandler(module, _ => { }), Is.True);
                Assert.That(m_Transport.RegisterFullSnapshotProducer(new EmptyProducer(module)), Is.True);
            }
        }

        private void AdmitLocalClient()
        {
            m_Transport.NotifyLocalSceneReady();
            m_Transport.NotifyLocalGameplayReady();
            Assert.That(m_Transport.IsLocalGameplayReady && m_Transport.IsClientReady(m_ClientId), Is.True);
            Assert.That(m_Transport.LocalGameplaySnapshotRevision, Is.Not.Zero);
        }

        private void AddReadyRecipient(uint id)
        {
            ReadField<HashSet<uint>>(m_Transport, "m_ConnectedClientIds").Add(id);
            ReadField<HashSet<uint>>(m_Transport, "m_GameplayReadyClients").Add(id);
            Assert.That(m_Transport.IsClientReady(id), Is.True);
        }

        private void InstallSendProbe(Func<uint?, byte[], bool?> probe) =>
            Field(m_Melee, "m_EquipmentSendProbe").SetValue(m_Melee, probe);

        private void AssertEquipment(byte[] payload, int hash) => Assert.That(ReadEquipment(payload), Is.EqualTo(hash));

        private int ReadEquipment(byte[] payload)
        {
            var reader = new FusionValueReader(payload);
            uint id = 0;
            NetworkMeleeWeaponState state = default;
            reader.Read(ref id);
            reader.Read(ref state);
            Assert.That(reader.End, Is.True);
            Assert.That(id, Is.EqualTo(m_ActorId));
            return state.WeaponHash;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!Application.isPlaying) yield break;
            if (m_Melee != null) m_Melee.enabled = false;
            if (m_Runner != null && m_Runner.IsRunning && !m_Runner.IsShutdown)
                yield return Await(m_Runner.Shutdown(destroyGameObject: false));
            for (int i = m_Objects.Count - 1; i >= 0; --i)
                if (m_Objects[i] != null) UnityEngine.Object.Destroy(m_Objects[i]);
            m_Objects.Clear();
            yield return null;
            yield return new ExitPlayMode();
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Objects.Add(value);
            return value;
        }

        private static IEnumerator Await(Task task)
        {
            yield return WaitUntil(() => task.IsCompleted, 15f);
            Assert.That(task.IsCompleted, Is.True);
            task.GetAwaiter().GetResult();
        }

        private static IEnumerator WaitUntil(Func<bool> predicate, float seconds)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!predicate() && Time.realtimeSinceStartup < deadline) yield return null;
        }

        private static T ReadField<T>(object target, string name) => (T)Field(target, name).GetValue(target);
        private static FieldInfo Field(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(target.GetType().Name, name);

        private sealed class EmptyProducer : IFusionFullSnapshotProducer
        {
            public EmptyProducer(ushort module) => FullSnapshotModuleId = module;
            public ushort FullSnapshotModuleId { get; }
            public string FullSnapshotProducerName => "Equipment refusal fixture";
            public FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context) => context.Complete();
        }
    }
}
