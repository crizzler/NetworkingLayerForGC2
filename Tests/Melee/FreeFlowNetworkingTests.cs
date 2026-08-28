#if GC2_MELEE
using System;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.TestUtilities;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Tests
{
    [NUnit.Framework.Category("GC2Networking.FreeFlow")]
    public sealed class FreeFlowNetworkingTests
    {
        private const BindingFlags InstanceFieldFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags StaticMethodFlags =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<UnityEngine.Object> m_Cleanup = new();

        private const string CanonicalFreeFlowWeaponPath =
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/" +
            "FreeFlow_Brawl_FreeFlowWeapon.asset";
        private const string CanonicalCounterSkillPath =
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/Skills/" +
            "FreeFlow_Brawl_Counter.asset";
        private static readonly string[] CanonicalComboSkillPaths =
        {
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/Skills/" +
            "FreeFlow_Brawl_Combo1.asset",
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/Skills/" +
            "FreeFlow_Brawl_Combo2.asset",
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/Skills/" +
            "FreeFlow_Brawl_Combo3.asset"
        };

        private sealed class TestTransportBridge : NetworkTransportBridge
        {
            public override bool IsServer => true;
            public override bool IsClient => false;
            public override bool IsHost => false;
            public override float ServerTime => 10f;

            public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
            {
            }

            public override void SendToOwner(
                uint ownerClientId,
                uint characterNetworkId,
                NetworkPositionState state,
                float serverTime)
            {
            }

            public override void Broadcast(
                uint characterNetworkId,
                NetworkPositionState state,
                float serverTime,
                uint excludeClientId = uint.MaxValue,
                NetworkRecipientFilter relevanceFilter = null)
            {
            }
        }

        private sealed class TestFreeFlowReceiver : MonoBehaviour, INetworkFreeFlowCombatReceiver
        {
            public bool TreatWeaponAsFreeFlow = true;
            public bool Attackable = true;
            public bool Counterable;
            public bool AttackToken;
            public float ScoreBonus;
            public float CounterCooldown;
            public int CounterConsumeAttempts;
            public int CounterConsumeSuccesses;
            public Skill RegisteredDirectSkill;
            public NetworkFreeFlowSkillUse RegisteredSkillUse =
                NetworkFreeFlowSkillUse.TargetedAttack;
            public Skill RegisteredAdditionalDirectSkill;
            public NetworkFreeFlowSkillUse RegisteredAdditionalSkillUse =
                NetworkFreeFlowSkillUse.TargetedAttack;
            public int SimulationSetCalls;
            public bool RunLocalPlayerRuntime;
            public bool RunAuthoritativeNpcSimulation;

            public bool IsNetworkFreeFlowWeapon(MeleeWeapon weapon) =>
                TreatWeaponAsFreeFlow && weapon != null;

            public void CollectNetworkSkills(
                MeleeWeapon weapon,
                Action<Skill, NetworkFreeFlowSkillUse> registerUse,
                Action<Skill> registerDirectPlay)
            {
                if (RegisteredDirectSkill != null)
                {
                    registerUse?.Invoke(
                        RegisteredDirectSkill,
                        RegisteredSkillUse);
                    registerDirectPlay?.Invoke(RegisteredDirectSkill);
                }

                if (RegisteredAdditionalDirectSkill != null)
                {
                    registerUse?.Invoke(
                        RegisteredAdditionalDirectSkill,
                        RegisteredAdditionalSkillUse);
                    registerDirectPlay?.Invoke(RegisteredAdditionalDirectSkill);
                }
            }

            public void GetNetworkSettings(
                MeleeWeapon weapon,
                out float attackRadius,
                out float scanRadius,
                out float counterRadius,
                out float counterCooldown,
                out bool requireAttackLineOfSight,
                out bool requireCounterLineOfSight,
                out int occlusionLayerMask)
            {
                attackRadius = 3f;
                scanRadius = 8f;
                counterRadius = 4f;
                counterCooldown = CounterCooldown;
                requireAttackLineOfSight = false;
                requireCounterLineOfSight = false;
                occlusionLayerMask = Physics.DefaultRaycastLayers;
            }

            public bool TryCaptureNetworkState(
                out bool isAttackable,
                out bool isCounterable,
                out bool hasAttackToken,
                out float scoreBonus)
            {
                isAttackable = Attackable;
                isCounterable = Counterable;
                hasAttackToken = AttackToken;
                scoreBonus = ScoreBonus;
                return true;
            }

            public void ApplyReplicatedNetworkState(
                bool isAttackable,
                bool isCounterable,
                bool hasAttackToken,
                float scoreBonus,
                GameObject selectedPlayer)
            {
                Attackable = isAttackable;
                Counterable = isCounterable;
                AttackToken = hasAttackToken;
                ScoreBonus = scoreBonus;
            }

            public bool TryConsumeNetworkCounterWindow()
            {
                CounterConsumeAttempts++;
                if (!Counterable) return false;

                Counterable = false;
                CounterConsumeSuccesses++;
                return true;
            }

            public Vector3 GetNetworkAimPoint() => transform.position;

            public void SetNetworkSelectedPlayer(GameObject selectedPlayer)
            {
            }

            public void SetNetworkSimulation(
                bool runLocalPlayerRuntime,
                bool runAuthoritativeNpcSimulation)
            {
                SimulationSetCalls++;
                RunLocalPlayerRuntime = runLocalPlayerRuntime;
                RunAuthoritativeNpcSimulation = runAuthoritativeNpcSimulation;
            }
        }

        private sealed class TestBusyAttackState : TAttackState
        {
            public bool WasForceCancelled { get; private set; }

            public override MeleePhase Phase => MeleePhase.Reaction;

            public TestBusyAttackState(Attacks attacks) : base(attacks)
            { }

            public override bool TryToCancel() => false;

            public override void ForceCancel()
            {
                this.WasForceCancelled = true;
                this.Attacks.ToNone();
            }
        }

        [Test]
        public void CanonicalDirectComboSkills_EnableTheirAuthoredMotionWarpTracks()
        {
            foreach (string path in CanonicalComboSkillPaths)
            {
                Skill skill = AssetDatabase.LoadAssetAtPath<Skill>(path);
                Assert.That(skill, Is.Not.Null, $"Missing canonical combo fixture {path}.");
                Assert.That(
                    skill.Motion,
                    Is.EqualTo(MeleeMotion.MotionWarp),
                    $"{skill.name} contains target-relative motion-warp authoring and must " +
                    "enable GC2's MotionWarp mode rather than leaving that track dormant.");

                var serialized = new SerializedObject(skill);
                bool hasMotionWarpClip = HasManagedReferenceOfType(
                    serialized,
                    typeof(ClipMeleeMotionWarping));

                Assert.That(
                    hasMotionWarpClip,
                    Is.True,
                    $"{skill.name} must retain an executable GC2 " +
                    $"{nameof(ClipMeleeMotionWarping)} managed-reference clip.");
            }
        }

        [Test]
        public void CanonicalCounterSkill_EnablesItsAuthoredMotionWarpTrack()
        {
            Skill skill = AssetDatabase.LoadAssetAtPath<Skill>(CanonicalCounterSkillPath);
            Assert.That(
                skill,
                Is.Not.Null,
                $"Missing canonical counter fixture {CanonicalCounterSkillPath}.");
            Assert.That(
                skill.Motion,
                Is.EqualTo(MeleeMotion.MotionWarp),
                "The Free Flow counter must enable GC2 MotionWarp so its authored " +
                "target-relative warp track is evaluated.");

            var serialized = new SerializedObject(skill);
            bool hasMotionWarpClip = HasManagedReferenceOfType(
                serialized,
                typeof(ClipMeleeMotionWarping));

            Assert.That(
                hasMotionWarpClip,
                Is.True,
                "The Free Flow counter must retain an executable GC2 " +
                $"{nameof(ClipMeleeMotionWarping)} managed-reference clip.");
        }

        [Test]
        public void MovementLock_RepeatedStopDirectionCoalescesAndPreservesTransitions()
        {
            GameObject characterObject = Track(new GameObject("Free Flow Repeated Movement Lock"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            var motion = new UnitMotionNetworkController
            {
                IsServer = false
            };
            motion.OnStartup(character);

            var commands = new List<NetworkMotionCommand>();
            motion.OnSendCommand += commands.Add;

            for (int i = 0; i < 8; i++)
            {
                motion.StopToDirection(10);
            }

            Assert.That(
                commands.Count,
                Is.EqualTo(1),
                "A frame-held Free Flow movement lock must send only its first reliable stop");
            Assert.That(commands[0].commandType, Is.EqualTo(NetworkMotionCommandType.StopDirection));
            Assert.That(commands[0].priority, Is.EqualTo(10));

            motion.StopToDirection(11);
            motion.StopToDirection(11);

            Assert.That(
                commands.Count,
                Is.EqualTo(2),
                "Changing the stop priority must send immediately, while its duplicate coalesces");
            Assert.That(commands[1].commandType, Is.EqualTo(NetworkMotionCommandType.StopDirection));
            Assert.That(commands[1].priority, Is.EqualTo(11));

            motion.MoveToDirection(Vector3.right, Space.World, 11);

            Assert.That(
                commands.Count,
                Is.EqualTo(3),
                "The first nonzero direction after a stopped state must send immediately");
            Assert.That(commands[2].commandType, Is.EqualTo(NetworkMotionCommandType.MoveToDirection));
            Assert.That(commands[2].GetVelocity(), Is.EqualTo(Vector3.right));
            Assert.That(commands[2].priority, Is.EqualTo(11));
        }

        [Test]
        public void SemanticSmoke_AdoptsActualNpcBroadcastInsteadOfPrimedCandidate()
        {
            Type utility = typeof(NetworkFreeFlowCombatSmokeUtility);
            SetStaticField(utility, "s_LocalSemanticAttackIssued", true);
            SetStaticField(utility, "s_LocalSemanticAttackActorNetworkId", 7u);
            SetStaticField(utility, "s_LocalSemanticAttackTargetNetworkId", 2u);

            HashSet<uint> npcIds = GetStaticField<HashSet<uint>>(
                utility,
                "s_NpcNetworkIds");
            npcIds.Add(2u);
            npcIds.Add(6u);
            Dictionary<uint, float> attackTimeNpcDistances =
                GetStaticField<Dictionary<uint, float>>(
                    utility,
                    "s_LocalSemanticAttackStartDistances");
            attackTimeNpcDistances[2u] = 4f;
            attackTimeNpcDistances[6u] = 5f;

            // A valid tuple from before Attack() was invoked must not satisfy the smoke.
            InvokePrivateStatic(
                utility,
                "OnSkillExecuted",
                new NetworkSkillBroadcast
                {
                    CharacterNetworkId = 7u,
                    TargetNetworkId = 2u,
                    SkillHash = 333
                });
            SetStaticField(
                utility,
                "s_LocalSemanticAttackObservedEventStartIndex",
                GetStaticField<List<NetworkSkillBroadcast>>(
                    utility,
                    "s_ObservedSkillEvents").Count);

            InvokePrivateStatic(
                utility,
                "OnSkillExecuted",
                new NetworkSkillBroadcast
                {
                    CharacterNetworkId = 8u,
                    TargetNetworkId = 6u,
                    SkillHash = 111
                });
            InvokePrivateStatic(
                utility,
                "OnSkillExecuted",
                new NetworkSkillBroadcast
                {
                    CharacterNetworkId = 7u,
                    TargetNetworkId = 99u,
                    SkillHash = 222
                });
            InvokePrivateStatic(
                utility,
                "OnSkillExecuted",
                new NetworkSkillBroadcast
                {
                    CharacterNetworkId = 7u,
                    TargetNetworkId = 6u,
                    SkillHash = 0
                });

            // Motion Warp may intentionally hide both indicators after entering AttackRadius,
            // causing the next broad capture to lose its transient registry view. The exact
            // post-baseline broadcast must still match the explicit NPC set recorded at Attack().
            npcIds.Clear();
            InvokePrivateStatic(
                utility,
                "OnSkillExecuted",
                new NetworkSkillBroadcast
                {
                    CharacterNetworkId = 7u,
                    TargetNetworkId = 6u,
                    SkillHash = -342895613
                });

            var result = new NetworkFreeFlowCombatSmokeUtility.Result();
            NetworkFreeFlowCombatSmokeUtility.TryValidateLocalPlayerTargetedAttack(
                result,
                out _);

            Assert.That(result.semanticPlayerAttackObserverReceived, Is.True);
            Assert.That(result.observedPlayerSkillEvents, Is.GreaterThanOrEqualTo(1));
            Assert.That(result.semanticPlayerAttackActorNetworkId, Is.EqualTo(7u));
            Assert.That(
                result.semanticPlayerAttackTargetNetworkId,
                Is.EqualTo(6u),
                "The real authored Free Flow selection may differ from the primed candidate.");
            Assert.That(result.semanticPlayerAttackSkillHash, Is.EqualTo(-342895613));
        }

        private sealed class ActorFixture
        {
            public GameObject Root;
            public NetworkCharacter Character;
            public NetworkMeleeController Controller;
            public TestFreeFlowReceiver Receiver;
            public NetworkFreeFlowCombatAdapter Adapter;
        }

        [SetUp]
        public void SetUp()
        {
            ResetFreeFlowSmokeState();
            NetworkMeleeManager.ClearRegistries();
            SecurityIntegration.ClearModuleServerContexts();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
            ResetFreeFlowSmokeState();
            NetworkMeleeManager.ClearRegistries();
            SecurityIntegration.ClearModuleServerContexts();
        }

        [Test]
        public void SkillContext_UnknownFlagsAreRejectedBeforeActorLookup()
        {
            NetworkSkillRequest request = default;
            request.ActionFlags = (NetworkMeleeSkillActionFlags)0x80;

            bool accepted = ValidateSkillRequest(
                null,
                null,
                null,
                request,
                out string details);

            Assert.That(accepted, Is.False);
            StringAssert.Contains("unknown skill action flags", details);
        }

        [Test]
        public void SkillContext_FreeFlowCounterOnOrdinaryWeaponIsRejected()
        {
            ActorFixture actor = CreateUninitializedActor(101, "Ordinary Melee Actor");
            actor.Receiver.TreatWeaponAsFreeFlow = false;
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill skill = Track(ScriptableObject.CreateInstance<Skill>());

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 101,
                SkillHash = StableHashUtility.GetStableHash(skill.name),
                ComboNodeId = ComboTree.NODE_INVALID,
                ActionFlags = NetworkMeleeSkillActionFlags.FreeFlowCounter,
                ActionStateRevision = 7
            };

            bool accepted = ValidateSkillRequest(
                actor.Controller,
                weapon,
                skill,
                request,
                out string details);

            Assert.That(accepted, Is.False);
            StringAssert.Contains("non-Free-Flow weapon", details);
        }

        [Test]
        public void SkillContext_CounterOnlyDirectSkillRequiresCounterRevision()
        {
            ActorFixture actor = CreateUninitializedActor(102, "Free Flow Counter Actor");
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill counterSkill = Track(ScriptableObject.CreateInstance<Skill>());
            counterSkill.name = "Free Flow Counter Only Skill";
            int skillHash = StableHashUtility.GetStableHash(counterSkill.name);

            Dictionary<int, NetworkFreeFlowSkillUse> skillUses =
                GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                    actor.Adapter,
                    "m_SkillUses");
            skillUses[skillHash] = NetworkFreeFlowSkillUse.Counter;
            GetField<HashSet<int>>(actor.Adapter, "m_DirectSkillHashes").Add(skillHash);

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 102,
                SkillHash = skillHash,
                ComboNodeId = ComboTree.NODE_INVALID,
                ActionFlags = NetworkMeleeSkillActionFlags.None,
                ActionStateRevision = 0
            };

            bool accepted = ValidateSkillRequest(
                actor.Controller,
                weapon,
                counterSkill,
                request,
                out string details);

            Assert.That(accepted, Is.False);
            StringAssert.Contains("counter-only Free Flow Skill", details);
        }

        [Test]
        public void SkillContext_ComboCounterUsesWeaponCounterAllowList()
        {
            ActorFixture actor = CreateUninitializedActor(103, "Free Flow Combo Counter Actor");
            ActorFixture target = CreateUninitializedActor(104, "Free Flow Combo Counter NPC");
            SetField(target.Character, "m_ActorType", NetworkCharacterActorType.NPC);
            SetField(
                target.Character,
                "m_NPCMode",
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            SetField(
                target.Adapter,
                "m_CurrentState",
                new NetworkFreeFlowCombatState
                {
                    CharacterNetworkId = 104,
                    StateVersion = 3,
                    TargetNetworkId = 103,
                    Flags = NetworkFreeFlowCombatState.AttackableFlag |
                            NetworkFreeFlowCombatState.CounterableFlag
                });

            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill counterSkill = Track(ScriptableObject.CreateInstance<Skill>());
            counterSkill.name = "Free Flow Combo Counter Skill";
            int skillHash = StableHashUtility.GetStableHash(counterSkill.name);
            Dictionary<int, NetworkFreeFlowSkillUse> skillUses =
                GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                    actor.Adapter,
                    "m_SkillUses");
            skillUses[skillHash] = NetworkFreeFlowSkillUse.Counter;

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 103,
                TargetNetworkId = 104,
                SkillHash = skillHash,
                ComboNodeId = 12345,
                ActionFlags = NetworkMeleeSkillActionFlags.FreeFlowCounter,
                ActionStateRevision = 3
            };

            bool accepted = ValidateSkillRequest(
                actor.Controller,
                weapon,
                counterSkill,
                request,
                out string details);

            Assert.That(accepted, Is.True, details);
        }

        [Test]
        public void SkillContext_ClassifiedLaterComboWithoutDirectPathIsRejected()
        {
            ActorFixture actor = CreateUninitializedActor(105, "Free Flow Later Combo Actor");
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill laterComboSkill = Track(ScriptableObject.CreateInstance<Skill>());
            laterComboSkill.name = "Free Flow Later Combo Skill";
            int skillHash = StableHashUtility.GetStableHash(laterComboSkill.name);
            GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                actor.Adapter,
                "m_SkillUses")[skillHash] = NetworkFreeFlowSkillUse.TargetedAttack;

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 105,
                SkillHash = skillHash,
                ComboNodeId = ComboTree.NODE_INVALID
            };

            bool accepted = ValidateSkillRequest(
                actor.Controller,
                weapon,
                laterComboSkill,
                request,
                out string details);

            Assert.That(accepted, Is.False);
            StringAssert.Contains("authored Free Flow direct-play path", details);
        }

        [Test]
        public void SkillContext_AuthoredDirectFallbackWithoutTargetRemainsValid()
        {
            ActorFixture actor = CreateUninitializedActor(106, "Free Flow Direct Fallback Actor");
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill fallbackSkill = Track(ScriptableObject.CreateInstance<Skill>());
            fallbackSkill.name = "Free Flow Direct Fallback Skill";
            int skillHash = StableHashUtility.GetStableHash(fallbackSkill.name);
            GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                actor.Adapter,
                "m_SkillUses")[skillHash] = NetworkFreeFlowSkillUse.Fallback;
            GetField<HashSet<int>>(actor.Adapter, "m_DirectSkillHashes").Add(skillHash);

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 106,
                SkillHash = skillHash,
                ComboNodeId = ComboTree.NODE_INVALID
            };

            Assert.That(
                ValidateSkillRequest(
                    actor.Controller,
                    weapon,
                    fallbackSkill,
                    request,
                    out string details),
                Is.True,
                details);
        }

        [Test]
        public void SkillRequestTarget_DirectFallbackPreservesGc2ArgsCandidateWhenPrimaryIsCleared()
        {
            ActorFixture actor = CreateUninitializedActor(
                114,
                "Free Flow Fallback Request Actor");
            ActorFixture target = CreateAttackableNpc(
                115,
                114,
                "Free Flow Fallback Args Target");
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill fallbackSkill = Track(ScriptableObject.CreateInstance<Skill>());
            fallbackSkill.name = "Free Flow Args Fallback Skill";
            actor.Receiver.RegisteredDirectSkill = fallbackSkill;
            actor.Receiver.RegisteredSkillUse = NetworkFreeFlowSkillUse.Fallback;

            Character gc2Character = actor.Root.GetComponent<Character>();
            gc2Character.Combat.Targets.Primary = null;
            ConfigureCurrentMeleeAttack(
                actor.Controller,
                gc2Character,
                weapon,
                fallbackSkill,
                target.Root);

            uint targetNetworkId = ResolveSkillRequestTargetNetworkId(
                actor.Controller,
                new NetworkAttackState
                {
                    SkillHash = StableHashUtility.GetStableHash(fallbackSkill.name),
                    WeaponHash = weapon.Id.Hash,
                    ComboNodeId = ComboTree.NODE_INVALID,
                    Phase = (byte)MeleePhase.Anticipation
                });

            Assert.That(
                targetNetworkId,
                Is.EqualTo(115),
                "The exact Args target used by the local fallback motion warp must survive in " +
                "NetworkSkillRequest.TargetNetworkId for authority and observer replay.");
        }

        [Test]
        public void SkillRequestTarget_NonFallbackDirectSkillCannotPromoteGc2ArgsCandidate()
        {
            ActorFixture actor = CreateUninitializedActor(
                116,
                "Free Flow Non-Fallback Request Actor");
            ActorFixture target = CreateAttackableNpc(
                117,
                116,
                "Free Flow Non-Fallback Args Target");
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill targetedSkill = Track(ScriptableObject.CreateInstance<Skill>());
            targetedSkill.name = "Free Flow Targeted Direct Skill";
            actor.Receiver.RegisteredDirectSkill = targetedSkill;
            actor.Receiver.RegisteredSkillUse = NetworkFreeFlowSkillUse.TargetedAttack;

            Character gc2Character = actor.Root.GetComponent<Character>();
            gc2Character.Combat.Targets.Primary = null;
            ConfigureCurrentMeleeAttack(
                actor.Controller,
                gc2Character,
                weapon,
                targetedSkill,
                target.Root);

            uint targetNetworkId = ResolveSkillRequestTargetNetworkId(
                actor.Controller,
                new NetworkAttackState
                {
                    SkillHash = StableHashUtility.GetStableHash(targetedSkill.name),
                    WeaponHash = weapon.Id.Hash,
                    ComboNodeId = ComboTree.NODE_INVALID,
                    Phase = (byte)MeleePhase.Anticipation
                });

            Assert.That(
                targetNetworkId,
                Is.Zero,
                "Only an authored fallback classification may recover a target after Primary " +
                "was cleared; ordinary direct Skills retain the existing target policy.");
        }

        [Test]
        public void SkillContext_ValidComboNodeDoesNotRequireDirectPlayRegistration()
        {
            ActorFixture actor = CreateUninitializedActor(107, "Free Flow Combo Actor");
            ActorFixture target = CreateAttackableNpc(108, 107, "Free Flow Combo Target");
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill comboSkill = Track(ScriptableObject.CreateInstance<Skill>());
            comboSkill.name = "Free Flow Valid Combo Skill";
            int skillHash = StableHashUtility.GetStableHash(comboSkill.name);
            GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                actor.Adapter,
                "m_SkillUses")[skillHash] = NetworkFreeFlowSkillUse.TargetedAttack;

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 107,
                TargetNetworkId = 108,
                SkillHash = skillHash,
                ComboNodeId = 7001
            };

            Assert.That(
                ValidateSkillRequest(
                    actor.Controller,
                    weapon,
                    comboSkill,
                    request,
                    out string details),
                Is.True,
                details);
            Assert.That(
                GetField<HashSet<int>>(actor.Adapter, "m_DirectSkillHashes"),
                Has.No.Member(skillHash));
        }

        [Test]
        public void SkillContext_AuthoredDirectCounterRemainsValidWithFreshRevision()
        {
            ActorFixture actor = CreateUninitializedActor(109, "Free Flow Direct Counter Actor");
            CreateAttackableNpc(110, 109, "Free Flow Direct Counter Target", counterable: true);
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            Skill counterSkill = Track(ScriptableObject.CreateInstance<Skill>());
            counterSkill.name = "Free Flow Direct Counter Skill";
            int skillHash = StableHashUtility.GetStableHash(counterSkill.name);
            GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                actor.Adapter,
                "m_SkillUses")[skillHash] = NetworkFreeFlowSkillUse.Counter;
            GetField<HashSet<int>>(actor.Adapter, "m_DirectSkillHashes").Add(skillHash);

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 109,
                TargetNetworkId = 110,
                SkillHash = skillHash,
                ComboNodeId = ComboTree.NODE_INVALID,
                ActionFlags = NetworkMeleeSkillActionFlags.FreeFlowCounter,
                ActionStateRevision = 3
            };

            Assert.That(
                ValidateSkillRequest(
                    actor.Controller,
                    weapon,
                    counterSkill,
                    request,
                    out string details),
                Is.True,
                details);
        }

        [Test]
        public void CounterDecoration_SameTargetOrdinarySkillCannotInheritPendingCounter()
        {
            ActorFixture actor = CreateUninitializedActor(111, "Free Flow Pending Counter Actor");
            Skill ordinarySkill = Track(ScriptableObject.CreateInstance<Skill>());
            ordinarySkill.name = "Free Flow Ordinary Same Target Skill";
            int ordinaryHash = StableHashUtility.GetStableHash(ordinarySkill.name);
            Skill counterSkill = Track(ScriptableObject.CreateInstance<Skill>());
            counterSkill.name = "Free Flow Prepared Counter Skill";
            int counterHash = StableHashUtility.GetStableHash(counterSkill.name);
            Dictionary<int, NetworkFreeFlowSkillUse> uses =
                GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(actor.Adapter, "m_SkillUses");
            uses[ordinaryHash] = NetworkFreeFlowSkillUse.TargetedAttack;
            uses[counterHash] = NetworkFreeFlowSkillUse.Counter;

            SetPendingCounter(actor.Adapter, targetNetworkId: 112, revision: 17);
            NetworkSkillRequest ordinary = DecorateSkillRequest(
                actor.Controller,
                new NetworkSkillRequest
                {
                    ActorNetworkId = 111,
                    TargetNetworkId = 112,
                    SkillHash = ordinaryHash
                });
            Assert.That(ordinary.ActionFlags, Is.EqualTo(NetworkMeleeSkillActionFlags.None));
            Assert.That(ordinary.ActionStateRevision, Is.Zero);
            Assert.That(GetField<uint>(actor.Adapter, "m_PendingCounterTargetId"), Is.Zero,
                "An unrelated next Skill consumes/cancels the one-shot prepared-counter intent.");

            SetPendingCounter(actor.Adapter, targetNetworkId: 112, revision: 18);
            NetworkSkillRequest counter = DecorateSkillRequest(
                actor.Controller,
                new NetworkSkillRequest
                {
                    ActorNetworkId = 111,
                    TargetNetworkId = 112,
                    SkillHash = counterHash
                });
            Assert.That(
                counter.ActionFlags,
                Is.EqualTo(NetworkMeleeSkillActionFlags.FreeFlowCounter));
            Assert.That(counter.ActionStateRevision, Is.EqualTo(18));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CounterPreparation_RequiresWindowAssignedToAuthenticatedLocalPlayer(
            bool isHostAuthority)
        {
            const uint playerNetworkId = 118;
            const uint npcNetworkId = 119;
            const uint stateRevision = 23;

            ActorFixture player = CreateUninitializedActor(
                playerNetworkId,
                isHostAuthority
                    ? "Host Free Flow Counter Player"
                    : "Connected Free Flow Counter Player");
            SetField(player.Character, "m_ActorType", NetworkCharacterActorType.PlayerOwned);
            EditModeLifecycle.InitializeNetworkRole(
                player.Character,
                isServer: isHostAuthority,
                isOwner: true,
                isHost: isHostAuthority,
                hasAuthenticatedPlayerOwner: true);

            ActorFixture npc = CreateUninitializedActor(
                npcNetworkId,
                isHostAuthority
                    ? "Host Authority Counter NPC"
                    : "Observed Counter NPC");
            SetField(npc.Character, "m_ActorType", NetworkCharacterActorType.NPC);
            SetField(
                npc.Character,
                "m_NPCMode",
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            EditModeLifecycle.InitializeNetworkRole(
                npc.Character,
                isServer: isHostAuthority,
                isOwner: false,
                isHost: isHostAuthority,
                hasAuthenticatedPlayerOwner: false);

            SetField(
                npc.Adapter,
                "m_CurrentState",
                new NetworkFreeFlowCombatState
                {
                    CharacterNetworkId = npcNetworkId,
                    StateVersion = stateRevision,
                    TargetNetworkId = playerNetworkId + 1,
                    Flags = NetworkFreeFlowCombatState.AttackableFlag |
                            NetworkFreeFlowCombatState.CounterableFlag
                });

            Assert.That(
                player.Adapter.IsCounterAvailableToLocalPlayer(npc.Root),
                Is.False,
                "Eligibility must reject a counter window assigned to a different player ID.");
            SetPendingCounter(player.Adapter, targetNetworkId: 777, revision: 99);
            Assert.That(
                player.Adapter.PrepareFreeFlowCounter(npc.Root, forcedByShield: false),
                Is.EqualTo(NetworkFreeFlowCounterPreparation.Rejected),
                "A local owner must not prepare another player's assigned counter window.");
            Assert.That(
                GetField<uint>(player.Adapter, "m_PendingCounterTargetId"),
                Is.Zero,
                "Rejected preparation must not retain a counter intent.");
            Assert.That(
                GetField<uint>(player.Adapter, "m_PendingCounterRevision"),
                Is.Zero,
                "Rejected preparation must clear the stale authoritative state revision.");
            Assert.That(
                GetField<float>(player.Adapter, "m_PendingCounterExpiresAt"),
                Is.Zero,
                "Rejected preparation must clear the stale counter-intent lifetime.");

            SetField(
                npc.Adapter,
                "m_CurrentState",
                new NetworkFreeFlowCombatState
                {
                    CharacterNetworkId = npcNetworkId,
                    StateVersion = stateRevision,
                    TargetNetworkId = playerNetworkId,
                    Flags = NetworkFreeFlowCombatState.AttackableFlag |
                            NetworkFreeFlowCombatState.CounterableFlag
                });

            Assert.That(
                player.Adapter.IsCounterAvailableToLocalPlayer(npc.Root),
                Is.True,
                "Eligibility must accept the exact authenticated local player assignment.");
            Assert.That(
                player.Adapter.PrepareFreeFlowCounter(npc.Root, forcedByShield: false),
                Is.EqualTo(NetworkFreeFlowCounterPreparation.Prepared),
                "The assigned authenticated local player must be able to prepare the window.");
            Assert.That(
                GetField<uint>(player.Adapter, "m_PendingCounterTargetId"),
                Is.EqualTo(npcNetworkId));
            Assert.That(
                GetField<uint>(player.Adapter, "m_PendingCounterRevision"),
                Is.EqualTo(stateRevision));
        }

        [Test]
        public void CanonicalSkillCollection_RoleScopesEnemyGraphDirectSkills()
        {
            MeleeWeapon weapon = AssetDatabase.LoadAssetAtPath<MeleeWeapon>(
                CanonicalFreeFlowWeaponPath);
            Assert.That(weapon, Is.Not.Null, "Missing canonical Free Flow weapon fixture.");

            Type integrationType = Type.GetType(
                "Arawn.FreeFlowCombat.FreeFlowNetworkIntegration, Assembly-CSharp");
            Assert.That(integrationType, Is.Not.Null,
                "Free Flow networking integration is not compiled into Assembly-CSharp.");
            MethodInfo collect = integrationType.GetMethod(
                "CollectNetworkSkills",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.That(collect, Is.Not.Null);

            Component playerIntegration = CreateIntegrationActor(
                integrationType,
                NetworkCharacterActorType.PlayerOwned,
                "Canonical Free Flow Player Collection");
            Component npcIntegration = CreateIntegrationActor(
                integrationType,
                NetworkCharacterActorType.NPC,
                "Canonical Free Flow NPC Collection");

            CollectSkills(
                collect,
                playerIntegration,
                weapon,
                out Dictionary<Skill, NetworkFreeFlowSkillUse> playerUses,
                out HashSet<Skill> playerDirect);
            CollectSkills(
                collect,
                npcIntegration,
                weapon,
                out Dictionary<Skill, NetworkFreeFlowSkillUse> npcUses,
                out HashSet<Skill> npcDirect);

            foreach (string path in CanonicalComboSkillPaths)
            {
                Skill comboSkill = AssetDatabase.LoadAssetAtPath<Skill>(path);
                Assert.That(comboSkill, Is.Not.Null, $"Missing canonical combo fixture {path}.");
                Assert.That(playerUses.Keys, Has.Member(comboSkill),
                    "The complete map must retain every valid Combo node Skill.");
                Assert.That(playerDirect, Has.No.Member(comboSkill),
                    "A PlayerOwned actor must not gain direct access to later Combo Skills.");
                Assert.That(npcUses.Keys, Has.Member(comboSkill));
                Assert.That(npcDirect, Has.Member(comboSkill),
                    "The authoritative NPC behavior graph directly plays all canonical attacks.");
            }
        }

        [Test]
        public void ObserverRuntime_DisabledDirectEntryPointsCannotStartSkillsOrComboInput()
        {
            Type runtimeType = Type.GetType(
                "Arawn.FreeFlowCombat.FreeFlowCombatRuntime, Assembly-CSharp");
            Assert.That(runtimeType, Is.Not.Null,
                "Free Flow runtime is not compiled into Assembly-CSharp.");

            GameObject root = Track(new GameObject("Free Flow Observer Runtime"));
            EditModeLifecycle.AddComponent<Character>(root);
            Component runtime = EditModeLifecycle.AddComponent(root, runtimeType);
            ((Behaviour)runtime).enabled = false;

            Skill skill = Track(ScriptableObject.CreateInstance<Skill>());
            MethodInfo playSkill = runtimeType.GetMethod(
                "PlaySkill",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Skill), typeof(GameObject) },
                null);
            MethodInfo inputExecute = runtimeType.GetMethod(
                "InputExecute",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(MeleeKey) },
                null);
            Assert.That(playSkill, Is.Not.Null);
            Assert.That(inputExecute, Is.Not.Null);
            Assert.That((bool)playSkill.Invoke(runtime, new object[] { skill, root }), Is.False);
            Assert.That((bool)inputExecute.Invoke(runtime, new object[] { MeleeKey.A }), Is.False);
        }

        [Test]
        public void NetworkIntegration_RetainsPreviouslyVerifiedProcessorAcrossUnequipOrdering()
        {
            Type integrationType = Type.GetType(
                "Arawn.FreeFlowCombat.FreeFlowNetworkIntegration, Assembly-CSharp");
            Type agentType = Type.GetType(
                "Arawn.FreeFlowCombat.AI.FreeFlowEnemyAgent, Assembly-CSharp");
            Type processorType = Type.GetType(
                "GameCreator.Runtime.Behavior.Processor, GameCreator.Runtime.Behavior");
            Assert.That(integrationType, Is.Not.Null);
            Assert.That(agentType, Is.Not.Null);
            Assert.That(processorType, Is.Not.Null);

            GameObject root = Track(new GameObject("Free Flow Processor Migration"));
            EditModeLifecycle.AddComponent<Character>(root);
            EditModeLifecycle.AddComponent(root, agentType);
            Component processor = EditModeLifecycle.AddComponent(root, processorType);
            Component integration = EditModeLifecycle.AddComponent(root, integrationType);

            // Simulate an already verified Free Flow processor followed by an equipment teardown
            // that happens before the observer role disables simulation.
            SetField(integration, "m_Processor", processor);
            InvokePrivate(integration, "RefreshReferences");

            Assert.That(
                GetField<Component>(integration, "m_Processor"),
                Is.SameAs(processor),
                "Unequip ordering must not lose the verified Processor before it can be disabled.");
        }

        [Test]
        public void RuntimeReadyNotification_ImmediatelyBindsLateReceiverAndAppliesObserverPolicy()
        {
            GameObject root = Track(new GameObject("Free Flow Late Receiver Observer"));
            EditModeLifecycle.AddComponent<Character>(root);
            NetworkCharacter character = EditModeLifecycle.AddComponent<NetworkCharacter>(root);
            SetField(character, "m_ActorType", NetworkCharacterActorType.NPC);
            SetField(
                character,
                "m_NPCMode",
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);

            NetworkMeleeController controller =
                EditModeLifecycle.AddComponent<NetworkMeleeController>(root);
            controller.Initialize(false, false);
            NetworkFreeFlowCombatAdapter adapter =
                EditModeLifecycle.AddComponent<NetworkFreeFlowCombatAdapter>(root);

            TestFreeFlowReceiver receiver = root.AddComponent<TestFreeFlowReceiver>();
            receiver.RunLocalPlayerRuntime = true;
            receiver.RunAuthoritativeNpcSimulation = true;

            adapter.NotifyFreeFlowRuntimeReady();

            Assert.That(adapter.HasReceiver, Is.True);
            Assert.That(receiver.SimulationSetCalls, Is.EqualTo(1),
                "A receiver created during async weapon equip must receive its policy without " +
                "waiting for the adapter's next Update.");
            Assert.That(receiver.RunLocalPlayerRuntime, Is.False);
            Assert.That(receiver.RunAuthoritativeNpcSimulation, Is.False,
                "An unresolved/observer NPC must never run its newly created AI components.");
        }

        [Test]
        public void SkillRegistration_RoleResetClearsRoleScopedDirectAllowList()
        {
            ActorFixture actor = CreateUninitializedActor(113, "Free Flow Role Reset Actor");
            GetField<Dictionary<int, NetworkFreeFlowSkillUse>>(
                actor.Adapter,
                "m_SkillUses")[731] = NetworkFreeFlowSkillUse.TargetedAttack;
            GetField<HashSet<int>>(actor.Adapter, "m_DirectSkillHashes").Add(731);

            InvokePrivate(actor.Adapter, "OnRoleReset");

            Assert.That(actor.Adapter.RegisteredSkillCount, Is.Zero);
            Assert.That(actor.Adapter.RegisteredDirectSkillCount, Is.Zero);
        }

        [Test]
        public void CounterCommit_StaleAndReplayedRevisionConsumeWindowExactlyOnce()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture player = CreateAuthoritativeActor(
                manager,
                bridge,
                201,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: true,
                hasAuthenticatedPlayerOwner: true,
                "Counter Player");
            ActorFixture npc = CreateAuthoritativeActor(
                manager,
                bridge,
                202,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Counter NPC");

            player.Receiver.CounterCooldown = 0f;
            npc.Receiver.Attackable = true;
            npc.Receiver.Counterable = true;
            SetField(
                npc.Adapter,
                "m_CurrentState",
                new NetworkFreeFlowCombatState
                {
                    CharacterNetworkId = 202,
                    StateVersion = 9,
                    TargetNetworkId = 201,
                    Flags = NetworkFreeFlowCombatState.AttackableFlag |
                            NetworkFreeFlowCombatState.CounterableFlag
                });

            var request = new NetworkSkillRequest
            {
                ActorNetworkId = 201,
                TargetNetworkId = 202,
                ActionFlags = NetworkMeleeSkillActionFlags.FreeFlowCounter,
                ActionStateRevision = 8
            };

            Assert.That(
                CommitSkillRequest(player.Controller, request, out string staleDetails),
                Is.False);
            StringAssert.Contains("stale", staleDetails);
            Assert.That(npc.Receiver.CounterConsumeAttempts, Is.Zero);

            request.ActionStateRevision = 9;
            Assert.That(
                CommitSkillRequest(player.Controller, request, out string acceptedDetails),
                Is.True,
                acceptedDetails);
            Assert.That(npc.Receiver.CounterConsumeSuccesses, Is.EqualTo(1));

            Assert.That(
                CommitSkillRequest(player.Controller, request, out string replayDetails),
                Is.False);
            StringAssert.Contains("stale", replayDetails);
            Assert.That(npc.Receiver.CounterConsumeAttempts, Is.EqualTo(1));
            Assert.That(npc.Receiver.CounterConsumeSuccesses, Is.EqualTo(1));
            Assert.That(npc.Adapter.CurrentState.StateVersion, Is.EqualTo(10));
            Assert.That(npc.Adapter.CurrentState.IsCounterable, Is.False);
        }

        [Test]
        public void BusyRemoteOwner_OnlyFreshAssignedFreeFlowCounterMayInterrupt()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture player = CreateAuthoritativeActor(
                manager,
                bridge,
                211,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: false,
                hasAuthenticatedPlayerOwner: true,
                "Busy Remote Free Flow Player");
            ActorFixture npc = CreateAuthoritativeActor(
                manager,
                bridge,
                212,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Assigned Counter NPC");

            player.Receiver.CounterCooldown = 0f;
            npc.Receiver.Attackable = true;
            npc.Receiver.Counterable = true;
            SetField(
                npc.Adapter,
                "m_CurrentState",
                new NetworkFreeFlowCombatState
                {
                    CharacterNetworkId = 212,
                    StateVersion = 31,
                    TargetNetworkId = 211,
                    Flags = NetworkFreeFlowCombatState.AttackableFlag |
                            NetworkFreeFlowCombatState.CounterableFlag
                });

            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            weapon.name = "Busy Remote Free Flow Weapon";
            Skill ordinarySkill = Track(ScriptableObject.CreateInstance<Skill>());
            ordinarySkill.name = "Busy Remote Ordinary Skill";
            Skill counterSkill = Track(ScriptableObject.CreateInstance<Skill>());
            counterSkill.name = "Busy Remote Counter Skill";
            player.Receiver.RegisteredDirectSkill = ordinarySkill;
            player.Receiver.RegisteredSkillUse = NetworkFreeFlowSkillUse.TargetedAttack;
            player.Receiver.RegisteredAdditionalDirectSkill = counterSkill;
            player.Receiver.RegisteredAdditionalSkillUse = NetworkFreeFlowSkillUse.Counter;
            NetworkMeleeManager.RegisterMeleeWeapon(weapon);
            NetworkMeleeManager.RegisterSkill(ordinarySkill);
            NetworkMeleeManager.RegisterSkill(counterSkill);

            Character playerCharacter = player.Character.Character;
            MeleeStance stance = ConfigureCurrentMeleeAttack(
                player.Controller,
                playerCharacter,
                weapon,
                ordinarySkill,
                npc.Root);
            Attacks attacks = GetField<Attacks>(stance, "m_Attacks");
            var busyAttack = new TestBusyAttackState(attacks);
            SetField(
                attacks,
                "<Current>k__BackingField",
                busyAttack);
            playerCharacter.Busy.SetBusy();
            Assert.That(playerCharacter.Busy.IsBusy, Is.True);
            Assert.That(stance.CurrentPhase, Is.EqualTo(MeleePhase.Reaction));

            NetworkSkillRequest staleCounter = new NetworkSkillRequest
            {
                RequestId = 1,
                ActorNetworkId = 211,
                CorrelationId = NetworkCorrelation.Compose(211, 1),
                TargetNetworkId = 212,
                SkillHash = StableHashUtility.GetStableHash(counterSkill.name),
                WeaponHash = weapon.Id.Hash,
                ComboNodeId = ComboTree.NODE_INVALID,
                ActionFlags = NetworkMeleeSkillActionFlags.FreeFlowCounter,
                ActionStateRevision = 30
            };
            NetworkSkillResponse staleResponse = player.Controller.ProcessSkillRequest(
                staleCounter,
                clientNetworkId: 77);
            Assert.That(staleResponse.Validated, Is.False);
            Assert.That(
                staleResponse.RejectionReason,
                Is.EqualTo(SkillRejectionReason.InvalidActionContext),
                "A counter flag must not bypass Busy without the exact authority revision.");
            Assert.That(npc.Receiver.CounterConsumeAttempts, Is.Zero);
            Assert.That(stance.CurrentPhase, Is.EqualTo(MeleePhase.Reaction));
            Assert.That(busyAttack.WasForceCancelled, Is.False);

            NetworkSkillRequest ordinaryRequest = staleCounter;
            ordinaryRequest.RequestId = 2;
            ordinaryRequest.CorrelationId = NetworkCorrelation.Compose(211, 2);
            ordinaryRequest.SkillHash = StableHashUtility.GetStableHash(ordinarySkill.name);
            ordinaryRequest.ActionFlags = NetworkMeleeSkillActionFlags.None;
            ordinaryRequest.ActionStateRevision = 0;
            NetworkSkillResponse ordinaryResponse = player.Controller.ProcessSkillRequest(
                ordinaryRequest,
                clientNetworkId: 77);
            Assert.That(ordinaryResponse.Validated, Is.False);
            Assert.That(
                ordinaryResponse.RejectionReason,
                Is.EqualTo(SkillRejectionReason.CharacterBusy),
                "The counter interrupt must not widen Busy bypasses for ordinary direct Skills.");
            Assert.That(npc.Receiver.CounterConsumeAttempts, Is.Zero);
            Assert.That(stance.CurrentPhase, Is.EqualTo(MeleePhase.Reaction));
            Assert.That(busyAttack.WasForceCancelled, Is.False);

            NetworkSkillRequest freshCounter = staleCounter;
            freshCounter.RequestId = 3;
            freshCounter.CorrelationId = NetworkCorrelation.Compose(211, 3);
            freshCounter.ActionStateRevision = 31;
            NetworkSkillResponse freshResponse = player.Controller.ProcessSkillRequest(
                freshCounter,
                clientNetworkId: 77);

            Assert.That(freshResponse.Validated, Is.True);
            Assert.That(freshResponse.RejectionReason, Is.EqualTo(SkillRejectionReason.None));
            Assert.That(npc.Receiver.CounterConsumeAttempts, Is.EqualTo(1));
            Assert.That(npc.Receiver.CounterConsumeSuccesses, Is.EqualTo(1));
            Assert.That(npc.Adapter.CurrentState.IsCounterable, Is.False);
            Assert.That(
                busyAttack.WasForceCancelled,
                Is.True,
                "Authority must cancel the old server replica only after consuming the exact counter revision.");
            Assert.That(
                GetField<NetworkAttackState>(player.Controller, "m_LastAttackState").SkillHash,
                Is.EqualTo(freshCounter.SkillHash),
                "The committed counter must replace the busy server replica's prior Skill.");
        }

        [Test]
        public void AuthoritativeState_RequiresUnownedServerNpcAndDeduplicatesRevision()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture npc = CreateAuthoritativeActor(
                manager,
                bridge,
                301,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Trusted Free Flow NPC");

            int broadcasts = 0;
            manager.BroadcastFreeFlowStateToAllClients += _ => broadcasts++;

            NetworkFreeFlowCombatState state = new NetworkFreeFlowCombatState
            {
                CharacterNetworkId = 301,
                StateVersion = 0,
                Flags = NetworkFreeFlowCombatState.AttackableFlag
            };
            Assert.That(
                manager.PublishAuthoritativeFreeFlowState(npc.Adapter, state),
                Is.False,
                "Revision zero is the unsynchronized sentinel and cannot become authority state.");

            state.StateVersion = 3;
            Assert.That(manager.PublishAuthoritativeFreeFlowState(npc.Adapter, state), Is.True);
            Assert.That(manager.PublishAuthoritativeFreeFlowState(npc.Adapter, state), Is.True);

            NetworkFreeFlowCombatState stale = state;
            stale.StateVersion = 2;
            Assert.That(manager.PublishAuthoritativeFreeFlowState(npc.Adapter, stale), Is.False);
            Assert.That(broadcasts, Is.EqualTo(1));

            InjectTransportOwner(bridge, 301, 77);
            state.StateVersion = 4;
            Assert.That(manager.PublishAuthoritativeFreeFlowState(npc.Adapter, state), Is.False);
            Assert.That(broadcasts, Is.EqualTo(1));
        }

        [Test]
        public void AttackLease_PlayerFreeFlowAllowsMultipleServerNpcsButRejectsPlayerRedirect()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture attacker = CreateAuthoritativeActor(
                manager,
                bridge,
                351,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: true,
                hasAuthenticatedPlayerOwner: true,
                "Free Flow Player Attacker");
            CreateAuthoritativeActor(
                manager,
                bridge,
                352,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Free Flow NPC Target A");
            CreateAuthoritativeActor(
                manager,
                bridge,
                353,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Free Flow NPC Target B");
            CreateAuthoritativeActor(
                manager,
                bridge,
                354,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: false,
                hasAuthenticatedPlayerOwner: true,
                "Player Redirect Target");

            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            weapon.name = "Free Flow Lease Weapon";
            NetworkMeleeManager.RegisterMeleeWeapon(weapon);

            var skillRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 351,
                CorrelationId = NetworkCorrelation.Compose(351, 1),
                TargetNetworkId = 352,
                SkillHash = 731,
                WeaponHash = weapon.Id.Hash,
                ComboNodeId = ComboTree.NODE_INVALID
            };
            InvokePrivate(attacker.Controller, "RecordAuthoritativeAttackLease", skillRequest, 10f);

            var hit = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 351,
                AttackerNetworkId = 351,
                TargetNetworkId = 352,
                AttackCorrelationId = skillRequest.CorrelationId,
                SkillHash = skillRequest.SkillHash,
                WeaponHash = skillRequest.WeaponHash,
                ComboNodeId = skillRequest.ComboNodeId
            };

            Assert.That(
                EvaluateAttackAuthorization(attacker.Controller, hit, 10.1f, true).status,
                Is.EqualTo("Authorized"));
            hit.TargetNetworkId = 353;
            Assert.That(
                EvaluateAttackAuthorization(attacker.Controller, hit, 10.2f, true).status,
                Is.EqualTo("Authorized"),
                "The NPC-only policy must preserve authored multi-target sweeps.");

            hit.TargetNetworkId = 354;
            (string redirectedStatus, MeleeHitRejectionReason redirectedReason) =
                EvaluateAttackAuthorization(attacker.Controller, hit, 10.3f, true);
            Assert.That(redirectedStatus, Is.EqualTo("Rejected"));
            Assert.That(redirectedReason, Is.EqualTo(MeleeHitRejectionReason.CheatSuspected));

            attacker.Receiver.TreatWeaponAsFreeFlow = false;
            skillRequest.CorrelationId = NetworkCorrelation.Compose(351, 2);
            skillRequest.TargetNetworkId = 354;
            InvokePrivate(attacker.Controller, "RecordAuthoritativeAttackLease", skillRequest, 10.4f);
            hit.AttackCorrelationId = skillRequest.CorrelationId;
            Assert.That(
                EvaluateAttackAuthorization(attacker.Controller, hit, 10.5f, true).status,
                Is.EqualTo("Authorized"),
                "Ordinary Melee/PvP leases must remain unrestricted by Free Flow policy.");
        }

        [Test]
        public void AttackLease_ServerNpcFreeFlowRemainsAuthenticatedPlayerOnly()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture attacker = CreateAuthoritativeActor(
                manager,
                bridge,
                361,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Free Flow NPC Attacker");
            CreateAuthoritativeActor(
                manager,
                bridge,
                362,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: false,
                hasAuthenticatedPlayerOwner: true,
                "Authenticated Player Target");
            CreateAuthoritativeActor(
                manager,
                bridge,
                363,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Invalid NPC Redirect Target");

            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            weapon.name = "Free Flow NPC Lease Weapon";
            NetworkMeleeManager.RegisterMeleeWeapon(weapon);
            var skillRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 361,
                CorrelationId = NetworkCorrelation.Compose(361, 1),
                TargetNetworkId = 362,
                SkillHash = 741,
                WeaponHash = weapon.Id.Hash,
                ComboNodeId = ComboTree.NODE_INVALID
            };
            InvokePrivate(attacker.Controller, "RecordAuthoritativeAttackLease", skillRequest, 20f);

            var hit = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 361,
                AttackerNetworkId = 361,
                TargetNetworkId = 362,
                AttackCorrelationId = skillRequest.CorrelationId,
                SkillHash = skillRequest.SkillHash,
                WeaponHash = skillRequest.WeaponHash,
                ComboNodeId = skillRequest.ComboNodeId
            };
            Assert.That(
                EvaluateAttackAuthorization(attacker.Controller, hit, 20.1f, true).status,
                Is.EqualTo("Authorized"));

            hit.TargetNetworkId = 363;
            (string redirectedStatus, MeleeHitRejectionReason redirectedReason) =
                EvaluateAttackAuthorization(attacker.Controller, hit, 20.2f, true);
            Assert.That(redirectedStatus, Is.EqualTo("Rejected"));
            Assert.That(redirectedReason, Is.EqualTo(MeleeHitRejectionReason.CheatSuspected));
        }

        [Test]
        public void TrustedNpcSkillPathRejectsPlayerActorAndClientOwnedNpcBeforeAssetLookup()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture player = CreateAuthoritativeActor(
                manager,
                bridge,
                401,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: true,
                hasAuthenticatedPlayerOwner: true,
                "Forged Trusted Player");
            ActorFixture npc = CreateAuthoritativeActor(
                manager,
                bridge,
                402,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Client-Owned NPC");
            InjectTransportOwner(bridge, 402, 88);

            var playerRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 401,
                CorrelationId = NetworkCorrelation.Compose(401, 1)
            };
            var npcRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 402,
                CorrelationId = NetworkCorrelation.Compose(402, 1)
            };

            Assert.That(
                manager.TryPublishTrustedServerSkill(player.Controller, playerRequest),
                Is.False);
            Assert.That(
                manager.TryPublishTrustedServerSkill(npc.Controller, npcRequest),
                Is.False);
        }

        [Test]
        public void RejectedTrustedNpcSkill_SubsequentNativeHitCannotCreateLeaseOrQueue()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture npc = CreateAuthoritativeActor(
                manager,
                bridge,
                411,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Rejected Trusted Free Flow NPC");
            ActorFixture player = CreateAuthoritativeActor(
                manager,
                bridge,
                412,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: false,
                hasAuthenticatedPlayerOwner: true,
                "Rejected Trusted Skill Target");

            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            weapon.name = "Rejected Trusted Free Flow Weapon";
            Skill skill = Track(ScriptableObject.CreateInstance<Skill>());
            skill.name = "Unallowlisted Trusted Free Flow Skill";
            MeleeStance stance = ConfigureCurrentMeleeAttack(npc.Controller, weapon, skill);
            NetworkMeleeManager.RegisterMeleeWeapon(weapon);
            NetworkMeleeManager.RegisterSkill(skill);

            int skillBroadcasts = 0;
            manager.BroadcastSkillToAllClients += _ => skillBroadcasts++;

            InvokePrivate(
                npc.Controller,
                "TryPublishTrustedServerNpcSkill",
                stance,
                weapon,
                skill,
                ComboTree.NODE_INVALID);
            Assert.That(
                GetField<uint>(npc.Controller, "m_CurrentTrustedAttackCorrelationId"),
                Is.Zero,
                "A rejected trusted Skill must retire the correlation before its native hit.");

            bool nativeHit = npc.Controller.InterceptHit(
                player.Root,
                player.Root.transform.position,
                Vector3.forward,
                skill);

            Assert.That(nativeHit, Is.False);
            Assert.That(skillBroadcasts, Is.Zero);
            Assert.That(
                GetField<System.Collections.IDictionary>(
                    npc.Controller,
                    "m_ServerAttackLeases").Count,
                Is.Zero,
                "InterceptHit must not synthesize a lease after Skill validation failed.");
            Assert.That(
                GetField<System.Collections.IEnumerable>(
                    manager,
                    "m_ServerHitQueue").GetEnumerator().MoveNext(),
                Is.False,
                "A hit without an accepted Skill lease must fail before the trusted queue.");
        }

        [Test]
        public void AcceptedTrustedNpcSkill_BroadcastsOnceAndAuthorizesOneHitPerTarget()
        {
            TestTransportBridge bridge = CreateBridge();
            NetworkMeleeManager manager = CreateManager(isServer: true, isClient: false);
            ActorFixture npc = CreateAuthoritativeActor(
                manager,
                bridge,
                421,
                NetworkCharacterActorType.NPC,
                isOwner: false,
                hasAuthenticatedPlayerOwner: false,
                "Accepted Trusted Free Flow NPC");
            CreateAuthoritativeActor(
                manager,
                bridge,
                422,
                NetworkCharacterActorType.PlayerOwned,
                isOwner: false,
                hasAuthenticatedPlayerOwner: true,
                "Accepted Trusted Skill Target");

            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            weapon.name = "Accepted Trusted Free Flow Weapon";
            Skill skill = Track(ScriptableObject.CreateInstance<Skill>());
            skill.name = "Allowlisted Trusted Free Flow Skill";
            npc.Receiver.RegisteredDirectSkill = skill;
            ConfigureCurrentMeleeAttack(npc.Controller, weapon, skill);
            NetworkMeleeManager.RegisterMeleeWeapon(weapon);
            NetworkMeleeManager.RegisterSkill(skill);

            uint correlationId = NetworkCorrelation.Compose(421, 1);
            var skillRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 421,
                CorrelationId = correlationId,
                TargetNetworkId = 422,
                SkillHash = StableHashUtility.GetStableHash(skill.name),
                WeaponHash = weapon.Id.Hash,
                ComboNodeId = ComboTree.NODE_INVALID
            };
            int skillBroadcasts = 0;
            int validatedSkills = 0;
            manager.BroadcastSkillToAllClients += _ => skillBroadcasts++;
            manager.OnSkillValidated += _ => validatedSkills++;

            Assert.That(NetworkCorrelation.MatchesActor(correlationId, 421), Is.True);
            Assert.That(npc.Controller.CurrentMeleeWeapon, Is.SameAs(weapon));
            Assert.That(NetworkMeleeManager.GetSkillByHash(skillRequest.SkillHash), Is.SameAs(skill));
            Assert.That(bridge.ResolveCharacter(421), Is.SameAs(npc.Character.Character));
            Assert.That(bridge.TryGetCharacterOwner(421, out _), Is.False);
            Assert.That(
                InvokeAdapterValidation(
                    "ValidateTrustedServerNpcSkill",
                    npc.Controller,
                    weapon,
                    skill,
                    skillRequest,
                    out string trustedDetails),
                Is.True,
                trustedDetails);

            Assert.That(manager.TryPublishTrustedServerSkill(npc.Controller, skillRequest), Is.True);
            Assert.That(manager.TryPublishTrustedServerSkill(npc.Controller, skillRequest), Is.True);
            Assert.That(skillBroadcasts, Is.EqualTo(1));
            Assert.That(validatedSkills, Is.EqualTo(1));

            var hit = new NetworkMeleeHitRequest
            {
                RequestId = 1,
                ActorNetworkId = 421,
                AttackerNetworkId = 421,
                TargetNetworkId = 422,
                AttackCorrelationId = correlationId,
                SkillHash = skillRequest.SkillHash,
                WeaponHash = skillRequest.WeaponHash,
                ComboNodeId = skillRequest.ComboNodeId
            };
            Assert.That(manager.TryServerQueueTrustedHit(hit), Is.True);
            Assert.That(
                EvaluateAttackAuthorization(npc.Controller, hit, Time.time, true).status,
                Is.EqualTo("Authorized"));
            (string replayStatus, MeleeHitRejectionReason replayReason) =
                EvaluateAttackAuthorization(npc.Controller, hit, Time.time, true);
            Assert.That(replayStatus, Is.EqualTo("Rejected"));
            Assert.That(replayReason, Is.EqualTo(MeleeHitRejectionReason.AlreadyHit));
            Assert.That(manager.TryServerQueueTrustedHit(hit), Is.False,
                "A consumed target cannot be queued again under the same accepted operation.");
        }

        private TestTransportBridge CreateBridge()
        {
            GameObject root = Track(new GameObject("Free Flow Test Transport"));
            return EditModeLifecycle.AddComponent<TestTransportBridge>(root);
        }

        private NetworkMeleeManager CreateManager(bool isServer, bool isClient)
        {
            GameObject root = Track(new GameObject("Free Flow Melee Manager"));
            NetworkMeleeManager manager =
                EditModeLifecycle.AddComponent<NetworkMeleeManager>(root);
            manager.Initialize(isServer, isClient);
            return manager;
        }

        private ActorFixture CreateUninitializedActor(uint networkId, string name)
        {
            GameObject root = Track(new GameObject(name));
            EditModeLifecycle.AddComponent<Character>(root);
            NetworkCharacter character = EditModeLifecycle.AddComponent<NetworkCharacter>(root);
            character.SetManualNetworkId(networkId);
            NetworkMeleeController controller =
                EditModeLifecycle.AddComponent<NetworkMeleeController>(root);
            controller.Initialize(false, false);
            TestFreeFlowReceiver receiver = root.AddComponent<TestFreeFlowReceiver>();
            NetworkFreeFlowCombatAdapter adapter =
                EditModeLifecycle.AddComponent<NetworkFreeFlowCombatAdapter>(root);

            return new ActorFixture
            {
                Root = root,
                Character = character,
                Controller = controller,
                Receiver = receiver,
                Adapter = adapter
            };
        }

        private ActorFixture CreateAttackableNpc(
            uint networkId,
            uint targetNetworkId,
            string name,
            bool counterable = false)
        {
            ActorFixture target = CreateUninitializedActor(networkId, name);
            SetField(target.Character, "m_ActorType", NetworkCharacterActorType.NPC);
            SetField(
                target.Character,
                "m_NPCMode",
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            SetField(
                target.Adapter,
                "m_CurrentState",
                new NetworkFreeFlowCombatState
                {
                    CharacterNetworkId = networkId,
                    StateVersion = 3,
                    TargetNetworkId = targetNetworkId,
                    Flags = (byte)(NetworkFreeFlowCombatState.AttackableFlag |
                                   (counterable
                                       ? NetworkFreeFlowCombatState.CounterableFlag
                                       : (byte)0))
                });
            return target;
        }

        private Component CreateIntegrationActor(
            Type integrationType,
            NetworkCharacterActorType actorType,
            string name)
        {
            GameObject root = Track(new GameObject(name));
            EditModeLifecycle.AddComponent<Character>(root);
            NetworkCharacter character = EditModeLifecycle.AddComponent<NetworkCharacter>(root);
            SetField(character, "m_ActorType", actorType);
            SetField(
                character,
                "m_NPCMode",
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            NetworkMeleeController controller =
                EditModeLifecycle.AddComponent<NetworkMeleeController>(root);
            controller.Initialize(false, false);
            EditModeLifecycle.AddComponent<NetworkFreeFlowCombatAdapter>(root);
            return EditModeLifecycle.AddComponent(root, integrationType);
        }

        private static void CollectSkills(
            MethodInfo method,
            Component integration,
            MeleeWeapon weapon,
            out Dictionary<Skill, NetworkFreeFlowSkillUse> uses,
            out HashSet<Skill> direct)
        {
            uses = new Dictionary<Skill, NetworkFreeFlowSkillUse>();
            direct = new HashSet<Skill>();
            Dictionary<Skill, NetworkFreeFlowSkillUse> capturedUses = uses;
            HashSet<Skill> capturedDirect = direct;
            Action<Skill, NetworkFreeFlowSkillUse> registerUse = (skill, use) =>
            {
                capturedUses.TryGetValue(skill, out NetworkFreeFlowSkillUse existing);
                capturedUses[skill] = existing | use;
            };
            Action<Skill> registerDirect = skill => capturedDirect.Add(skill);
            method.Invoke(
                integration,
                new object[] { weapon, registerUse, registerDirect });
        }

        private ActorFixture CreateAuthoritativeActor(
            NetworkMeleeManager manager,
            TestTransportBridge bridge,
            uint networkId,
            NetworkCharacterActorType actorType,
            bool isOwner,
            bool hasAuthenticatedPlayerOwner,
            string name)
        {
            GameObject root = Track(new GameObject(name));
            EditModeLifecycle.AddComponent<Character>(root);
            NetworkCharacter character = EditModeLifecycle.AddComponent<NetworkCharacter>(root);
            SetField(character, "m_ActorType", actorType);
            SetField(
                character,
                "m_NPCMode",
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            character.SetManualNetworkId(networkId);
            EditModeLifecycle.InitializeNetworkRole(
                character,
                isServer: true,
                isOwner: isOwner,
                isHost: isOwner,
                hasAuthenticatedPlayerOwner: hasAuthenticatedPlayerOwner);

            NetworkMeleeController controller =
                EditModeLifecycle.AddComponent<NetworkMeleeController>(root);
            controller.Initialize(true, isOwner);
            TestFreeFlowReceiver receiver = root.AddComponent<TestFreeFlowReceiver>();
            NetworkFreeFlowCombatAdapter adapter =
                EditModeLifecycle.AddComponent<NetworkFreeFlowCombatAdapter>(root);

            bridge.RegisterCharacter(character);
            manager.RegisterController(networkId, controller);
            manager.RegisterFreeFlowAdapter(networkId, adapter);
            return new ActorFixture
            {
                Root = root,
                Character = character,
                Controller = controller,
                Receiver = receiver,
                Adapter = adapter
            };
        }

        private static bool ValidateSkillRequest(
            NetworkMeleeController controller,
            MeleeWeapon weapon,
            Skill skill,
            NetworkSkillRequest request,
            out string details)
        {
            return InvokeAdapterValidation(
                "ValidateSkillRequest",
                controller,
                weapon,
                skill,
                request,
                out details);
        }

        private static bool InvokeAdapterValidation(
            string methodName,
            NetworkMeleeController controller,
            MeleeWeapon weapon,
            Skill skill,
            NetworkSkillRequest request,
            out string details)
        {
            MethodInfo method = typeof(NetworkFreeFlowCombatAdapter).GetMethod(
                methodName,
                StaticMethodFlags);
            Assert.That(method, Is.Not.Null, $"Missing {methodName} test seam");
            object[] arguments = { controller, weapon, skill, request, null };
            bool result = (bool)method.Invoke(null, arguments);
            details = arguments[4] as string ?? string.Empty;
            return result;
        }

        private static bool CommitSkillRequest(
            NetworkMeleeController controller,
            NetworkSkillRequest request,
            out string details)
        {
            MethodInfo method = typeof(NetworkFreeFlowCombatAdapter).GetMethod(
                "CommitSkillRequest",
                StaticMethodFlags);
            Assert.That(method, Is.Not.Null, "Missing CommitSkillRequest test seam");
            object[] arguments = { controller, request, null };
            bool result = (bool)method.Invoke(null, arguments);
            details = arguments[2] as string ?? string.Empty;
            return result;
        }

        private static NetworkSkillRequest DecorateSkillRequest(
            NetworkMeleeController controller,
            NetworkSkillRequest request)
        {
            MethodInfo method = typeof(NetworkFreeFlowCombatAdapter).GetMethod(
                "DecorateSkillRequest",
                StaticMethodFlags);
            Assert.That(method, Is.Not.Null, "Missing DecorateSkillRequest test seam");
            object[] arguments = { controller, request };
            method.Invoke(null, arguments);
            return (NetworkSkillRequest)arguments[1];
        }

        private static void SetPendingCounter(
            NetworkFreeFlowCombatAdapter adapter,
            uint targetNetworkId,
            uint revision)
        {
            SetField(adapter, "m_PendingCounterTargetId", targetNetworkId);
            SetField(adapter, "m_PendingCounterRevision", revision);
            SetField(adapter, "m_PendingCounterExpiresAt", float.PositiveInfinity);
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = FindInstanceField(target.GetType(), name);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            return (T)field.GetValue(target);
        }

        private static bool HasManagedReferenceOfType(
            SerializedObject serialized,
            Type managedReferenceType)
        {
            Assert.That(serialized, Is.Not.Null);
            Assert.That(managedReferenceType, Is.Not.Null);

            string expectedSuffix = $" {managedReferenceType.FullName}";
            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ManagedReference) continue;

                string fullTypeName = iterator.managedReferenceFullTypename;
                if (!string.IsNullOrEmpty(fullTypeName) &&
                    fullTypeName.EndsWith(expectedSuffix, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = FindInstanceField(target.GetType(), name);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }

        private static void InvokePrivate(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {name}");
            method.Invoke(target, arguments);
        }

        private static MeleeStance ConfigureCurrentMeleeAttack(
            NetworkMeleeController controller,
            MeleeWeapon weapon,
            Skill skill)
        {
            var stance = new MeleeStance();
            FieldInfo attacksField = typeof(MeleeStance).GetField(
                "m_Attacks",
                InstanceFieldFlags);
            Assert.That(attacksField, Is.Not.Null, "Missing GC2 MeleeStance attack state");
            object attacks = attacksField.GetValue(stance);
            Assert.That(attacks, Is.Not.Null);
            SetField(attacks, "<Weapon>k__BackingField", weapon);
            SetField(attacks, "<ComboSkill>k__BackingField", skill);
            SetField(attacks, "<ComboId>k__BackingField", ComboTree.NODE_INVALID);
            SetField(controller, "m_MeleeStance", stance);
            return stance;
        }

        private static MeleeStance ConfigureCurrentMeleeAttack(
            NetworkMeleeController controller,
            Character character,
            MeleeWeapon weapon,
            Skill skill,
            GameObject skillArgsTarget)
        {
            MeleeStance stance = ConfigureCurrentMeleeAttack(controller, weapon, skill);
            SetField(stance, "<Character>k__BackingField", character);
            SetField(
                stance,
                "<Args>k__BackingField",
                new Args(character.gameObject, skillArgsTarget));
            return stance;
        }

        private static uint ResolveSkillRequestTargetNetworkId(
            NetworkMeleeController controller,
            NetworkAttackState attackState)
        {
            MethodInfo method = controller.GetType().GetMethod(
                "ResolveSkillRequestTargetNetworkId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing Skill request target-resolution test seam");
            return (uint)method.Invoke(controller, new object[] { attackState });
        }

        private static (string status, MeleeHitRejectionReason reason)
            EvaluateAttackAuthorization(
                NetworkMeleeController controller,
                NetworkMeleeHitRequest request,
                float now,
                bool consume)
        {
            MethodInfo method = controller.GetType().GetMethod(
                "EvaluateAuthoritativeAttackAuthorization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing attack-authorization test seam");
            object[] arguments =
            {
                request,
                now,
                consume,
                MeleeHitRejectionReason.None
            };
            object status = method.Invoke(controller, arguments);
            return (status?.ToString() ?? string.Empty, (MeleeHitRejectionReason)arguments[3]);
        }

        private static FieldInfo FindInstanceField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    name,
                    InstanceFieldFlags | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }

            return null;
        }

        private static T GetStaticField<T>(Type type, string name)
        {
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing static field {name}");
            return (T)field.GetValue(null);
        }

        private static void SetStaticField(Type type, string name, object value)
        {
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing static field {name}");
            field.SetValue(null, value);
        }

        private static object InvokePrivateStatic(
            Type type,
            string name,
            params object[] arguments)
        {
            MethodInfo method = type.GetMethod(name, StaticMethodFlags);
            Assert.That(method, Is.Not.Null, $"Missing static method {name}");
            return method.Invoke(null, arguments);
        }

        private static void ResetFreeFlowSmokeState()
        {
            InvokePrivateStatic(
                typeof(NetworkFreeFlowCombatSmokeUtility),
                "ResetStaticState");
        }

        private static void InjectTransportOwner(
            NetworkTransportBridge bridge,
            uint characterNetworkId,
            uint ownerClientId)
        {
            // The public bridge API correctly refuses to assign an owner to an explicit NPC.
            // Inject an inconsistent native/cache result to prove the trusted-NPC path still
            // fails closed if a transport ever reports one.
            Dictionary<uint, uint> owners =
                GetField<Dictionary<uint, uint>>(bridge, "m_CharacterOwners");
            owners[characterNetworkId] = ownerClientId;
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }
    }
}
#endif
