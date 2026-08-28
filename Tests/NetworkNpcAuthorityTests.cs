using System;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.TestUtilities;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkNpcAuthorityTests
    {
        private sealed class TestBridge : NetworkTransportBridge
        {
            public bool Server = true;
            public override bool IsServer => Server;
            public override bool IsClient => !Server;
            public override bool IsHost => false;
            public override float ServerTime => Time.time;
            public override void SendToServer(uint id, NetworkInputState[] inputs) { }
            public override void SendToOwner(
                uint owner,
                uint id,
                NetworkPositionState state,
                float serverTime) { }
            public override void Broadcast(
                uint id,
                NetworkPositionState state,
                float serverTime,
                uint excludeClientId = uint.MaxValue,
                NetworkRecipientFilter relevanceFilter = null) { }
        }

        private sealed class TestBotCoordinator : NetworkBotSlotCoordinator
        {
            public bool Authority;
            public int SpawnCount;
            public int DespawnCount;

            private readonly Dictionary<GameObject, uint> m_Ids = new();
            private readonly Dictionary<uint, GameObject> m_Actors = new();
            private uint m_NextId = 10;

            protected override bool IsTransportAuthority => Authority;

            protected override GameObject SpawnBotActor(
                GameObject prefab,
                Vector3 position,
                Quaternion rotation,
                uint slotIdHash)
            {
                var actor = new GameObject($"Bot-{slotIdHash}");
                actor.transform.SetPositionAndRotation(position, rotation);
                Register(actor);
                SpawnCount++;
                return actor;
            }

            protected override bool DespawnBotActor(GameObject actor)
            {
                if (actor == null) return false;
                if (m_Ids.TryGetValue(actor, out uint id)) m_Actors.Remove(id);
                m_Ids.Remove(actor);
                UnityEngine.Object.DestroyImmediate(actor);
                DespawnCount++;
                return true;
            }

            protected override uint ResolveActorNetworkId(GameObject actor)
            {
                if (actor == null) return 0;
                if (!m_Ids.TryGetValue(actor, out uint id)) id = Register(actor);
                return id;
            }

            protected override GameObject ResolveActor(uint actorNetworkId) =>
                m_Actors.TryGetValue(actorNetworkId, out GameObject actor) ? actor : null;

            public void Adopt(uint actorNetworkId, GameObject actor)
            {
                m_Ids[actor] = actorNetworkId;
                m_Actors[actorNetworkId] = actor;
                m_NextId = Math.Max(m_NextId, actorNetworkId + 1);
            }

            protected override void PublishAuthoritativeSnapshot(
                IReadOnlyList<NetworkBotSlotState> states) { }

            private void OnDestroy()
            {
                foreach (GameObject actor in m_Actors.Values)
                {
                    if (actor != null) UnityEngine.Object.DestroyImmediate(actor);
                }
                m_Actors.Clear();
                m_Ids.Clear();
            }

            private uint Register(GameObject actor)
            {
                uint id = m_NextId++;
                m_Ids[actor] = id;
                m_Actors[id] = actor;
                return id;
            }
        }

        private readonly List<GameObject> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            ShortcutPlayer.Change(null);
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }
            m_Cleanup.Clear();
        }

        [Test]
        public void ExplicitNpc_CannotBecomeOwnerOrPlayer_WhenIsPlayerIsFlipped()
        {
            NetworkCharacter networkCharacter = CreateNetworkCharacter(
                "Explicit NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: true);

            EditModeLifecycle.InitializeNetworkRole(
                networkCharacter,
                isServer: true,
                isOwner: true,
                isHost: true,
                hasAuthenticatedPlayerOwner: true);

            Assert.That(networkCharacter.IsOwnerInstance, Is.False);
            Assert.That(networkCharacter.IsServerAuthoritativeNPC, Is.True);
            Assert.That(networkCharacter.HasSimulationAuthority, Is.True);
            Assert.That(networkCharacter.Character.IsPlayer, Is.False);
            Assert.That(ShortcutPlayer.Instance, Is.Not.EqualTo(networkCharacter.gameObject));

            networkCharacter.Character.IsPlayer = true;
            EditModeLifecycle.Invoke(networkCharacter, "Update");
            Assert.That(networkCharacter.Character.IsPlayer, Is.False,
                "A runtime GC2 flag mutation must not grant NPC authority.");
        }

        [Test]
        public void ExplicitPlayerOwned_OnlyAuthenticatedLocalOwnerBecomesShortcutPlayer()
        {
            NetworkCharacter remote = CreateNetworkCharacter(
                "Remote Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: true);
            EditModeLifecycle.InitializeNetworkRole(remote, false, false, false, true);

            Assert.That(remote.IsPlayerOwnedActor, Is.True);
            Assert.That(remote.Character.IsPlayer, Is.False);
            Assert.That(ShortcutPlayer.Instance, Is.Not.EqualTo(remote.gameObject));

            NetworkCharacter local = CreateNetworkCharacter(
                "Local Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: false);
            EditModeLifecycle.InitializeNetworkRole(local, false, true, false, true);

            Assert.That(local.Character.IsPlayer, Is.True);
            Assert.That(ShortcutPlayer.Instance, Is.EqualTo(local.gameObject));
        }

        [Test]
        public void ExplicitRemotePlayerFlagCorrection_PreservesAuthenticatedLocalShortcut()
        {
            NetworkCharacter local = CreateNetworkCharacter(
                "Local Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: false);
            EditModeLifecycle.InitializeNetworkRole(local, false, true, false, true);
            Assert.That(ShortcutPlayer.Instance, Is.EqualTo(local.gameObject));

            NetworkCharacter remote = CreateNetworkCharacter(
                "Remote Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: true);
            EditModeLifecycle.InitializeNetworkRole(remote, false, false, false, true);

            Assert.That(remote.Character.IsPlayer, Is.False);
            Assert.That(local.Character.IsPlayer, Is.True);
            Assert.That(
                ShortcutPlayer.Instance,
                Is.EqualTo(local.gameObject),
                "Correcting a remote replica's GC2 player flag must not clear the " +
                "process-global shortcut owned by the authenticated local player.");

            remote.Character.IsPlayer = true;
            EditModeLifecycle.Invoke(remote, "Update");
            Assert.That(remote.Character.IsPlayer, Is.False);
            Assert.That(ShortcutPlayer.Instance, Is.EqualTo(local.gameObject));
        }

        [Test]
        public void LegacyAutomatic_PreservesAuthoredPlayerCompatibility()
        {
            NetworkCharacter legacy = CreateNetworkCharacter(
                "Legacy Player",
                NetworkCharacterActorType.LegacyAutomatic,
                authoredIsPlayer: true);

            EditModeLifecycle.InitializeNetworkRole(legacy, false, false);
            Assert.That(legacy.EffectiveActorType, Is.EqualTo(NetworkCharacterActorType.PlayerOwned));
            Assert.That(legacy.Character.IsPlayer, Is.False,
                "An observer is a player actor but never a local GC2 player.");
        }

        [Test]
        public void Bridge_RejectsOwnerMappingAndInputOwnershipForNpc()
        {
            GameObject bridgeObject = Track(new GameObject("Test Bridge"));
            TestBridge bridge = bridgeObject.AddComponent<TestBridge>();
            EditModeLifecycle.Invoke(bridge, "Awake");

            NetworkCharacter npc = CreateNetworkCharacter(
                "Bridge NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: false);
            npc.SetManualNetworkId(91);
            EditModeLifecycle.InitializeNetworkRole(npc, true, false, false, false);
            bridge.RegisterCharacter(npc);
            bridge.SetCharacterOwner(91, 7);

            Assert.That(bridge.TryGetCharacterOwner(91, out _), Is.False);
            Assert.That(bridge.TryVerifyActorOwnership(7, 91, out _), Is.False);
        }

        [Test]
        public void Bridge_RejectsUnauthenticatedPlayerOwnerMapping()
        {
            GameObject bridgeObject = Track(new GameObject("Unauthenticated Bridge"));
            TestBridge bridge = bridgeObject.AddComponent<TestBridge>();
            EditModeLifecycle.Invoke(bridge, "Awake");

            NetworkCharacter player = CreateNetworkCharacter(
                "Unauthenticated Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: false);
            player.SetManualNetworkId(92);
            EditModeLifecycle.InitializeNetworkRole(player, true, false, true, false);
            bridge.RegisterCharacter(player);
            bridge.SetCharacterOwner(92, 7);

            Assert.That(bridge.TryGetCharacterOwner(92, out _), Is.False);
            Assert.That(bridge.TryVerifyActorOwnership(7, 92, out _), Is.False);
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void AuthorityGate_DisablesSelectedAiRootsUntilServerNpcRoleIsReady()
        {
            NetworkCharacter npc = CreateNetworkCharacter(
                "Gated NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: false);
            GameObject aiRoot = Track(new GameObject("AI Triggers"));
            aiRoot.transform.SetParent(npc.transform);

            NetworkCharacterAuthorityGate gate =
                npc.gameObject.AddComponent<NetworkCharacterAuthorityGate>();
            SetField(gate, "m_AuthorityOnlyRoots", new[] { aiRoot });
            EditModeLifecycle.Invoke(gate, "Awake");
            EditModeLifecycle.Invoke(gate, "OnEnable");
            Assert.That(aiRoot.activeSelf, Is.False);

            EditModeLifecycle.InitializeNetworkRole(npc, true, false, true, false);
            Assert.That(aiRoot.activeSelf, Is.True);

            npc.ResetNetworkRole();
            Assert.That(aiRoot.activeSelf, Is.False);
        }

        [Test]
        public void SharedNpcSetup_PreparesExplicitAuthorityDriverGateAndTargetSelector()
        {
            GameObject npcObject = Track(new GameObject("Prepared NPC"));
            Character character = EditModeLifecycle.AddComponent<Character>(npcObject);
            character.IsPlayer = true;
            GameObject aiRoot = Track(new GameObject("Authority AI"));
            aiRoot.transform.SetParent(npcObject.transform);

            var changes = new List<string>();
            var errors = new List<string>();
            bool configured = NetworkNpcSetupEditorUtility.ConfigureServerNpc(
                npcObject,
                NetworkPredictionBackend.BuiltIn,
                new[] { "Authority AI" },
                Array.Empty<Type>(),
                changes,
                errors);

            Assert.That(configured, Is.True, string.Join("\n", errors));
            Assert.That(errors, Is.Empty);
            Assert.That(character.IsPlayer, Is.False);
            Assert.That(character.Driver, Is.TypeOf<UnitDriverNavmeshNetworkServer>());

            NetworkCharacter networkCharacter = npcObject.GetComponent<NetworkCharacter>();
            Assert.That(networkCharacter, Is.Not.Null);
            Assert.That(networkCharacter.ActorType, Is.EqualTo(NetworkCharacterActorType.NPC));
            Assert.That(networkCharacter.NPCMode,
                Is.EqualTo(NetworkCharacter.NPCSyncMode.ServerAuthoritative));
            Assert.That(networkCharacter.PredictionBackend,
                Is.EqualTo(NetworkPredictionBackend.BuiltIn));

            NetworkCharacterAuthorityGate gate =
                npcObject.GetComponent<NetworkCharacterAuthorityGate>();
            Assert.That(gate, Is.Not.Null);
            CollectionAssert.AreEqual(new[] { aiRoot }, gate.AuthorityOnlyRoots);
            Assert.That(npcObject.GetComponent<NetworkNpcTargetSelector>(), Is.Not.Null);
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void ServerNpc_NavMeshClientDriverFallsBackToSampledServerDriver()
        {
            NetworkCharacter misconfigured = CreateNetworkCharacter(
                "Misconfigured NavMesh NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: false);
            var authoredClientDriver = new UnitDriverNavmeshNetworkClient();
            SetField(misconfigured, "m_AuthoredDriver", authoredClientDriver);

            int samples = 0;
            misconfigured.OnStatePayloadReady += (_, _, _) => samples++;
            EditModeLifecycle.InitializeNetworkRole(
                misconfigured,
                isServer: true,
                isOwner: false,
                isHost: true,
                hasAuthenticatedPlayerOwner: false);

            Assert.That(
                misconfigured.ActiveDriver,
                Is.TypeOf<UnitDriverNavmeshNetworkServer>(),
                "A client path-consumer must never become the authoritative NPC writer.");
            Assert.That(misconfigured.ActiveDriver, Is.Not.SameAs(authoredClientDriver));

            misconfigured.transform.position += Vector3.right;
            EditModeLifecycle.Invoke(misconfigured, "LateUpdate");
            Assert.That(
                samples,
                Is.EqualTo(1),
                "The runtime server NavMesh replacement must publish through the common NPC pose sampler.");

            NetworkCharacter valid = CreateNetworkCharacter(
                "Authored Server NavMesh NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: false);
            var authoredServerDriver = new UnitDriverNavmeshNetworkServer();
            SetField(valid, "m_AuthoredDriver", authoredServerDriver);
            EditModeLifecycle.InitializeNetworkRole(
                valid,
                isServer: true,
                isOwner: false,
                isHost: true,
                hasAuthenticatedPlayerOwner: false);

            Assert.That(
                valid.ActiveDriver,
                Is.SameAs(authoredServerDriver),
                "A legitimate authored server NavMesh driver must remain the authority driver.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NavMeshServerDriver_StartupAndAuthorityMigrationPreserveRuntimeBindings()
        {
            GameObject npcObject = Track(new GameObject("NavMesh Server Driver NPC"));
            Character character = npcObject.AddComponent<Character>();
            Animator animator = npcObject.AddComponent<Animator>();
            character.Animim.Animator = animator;

            var driver = new UnitDriverNavmeshNetworkServer();
            SetField(character.Kernel, "m_Driver", driver);
            EditModeLifecycle.Invoke(character, "Awake");

            Assert.That(character.Driver, Is.SameAs(driver));
            NavMeshAgent agent = npcObject.GetComponent<NavMeshAgent>();
            Assert.That(agent, Is.Not.Null,
                "The server driver must bind a real NavMeshAgent during startup.");
            Assert.That(agent.updatePosition, Is.True);
            Assert.That(agent.updateRotation, Is.False,
                "Facing remains owned by GC2's authoritative Facing unit.");
            Assert.That(agent.updateUpAxis, Is.False);
            CapsuleCollider capsule = npcObject.GetComponent<CapsuleCollider>();
            Assert.That(capsule, Is.Not.Null,
                "The server driver must bind its collision capsule during startup.");
            OffMeshLinkNetworkServer linkController =
                npcObject.GetComponent<OffMeshLinkNetworkServer>();
            Assert.That(linkController, Is.Not.Null,
                "The server driver must initialize its off-mesh-link authority controller.");
            CharacterController remoteController = npcObject.AddComponent<CharacterController>();
            Assert.That(remoteController.enabled, Is.True);

            // Authority must make a controller retained for observer interpolation inert.
            driver.OnDispose(character);
            driver.OnStartup(character);
            Assert.That(remoteController.enabled, Is.False,
                "NavMesh authority must not run beside the observer CharacterController.");

            driver.OnDispose(character);
            Assert.That(agent.enabled, Is.False,
                "An observer must not keep the authoritative NavMeshAgent active.");
            Assert.That(capsule.enabled, Is.False,
                "An observer must not keep the authoritative collision capsule active.");

            var remoteDriver = new UnitDriverNetworkRemote();
            remoteDriver.OnStartup(character);
            Assert.That(remoteController.enabled, Is.True,
                "The observer driver must reactivate its retained CharacterController.");
            remoteDriver.OnDispose(character);

            driver.OnStartup(character);
            Assert.That(driver.BoundAgent, Is.SameAs(agent),
                "Authority regain must reuse the authored/runtime NavMeshAgent.");
            Assert.That(npcObject.GetComponent<CapsuleCollider>(), Is.SameAs(capsule),
                "Authority regain must reuse the authored/runtime collision capsule.");
            Assert.That(agent.enabled, Is.True);
            Assert.That(capsule.enabled, Is.True);
            Assert.That(remoteController.enabled, Is.False,
                "Authority regain must disable the observer controller again.");
            Assert.That(npcObject.GetComponent<OffMeshLinkNetworkServer>(), Is.SameAs(linkController));

            Action<NetworkOffMeshLinkStart> forwarding =
                GetField<Action<NetworkOffMeshLinkStart>>(linkController, "OnLinkStartReady");
            Assert.That(forwarding, Is.Not.Null);
            Assert.That(
                forwarding.GetInvocationList().Length,
                Is.EqualTo(1),
                "Authority migration must not duplicate off-mesh-link forwarding subscriptions.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NavMeshServerDriver_RootMotionSchedulesTheFollowingIdleCleanup()
        {
            var driver = new UnitDriverNavmeshNetworkServer();
            Vector3 rootMotionDelta = new Vector3(0.15f, 0f, 0.25f);

            InvokePrivate(driver, "BeginRootMotionFrame", rootMotionDelta);

            Assert.That(GetField<bool>(driver, "m_UsingAuthoredMotion"), Is.True,
                "Root motion that starts from idle must retain an authored cleanup frame.");
            Assert.That(GetField<Vector3>(driver, "m_MoveDirection"), Is.EqualTo(rootMotionDelta));
            Assert.That(
                InvokePrivateResult<bool>(
                    driver,
                    "ShouldUpdateAuthoredMotion",
                    Character.MovementType.None),
                Is.True,
                "The first idle frame after a Skill must enter authored cleanup instead of " +
                "replaying the final root-motion delta as a legacy movement command.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NavMeshServerDriver_UnboundAgentDiscardsPerFrameMotionWarpTranslation()
        {
            var driver = new UnitDriverNavmeshNetworkServer();
            driver.AddPosition(new Vector3(1f, 0f, 2f));

            DriverAdditionalTranslation pending =
                GetField<DriverAdditionalTranslation>(driver, "m_AddTranslation");
            Assert.That(pending.HasValue, Is.True);

            InvokePrivate(driver, "DiscardDeferredTranslationWhileUnbound");

            pending = GetField<DriverAdditionalTranslation>(driver, "m_AddTranslation");
            Assert.That(pending.HasValue, Is.False,
                "Per-frame Motion Warp translation must not accumulate while NavMesh binding " +
                "is unavailable and then apply as a teleport.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NetworkDirectionalPlayerUnit_NpcUpdatePreservesAuthoredAiMotion()
        {
            GameObject npcObject = Track(new GameObject("Directional Unit NPC"));
            Character character = EditModeLifecycle.AddComponent<Character>(npcObject);
            var player = new UnitPlayerDirectionalNetwork();
            character.IsPlayer = false;
            character.Kernel.ChangePlayer(character, player);

            Vector3 authoredVelocity = new Vector3(3f, 0f, 4f);
            character.Motion.MoveToDirection(authoredVelocity, Space.World, priority: 1);
            Vector3 motionBeforePlayerUpdate = character.Motion.MoveDirection;
            Character.MovementType movementBeforePlayerUpdate =
                character.Motion.MovementType;
            Assert.That(motionBeforePlayerUpdate.sqrMagnitude, Is.GreaterThan(0f),
                "The fixture must author a nonzero GC2 AI motion command.");

            // CharacterKernel invokes Player before Motion and Driver. An NPC player unit must
            // not zero the Behavior Tree's command during that first phase.
            player.OnUpdate();

            Assert.That(character.Motion.MoveDirection, Is.EqualTo(motionBeforePlayerUpdate),
                "The inactive player-input unit must not overwrite server-authored NPC motion.");
            Assert.That(character.Motion.MovementType,
                Is.EqualTo(movementBeforePlayerUpdate));
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NetworkFacing_ServerAuthorityHonorsDirectionLayerWhileMovementIsZero()
        {
            NetworkCharacter npc = CreateNetworkCharacter(
                "Layer Facing NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: false);
            npc.SetManualNetworkId(109);

            Character character = npc.Character;
            var facing = new UnitFacingNetworkPivot();
            character.Kernel.ChangeFacing(character, facing);
            EditModeLifecycle.InitializeNetworkRole(
                npc,
                isServer: true,
                isOwner: false,
                isHost: true,
                hasAuthenticatedPlayerOwner: false);

            character.Motion.AngularSpeed = -1f;
            Assert.That(character.Motion.MoveDirection, Is.EqualTo(Vector3.zero));
            facing.SetLayerDirection(0, Vector3.right, autoDestroyOnReach: false);
            facing.OnUpdate();

            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(character.transform.eulerAngles.y, 90f)),
                Is.LessThan(0.01f),
                "An authoritative NPC must turn toward a GC2 facing layer even while stopped.");
            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(facing.ServerYaw, 90f)),
                Is.LessThan(0.01f),
                "The replicated authoritative yaw must be captured from the resolved GC2 layer.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NetworkFacing_ConnectedLocalClientRequestsDirectionLayerWhileMovementIsZero()
        {
            NetworkCharacter player = CreateNetworkCharacter(
                "Connected Layer Facing Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: false);
            player.SetManualNetworkId(110);

            Character character = player.Character;
            var facing = new UnitFacingNetworkPivot();
            character.Kernel.ChangeFacing(character, facing);
            EditModeLifecycle.InitializeNetworkRole(
                player,
                isServer: false,
                isOwner: true,
                isHost: false,
                hasAuthenticatedPlayerOwner: true);

            character.Motion.AngularSpeed = -1f;
            Assert.That(character.Motion.MoveDirection, Is.EqualTo(Vector3.zero));
            facing.SetLayerDirection(0, Vector3.right, autoDestroyOnReach: false);
            facing.OnUpdate();

            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(GetField<float>(facing, "m_LastSentYaw"), 90f)),
                Is.LessThan(0.01f),
                "The connected owner must request the layer yaw rather than zero movement yaw.");
            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(facing.ServerYaw, 90f)),
                Is.LessThan(0.01f),
                "GC2's negative AngularSpeed convention must validate the requested yaw " +
                "immediately instead of clamping with reversed bounds.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void NetworkFacing_ConnectedOwnerRequestsRawLayerYawBeforeAuthoritySmoothing()
        {
            NetworkCharacter player = CreateNetworkCharacter(
                "Finite Speed Layer Facing Player",
                NetworkCharacterActorType.PlayerOwned,
                authoredIsPlayer: false);
            player.SetManualNetworkId(111);

            Character character = player.Character;
            var facing = new UnitFacingNetworkPivot();
            character.Kernel.ChangeFacing(character, facing);
            EditModeLifecycle.InitializeNetworkRole(
                player,
                isServer: false,
                isOwner: true,
                isHost: false,
                hasAuthenticatedPlayerOwner: true);

            character.Motion.AngularSpeed = 180f;
            facing.SetLayerDirection(0, Vector3.right, autoDestroyOnReach: false);
            facing.OnUpdate();

            Assert.That(
                Mathf.Abs(Mathf.DeltaAngle(GetField<float>(facing, "m_LastSentYaw"), 90f)),
                Is.LessThan(0.01f),
                "The owner must request the raw layer target. Sending GC2's already-smoothed " +
                "Transform yaw would make authority clamp and observer interpolation smooth it " +
                "again.");
        }

        [Test]
        public void BotSlot_HandoffIsTransformOnly_Idempotent_AndRollsBack()
        {
            GameObject coordinatorObject = Track(new GameObject("Bot Slots"));
            TestBotCoordinator coordinator = coordinatorObject.AddComponent<TestBotCoordinator>();
            SetField(coordinator, "m_Slots", new[]
            {
                CreateSlotDefinition(
                    "slot-a",
                    new Vector3(3f, 0f, 5f),
                    Quaternion.Euler(0f, 45f, 0f))
            });
            EditModeLifecycle.Invoke(coordinator, "Awake");
            coordinator.Authority = true;
            EditModeLifecycle.Invoke(coordinator, "Update");
            Assert.That(coordinator.SpawnCount, Is.EqualTo(1));

            Assert.That(coordinator.TryReserveSlotForHuman(4, out var reservation), Is.True);
            Assert.That(reservation.Position, Is.EqualTo(new Vector3(3f, 0f, 5f)));
            Assert.That(coordinator.TryReserveSlotForHuman(4, out _), Is.False,
                "Duplicate callbacks must not reserve twice.");

            GameObject human = Track(new GameObject("Human"));
            human.transform.SetPositionAndRotation(reservation.Position, reservation.Rotation);
            Assert.That(coordinator.CommitHumanReservation(reservation, human), Is.True);
            Assert.That(coordinator.TryReserveSlotForHuman(5, out _), Is.False,
                "Humans beyond configured slots must overflow to normal spawn points.");

            Vector3 disconnectPosition = new Vector3(9f, 0f, -2f);
            Assert.That(coordinator.ReplaceDisconnectedHumanWithBot(
                4,
                disconnectPosition,
                Quaternion.identity), Is.True);
            Assert.That(coordinator.CurrentStates[0].OccupantType,
                Is.EqualTo(NetworkBotSlotOccupantType.Bot));
            Assert.That(coordinator.CurrentStates[0].Position, Is.EqualTo(disconnectPosition));

            Assert.That(coordinator.TryReserveSlotForHuman(6, out var rollback), Is.True);
            Assert.That(coordinator.RollbackHumanReservation(rollback), Is.True);
            Assert.That(coordinator.CurrentStates[0].OccupantType,
                Is.EqualTo(NetworkBotSlotOccupantType.Bot));
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void TargetSelector_ChoosesNearestAuthenticatedLivingPlayer_AndRetargets()
        {
            GameObject bridgeObject = Track(new GameObject("Target Bridge"));
            TestBridge bridge = bridgeObject.AddComponent<TestBridge>();
            EditModeLifecycle.Invoke(bridge, "Awake");

            NetworkCharacter npc = CreateNetworkCharacter(
                "Targeting NPC", NetworkCharacterActorType.NPC, false);
            npc.SetManualNetworkId(100);
            EditModeLifecycle.InitializeNetworkRole(npc, true, false, true, false);
            bridge.RegisterCharacter(npc);

            NetworkCharacter near = CreateNetworkCharacter(
                "Near Player", NetworkCharacterActorType.PlayerOwned, false);
            near.SetManualNetworkId(101);
            near.transform.position = Vector3.right * 2f;
            EditModeLifecycle.InitializeNetworkRole(near, true, false, true, true);
            bridge.RegisterCharacter(near);

            NetworkCharacter far = CreateNetworkCharacter(
                "Far Player", NetworkCharacterActorType.PlayerOwned, false);
            far.SetManualNetworkId(102);
            far.transform.position = Vector3.right * 5f;
            EditModeLifecycle.InitializeNetworkRole(far, true, false, true, true);
            bridge.RegisterCharacter(far);

            NetworkCharacter unauthenticated = CreateNetworkCharacter(
                "Spoofed Player", NetworkCharacterActorType.PlayerOwned, false);
            unauthenticated.SetManualNetworkId(103);
            unauthenticated.transform.position = Vector3.right;
            EditModeLifecycle.InitializeNetworkRole(
                unauthenticated, true, false, true, false);
            bridge.RegisterCharacter(unauthenticated);

            NetworkNpcTargetSelector selector =
                npc.gameObject.AddComponent<NetworkNpcTargetSelector>();
            SetField(selector, "m_FollowSelectedTarget", false);
            EditModeLifecycle.Invoke(selector, "Awake");
            EditModeLifecycle.Invoke(selector, "OnEnable");
            selector.RefreshTarget();
            Assert.That(selector.SelectedTarget, Is.SameAs(near));

            selector.SetTargetLocked(true);
            near.transform.position = Vector3.right * 20f;
            far.transform.position = Vector3.right;
            selector.RefreshTarget();
            Assert.That(selector.SelectedTarget, Is.SameAs(near),
                "A valid locked combat target must not be replaced by a newly nearer player.");

            // The direct helper is supplied by the Core authority patch and is not available
            // until after the isolated project's first compile. This selector test only needs a
            // dead-state fixture, so set the backing field without introducing a bootstrap-time
            // dependency on patched third-party source.
            SetField(near.Character, "m_IsDead", true);
            selector.RefreshTarget();
            Assert.That(selector.SelectedTarget, Is.SameAs(far),
                "A dead locked target must be released and deterministically retargeted.");

            bridge.UnregisterCharacter(far);
            far.gameObject.SetActive(false);
            selector.RefreshTarget();
            Assert.That(selector.SelectedTarget, Is.Null,
                "A disconnected/despawned locked target must not remain selected.");
        }

        [Test]
        public void ServerNpc_PreservesAuthoredDriver_SamplesPose_AndRestoresAfterMigration()
        {
            GameObject actorObject = Track(new GameObject("Migrating NPC"));
            Character character = EditModeLifecycle.AddComponent<Character>(actorObject);
            character.IsPlayer = false;
            object authoredDriver = character.Driver;

            NetworkCharacter npc = actorObject.AddComponent<NetworkCharacter>();
            SetField(npc, "m_ActorType", NetworkCharacterActorType.NPC);
            SetField(npc, "m_UseNetworkIK", false);
            SetField(npc, "m_UseNetworkMotion", false);
            SetField(npc, "m_UseAnimationSync", false);
            SetField(npc, "m_UseCoreNetworking", false);
            EditModeLifecycle.Invoke(npc, "Awake");
            npc.SetManualNetworkId(111);

            NetworkPositionState sampled = default;
            int samples = 0;
            npc.OnStatePayloadReady += (_, state, _) =>
            {
                sampled = state;
                samples++;
            };

            EditModeLifecycle.InitializeNetworkRole(npc, true, false, true, false);
            Assert.That(npc.ActiveDriver, Is.SameAs(authoredDriver));
            actorObject.transform.position = new Vector3(4f, 1f, -3f);
            EditModeLifecycle.Invoke(npc, "LateUpdate");
            Assert.That(samples, Is.EqualTo(1));
            Assert.That(sampled.GetPosition(), Is.EqualTo(actorObject.transform.position));

            npc.ResetNetworkRole();
            EditModeLifecycle.InitializeNetworkRole(npc, false, false, false, false);
            Assert.That(npc.ActiveDriver, Is.TypeOf<UnitDriverNetworkRemote>());

            npc.ResetNetworkRole();
            EditModeLifecycle.InitializeNetworkRole(npc, true, false, true, false);
            Assert.That(npc.ActiveDriver, Is.SameAs(authoredDriver),
                "The authored NPC driver must return when simulation authority migrates here.");
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void DedicatedServerPresentationOptimization_RestoresAuthoredEnabledStatesBeforeHostRole()
        {
            NetworkCharacter npc = CreateNetworkCharacter(
                "Server Presentation NPC",
                NetworkCharacterActorType.NPC,
                authoredIsPlayer: false);
            SetField(npc, "m_DisableVisualsOnServer", true);
            SetField(npc, "m_DisableAudioOnServer", true);

            GameObject visibleObject = Track(new GameObject("Authored Visible"));
            visibleObject.transform.SetParent(npc.transform, false);
            MeshRenderer visibleRenderer = visibleObject.AddComponent<MeshRenderer>();
            visibleRenderer.enabled = true;

            GameObject hiddenObject = Track(new GameObject("Authored Hidden"));
            hiddenObject.transform.SetParent(npc.transform, false);
            MeshRenderer hiddenRenderer = hiddenObject.AddComponent<MeshRenderer>();
            hiddenRenderer.enabled = false;

            AudioSource enabledAudio = npc.gameObject.AddComponent<AudioSource>();
            enabledAudio.enabled = true;
            AudioSource disabledAudio = npc.gameObject.AddComponent<AudioSource>();
            disabledAudio.enabled = false;

            EditModeLifecycle.InitializeNetworkRole(
                npc,
                isServer: true,
                isOwner: false,
                isHost: false,
                hasAuthenticatedPlayerOwner: false);

            Assert.That(visibleRenderer.enabled, Is.False);
            Assert.That(hiddenRenderer.enabled, Is.False);
            Assert.That(enabledAudio.enabled, Is.False);
            Assert.That(disabledAudio.enabled, Is.False);

            npc.ResetNetworkRole();
            Assert.That(visibleRenderer.enabled, Is.True);
            Assert.That(hiddenRenderer.enabled, Is.False,
                "An authored-disabled renderer must not be enabled by role restoration.");
            Assert.That(enabledAudio.enabled, Is.True);
            Assert.That(disabledAudio.enabled, Is.False,
                "An authored-disabled AudioSource must not be enabled by role restoration.");

            EditModeLifecycle.InitializeNetworkRole(
                npc,
                isServer: true,
                isOwner: false,
                isHost: true,
                hasAuthenticatedPlayerOwner: false);

            Assert.That(visibleRenderer.enabled, Is.True,
                "A listen-host NPC must retain its visible presentation.");
            Assert.That(hiddenRenderer.enabled, Is.False);
            Assert.That(enabledAudio.enabled, Is.True);
            Assert.That(disabledAudio.enabled, Is.False);
        }

        [Test]
        public void BotSlot_ReplicatedMembershipSurvivesAuthorityMigrationWithoutDuplicateActor()
        {
            NetworkBotSlotDefinition definition = CreateSlotDefinition(
                "migration-slot", new Vector3(2f, 0f, 8f), Quaternion.identity);

            GameObject sourceObject = Track(new GameObject("Source Slots"));
            TestBotCoordinator source = sourceObject.AddComponent<TestBotCoordinator>();
            SetField(source, "m_Slots", new[] { definition });
            EditModeLifecycle.Invoke(source, "Awake");
            source.Authority = true;
            EditModeLifecycle.Invoke(source, "Update");
            Assert.That(source.TryReserveSlotForHuman(12, out var reservation), Is.True);
            GameObject human = Track(new GameObject("Migrating Human"));
            human.transform.SetPositionAndRotation(reservation.Position, reservation.Rotation);
            Assert.That(source.CommitHumanReservation(reservation, human), Is.True);

            NetworkBotSlotState[] snapshot =
                new List<NetworkBotSlotState>(source.CurrentStates).ToArray();
            uint actorId = snapshot[0].ActorNetworkId;

            GameObject promotedObject = Track(new GameObject("Promoted Slots"));
            TestBotCoordinator promoted = promotedObject.AddComponent<TestBotCoordinator>();
            SetField(promoted, "m_Slots", new[] { definition });
            EditModeLifecycle.Invoke(promoted, "Awake");
            promoted.Adopt(actorId, human);
            promoted.ApplyReplicatedSnapshot(snapshot);
            promoted.Authority = true;
            EditModeLifecycle.Invoke(promoted, "Update");

            Assert.That(promoted.SpawnCount, Is.Zero,
                "Promoting a peer with a resolvable human occupant must not create a bot duplicate.");
            Assert.That(promoted.CurrentStates[0].OccupantType,
                Is.EqualTo(NetworkBotSlotOccupantType.Human));
            Assert.That(promoted.TryReserveSlotForHuman(12, out _), Is.False);
        }

        private NetworkCharacter CreateNetworkCharacter(
            string name,
            NetworkCharacterActorType actorType,
            bool authoredIsPlayer)
        {
            GameObject gameObject = Track(new GameObject(name));
            Character character = EditModeLifecycle.AddComponent<Character>(gameObject);
            character.IsPlayer = authoredIsPlayer;
            NetworkCharacter networkCharacter = gameObject.AddComponent<NetworkCharacter>();
            SetField(networkCharacter, "m_ActorType", actorType);
            EditModeLifecycle.Invoke(networkCharacter, "Awake");
            return networkCharacter;
        }

        private NetworkBotSlotDefinition CreateSlotDefinition(
            string slotId,
            Vector3 position,
            Quaternion rotation)
        {
            GameObject anchorObject = Track(new GameObject($"{slotId}-anchor"));
            anchorObject.transform.SetPositionAndRotation(position, rotation);
            GameObject prefab = Track(new GameObject($"{slotId}-prefab"));
            var definition = new NetworkBotSlotDefinition();
            SetField(definition, "m_StableSlotId", slotId);
            SetField(definition, "m_Anchor", anchorObject.transform);
            SetField(definition, "m_BotPrefab", prefab);
            return definition;
        }

        private GameObject Track(GameObject gameObject)
        {
            m_Cleanup.Add(gameObject);
            return gameObject;
        }

        private static void SetField(object target, string name, object value)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
                type = type.BaseType;
            }
            Assert.Fail($"Field '{name}' was not found on {target.GetType().FullName}.");
        }

        private static T GetField<T>(object target, string name)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) return (T)field.GetValue(target);
                type = type.BaseType;
            }

            Assert.Fail($"Field '{name}' was not found on {target.GetType().FullName}.");
            return default;
        }

        private static object InvokePrivate(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Method '{name}' was not found.");
            return method.Invoke(target, arguments);
        }

        private static T InvokePrivateResult<T>(
            object target,
            string name,
            params object[] arguments)
        {
            return (T)InvokePrivate(target, name, arguments);
        }
    }
}
