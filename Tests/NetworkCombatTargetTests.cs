using System;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.TestUtilities;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    [NUnit.Framework.Category("GC2Networking.Targeting")]
    public sealed class NetworkCombatTargetTests
    {
        private sealed class TestBridge : NetworkTransportBridge
        {
            public override bool IsServer => true;
            public override bool IsClient => true;
            public override bool IsHost => true;
            public override float ServerTime => Time.time;
            public override void SendToServer(uint id, NetworkInputState[] inputs) { }
            public override void SendToOwner(uint owner, uint id, NetworkPositionState state, float serverTime) { }
            public override void Broadcast(uint id, NetworkPositionState state, float serverTime,
                uint excludeClientId = uint.MaxValue, NetworkRecipientFilter relevanceFilter = null) { }
        }
        private readonly List<GameObject> m_Objects = new();
        private TestBridge m_Bridge;

        [SetUp]
        public void Setup() { m_Bridge = EditModeLifecycle.AddComponent<TestBridge>(New("Bridge")); }
        [TearDown]
        public void Teardown()
        {
            ShortcutPlayer.Change(null);
            for (int i = m_Objects.Count - 1; i >= 0; i--)
                if (m_Objects[i] != null) UnityEngine.Object.DestroyImmediate(m_Objects[i]);
            m_Objects.Clear();
        }
        private GameObject New(string name) { var obj = new GameObject(name); m_Objects.Add(obj); return obj; }
        private static void Set(object target, string field, object value) => target.GetType().GetField(field,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private NetworkCharacter Actor(uint id, bool npc = false, bool owner = false, bool authenticated = true)
        {
            GameObject obj = New("Actor " + id);
            Character character = EditModeLifecycle.AddComponent<Character>(obj);
            var motion = new UnitMotionNetworkController();
            Arawn.GameCreator2.Networking.Editor.NetworkTargetingDemoEditorUtility.SetUnit(character, "m_Motion", motion);
            motion.OnStartup(character);
            NetworkCharacter actor = obj.AddComponent<NetworkCharacter>();
            Set(actor, "m_ActorType", npc ? NetworkCharacterActorType.NPC : NetworkCharacterActorType.PlayerOwned);
            EditModeLifecycle.Invoke(actor, "Awake");
            actor.SetManualNetworkId(id);
            EditModeLifecycle.InitializeNetworkRole(actor, true, owner, true, !npc && authenticated);
            m_Bridge.RegisterCharacter(actor);
            return actor;
        }
        private NetworkCombatTarget Targeting(NetworkCharacter actor) => EditModeLifecycle.AddComponent<NetworkCombatTarget>(actor.gameObject);
        private static NetworkMotionCommand State(uint target, uint revision) => new()
        {
            commandType = NetworkMotionCommandType.SetCombatTarget, targetNetworkId = target, dataX = unchecked((int)revision)
        };

        [TestCase(true)] [TestCase(false)]
        public void Selection_AllowsLivingRegisteredNpcAndPlayer(bool npc)
        {
            NetworkCharacter actor = Actor(1); NetworkCharacter target = Actor(2, npc);
            Assert.That(Targeting(actor).CanSelect(target.NetworkId), Is.True);
        }
        [Test]
        public void Selection_RejectsSelfMissingDeadDistantAndUnauthenticatedActors()
        {
            NetworkCharacter actor = Actor(1); NetworkCharacter target = Actor(2);
            NetworkCombatTarget targeting = Targeting(actor);
            Assert.That(targeting.CanSelect(1), Is.False);
            Assert.That(targeting.CanSelect(999), Is.False);
            target.transform.position = Vector3.right * 31;
            Assert.That(targeting.CanSelect(2), Is.False);
            target.transform.position = Vector3.zero;
            Set(target.Character, "m_IsDead", true);
            Assert.That(targeting.CanSelect(2), Is.False);
            Assert.That(targeting.CanSelect(Actor(3, authenticated: false).NetworkId), Is.False);
            Assert.That(targeting.CanSelect(0), Is.True);
        }
        [Test]
        public void Selection_RejectsClientDeterministicNpcAndUnownedRequester()
        {
            NetworkCharacter actor = Actor(1); NetworkCharacter npc = Actor(2, true);
            Set(npc, "m_NPCMode", NetworkCharacter.NPCSyncMode.ClientSideDeterministic);
            Assert.That(Targeting(actor).CanSelect(2), Is.False);
            Assert.That(Targeting(npc).CanSelect(1), Is.False);
        }
        [Test]
        public void ConfirmedState_IsIdempotentRejectsStaleConflictsAndClears()
        {
            NetworkCharacter actor = Actor(1); Actor(2, true); Actor(3);
            NetworkCombatTarget targeting = Targeting(actor);
            targeting.ApplyConfirmed(State(2, 4));
            targeting.ApplyConfirmed(State(3, 3));
            targeting.ApplyConfirmed(State(3, 4));
            Assert.That(targeting.TargetNetworkId, Is.EqualTo(2));
            targeting.ApplyConfirmed(State(2, 4));
            Assert.That(actor.Character.Combat.Targets.Primary, Is.SameAs(m_Bridge.ResolveCharacter(2).gameObject));
            targeting.ApplyConfirmed(State(0, 5));
            targeting.ApplyConfirmed(State(2, 4));
            Assert.That(actor.Character.Combat.Targets.Primary, Is.Null);
        }
        [Test]
        public void ConfirmedState_AcceptsFirstLateJoinRevisionAndWrapsSafely()
        {
            NetworkCharacter actor = Actor(1); Actor(2, true);
            NetworkCombatTarget targeting = Targeting(actor);
            targeting.ApplyConfirmed(State(2, uint.MaxValue));
            Assert.That(targeting.TargetNetworkId, Is.EqualTo(2));
            targeting.ApplyConfirmed(State(0, 1));
            targeting.ApplyConfirmed(State(2, uint.MaxValue));
            Assert.That(targeting.TargetNetworkId, Is.Zero);
            Assert.That(targeting.Revision, Is.EqualTo(1));
        }
        [Test]
        public void ConfirmedState_ResolvesSelectedActorAfterSpawn()
        {
            NetworkCharacter actor = Actor(1); NetworkCombatTarget targeting = Targeting(actor);
            targeting.ApplyConfirmed(State(2, 2));
            Assert.That(actor.Character.Combat.Targets.Primary, Is.Null);
            NetworkCharacter target = Actor(2, true);
            EditModeLifecycle.Invoke(targeting, "Update");
            Assert.That(actor.Character.Combat.Targets.Primary, Is.SameAs(target.gameObject));
        }
        [Test]
        public void ObserverNativeSelection_CannotOverwriteConfirmedTarget()
        {
            NetworkCharacter actor = Actor(1); NetworkCharacter target = Actor(2, true); NetworkCharacter other = Actor(3);
            NetworkCombatTarget targeting = Targeting(actor);
            targeting.ApplyConfirmed(State(target.NetworkId, 2));
            actor.Character.Combat.Targets.Primary = other.gameObject;
            Assert.That(actor.Character.Combat.Targets.Primary, Is.SameAs(target.gameObject));
        }
        [Test]
        public void Manager_QueuesSnapshotUntilCharacterRegistration()
        {
            NetworkMotionManager manager = EditModeLifecycle.AddComponent<NetworkMotionManager>(New("Motion"));
            manager.Initialize(false, true);
            manager.ReceiveBroadcast(new NetworkMotionCommandMessage { CharacterNetworkId = 1, Command = State(2, 7) });
            manager.ReceiveBroadcast(new NetworkMotionCommandMessage { CharacterNetworkId = 1, Command = State(0, 6) });
            NetworkCharacter actor = Actor(1); NetworkCharacter target = Actor(2, true);
            Targeting(actor);
            manager.RefreshControllerRegistry();
            Assert.That(actor.Character.Combat.Targets.Primary, Is.SameAs(target.gameObject));
        }
        [Test]
        public void Facing_StationaryAuthorityTracksItsOwnTargetWithoutShortcutPlayer()
        {
            NetworkCharacter actor = Actor(1); NetworkCharacter target = Actor(2, true);
            NetworkCombatTarget targeting = Targeting(actor);
            targeting.ApplyConfirmed(State(2, 2));
            target.transform.position = Vector3.right * 3;
            var facing = new UnitFacingNetworkTarget();
            actor.Character.Motion.AngularSpeed = -1;
            facing.OnStartup(actor.Character);
            facing.OnUpdate();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(actor.transform.eulerAngles.y, 90)), Is.LessThan(0.1f));
            target.transform.position = Vector3.back * 3;
            facing.OnUpdate();
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(actor.transform.eulerAngles.y, 180)), Is.LessThan(0.1f));
            facing.OnDispose(actor.Character);
        }
        [TestCase(true)] [TestCase(false)]
        public void AuthenticatedRequest_CommitsOnceIgnoresForgedRevisionAndRejectsReplay(bool localOwner)
        {
            NetworkSecurityManager security = EditModeLifecycle.AddComponent<NetworkSecurityManager>(New("Security"));
            security.Initialize(true, () => Time.time);
            NetworkCharacter actor = Actor(1, owner: localOwner); Actor(2, true);
            m_Bridge.SetCharacterOwner(1, 77);
            NetworkCombatTarget targeting = Targeting(actor);
            NetworkMotionManager manager = EditModeLifecycle.AddComponent<NetworkMotionManager>(New("Motion"));
            manager.Initialize(true, true);
            int broadcasts = 0;
            manager.BroadcastCommandToClients += message => { broadcasts++; manager.ReceiveBroadcast(message); };
            var command = State(2, 9000);
            command.sequenceNumber = 1;
            var request = new NetworkMotionCommandMessage { CharacterNetworkId = 1, Command = command };
            manager.ReceiveCommand(77, request);
            Assert.That(targeting.TargetNetworkId, Is.EqualTo(2));
            Assert.That(targeting.Revision, Is.EqualTo(2), "A requester cannot mint the authoritative revision.");
            Assert.That(broadcasts, Is.EqualTo(1), "Host echo must not commit twice.");
            manager.ReceiveCommand(77, request);
            Assert.That(broadcasts, Is.EqualTo(1));
            Assert.That(targeting.Revision, Is.EqualTo(2));
        }

        [Test]
        public void NativeClosestSelection_HostTraversesAuthenticatedLoopbackExactlyOnce()
        {
            NetworkSecurityManager security = EditModeLifecycle.AddComponent<NetworkSecurityManager>(New("Security"));
            security.Initialize(true, () => Time.time);
            NetworkCharacter actor = Actor(1, owner: true); NetworkCharacter npc = Actor(2, true);
            npc.transform.position = Vector3.right * 3;
            m_Bridge.SetCharacterOwner(1, 77);
            NetworkCombatTarget targeting = Targeting(actor);
            NetworkMotionManager manager = EditModeLifecycle.AddComponent<NetworkMotionManager>(New("Motion"));
            manager.Initialize(true, true);
            int requests = 0, broadcasts = 0;
            manager.SendCommandToServer += message => { requests++; manager.ReceiveCommand(77, message); };
            manager.BroadcastCommandToClients += message => { broadcasts++; manager.ReceiveBroadcast(message); };
            actor.Character.Combat.Targets.AddCandidate(npc.gameObject);
            CycleTargets.Closest(actor.Character);
            Assert.That(requests, Is.EqualTo(1));
            Assert.That(broadcasts, Is.EqualTo(1));
            Assert.That(targeting.TargetNetworkId, Is.EqualTo(2));
            Assert.That(targeting.Revision, Is.EqualTo(2));
            Assert.That(actor.Character.Combat.Targets.Primary, Is.SameAs(npc.gameObject));
        }

        [Test]
        public void SmokePlan_RequiresVisibleTurnsAcrossNpcAndPvpSelections()
        {
            NetworkCharacter actor = Actor(1); NetworkCharacter other = Actor(2);
            actor.transform.position = new Vector3(0, 1, -4);
            other.transform.position = new Vector3(2, 1, -2);
            var npcs = new List<NetworkCharacter>();
            uint id = 10;
            foreach (float x in new[] { -5f, 0f, 5f })
            foreach (float z in new[] { 0f, 2.5f, 5f })
            {
                NetworkCharacter npc = Actor(id++, true);
                npc.transform.position = new Vector3(x, 1, z); npcs.Add(npc);
            }
            var method = typeof(NetworkTargetingSmokeProbe).GetMethod("ChooseNpcTargets", BindingFlags.Static | BindingFlags.NonPublic);
            var targets = (uint[])method.Invoke(null, new object[] { actor, other, npcs.ToArray() });
            Assert.That(targets.Length, Is.EqualTo(2));
            Assert.That(targets[0], Is.Not.EqualTo(targets[1]));
            foreach (uint target in targets)
            {
                Vector3 direction = m_Bridge.ResolveCharacter(target).transform.position - actor.transform.position;
                Assert.That(Vector3.Angle(actor.transform.forward, direction), Is.GreaterThanOrEqualTo(25));
                Assert.That(Vector3.Angle(other.transform.position - actor.transform.position, direction), Is.GreaterThan(45));
            }
        }

        [Test]
        public void Manager_ForgedActorCannotCommitSelection()
        {
            NetworkCharacter actor = Actor(1); Actor(2, true); NetworkCombatTarget targeting = Targeting(actor);
            NetworkMotionManager manager = EditModeLifecycle.AddComponent<NetworkMotionManager>(New("Motion"));
            manager.Initialize(true, true);
            bool rejected = false;
            manager.SendResultToClient += (_, message) => rejected = !message.Result.approved;
            manager.ReceiveCommand(77, new NetworkMotionCommandMessage { CharacterNetworkId = 1, Command = State(2, 1) });
            Assert.That(rejected, Is.True);
            Assert.That(targeting.TargetNetworkId, Is.Zero);
        }
    }
}
