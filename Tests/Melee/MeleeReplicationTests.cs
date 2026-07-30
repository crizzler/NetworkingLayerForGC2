#if GC2_MELEE
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Arawn.GameCreator2.Networking.Melee.Transport.PurrNet;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Tests
{
    public sealed class MeleeReplicationTests
    {
        private readonly List<UnityEngine.Object> m_Cleanup = new();

        private sealed class TestReaction : Reaction
        {
        }

        private sealed class TestTransportBridge : NetworkTransportBridge
        {
            public override bool IsServer => true;
            public override bool IsClient => false;
            public override bool IsHost => false;
            public override float ServerTime => 1f;

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

        [SetUp]
        public void SetUp()
        {
            SetForcedDiagnostics(false);
            SecurityIntegration.ClearModuleServerContexts();
            NetworkMeleeManager.ClearRegistries();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
            NetworkMeleeManager.ClearRegistries();
            SecurityIntegration.ClearModuleServerContexts();
            SetForcedDiagnostics(false);
        }

        [Test]
        public void PurrNetMeleeTransport_DefaultLogging_HasNoTemporaryHostOrClientPrefixes()
        {
            string fullPath = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/Melee/PurrNetMeleeTransportBridge.cs");
            Assert.That(File.Exists(fullPath), Is.True, "Missing PurrNet Melee transport source");

            string source = File.ReadAllText(fullPath);
            StringAssert.DoesNotContain("[NetworkMeleeHostDebug]", source);
            StringAssert.DoesNotContain("[NetworkMeleeClientDebug]", source);
            StringAssert.DoesNotContain("NetworkMeleeHostTrace", source);
            StringAssert.DoesNotContain("HostTraceEnabled", source);
        }

        [Test]
        public void CharacterSnapshot_PurrNetRoundTrip_PreservesWeaponAndBlock()
        {
            var expected = new NetworkMeleeCharacterSnapshot
            {
                CharacterNetworkId = 73,
                HasWeaponState = true,
                WeaponState = new NetworkMeleeWeaponState
                {
                    WeaponHash = 991,
                    ShieldFlags = NetworkMeleeWeaponState.SHIELD_RAISED |
                                  NetworkMeleeWeaponState.SHIELD_PARRY_WINDOW,
                    BlockTiming = 128
                },
                HasBlockState = true,
                BlockState = new NetworkBlockBroadcast
                {
                    CharacterNetworkId = 73,
                    Action = NetworkBlockAction.Raise,
                    ServerTimestamp = 12.25f,
                    ShieldHash = 119
                }
            };

            NetworkMeleeCharacterSnapshot actual = RoundTrip(expected);

            Assert.That(actual.CharacterNetworkId, Is.EqualTo(73));
            Assert.That(actual.HasWeaponState, Is.True);
            Assert.That(actual.WeaponState.WeaponHash, Is.EqualTo(991));
            Assert.That(actual.WeaponState.ShieldFlags, Is.EqualTo(expected.WeaponState.ShieldFlags));
            Assert.That(actual.WeaponState.BlockTiming, Is.EqualTo(128));
            Assert.That(actual.HasBlockState, Is.True);
            Assert.That(actual.BlockState.Action, Is.EqualTo(NetworkBlockAction.Raise));
            Assert.That(actual.BlockState.ServerTimestamp, Is.EqualTo(12.25f));
        }

        [Test]
        public void ReactionBroadcast_PurrNetRoundTrip_PreservesSequenceAndVerticalDirection()
        {
            var expected = new NetworkReactionBroadcast
            {
                CharacterNetworkId = 17,
                FromNetworkId = 18,
                Sequence = 0xF1020304u,
                ReactionHash = 991,
                PlaybackKind = NetworkReactionPlaybackKind.DirectShield,
                Direction = NetworkReactionBroadcast.CompressDirection(Vector3.up),
                DirectionY = NetworkReactionBroadcast.CompressDirectionY(Vector3.up),
                Power = NetworkReactionBroadcast.CompressPower(4.5f)
            };

            NetworkReactionBroadcast actual = RoundTrip(expected);

            Assert.That(actual.CharacterNetworkId, Is.EqualTo(expected.CharacterNetworkId));
            Assert.That(actual.FromNetworkId, Is.EqualTo(expected.FromNetworkId));
            Assert.That(actual.Sequence, Is.EqualTo(expected.Sequence));
            Assert.That(actual.PlaybackKind, Is.EqualTo(expected.PlaybackKind));
            Assert.That(actual.ReactionHash, Is.EqualTo(expected.ReactionHash));
            Assert.That(actual.GetDirection().y, Is.GreaterThan(0.9f));
            Assert.That(actual.GetPower(), Is.EqualTo(expected.GetPower()).Within(0.001f));
        }

        [Test]
        public void ExplicitReactionRegistry_UsesStableAssetNameHash()
        {
            TestReaction reaction = Track(ScriptableObject.CreateInstance<TestReaction>());
            reaction.name = "Network Lift Reaction";

            NetworkMeleeManager.RegisterReaction(reaction);
            int hash = NetworkMeleeManager.GetReactionHash(reaction);

            Assert.That(hash, Is.EqualTo(StableHashUtility.GetStableHash(reaction.name)));
            Assert.That(NetworkMeleeManager.GetReactionByHash(hash), Is.SameAs(reaction));
        }

        [Test]
        public void ReactionBroadcast_PurrNetRoundTrip_PreservesAuthoredNoneDirection()
        {
            var expected = new NetworkReactionBroadcast
            {
                CharacterNetworkId = 27,
                FromNetworkId = 28,
                Sequence = 9,
                Direction = NetworkReactionBroadcast.CompressDirection(Vector3.zero),
                DirectionY = NetworkReactionBroadcast.CompressDirectionY(Vector3.zero),
                Power = NetworkReactionBroadcast.CompressPower(2f)
            };

            NetworkReactionBroadcast actual = RoundTrip(expected);

            Assert.That(actual.Direction, Is.Zero);
            Assert.That(actual.DirectionY, Is.Zero);
            Assert.That(actual.GetDirection(), Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void HitRequest_PurrNetRoundTrip_PreservesAttackTokenAndFullWidthComboId()
        {
            const int fullWidthComboId = unchecked((int)0x89ABCDEF);
            var expected = new NetworkMeleeHitRequest
            {
                RequestId = 31,
                ActorNetworkId = 41,
                CorrelationId = 0x71020304u,
                AttackCorrelationId = 0xF1020304u,
                ClientTimestamp = 12.5f,
                AttackerNetworkId = 41,
                TargetNetworkId = 42,
                HitPoint = new Vector3(1.25f, 2.5f, -3.75f),
                StrikeDirection = Vector3.up,
                SkillHash = -1352291518,
                WeaponHash = -1897370267,
                ComboNodeId = fullWidthComboId,
                AttackPhase = (byte)MeleePhase.Strike
            };

            NetworkMeleeHitRequest actual = RoundTrip(expected);

            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.ActorNetworkId, Is.EqualTo(expected.ActorNetworkId));
            Assert.That(actual.CorrelationId, Is.EqualTo(expected.CorrelationId));
            Assert.That(actual.AttackCorrelationId, Is.EqualTo(expected.AttackCorrelationId));
            Assert.That(actual.AttackerNetworkId, Is.EqualTo(expected.AttackerNetworkId));
            Assert.That(actual.TargetNetworkId, Is.EqualTo(expected.TargetNetworkId));
            Assert.That(actual.SkillHash, Is.EqualTo(expected.SkillHash));
            Assert.That(actual.WeaponHash, Is.EqualTo(expected.WeaponHash));
            Assert.That(actual.ComboNodeId, Is.EqualTo(fullWidthComboId));
            Assert.That(actual.AttackPhase, Is.EqualTo(expected.AttackPhase));
        }

        [Test]
        public void SkillPackets_PurrNetRoundTrip_PreserveFullWidthComboIds()
        {
            var expectedRequest = new NetworkSkillRequest
            {
                RequestId = 51,
                ActorNetworkId = 61,
                CorrelationId = 0x61020304u,
                TargetNetworkId = 62,
                SkillHash = 701,
                WeaponHash = 702,
                ComboNodeId = 0x1234ABCD,
                PreviousComboNodeId = unchecked((int)0x89ABCDEF),
                ClientTimestamp = 21.75f,
                InputKey = 3,
                IsChargeRelease = true,
                ChargeDuration = 1.25f
            };
            var expectedResponse = new NetworkSkillResponse
            {
                RequestId = 51,
                ActorNetworkId = 61,
                CorrelationId = 0x61020304u,
                Validated = true,
                RejectionReason = SkillRejectionReason.None,
                ComboNodeId = -123456789
            };
            var expectedBroadcast = new NetworkSkillBroadcast
            {
                CharacterNetworkId = 61,
                TargetNetworkId = 62,
                SkillHash = 701,
                WeaponHash = 702,
                ComboNodeId = int.MaxValue - 7,
                ServerTimestamp = 22.5f,
                IsCharged = true,
                ChargeLevel = 201
            };

            NetworkSkillRequest actualRequest = RoundTrip(expectedRequest);
            NetworkSkillResponse actualResponse = RoundTrip(expectedResponse);
            NetworkSkillBroadcast actualBroadcast = RoundTrip(expectedBroadcast);

            Assert.That(actualRequest.ComboNodeId, Is.EqualTo(expectedRequest.ComboNodeId));
            Assert.That(actualRequest.PreviousComboNodeId, Is.EqualTo(expectedRequest.PreviousComboNodeId));
            Assert.That(actualRequest.ClientTimestamp, Is.EqualTo(expectedRequest.ClientTimestamp));
            Assert.That(actualResponse.ComboNodeId, Is.EqualTo(expectedResponse.ComboNodeId));
            Assert.That(actualBroadcast.ComboNodeId, Is.EqualTo(expectedBroadcast.ComboNodeId));
        }

        [Test]
        public void OpaqueComboStates_AcceptFullWidthIdentifiers()
        {
            const int fullWidthComboId = unchecked((int)0x89ABCDEF);
            var attackState = new NetworkAttackState { ComboNodeId = fullWidthComboId };
            var chargeState = new NetworkChargeState { ChargeComboNodeId = fullWidthComboId };

            Assert.That(attackState.ComboNodeId, Is.EqualTo(fullWidthComboId));
            Assert.That(chargeState.ChargeComboNodeId, Is.EqualTo(fullWidthComboId));
        }

        [Test]
        public void ReactionSequence_DropsDuplicateButAllowsASecondValidatedHit()
        {
            NetworkMeleeController controller = CreateController("Melee Reaction Sequence");
            var first = new NetworkReactionBroadcast
            {
                CharacterNetworkId = 20,
                FromNetworkId = 21,
                Sequence = 7,
                ReactionHash = 22
            };

            Assert.That(
                InvokePrivateResult<bool>(controller, "ShouldSkipReactionBroadcastPlayback", first),
                Is.False);
            InvokePrivate(controller, "RememberReactionBroadcast", first);
            Assert.That(
                InvokePrivateResult<bool>(controller, "ShouldSkipReactionBroadcastPlayback", first),
                Is.True);

            NetworkReactionBroadcast replacement = first;
            replacement.Sequence = 8;
            Assert.That(
                InvokePrivateResult<bool>(controller, "ShouldSkipReactionBroadcastPlayback", replacement),
                Is.False,
                "An active reaction cannot suppress a distinct server-authorized strike.");
        }

        [Test]
        public void PendingSnapshot_LatestFullReplacementClearsOlderBlockState()
        {
            NetworkMeleeManager manager = CreateManager(false, true);

            manager.ReceiveCharacterSnapshot(new NetworkMeleeCharacterSnapshot
            {
                CharacterNetworkId = 44,
                HasWeaponState = true,
                WeaponState = new NetworkMeleeWeaponState { WeaponHash = 100 },
                HasBlockState = true,
                BlockState = new NetworkBlockBroadcast
                {
                    CharacterNetworkId = 44,
                    Action = NetworkBlockAction.Raise
                }
            });

            NetworkMeleeCharacterSnapshot replacement = NetworkMeleeCharacterSnapshot.Create(44);
            replacement.HasWeaponState = true;
            replacement.WeaponState = new NetworkMeleeWeaponState { WeaponHash = 200 };
            manager.ReceiveCharacterSnapshot(replacement);

            IDictionary pending = GetPrivateField<IDictionary>(manager, "m_PendingCharacterStates");
            Assert.That(pending.Count, Is.EqualTo(1));
            var actual = (NetworkMeleeCharacterSnapshot)pending[44u];
            Assert.That(actual.HasWeaponState, Is.True);
            Assert.That(actual.WeaponState.WeaponHash, Is.EqualTo(200));
            Assert.That(actual.HasBlockState, Is.True);
            Assert.That(actual.BlockState.Action, Is.EqualTo(NetworkBlockAction.Lower));
        }

        [Test]
        public void AuthoritativeUnregister_RemovesCharacterFromLateJoinSnapshots()
        {
            NetworkMeleeManager manager = CreateManager(true, false);
            NetworkMeleeController controller = CreateController("Melee Snapshot Despawn");
            manager.RegisterController(45, controller);
            manager.RecordAuthoritativeWeaponState(
                45,
                new NetworkMeleeWeaponState { WeaponHash = 700 });

            Assert.That(manager.CaptureCharacterSnapshots(), Has.Length.EqualTo(1));

            manager.UnregisterController(45);

            Assert.That(manager.CaptureCharacterSnapshots(), Is.Empty);
            IDictionary pending = GetPrivateField<IDictionary>(manager, "m_PendingCharacterStates");
            Assert.That(pending.Contains(45u), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HitRouting_ClientAndHost_PresentsOnceAndNotifiesBothRoles(bool isHost)
        {
            NetworkMeleeManager manager = CreateManager(isHost, true);
            NetworkMeleeController attacker = CreateController("Melee Attacker");
            NetworkMeleeController target = CreateController("Melee Target");
            manager.RegisterController(1, attacker);
            manager.RegisterController(2, target);

            int attackerConfirmations = 0;
            int targetConfirmations = 0;
            int presentations = 0;
            attacker.OnHitConfirmed += _ => attackerConfirmations++;
            target.OnHitConfirmed += _ => targetConfirmations++;
            manager.OnHitPresentationRequested += context =>
            {
                presentations++;
                context.Handled = true;
            };

            manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 1,
                TargetNetworkId = 2,
                HitPoint = Vector3.one,
                StrikeDirection = Vector3.forward,
                SkillHash = 555,
                BlockResult = (byte)NetworkBlockResult.Blocked
            });

            Assert.That(attackerConfirmations, Is.EqualTo(1));
            Assert.That(targetConfirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HostDiagnosticTracing_DoesNotAlterExactOnceHitRouting(bool diagnosticsEnabled)
        {
            SetForcedDiagnostics(diagnosticsEnabled);
            NetworkMeleeManager manager = CreateManager(true, true);
            SetPrivateField(manager, "m_LogHitBroadcasts", diagnosticsEnabled);
            SetPrivateField(manager, "m_LogMeleeFlow", diagnosticsEnabled);
            SetPrivateField(manager, "m_LogSkillFlowDiagnostics", diagnosticsEnabled);

            GameObject attackerObject = Track(new GameObject("Melee Host Diagnostic Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(3);
            NetworkMeleeController attacker = attackerObject.AddComponent<NetworkMeleeController>();
            attacker.Initialize(true, true);
            SetPrivateField(attacker, "m_LogMeleeSync", diagnosticsEnabled);

            GameObject targetObject = Track(new GameObject("Melee Host Diagnostic Remote Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(4);
            NetworkMeleeController target = targetObject.AddComponent<NetworkMeleeController>();
            target.Initialize(true, false);
            SetPrivateField(target, "m_LogMeleeSync", diagnosticsEnabled);

            manager.RegisterController(3, attacker);
            manager.RegisterController(4, target);

            int attackerConfirmations = 0;
            int targetConfirmations = 0;
            int presentations = 0;
            attacker.OnHitConfirmed += _ => attackerConfirmations++;
            target.OnHitConfirmed += _ => targetConfirmations++;
            manager.OnHitPresentationRequested += context =>
            {
                presentations++;
                context.Handled = true;
            };

            manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 3,
                TargetNetworkId = 4,
                HitPoint = Vector3.up,
                StrikeDirection = Vector3.forward,
                SkillHash = 556,
                BlockResult = (byte)NetworkBlockResult.None
            });

            Assert.That(attackerConfirmations, Is.EqualTo(1));
            Assert.That(targetConfirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
            Assert.That(GetPrivateField<IList>(manager, "m_PendingHitBroadcasts"), Is.Empty);
        }

        [Test]
        public void HitRouting_SelfHit_UsesOneCombinedPresentationPath()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            NetworkMeleeController controller = CreateController("Melee Self Hit");
            manager.RegisterController(8, controller);

            int confirmations = 0;
            int presentations = 0;
            controller.OnHitConfirmed += _ => confirmations++;
            manager.OnHitPresentationRequested += context =>
            {
                presentations++;
                context.Handled = true;
            };

            manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 8,
                TargetNetworkId = 8,
                HitPoint = Vector3.zero,
                StrikeDirection = Vector3.right,
                SkillHash = 777
            });

            Assert.That(confirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
        }

        [Test]
        public void SkillHitAudioSelection_UsesAuthoritativeBlockOutcome()
        {
            var effects = new SkillEffects();
            AudioClip hit = Track(AudioClip.Create("Melee Hit", 32, 1, 8000, false));
            AudioClip blocked = Track(AudioClip.Create("Melee Blocked", 32, 1, 8000, false));
            AudioClip parried = Track(AudioClip.Create("Melee Parried", 32, 1, 8000, false));

            SetPrivateField(effects, "m_SoundHit", new PropertyGetAudio(hit));
            SetPrivateField(effects, "m_SoundBlocked", new PropertyGetAudio(blocked));
            SetPrivateField(effects, "m_SoundParried", new PropertyGetAudio(parried));

            MethodInfo selector = typeof(NetworkMeleeManager).GetMethod(
                "ResolveSkillHitAudioClip",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(selector, Is.Not.Null, "Missing presentation-safe Skill audio selector");

            AudioClip Resolve(NetworkBlockResult result) =>
                (AudioClip)selector.Invoke(null, new object[] { effects, Args.EMPTY, result });

            Assert.That(Resolve(NetworkBlockResult.None), Is.SameAs(hit));
            Assert.That(Resolve(NetworkBlockResult.BlockBroken), Is.SameAs(hit));
            Assert.That(Resolve(NetworkBlockResult.Blocked), Is.SameAs(blocked));
            Assert.That(Resolve(NetworkBlockResult.Parried), Is.SameAs(parried));
        }

        [Test]
        public void PendingHit_DeliversOnlyMissingRoleWhenControllerRegisters()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            NetworkMeleeController attacker = CreateController("Melee Ready Attacker");
            manager.RegisterController(11, attacker);

            int attackerConfirmations = 0;
            int targetConfirmations = 0;
            int presentations = 0;
            attacker.OnHitConfirmed += _ => attackerConfirmations++;
            manager.OnHitPresentationRequested += context =>
            {
                presentations++;
                context.Handled = true;
            };

            var broadcast = new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 11,
                TargetNetworkId = 12,
                HitPoint = Vector3.one,
                StrikeDirection = Vector3.forward,
                SkillHash = 808
            };

            manager.ReceiveHitBroadcast(broadcast);

            Assert.That(attackerConfirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
            Assert.That(
                GetPrivateField<IList>(manager, "m_PendingHitBroadcasts").Count,
                Is.EqualTo(1));

            NetworkMeleeController target = CreateController("Melee Late Target");
            target.OnHitConfirmed += _ => targetConfirmations++;
            manager.RegisterController(12, target);

            Assert.That(attackerConfirmations, Is.EqualTo(1),
                "Flushing the target role must not replay attacker presentation/confirmation.");
            Assert.That(targetConfirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
            Assert.That(GetPrivateField<IList>(manager, "m_PendingHitBroadcasts").Count, Is.Zero);

            InvokePrivate(manager, "FlushPendingTransientBroadcasts");
            Assert.That(attackerConfirmations, Is.EqualTo(1));
            Assert.That(targetConfirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
        }

        [Test]
        public void PendingSelfHit_RegistersAsOneCombinedDelivery()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            int presentations = 0;
            manager.OnHitPresentationRequested += context =>
            {
                presentations++;
                context.Handled = true;
            };

            manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 21,
                TargetNetworkId = 21,
                SkillHash = 909
            });

            NetworkMeleeController controller = CreateController("Melee Late Self Target");
            int confirmations = 0;
            controller.OnHitConfirmed += _ => confirmations++;
            manager.RegisterController(21, controller);

            Assert.That(confirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
            Assert.That(GetPrivateField<IList>(manager, "m_PendingHitBroadcasts").Count, Is.Zero);

            InvokePrivate(manager, "FlushPendingTransientBroadcasts");
            Assert.That(confirmations, Is.EqualTo(1));
            Assert.That(presentations, Is.EqualTo(1));
        }

        [Test]
        public void PendingReaction_FlushesOnceAfterTargetRoleRegisters()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            var broadcast = new NetworkReactionBroadcast
            {
                CharacterNetworkId = 31,
                FromNetworkId = 32,
                Sequence = 17,
                Direction = NetworkReactionBroadcast.CompressDirection(Vector3.up),
                DirectionY = NetworkReactionBroadcast.CompressDirectionY(Vector3.up),
                Power = NetworkReactionBroadcast.CompressPower(3f)
            };

            manager.ReceiveReactionBroadcast(broadcast);
            Assert.That(
                GetPrivateField<IList>(manager, "m_PendingReactionBroadcasts").Count,
                Is.EqualTo(1));

            GameObject targetObject = Track(new GameObject("Melee Late Reaction Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = targetObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(31);
            NetworkMeleeController controller = targetObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(false, false);
            int received = 0;
            controller.OnReactionReceived += _ => received++;

            manager.RegisterController(31, controller);

            Assert.That(received, Is.EqualTo(1));
            Assert.That(GetPrivateField<IList>(manager, "m_PendingReactionBroadcasts").Count, Is.Zero);

            InvokePrivate(manager, "FlushPendingTransientBroadcasts");
            Assert.That(received, Is.EqualTo(1));
        }

        [Test]
        public void PendingTransient_ExpiresInsteadOfReplayingOldCombat()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 41,
                TargetNetworkId = 41,
                SkillHash = 1001
            });

            IList pending = GetPrivateField<IList>(manager, "m_PendingHitBroadcasts");
            Assert.That(pending.Count, Is.EqualTo(1));
            object expired = pending[0];
            FieldInfo receivedTime = expired.GetType().GetField(
                "ReceivedTime",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(receivedTime, Is.Not.Null);
            receivedTime.SetValue(expired, Time.unscaledTime - 10f);
            pending[0] = expired;

            InvokePrivate(manager, "FlushPendingTransientBroadcasts");
            Assert.That(pending.Count, Is.Zero);

            NetworkMeleeController controller = CreateController("Melee Expired Self Target");
            int confirmations = 0;
            controller.OnHitConfirmed += _ => confirmations++;
            manager.RegisterController(41, controller);
            Assert.That(confirmations, Is.Zero);
        }

        [Test]
        public void PendingTransient_CapacityEvictsTheOldestBroadcast()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            typeof(NetworkMeleeManager).GetField(
                    "m_MaxPendingTransientBroadcasts",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(manager, 2);

            for (uint networkId = 51; networkId <= 53; networkId++)
            {
                manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
                {
                    AttackerNetworkId = networkId,
                    TargetNetworkId = networkId,
                    SkillHash = (int)networkId
                });
            }

            IList pending = GetPrivateField<IList>(manager, "m_PendingHitBroadcasts");
            Assert.That(pending.Count, Is.EqualTo(2));

            NetworkMeleeController evictedController = CreateController("Melee Evicted Self Target");
            int evictedConfirmations = 0;
            evictedController.OnHitConfirmed += _ => evictedConfirmations++;
            manager.RegisterController(51, evictedController);

            NetworkMeleeController retainedController = CreateController("Melee Retained Self Target");
            int retainedConfirmations = 0;
            retainedController.OnHitConfirmed += _ => retainedConfirmations++;
            manager.RegisterController(52, retainedController);

            Assert.That(evictedConfirmations, Is.Zero);
            Assert.That(retainedConfirmations, Is.EqualTo(1));
            Assert.That(pending.Count, Is.EqualTo(1));
        }

        [Test]
        public void HostOwnedHit_UsesLoopbackRequestAndSuppressesNativeHit()
        {
            NetworkMeleeManager manager = CreateManager(true, true);
            GameObject attackerObject = Track(new GameObject("Melee Host Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(31);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, true);
            manager.RegisterController(31, controller);

            GameObject targetObject = Track(new GameObject("Melee Host Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(32);
            GameObject targetHurtbox = Track(new GameObject("Melee Host Target Hurtbox"));
            targetHurtbox.transform.SetParent(targetObject.transform, false);

            int requestCount = 0;
            NetworkMeleeHitRequest request = default;
            controller.OnHitDetected += value =>
            {
                requestCount++;
                request = value;
            };

            bool processNativeHit = controller.InterceptHit(
                targetHurtbox,
                targetObject.transform.position,
                Vector3.forward,
                null);

            Assert.That(processNativeHit, Is.False);
            Assert.That(requestCount, Is.EqualTo(1));
            Assert.That(request.ActorNetworkId, Is.EqualTo(31));
            Assert.That(request.TargetNetworkId, Is.EqualTo(32));
        }

        [Test]
        public void NonCharacterStrikeContact_IsNativeOnlyOnAuthoritativeServer()
        {
            GameObject worldObject = Track(new GameObject("Melee World Contact"));

            NetworkMeleeController client = CreateController("Melee World Client");
            client.Initialize(false, true);
            int clientRequests = 0;
            client.OnHitDetected += _ => clientRequests++;

            NetworkMeleeController server = CreateController("Melee World Server");
            server.Initialize(true, true);
            int serverRequests = 0;
            server.OnHitDetected += _ => serverRequests++;

            Assert.That(
                client.InterceptHit(worldObject, Vector3.zero, Vector3.forward, null),
                Is.False);
            Assert.That(
                server.InterceptHit(worldObject, Vector3.zero, Vector3.forward, null),
                Is.True);
            Assert.That(clientRequests, Is.Zero);
            Assert.That(serverRequests, Is.Zero);
        }

        [Test]
        public void LegacyConditionAndPatchedHook_DeduplicateTheSameStrike()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            GameObject attackerObject = Track(new GameObject("Melee Dedup Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(33);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(false, true);
            controller.OptimisticEffects = false;
            manager.RegisterController(33, controller);

            GameObject targetObject = Track(new GameObject("Melee Dedup Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(34);

            int requests = 0;
            controller.OnHitDetected += _ => requests++;

            bool legacyResult = controller.InterceptHit(
                targetObject,
                targetObject.transform.position,
                Vector3.forward,
                null);
            bool patchedHookResult = controller.InterceptHit(
                targetObject,
                targetObject.transform.position,
                Vector3.forward,
                null);

            Assert.That(legacyResult, Is.False);
            Assert.That(patchedHookResult, Is.False);
            Assert.That(requests, Is.EqualTo(1));
        }

        [Test]
        public void NewAttackToken_ClearsPreviousStrikeTargetDedupeBeforeUpdate()
        {
            NetworkMeleeManager manager = CreateManager(false, true);
            GameObject attackerObject = Track(new GameObject("Melee Chained Token Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(35);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(false, true);
            manager.RegisterController(35, controller);

            GameObject targetObject = Track(new GameObject("Melee Chained Token Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(36);

            Skill skill = Track(ScriptableObject.CreateInstance<Skill>());
            skill.name = "Chained Air Skill";
            int skillHash = StableHashUtility.GetStableHash(skill.name);
            const int weaponHash = 771;
            const int comboNodeId = -2012964192;
            SetPrivateField(
                controller,
                "m_LastAttackState",
                new NetworkAttackState
                {
                    SkillHash = skillHash,
                    WeaponHash = weaponHash,
                    ComboNodeId = comboNodeId,
                    Phase = (byte)MeleePhase.Strike
                });

            int requests = 0;
            controller.OnHitDetected += _ => requests++;

            uint firstToken = NetworkCorrelation.Compose(35u, 1u);
            SetPrivateField(controller, "m_CurrentLocalAttackCorrelationId", firstToken);
            SetPrivateField(controller, "m_CurrentLocalAttackSkillHash", skillHash);
            SetPrivateField(controller, "m_CurrentLocalAttackWeaponHash", weaponHash);
            SetPrivateField(controller, "m_CurrentLocalAttackComboNodeId", comboNodeId);
            controller.InterceptHit(targetObject, Vector3.zero, Vector3.forward, skill);

            // Simulate a chained attack entering Strike in Character.LateUpdate before the
            // controller's next Update observes the phase transition.
            uint secondToken = NetworkCorrelation.Compose(35u, 2u);
            SetPrivateField(controller, "m_CurrentLocalAttackCorrelationId", secondToken);
            controller.InterceptHit(targetObject, Vector3.zero, Vector3.forward, skill);

            Assert.That(requests, Is.EqualTo(2));
        }

        [Test]
        public void TrustedAttackOperation_RotatesAndMatchesItsActor()
        {
            GameObject attackerObject = Track(new GameObject("Melee Trusted Token Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = attackerObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(37);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, false);

            uint first = InvokePrivateResult<uint>(
                controller,
                "GetOrCreateTrustedAttackCorrelationId");
            SetPrivateField(controller, "m_CurrentTrustedAttackCorrelationId", 0u);
            uint second = InvokePrivateResult<uint>(
                controller,
                "GetOrCreateTrustedAttackCorrelationId");

            Assert.That(first, Is.Not.Zero);
            Assert.That(second, Is.Not.Zero);
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(NetworkCorrelation.MatchesActor(first, 37u), Is.True);
            Assert.That(NetworkCorrelation.MatchesActor(second, 37u), Is.True);
        }

        [Test]
        public void DedicatedServerHit_QueuesTrustedRequestAndSuppressesNativeHit()
        {
            TestTransportBridge bridge = Track(
                new GameObject("Melee Server Bridge").AddComponent<TestTransportBridge>());
            NetworkMeleeManager manager = CreateManager(true, false);

            GameObject attackerObject = Track(new GameObject("Melee Server Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(41);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, false);
            manager.RegisterController(41, controller);
            bridge.RegisterCharacter(attackerCharacter);

            GameObject targetObject = Track(new GameObject("Melee Server Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(42);
            GameObject targetHurtbox = Track(new GameObject("Melee Server Target Hurtbox"));
            targetHurtbox.transform.SetParent(targetObject.transform, false);

            int clientRequestCount = 0;
            controller.OnHitDetected += _ => clientRequestCount++;

            bool processNativeHit = controller.InterceptHit(
                targetHurtbox,
                targetObject.transform.position,
                Vector3.forward,
                null);

            Assert.That(processNativeHit, Is.False);
            Assert.That(clientRequestCount, Is.Zero);

            IEnumerable queue = GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue");
            IEnumerator enumerator = queue.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            object queued = enumerator.Current;
            FieldInfo trustedField = queued.GetType().GetField(
                "TrustedServerOrigin",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo requestField = queued.GetType().GetField(
                "Request",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(trustedField, Is.Not.Null);
            Assert.That(requestField, Is.Not.Null);
            Assert.That((bool)trustedField.GetValue(queued), Is.True);
            var request = (NetworkMeleeHitRequest)requestField.GetValue(queued);
            Assert.That(request.ActorNetworkId, Is.EqualTo(41));
            Assert.That(request.TargetNetworkId, Is.EqualTo(42));
        }

        [Test]
        public void RemoteOwnedServerReplica_SuppressesWithoutQueuingTrustedDuplicate()
        {
            NetworkMeleeManager manager = CreateManager(true, false);
            TestTransportBridge bridge = Track(
                new GameObject("Melee Owner Bridge").AddComponent<TestTransportBridge>());

            GameObject attackerObject = Track(new GameObject("Melee Remote-Owned Server Replica"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(51);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, false);
            manager.RegisterController(51, controller);
            bridge.RegisterCharacter(attackerCharacter);
            bridge.SetCharacterOwner(51, 7);

            GameObject targetObject = Track(new GameObject("Melee Remote-Owned Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(52);
            GameObject targetHurtbox = Track(new GameObject("Melee Remote-Owned Target Hurtbox"));
            targetHurtbox.transform.SetParent(targetObject.transform, false);

            bool processNativeHit = controller.InterceptHit(
                targetHurtbox,
                targetObject.transform.position,
                Vector3.forward,
                null);

            Assert.That(processNativeHit, Is.False);
            IEnumerable queue = GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue");
            Assert.That(queue.GetEnumerator().MoveNext(), Is.False);
        }

        [Test]
        public void ServerReactionLoopback_RaisesCompatibilityEventWithoutGameplayReplay()
        {
            GameObject targetObject = Track(new GameObject("Melee Host Reaction Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(53);
            NetworkMeleeController controller = targetObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, true);

            int compatibilityEvents = 0;
            controller.OnReactionReceived += _ => compatibilityEvents++;

            controller.ReceiveReactionBroadcast(new NetworkReactionBroadcast
            {
                CharacterNetworkId = 53,
                FromNetworkId = 54,
                Direction = NetworkReactionBroadcast.CompressDirection(Vector3.up),
                DirectionY = NetworkReactionBroadcast.CompressDirectionY(Vector3.up),
                Power = NetworkReactionBroadcast.CompressPower(3f)
            });

            Assert.That(compatibilityEvents, Is.EqualTo(1));
            Assert.That(
                GetPrivateField<bool>(controller, "m_HasLastReactionBroadcast"),
                Is.False,
                "Server/host loopback must return before the reaction playback path.");
        }

        [Test]
        public void ServerSkillLoopback_RaisesCompatibilityEventWithoutGameplayReplay()
        {
            GameObject actorObject = Track(new GameObject("Melee Server Skill Actor"));
            actorObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = actorObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(54);
            NetworkMeleeController controller = actorObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, false);

            var sentinelState = new NetworkAttackState
            {
                SkillHash = 1101,
                WeaponHash = 1102,
                ComboNodeId = 1103,
                Phase = (byte)MeleePhase.Recovery
            };
            SetPrivateField(controller, "m_LastAttackState", sentinelState);

            int compatibilityEvents = 0;
            NetworkSkillBroadcast received = default;
            controller.OnSkillExecuted += value =>
            {
                compatibilityEvents++;
                received = value;
            };

            var broadcast = new NetworkSkillBroadcast
            {
                CharacterNetworkId = 54,
                TargetNetworkId = 55,
                SkillHash = 1201,
                WeaponHash = 1202,
                ComboNodeId = 1203,
                ServerTimestamp = 12f
            };
            controller.ReceiveSkillBroadcast(broadcast);

            Assert.That(compatibilityEvents, Is.EqualTo(1));
            Assert.That(received.CharacterNetworkId, Is.EqualTo(54));
            Assert.That(received.SkillHash, Is.EqualTo(1201));
            Assert.That(
                GetPrivateField<MeleeStance>(controller, "m_MeleeStance"),
                Is.Null,
                "The test intentionally has no presentation stance; server loopback must still " +
                "raise compatibility events without attempting gameplay playback.");
            NetworkAttackState actualState = GetPrivateField<NetworkAttackState>(
                controller,
                "m_LastAttackState");
            Assert.That(actualState.SkillHash, Is.EqualTo(sentinelState.SkillHash));
            Assert.That(actualState.WeaponHash, Is.EqualTo(sentinelState.WeaponHash));
            Assert.That(actualState.ComboNodeId, Is.EqualTo(sentinelState.ComboNodeId));
            Assert.That(actualState.Phase, Is.EqualTo(sentinelState.Phase));
        }

        [Test]
        public void PatchedReactionTransition_BroadcastsImmediatelyAndPollDoesNotDuplicate()
        {
            NetworkMeleeManager manager = CreateManager(true, false);

            GameObject attackerObject = Track(new GameObject("Melee Reaction Source"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(55);

            GameObject targetObject = Track(new GameObject("Melee Reaction Callback Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(56);
            NetworkMeleeController controller = targetObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, false);
            manager.RegisterController(56, controller);

            int broadcasts = 0;
            NetworkReactionBroadcast actual = default;
            manager.OnReactionBroadcast += value =>
            {
                broadcasts++;
                actual = value;
            };

            InvokePrivate(
                controller,
                "OnAuthoritativeReactionStarted",
                attackerObject,
                new ReactionInput(Vector3.up, 3f),
                null);

            Assert.That(broadcasts, Is.EqualTo(1));
            Assert.That(actual.CharacterNetworkId, Is.EqualTo(56));
            Assert.That(actual.FromNetworkId, Is.EqualTo(55));
            Assert.That(actual.GetDirection().y, Is.GreaterThan(0.9f));

            InvokePrivate(controller, "BroadcastServerReactionIfNeeded", MeleePhase.Reaction);
            Assert.That(broadcasts, Is.EqualTo(1));
        }

        [Test]
        public void OptimisticClientHit_UsesPresentationOnlyAndConsumesConfirmationOnce()
        {
            NetworkMeleeManager manager = CreateManager(false, true);

            GameObject attackerObject = Track(new GameObject("Melee Optimistic Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter attackerCharacter = attackerObject.AddComponent<NetworkCharacter>();
            attackerCharacter.SetManualNetworkId(71);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(false, true);
            controller.OptimisticEffects = true;
            manager.RegisterController(71, controller);

            GameObject targetObject = Track(new GameObject("Melee Optimistic Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(72);
            GameObject targetHurtbox = Track(new GameObject("Melee Optimistic Target Hurtbox"));
            targetHurtbox.transform.SetParent(targetObject.transform, false);

            int presentations = 0;
            manager.OnHitPresentationRequested += context =>
            {
                presentations++;
                context.Handled = true;
            };

            bool processNativeHit = controller.InterceptHit(
                targetHurtbox,
                targetObject.transform.position,
                Vector3.up,
                null);

            Assert.That(processNativeHit, Is.False);
            Assert.That(presentations, Is.EqualTo(1));

            manager.ReceiveHitBroadcast(new NetworkMeleeHitBroadcast
            {
                AttackerNetworkId = 71,
                TargetNetworkId = 72,
                HitPoint = targetObject.transform.position,
                StrikeDirection = Vector3.up,
                SkillHash = 0,
                BlockResult = (byte)NetworkBlockResult.None
            });

            Assert.That(presentations, Is.EqualTo(1));
        }

        [Test]
        public void DamageHandler_DoesNotSuppressIndependentReactionOverride()
        {
            NetworkMeleeManager manager = CreateManager(true, false);
            int damageCalls = 0;
            int reactionCalls = 0;
            manager.TryApplyDamageFunc = (_, _) =>
            {
                damageCalls++;
                return true;
            };
            manager.TryApplyAuthoritativeReactionFunc = _ =>
            {
                reactionCalls++;
                return true;
            };

            var request = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 61,
                AttackerNetworkId = 61,
                TargetNetworkId = 62,
                SkillHash = 9001
            };
            var context = new NetworkMeleeReactionContext(
                request,
                NetworkBlockResult.None,
                10f,
                null,
                null,
                null,
                new GameCreator.Runtime.Characters.ReactionInput(Vector3.up, 4f));

            bool damageHandled = InvokePrivateResult<bool>(
                manager,
                "ApplyDamageOnServer",
                request,
                10f);
            bool reactionHandled = InvokePrivateResult<bool>(
                manager,
                "ApplyAuthoritativeReactionOnServer",
                context,
                null);

            Assert.That(damageHandled, Is.True);
            Assert.That(reactionHandled, Is.True);
            Assert.That(damageCalls, Is.EqualTo(1));
            Assert.That(reactionCalls, Is.EqualTo(1));
        }

        [Test]
        public void HitValidation_MissingAuthoredSkillFailsClosedBeforeGameplay()
        {
            GameObject attackerObject = Track(new GameObject("Melee Missing Skill Attacker"));
            attackerObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = attackerObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(79);
            NetworkMeleeController controller = attackerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, false);

            NetworkMeleeHitResponse response = controller.ProcessHitRequest(
                new NetworkMeleeHitRequest
                {
                    RequestId = 3,
                    ActorNetworkId = 79,
                    AttackerNetworkId = 79,
                    TargetNetworkId = 80,
                    SkillHash = 0,
                    WeaponHash = 0,
                    AttackPhase = (byte)MeleePhase.Strike
                },
                clientNetworkId: 1);

            Assert.That(response.Validated, Is.False);
            Assert.That(response.RejectionReason, Is.EqualTo(MeleeHitRejectionReason.SkillMismatch));
        }

        [Test]
        public void AttackAuthorization_MatchingTokenSurvivesServerPhaseExit()
        {
            NetworkMeleeController controller = CreateController("Melee Attack Lease");
            controller.Initialize(true, false);

            var skillRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 81,
                CorrelationId = NetworkCorrelation.Compose(81u, 1u),
                SkillHash = 101,
                WeaponHash = 202,
                ComboNodeId = unchecked((int)0x89ABCDEF)
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", skillRequest, 10f);

            var hitRequest = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 81,
                AttackerNetworkId = 81,
                TargetNetworkId = 82,
                AttackCorrelationId = skillRequest.CorrelationId,
                SkillHash = skillRequest.SkillHash,
                WeaponHash = skillRequest.WeaponHash,
                ComboNodeId = skillRequest.ComboNodeId,
                AttackPhase = (byte)MeleePhase.Strike
            };

            (string status, MeleeHitRejectionReason reason) = EvaluateAttackAuthorization(
                controller,
                hitRequest,
                10.5f,
                false);

            Assert.That(status, Is.EqualTo("Authorized"));
            Assert.That(reason, Is.EqualTo(MeleeHitRejectionReason.None));
        }

        [Test]
        public void ComboContinuation_AfterComboWindowAllowsFreshInOrderRootWhileHitLeaseRemainsValid()
        {
            NetworkMeleeController controller = CreateController("Melee Combo Lease Boundary");
            controller.Initialize(true, false);

            MeleeWeapon weapon = CreateComboWeapon(
                out ComboTree comboTree,
                out Skill previousSkill);
            int previousComboId = comboTree.AddToRoot(
                CreateComboItem(previousSkill, MeleeExecute.AnyTime));
            Skill freshRootSkill = Track(ScriptableObject.CreateInstance<Skill>());
            freshRootSkill.name = "Fresh In-Order Air Root";
            int freshRootComboId = comboTree.AddToRoot(
                CreateComboItem(freshRootSkill, MeleeExecute.InOrder));

            const uint actorNetworkId = 83;
            const int weaponHash = 204;
            var previousRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 1u),
                SkillHash = StableHashUtility.GetStableHash(previousSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = previousComboId,
                PreviousComboNodeId = ComboTree.NODE_INVALID,
                ClientTimestamp = 10f,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", previousRequest, 10f);
            SetAttackLeaseWindows(
                controller,
                previousRequest.CorrelationId,
                comboActiveUntil: 10.5f,
                hitValidUntil: 11.25f);

            var freshRootRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 2u),
                SkillHash = StableHashUtility.GetStableHash(freshRootSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = freshRootComboId,
                PreviousComboNodeId = ComboTree.NODE_INVALID,
                ClientTimestamp = 10.75f,
                InputKey = (byte)MeleeKey.A
            };

            bool continuationAllowed = InvokePrivateResult<bool>(
                controller,
                "IsAuthorizedServerComboContinuation",
                freshRootRequest,
                weapon,
                freshRootSkill,
                10.75f);
            var previousHit = new NetworkMeleeHitRequest
            {
                ActorNetworkId = actorNetworkId,
                AttackerNetworkId = actorNetworkId,
                TargetNetworkId = 84,
                AttackCorrelationId = previousRequest.CorrelationId,
                SkillHash = previousRequest.SkillHash,
                WeaponHash = previousRequest.WeaponHash,
                ComboNodeId = previousRequest.ComboNodeId,
                AttackPhase = (byte)MeleePhase.Strike
            };

            Assert.That(
                continuationAllowed,
                Is.True,
                "An expired combo window must not turn the hit-packet grace period into a combo lock.");
            Assert.That(
                EvaluateAttackAuthorization(controller, previousHit, 10.75f, false).status,
                Is.EqualTo("Authorized"),
                "The previous attack's dependent hit must remain valid throughout its separate hit grace window.");
        }

        [Test]
        public void ComboContinuation_NoPriorLeaseDoesNotAuthorizeFreshRootBusyBypass()
        {
            NetworkMeleeController controller = CreateController("Melee No-Lease Fresh Root");
            controller.Initialize(true, false);

            MeleeWeapon weapon = CreateComboWeapon(
                out ComboTree comboTree,
                out _);
            Skill freshRootSkill = Track(ScriptableObject.CreateInstance<Skill>());
            freshRootSkill.name = "No-Lease Fresh Root";
            int freshRootComboId = comboTree.AddToRoot(
                CreateComboItem(freshRootSkill, MeleeExecute.InOrder));

            bool continuationAllowed = IsComboContinuationAllowed(
                controller,
                weapon,
                freshRootSkill,
                actorNetworkId: 84,
                weaponHash: 205,
                comboNodeId: freshRootComboId,
                previousComboNodeId: ComboTree.NODE_INVALID,
                now: 12f);

            Assert.That(
                continuationAllowed,
                Is.False,
                "A root with no prior authoritative lease is an initial attack, not a combo " +
                "continuation that may bypass an unrelated Busy state.");
        }

        [Test]
        public void SkillRateLimit_InitializationUsesUnboundedFirstRequestSentinel()
        {
            NetworkMeleeController controller = CreateController("Melee First Skill Rate Limit");

            Assert.That(
                GetPrivateField<float>(controller, "m_LastValidatedSkillRequestTime"),
                Is.EqualTo(float.NegativeInfinity),
                "A newly created controller must not rate-limit its first request near time zero.");

            SetPrivateField(controller, "m_LastValidatedSkillRequestTime", 10f);
            controller.Initialize(true, true);

            float resetValue = GetPrivateField<float>(
                controller,
                "m_LastValidatedSkillRequestTime");
            Assert.That(
                resetValue,
                Is.EqualTo(float.NegativeInfinity),
                "Role initialization must reset rate limiting so the first request of the new " +
                "network lifecycle is accepted immediately.");
            Assert.That(0f - resetValue, Is.GreaterThanOrEqualTo(0.05f));
        }

        [Test]
        public void SkillRejection_RecordsCurrentIdentityButIgnoresUnrelatedStaleResponse()
        {
            GameObject actorObject = Track(new GameObject("Melee Rejection Identity Actor"));
            actorObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = actorObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(86);
            NetworkMeleeController controller = actorObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(true, true);

            uint rejectedCorrelation = NetworkCorrelation.Compose(86u, 1u);
            SetPrivateField(controller, "m_CurrentLocalAttackCorrelationId", rejectedCorrelation);
            SetPrivateField(controller, "m_CurrentLocalAttackSkillHash", 1301);
            SetPrivateField(controller, "m_CurrentLocalAttackWeaponHash", 1302);
            SetPrivateField(controller, "m_CurrentLocalAttackComboNodeId", 1303);

            controller.ReceiveSkillResponse(new NetworkSkillResponse
            {
                RequestId = 1,
                ActorNetworkId = 86,
                CorrelationId = rejectedCorrelation,
                Validated = false,
                RejectionReason = SkillRejectionReason.CharacterBusy,
                ComboNodeId = 1303
            });

            Assert.That(
                GetPrivateField<uint>(controller, "m_LastRejectedAttackCorrelationId"),
                Is.EqualTo(rejectedCorrelation));
            Assert.That(
                GetPrivateField<int>(controller, "m_LastRejectedAttackSkillHash"),
                Is.EqualTo(1301));
            Assert.That(
                GetPrivateField<int>(controller, "m_LastRejectedAttackWeaponHash"),
                Is.EqualTo(1302));
            Assert.That(
                GetPrivateField<int>(controller, "m_LastRejectedAttackComboNodeId"),
                Is.EqualTo(1303));
            Assert.That(
                GetPrivateField<SkillRejectionReason>(controller, "m_LastRejectedAttackReason"),
                Is.EqualTo(SkillRejectionReason.CharacterBusy));
            Assert.That(
                GetPrivateField<uint>(controller, "m_CurrentLocalAttackCorrelationId"),
                Is.Zero,
                "The rejection for the exact current operation must clear that operation.");

            uint currentCorrelation = NetworkCorrelation.Compose(86u, 2u);
            SetPrivateField(controller, "m_CurrentLocalAttackCorrelationId", currentCorrelation);
            SetPrivateField(controller, "m_CurrentLocalAttackSkillHash", 1401);
            SetPrivateField(controller, "m_CurrentLocalAttackWeaponHash", 1402);
            SetPrivateField(controller, "m_CurrentLocalAttackComboNodeId", 1403);

            controller.ReceiveSkillResponse(new NetworkSkillResponse
            {
                RequestId = 3,
                ActorNetworkId = 86,
                CorrelationId = NetworkCorrelation.Compose(86u, 3u),
                Validated = false,
                RejectionReason = SkillRejectionReason.InvalidComboTransition,
                ComboNodeId = 1503
            });

            Assert.That(
                GetPrivateField<uint>(controller, "m_CurrentLocalAttackCorrelationId"),
                Is.EqualTo(currentCorrelation),
                "A stale rejection must not cancel the unrelated current operation.");
            Assert.That(
                GetPrivateField<uint>(controller, "m_LastRejectedAttackCorrelationId"),
                Is.EqualTo(rejectedCorrelation),
                "A stale/unknown response must not be attributed to the active attack.");
            Assert.That(
                GetPrivateField<int>(controller, "m_LastRejectedAttackSkillHash"),
                Is.EqualTo(1301));
            Assert.That(
                GetPrivateField<SkillRejectionReason>(controller, "m_LastRejectedAttackReason"),
                Is.EqualTo(SkillRejectionReason.CharacterBusy));
        }

        [Test]
        public void ComboContinuation_DuringComboWindowAllowsOnlyChildOrAnyTimeRoot()
        {
            NetworkMeleeController controller = CreateController("Melee Active Combo Lease");
            controller.Initialize(true, false);

            MeleeWeapon weapon = CreateComboWeapon(
                out ComboTree comboTree,
                out Skill previousSkill);
            int previousComboId = comboTree.AddToRoot(
                CreateComboItem(previousSkill, MeleeExecute.AnyTime));

            Skill childSkill = Track(ScriptableObject.CreateInstance<Skill>());
            childSkill.name = "Ordered Air Combo Child";
            int childComboId = comboTree.AddChild(
                CreateComboItem(childSkill, MeleeExecute.InOrder),
                previousComboId);

            Skill anyTimeRootSkill = Track(ScriptableObject.CreateInstance<Skill>());
            anyTimeRootSkill.name = "Any-Time Air Root";
            int anyTimeRootComboId = comboTree.AddToRoot(
                CreateComboItem(anyTimeRootSkill, MeleeExecute.AnyTime));

            Skill inOrderRootSkill = Track(ScriptableObject.CreateInstance<Skill>());
            inOrderRootSkill.name = "Premature In-Order Air Root";
            int inOrderRootComboId = comboTree.AddToRoot(
                CreateComboItem(inOrderRootSkill, MeleeExecute.InOrder));

            const uint actorNetworkId = 85;
            const int weaponHash = 206;
            var previousRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 1u),
                SkillHash = StableHashUtility.GetStableHash(previousSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = previousComboId,
                PreviousComboNodeId = ComboTree.NODE_INVALID,
                ClientTimestamp = 20f,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", previousRequest, 20f);
            SetAttackLeaseWindows(
                controller,
                previousRequest.CorrelationId,
                comboActiveUntil: 20.5f,
                hitValidUntil: 21.25f);

            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    childSkill,
                    actorNetworkId,
                    weaponHash,
                    childComboId,
                    previousComboId,
                    20.25f),
                Is.True,
                "A direct child is the normal in-order continuation while the prior combo is active.");
            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    childSkill,
                    actorNetworkId,
                    weaponHash,
                    childComboId,
                    ComboTree.NODE_INVALID,
                    20.25f),
                Is.False,
                "A client cannot claim an active child transition without naming its exact prior combo node.");
            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    anyTimeRootSkill,
                    actorNetworkId,
                    weaponHash,
                    anyTimeRootComboId,
                    previousComboId,
                    20.25f),
                Is.True,
                "An AnyTime root may interrupt an active combo.");
            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    inOrderRootSkill,
                    actorNetworkId,
                    weaponHash,
                    inOrderRootComboId,
                    previousComboId,
                    20.25f),
                Is.False,
                "A separate InOrder root is not a legal interrupt while the prior combo is active.");
        }

        [Test]
        public void ComboContinuation_OverlappingLeasesUsesNewestAcceptedPredecessor()
        {
            NetworkMeleeController controller = CreateController("Melee Overlapping Combo Leases");
            controller.Initialize(true, false);

            MeleeWeapon weapon = CreateComboWeapon(
                out ComboTree comboTree,
                out Skill olderSkill);
            int olderComboId = comboTree.AddToRoot(
                CreateComboItem(olderSkill, MeleeExecute.AnyTime));
            Skill olderChildSkill = Track(ScriptableObject.CreateInstance<Skill>());
            olderChildSkill.name = "Obsolete Combo Child";
            int olderChildComboId = comboTree.AddChild(
                CreateComboItem(olderChildSkill, MeleeExecute.InOrder),
                olderComboId);

            Skill newestSkill = Track(ScriptableObject.CreateInstance<Skill>());
            newestSkill.name = "Newest Accepted Combo";
            int newestComboId = comboTree.AddToRoot(
                CreateComboItem(newestSkill, MeleeExecute.AnyTime));
            Skill newestChildSkill = Track(ScriptableObject.CreateInstance<Skill>());
            newestChildSkill.name = "Newest Combo Child";
            int newestChildComboId = comboTree.AddChild(
                CreateComboItem(newestChildSkill, MeleeExecute.InOrder),
                newestComboId);

            const uint actorNetworkId = 87;
            const int weaponHash = 208;
            var olderRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 1u),
                ClientTimestamp = 30f,
                SkillHash = StableHashUtility.GetStableHash(olderSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = olderComboId,
                PreviousComboNodeId = ComboTree.NODE_INVALID,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", olderRequest, 30f);
            SetAttackLeaseWindows(
                controller,
                olderRequest.CorrelationId,
                comboActiveUntil: 31f,
                hitValidUntil: 31.75f);

            var newestRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 2u),
                ClientTimestamp = 30.1f,
                SkillHash = StableHashUtility.GetStableHash(newestSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = newestComboId,
                PreviousComboNodeId = olderComboId,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", newestRequest, 30.1f);
            SetAttackLeaseWindows(
                controller,
                newestRequest.CorrelationId,
                comboActiveUntil: 31f,
                hitValidUntil: 31.75f);

            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    olderChildSkill,
                    actorNetworkId,
                    weaponHash,
                    olderChildComboId,
                    olderComboId,
                    30.25f),
                Is.False,
                "An older still-active lease cannot roll the combo state back past a newer accepted operation.");
            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    newestChildSkill,
                    actorNetworkId,
                    weaponHash,
                    newestChildComboId,
                    newestComboId,
                    30.25f),
                Is.True,
                "The newest accepted active combo remains the only valid predecessor.");
        }

        [Test]
        public void ComboContinuation_EqualAcceptedAtUsesLaterMonotonicAcceptVersion()
        {
            NetworkMeleeController controller = CreateController("Melee Equal-Time Combo Leases");
            controller.Initialize(true, false);

            MeleeWeapon weapon = CreateComboWeapon(
                out ComboTree comboTree,
                out Skill olderSkill);
            int olderComboId = comboTree.AddToRoot(
                CreateComboItem(olderSkill, MeleeExecute.AnyTime));
            Skill olderChildSkill = Track(ScriptableObject.CreateInstance<Skill>());
            olderChildSkill.name = "Equal-Time Obsolete Child";
            int olderChildComboId = comboTree.AddChild(
                CreateComboItem(olderChildSkill, MeleeExecute.InOrder),
                olderComboId);

            Skill laterSkill = Track(ScriptableObject.CreateInstance<Skill>());
            laterSkill.name = "Equal-Time Later Accepted Combo";
            int laterComboId = comboTree.AddToRoot(
                CreateComboItem(laterSkill, MeleeExecute.AnyTime));
            Skill laterChildSkill = Track(ScriptableObject.CreateInstance<Skill>());
            laterChildSkill.name = "Equal-Time Current Child";
            int laterChildComboId = comboTree.AddChild(
                CreateComboItem(laterChildSkill, MeleeExecute.InOrder),
                laterComboId);

            const uint actorNetworkId = 88;
            const int weaponHash = 209;
            const float acceptedAt = 35f;
            var olderRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 1u),
                ClientTimestamp = acceptedAt,
                SkillHash = StableHashUtility.GetStableHash(olderSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = olderComboId,
                PreviousComboNodeId = ComboTree.NODE_INVALID,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", olderRequest, acceptedAt);
            SetAttackLeaseWindows(
                controller,
                olderRequest.CorrelationId,
                comboActiveUntil: 36f,
                hitValidUntil: 36.75f);

            var laterRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 2u),
                ClientTimestamp = acceptedAt,
                SkillHash = StableHashUtility.GetStableHash(laterSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = laterComboId,
                PreviousComboNodeId = olderComboId,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", laterRequest, acceptedAt);
            SetAttackLeaseWindows(
                controller,
                laterRequest.CorrelationId,
                comboActiveUntil: 36f,
                hitValidUntil: 36.75f);

            IDictionary leases = GetPrivateField<IDictionary>(
                controller,
                "m_ServerAttackLeases");
            object olderLease = leases[olderRequest.CorrelationId];
            object laterLease = leases[laterRequest.CorrelationId];
            Assert.That(GetPrivateField<float>(olderLease, "AcceptedAt"), Is.EqualTo(acceptedAt));
            Assert.That(GetPrivateField<float>(laterLease, "AcceptedAt"), Is.EqualTo(acceptedAt));
            Assert.That(
                GetPrivateField<ulong>(laterLease, "AcceptVersion"),
                Is.GreaterThan(GetPrivateField<ulong>(olderLease, "AcceptVersion")));

            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    olderChildSkill,
                    actorNetworkId,
                    weaponHash,
                    olderChildComboId,
                    olderComboId,
                    acceptedAt + 0.1f),
                Is.False,
                "Equal floating-point timestamps must not let the older lease win iteration order.");
            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    laterChildSkill,
                    actorNetworkId,
                    weaponHash,
                    laterChildComboId,
                    laterComboId,
                    acceptedAt + 0.1f),
                Is.True,
                "The later monotonic acceptance version is the authoritative predecessor.");
        }

        [Test]
        public void ComboContinuation_ExpiredNewestLeaseDoesNotResurrectOlderActiveLease()
        {
            NetworkMeleeController controller = CreateController("Melee Superseded Combo Lease");
            controller.Initialize(true, false);

            MeleeWeapon weapon = CreateComboWeapon(
                out ComboTree comboTree,
                out Skill olderSkill);
            int olderComboId = comboTree.AddToRoot(
                CreateComboItem(olderSkill, MeleeExecute.AnyTime));
            Skill olderChildSkill = Track(ScriptableObject.CreateInstance<Skill>());
            olderChildSkill.name = "Superseded Older Combo Child";
            int olderChildComboId = comboTree.AddChild(
                CreateComboItem(olderChildSkill, MeleeExecute.InOrder),
                olderComboId);

            Skill newestSkill = Track(ScriptableObject.CreateInstance<Skill>());
            newestSkill.name = "Short Newest Combo";
            int newestComboId = comboTree.AddToRoot(
                CreateComboItem(newestSkill, MeleeExecute.AnyTime));

            Skill freshRootSkill = Track(ScriptableObject.CreateInstance<Skill>());
            freshRootSkill.name = "Fresh Root After Newest Ends";
            int freshRootComboId = comboTree.AddToRoot(
                CreateComboItem(freshRootSkill, MeleeExecute.InOrder));

            const uint actorNetworkId = 89;
            const int weaponHash = 210;
            var olderRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 1u),
                ClientTimestamp = 40f,
                SkillHash = StableHashUtility.GetStableHash(olderSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = olderComboId,
                PreviousComboNodeId = ComboTree.NODE_INVALID,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", olderRequest, 40f);
            SetAttackLeaseWindows(
                controller,
                olderRequest.CorrelationId,
                comboActiveUntil: 41f,
                hitValidUntil: 41.75f);

            var newestRequest = new NetworkSkillRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, 2u),
                ClientTimestamp = 40.1f,
                SkillHash = StableHashUtility.GetStableHash(newestSkill.name),
                WeaponHash = weaponHash,
                ComboNodeId = newestComboId,
                PreviousComboNodeId = olderComboId,
                InputKey = (byte)MeleeKey.A
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", newestRequest, 40.1f);
            SetAttackLeaseWindows(
                controller,
                newestRequest.CorrelationId,
                comboActiveUntil: 40.2f,
                hitValidUntil: 41.75f);

            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    olderChildSkill,
                    actorNetworkId,
                    weaponHash,
                    olderChildComboId,
                    olderComboId,
                    40.3f),
                Is.False,
                "An older long-running lease stays superseded after the newest combo window ends.");
            Assert.That(
                IsComboContinuationAllowed(
                    controller,
                    weapon,
                    freshRootSkill,
                    actorNetworkId,
                    weaponHash,
                    freshRootComboId,
                    ComboTree.NODE_INVALID,
                    40.3f),
                Is.True,
                "Once the newest accepted combo ends, GC2 may begin a fresh InOrder root.");
        }

        [Test]
        public void AttackAuthorization_DeduplicatesTargetButAllowsMultiTargetAndNextAttack()
        {
            NetworkMeleeController controller = CreateController("Melee Attack Lease Dedupe");
            controller.Initialize(true, false);

            var skillRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 91,
                CorrelationId = NetworkCorrelation.Compose(91u, 1u),
                SkillHash = 303,
                WeaponHash = 404,
                ComboNodeId = -2012964192
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", skillRequest, 20f);

            var hitRequest = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 91,
                AttackerNetworkId = 91,
                TargetNetworkId = 92,
                AttackCorrelationId = skillRequest.CorrelationId,
                SkillHash = skillRequest.SkillHash,
                WeaponHash = skillRequest.WeaponHash,
                ComboNodeId = skillRequest.ComboNodeId,
                AttackPhase = (byte)MeleePhase.Strike
            };

            Assert.That(
                EvaluateAttackAuthorization(controller, hitRequest, 20.1f, true).status,
                Is.EqualTo("Authorized"));
            (string duplicateStatus, MeleeHitRejectionReason duplicateReason) =
                EvaluateAttackAuthorization(controller, hitRequest, 20.2f, true);
            Assert.That(duplicateStatus, Is.EqualTo("Rejected"));
            Assert.That(duplicateReason, Is.EqualTo(MeleeHitRejectionReason.AlreadyHit));

            hitRequest.TargetNetworkId = 93;
            Assert.That(
                EvaluateAttackAuthorization(controller, hitRequest, 20.2f, true).status,
                Is.EqualTo("Authorized"));

            skillRequest.CorrelationId = NetworkCorrelation.Compose(91u, 2u);
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", skillRequest, 20.3f);
            hitRequest.TargetNetworkId = 92;
            hitRequest.AttackCorrelationId = skillRequest.CorrelationId;
            Assert.That(
                EvaluateAttackAuthorization(controller, hitRequest, 20.4f, true).status,
                Is.EqualTo("Authorized"));
        }

        [Test]
        public void AttackAuthorization_UnknownTokenDefersButZeroAndExpiredReject()
        {
            NetworkMeleeController controller = CreateController("Melee Attack Lease Ordering");
            controller.Initialize(true, false);

            var hitRequest = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 101,
                AttackerNetworkId = 101,
                TargetNetworkId = 102,
                AttackCorrelationId = NetworkCorrelation.Compose(101u, 1u),
                SkillHash = 505,
                WeaponHash = 606,
                ComboNodeId = 707,
                AttackPhase = (byte)MeleePhase.Strike
            };

            Assert.That(
                EvaluateAttackAuthorization(controller, hitRequest, 30f, false).status,
                Is.EqualTo("Pending"));

            hitRequest.AttackCorrelationId = 0;
            (string zeroStatus, MeleeHitRejectionReason zeroReason) =
                EvaluateAttackAuthorization(controller, hitRequest, 30f, false);
            Assert.That(zeroStatus, Is.EqualTo("Rejected"));
            Assert.That(zeroReason, Is.EqualTo(MeleeHitRejectionReason.CheatSuspected));

            uint malformedToken = hitRequest.AttackCorrelationId =
                NetworkCorrelation.Compose(999u, 2u);
            while (NetworkCorrelation.MatchesActor(malformedToken, 101u))
            {
                malformedToken ^= 1u << 16;
            }
            hitRequest.AttackCorrelationId = malformedToken;
            (string malformedStatus, MeleeHitRejectionReason malformedReason) =
                EvaluateAttackAuthorization(controller, hitRequest, 30f, false);
            Assert.That(malformedStatus, Is.EqualTo("Rejected"));
            Assert.That(malformedReason, Is.EqualTo(MeleeHitRejectionReason.CheatSuspected));

            var skillRequest = new NetworkSkillRequest
            {
                ActorNetworkId = 101,
                CorrelationId = NetworkCorrelation.Compose(101u, 3u),
                SkillHash = 505,
                WeaponHash = 606,
                ComboNodeId = 707
            };
            InvokePrivate(controller, "RecordAuthoritativeAttackLease", skillRequest, 30f);
            hitRequest.AttackCorrelationId = skillRequest.CorrelationId;
            Assert.That(
                EvaluateAttackAuthorization(controller, hitRequest, 31.6f, false).status,
                Is.EqualTo("Rejected"));
        }

        [TestCase(NetworkBlockResult.Blocked)]
        [TestCase(NetworkBlockResult.Parried)]
        public void FullyDefendedHit_DoesNotInvokeReactionOverride(NetworkBlockResult blockResult)
        {
            NetworkMeleeManager manager = CreateManager(true, false);
            int reactionCalls = 0;
            manager.TryApplyAuthoritativeReactionFunc = _ =>
            {
                reactionCalls++;
                return true;
            };

            var request = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 81,
                AttackerNetworkId = 81,
                TargetNetworkId = 82
            };
            var context = new NetworkMeleeReactionContext(
                request,
                blockResult,
                0f,
                null,
                null,
                null,
                new GameCreator.Runtime.Characters.ReactionInput(Vector3.forward, 1f));

            bool handled = InvokePrivateResult<bool>(
                manager,
                "ApplyAuthoritativeReactionOnServer",
                context,
                null);

            Assert.That(handled, Is.False);
            Assert.That(reactionCalls, Is.Zero);
        }

        [TestCase(NetworkBlockResult.None)]
        [TestCase(NetworkBlockResult.BlockBroken)]
        public void UndefendedOrBrokenBlockHit_InvokesReactionOverride(NetworkBlockResult blockResult)
        {
            NetworkMeleeManager manager = CreateManager(true, false);
            int reactionCalls = 0;
            manager.TryApplyAuthoritativeReactionFunc = _ =>
            {
                reactionCalls++;
                return true;
            };

            var request = new NetworkMeleeHitRequest
            {
                ActorNetworkId = 91,
                AttackerNetworkId = 91,
                TargetNetworkId = 92
            };
            var context = new NetworkMeleeReactionContext(
                request,
                blockResult,
                5f,
                null,
                null,
                null,
                new GameCreator.Runtime.Characters.ReactionInput(Vector3.up, 3f));

            bool handled = InvokePrivateResult<bool>(
                manager,
                "ApplyAuthoritativeReactionOnServer",
                context,
                null);

            Assert.That(handled, Is.True);
            Assert.That(reactionCalls, Is.EqualTo(1));
        }

        [Test]
        public void WeaponStateEvent_PreservesLegacyAndIncludesSender()
        {
            NetworkMeleeController controller = CreateController("Melee Sender Event");
            var expected = new NetworkMeleeWeaponState { WeaponHash = 17, ShieldFlags = 1 };

            int legacyCount = 0;
            int senderAwareCount = 0;
            NetworkMeleeController actualSender = null;
            controller.OnWeaponStateChanged += _ => legacyCount++;
            controller.OnWeaponStateChangedWithSender += (sender, _) =>
            {
                senderAwareCount++;
                actualSender = sender;
            };

            InvokePrivate(controller, "RaiseWeaponStateChanged", expected);

            Assert.That(legacyCount, Is.EqualTo(1));
            Assert.That(senderAwareCount, Is.EqualTo(1));
            Assert.That(actualSender, Is.SameAs(controller));
        }

        [Test]
        public void RemoteWeaponApplyVersion_ConvergesToNewestSynchronousState()
        {
            GameObject controllerObject = Track(new GameObject("Melee Version Test"));
            controllerObject.AddComponent<Character>();
            NetworkMeleeController controller = controllerObject.AddComponent<NetworkMeleeController>();
            controller.Initialize(false, false);

            controller.ApplyRemoteWeaponState(
                new NetworkMeleeWeaponState { WeaponHash = 0, ShieldFlags = 1 },
                null);
            controller.ApplyRemoteWeaponState(
                new NetworkMeleeWeaponState { WeaponHash = 0, ShieldFlags = 4 },
                null);

            uint requested = GetPrivateField<uint>(controller, "m_RemoteWeaponApplyVersion");
            uint applied = GetPrivateField<uint>(controller, "m_AppliedRemoteWeaponVersion");
            NetworkMeleeWeaponState state = GetPrivateField<NetworkMeleeWeaponState>(
                controller,
                "m_LastWeaponState");
            Assert.That(requested, Is.EqualTo(2));
            Assert.That(applied, Is.EqualTo(requested));
            Assert.That(state.ShieldFlags, Is.EqualTo(4));
        }

        private NetworkMeleeManager CreateManager(bool isServer, bool isClient)
        {
            GameObject managerObject = Track(new GameObject("Melee Manager Test"));
            NetworkMeleeManager manager = managerObject.AddComponent<NetworkMeleeManager>();
            manager.Initialize(isServer, isClient);
            return manager;
        }

        private NetworkMeleeController CreateController(string name)
        {
            return Track(new GameObject(name)).AddComponent<NetworkMeleeController>();
        }

        private MeleeWeapon CreateComboWeapon(out ComboTree comboTree, out Skill previousSkill)
        {
            MeleeWeapon weapon = Track(ScriptableObject.CreateInstance<MeleeWeapon>());
            weapon.name = "Melee Combo Lease Weapon";

            object selector = GetPrivateField<object>(weapon, "m_Combos");
            FieldInfo sourceField = selector.GetType().GetField(
                "m_CombosFrom",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(sourceField, Is.Not.Null, "Missing ComboSelector source field");
            sourceField.SetValue(selector, System.Enum.ToObject(sourceField.FieldType, 1));
            comboTree = GetPrivateField<ComboTree>(selector, "m_CombosEmbed");

            previousSkill = Track(ScriptableObject.CreateInstance<Skill>());
            previousSkill.name = "Previous Air Combo Skill";
            return weapon;
        }

        private static ComboItem CreateComboItem(Skill skill, MeleeExecute execute)
        {
            var item = new ComboItem();
            SetPrivateField(item, "m_Skill", skill);
            SetPrivateField(item, "m_When", execute);
            return item;
        }

        private static void SetForcedDiagnostics(bool enabled)
        {
            NetworkMeleeDebug.ForceSkillDiagnostics = enabled;
            NetworkMeleeDebug.ForcePacketDiagnostics = enabled;
            NetworkMeleeDebug.ForceInputLockDiagnostics = enabled;
            NetworkMeleeDebug.ForceReactionDiagnostics = enabled;
        }

        private static void SetAttackLeaseWindows(
            NetworkMeleeController controller,
            uint correlationId,
            float comboActiveUntil,
            float hitValidUntil)
        {
            IDictionary leases = GetPrivateField<IDictionary>(controller, "m_ServerAttackLeases");
            object lease = leases[correlationId];
            Assert.That(lease, Is.Not.Null, $"Missing attack lease {correlationId}");
            SetPrivateField(lease, "ComboActiveUntil", comboActiveUntil);
            SetPrivateField(lease, "ExpiresAt", hitValidUntil);
        }

        private static bool IsComboContinuationAllowed(
            NetworkMeleeController controller,
            MeleeWeapon weapon,
            Skill skill,
            uint actorNetworkId,
            int weaponHash,
            int comboNodeId,
            int previousComboNodeId,
            float now)
        {
            return InvokePrivateResult<bool>(
                controller,
                "IsAuthorizedServerComboContinuation",
                new NetworkSkillRequest
                {
                    ActorNetworkId = actorNetworkId,
                    CorrelationId = NetworkCorrelation.Compose(actorNetworkId, unchecked((uint)comboNodeId)),
                    SkillHash = StableHashUtility.GetStableHash(skill.name),
                    WeaponHash = weaponHash,
                    ComboNodeId = comboNodeId,
                    PreviousComboNodeId = previousComboNodeId,
                    ClientTimestamp = now,
                    InputKey = (byte)MeleeKey.A
                },
                weapon,
                skill,
                now);
        }

        private static NetworkMeleeCharacterSnapshot RoundTrip(NetworkMeleeCharacterSnapshot value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetMeleeValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkMeleeCharacterSnapshot result = default;
            PurrNetMeleeValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkReactionBroadcast RoundTrip(NetworkReactionBroadcast value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetMeleeValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkReactionBroadcast result = default;
            PurrNetMeleeValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkMeleeHitRequest RoundTrip(NetworkMeleeHitRequest value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetMeleeValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkMeleeHitRequest result = default;
            PurrNetMeleeValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkSkillRequest RoundTrip(NetworkSkillRequest value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetMeleeValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkSkillRequest result = default;
            PurrNetMeleeValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkSkillResponse RoundTrip(NetworkSkillResponse value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetMeleeValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkSkillResponse result = default;
            PurrNetMeleeValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkSkillBroadcast RoundTrip(NetworkSkillBroadcast value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetMeleeValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkSkillBroadcast result = default;
            PurrNetMeleeValuePackers.Read(packer, ref result);
            return result;
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }

        private static T GetPrivateField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            return (T)field.GetValue(target);
        }

        private static void SetPrivateField<T>(object target, string name, T value)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
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

        private static T InvokePrivateResult<T>(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {name}");
            return (T)method.Invoke(target, arguments);
        }

        private static (string status, MeleeHitRejectionReason reason) EvaluateAttackAuthorization(
            NetworkMeleeController controller,
            NetworkMeleeHitRequest request,
            float now,
            bool consume)
        {
            MethodInfo method = controller.GetType().GetMethod(
                "EvaluateAuthoritativeAttackAuthorization",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            object[] arguments =
            {
                request,
                now,
                consume,
                MeleeHitRejectionReason.None
            };
            object result = method.Invoke(controller, arguments);
            return (result.ToString(), (MeleeHitRejectionReason)arguments[3]);
        }
    }
}
#endif
