#if GC2_SHOOTER
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.Security;
using Arawn.GameCreator2.Networking.Shooter.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Shooter;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter.Tests
{
    public sealed class ShooterTestTransportBridge : NetworkTransportBridge
    {
        public Character ResolvedCharacter { get; set; }
        public bool Server { get; set; } = true;
        public bool Client { get; set; }
        public bool Host { get; set; }
        public uint LocalClientId { get; set; } = NetworkTransportBridge.InvalidClientId;

        public override bool IsServer => Server;
        public override bool IsClient => Client;
        public override bool IsHost => Host;
        public override float ServerTime => Time.time;

        public override bool TryGetLocalClientId(out uint clientId)
        {
            clientId = LocalClientId;
            return Client && NetworkTransportBridge.IsValidClientId(clientId);
        }

        public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs) { }
        public override void SendToOwner(
            uint ownerClientId,
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime) { }

        public override void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null) { }

        public override Character ResolveCharacter(uint networkId)
        {
            return ResolvedCharacter;
        }
    }

    public sealed class ShooterReplicationTests
    {
        private readonly List<UnityEngine.Object> m_Cleanup = new();

        [SetUp]
        public void SetUp()
        {
            NetworkShooterDebug.ForceDiagnostics = false;
            NetworkShooterManager.ClearRegistries();
            SecurityIntegration.ClearModuleServerContexts();
        }

        [TearDown]
        public void TearDown()
        {
            Transform[] transforms = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null &&
                    (transforms[i].name == "Network Shooter Impact" ||
                     transforms[i].name == "Network Shooter Tracer"))
                {
                    UnityEngine.Object.DestroyImmediate(transforms[i].gameObject);
                }
            }

            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
            NetworkShooterManager.ClearRegistries();
            SecurityIntegration.ClearModuleServerContexts();
        }

        [Test]
        public void ReactionMotionWindow_UsesAuthoredDurationAndGrace()
        {
            var output = new ReactionOutput(2.4f, 0.5f, 0f, 0f, null);

            float duration = NetworkShooterReactionContext.CalculateOwnerMotionWindow(output);

            Assert.That(duration, Is.EqualTo(4.95f).Within(0.001f));
        }

        [Test]
        public void ReactionMotionWindow_ClampsNearZeroAndInvalidDurations()
        {
            var nearZero = new ReactionOutput(2f, 0.00001f, 0f, 0f, null);
            var invalid = new ReactionOutput(2f, float.NaN, 0f, 0f, null);

            Assert.That(
                NetworkShooterReactionContext.CalculateOwnerMotionWindow(nearZero),
                Is.EqualTo(NetworkShooterReactionContext.DefaultOwnerMotionWindowSeconds));
            Assert.That(
                NetworkShooterReactionContext.CalculateOwnerMotionWindow(invalid),
                Is.EqualTo(NetworkShooterReactionContext.DefaultOwnerMotionWindowSeconds));

            var context = new NetworkShooterReactionContext(
                default,
                0f,
                null,
                null,
                null,
                Vector3.zero,
                NetworkBlockResult.None,
                default,
                null);

            context.SetOwnerMotionWindow(100f);
            Assert.That(
                context.OwnerMotionWindowSeconds,
                Is.EqualTo(NetworkShooterReactionContext.MaximumOwnerMotionWindowSeconds));

            context.SetOwnerMotionWindow(float.PositiveInfinity);
            Assert.That(
                context.OwnerMotionWindowSeconds,
                Is.EqualTo(NetworkShooterReactionContext.DefaultOwnerMotionWindowSeconds));
        }

        [Test]
        public void PersistentSnapshots_PurrNetRoundTrip_PreserveCharacterAndImpactMotion()
        {
            var character = new NetworkShooterCharacterSnapshot
            {
                CharacterNetworkId = 22,
                WeaponState = new NetworkWeaponState
                {
                    WeaponHash = 901,
                    SightHash = 44,
                    AmmoInMagazine = 17,
                    StateFlags = NetworkWeaponState.FLAG_IS_AIMING |
                                 NetworkWeaponState.FLAG_IS_RELOADING,
                    LeanAmount = 11.5f,
                    LeanDecay = 0.4f
                },
                AimState = new NetworkAimState
                {
                    AimPoint = new Vector3(4f, 5f, 6f),
                    Accuracy = 13,
                    IsAiming = true,
                    CompressedDirection = 65000
                },
                ServerTime = 31.5f
            };

            NetworkShooterCharacterSnapshot characterResult = RoundTrip(character);
            Assert.That(characterResult.CharacterNetworkId, Is.EqualTo(22));
            Assert.That(characterResult.WeaponState.WeaponHash, Is.EqualTo(901));
            Assert.That(characterResult.WeaponState.AmmoInMagazine, Is.EqualTo(17));
            Assert.That(characterResult.WeaponState.LeanAmount, Is.EqualTo(11.5f));
            Assert.That(characterResult.AimState.AimPoint, Is.EqualTo(character.AimState.AimPoint));
            Assert.That(characterResult.AimState.CompressedDirection, Is.EqualTo(65000));

            var motion = new NetworkShooterImpactMotion
            {
                PropNetworkId = 77,
                StartPosition = Vector3.one,
                StartRotation = Quaternion.Euler(0f, 15f, 0f),
                TargetPosition = new Vector3(8f, 1f, 2f),
                TargetRotation = Quaternion.Euler(0f, 90f, 20f),
                HitPoint = new Vector3(1f, 2f, 3f),
                ImpactDirection = Vector3.right,
                StartTime = 30f,
                Duration = 0.75f,
                ImpactStrength = 5f
            };
            var impact = new NetworkShooterImpactPropSnapshot
            {
                PropNetworkId = 77,
                Position = new Vector3(2f, 3f, 4f),
                Rotation = Quaternion.Euler(5f, 10f, 15f),
                HasActiveMotion = true,
                ActiveMotion = motion,
                ServerTime = 31f
            };

            NetworkShooterImpactPropSnapshot impactResult = RoundTrip(impact);
            Assert.That(impactResult.PropNetworkId, Is.EqualTo(77));
            Assert.That(impactResult.HasActiveMotion, Is.True);
            Assert.That(impactResult.ActiveMotion.TargetPosition, Is.EqualTo(motion.TargetPosition));
            Assert.That(impactResult.ActiveMotion.Duration, Is.EqualTo(0.75f));
            Assert.That(Quaternion.Angle(impactResult.Rotation, impact.Rotation), Is.LessThan(0.001f));
        }

        [TestCase(NetworkBlockResult.Blocked)]
        [TestCase(NetworkBlockResult.Parried)]
        [TestCase(NetworkBlockResult.BlockBroken)]
        [TestCase(NetworkBlockResult.UnresolvedNative)]
        public void HitPackets_PurrNetRoundTrip_PreserveAuthoritativeBlockOutcome(
            NetworkBlockResult blockResult)
        {
            var response = new NetworkShooterHitResponse
            {
                RequestId = 8,
                ActorNetworkId = 10,
                CorrelationId = 11,
                Validated = true,
                Damage = 12f,
                BlockResult = blockResult
            };
            NetworkShooterHitResponse responseResult = RoundTrip(response);
            Assert.That(responseResult.BlockResult, Is.EqualTo(blockResult));

            var broadcast = new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 10,
                TargetNetworkId = 20,
                BlockResult = (byte)blockResult,
                ReactionPower = 37.25f
            };
            NetworkShooterHitBroadcast broadcastResult = RoundTrip(broadcast);
            Assert.That(
                (NetworkBlockResult)broadcastResult.BlockResult,
                Is.EqualTo(blockResult));
            Assert.That(broadcastResult.ReactionPower, Is.EqualTo(37.25f));
        }

        [Test]
        public void RemoteReactionPower_UsesServerBroadcastAndRejectsNonFiniteValues()
        {
            var broadcast = new NetworkShooterHitBroadcast { ReactionPower = -8.5f };
            float authoritativePower = InvokePrivateStatic<float>(
                typeof(NetworkShooterController),
                "ResolveBroadcastReactionPower",
                broadcast);

            Assert.That(
                authoritativePower,
                Is.EqualTo(-8.5f),
                "The observing client must use the server-evaluated power verbatim, not its local weapon property.");

            broadcast.ReactionPower = float.NaN;
            Assert.That(
                InvokePrivateStatic<float>(
                    typeof(NetworkShooterController),
                    "ResolveBroadcastReactionPower",
                    broadcast),
                Is.Zero);
        }

        [Test]
        public void ImpactPropSnapshot_IgnoresOlderStateAndContinuesActiveMotion()
        {
            GameObject propObject = Track(new GameObject("Impact Prop Test"));
            propObject.SetActive(false);
            NetworkShooterImpactProp prop = propObject.AddComponent<NetworkShooterImpactProp>();
            SetPrivateField(prop, "m_UseAutomaticNetworkId", false);
            SetPrivateField(prop, "m_ManualNetworkId", 77u);
            propObject.SetActive(true);

            float now = Time.time;

            prop.ApplySnapshot(new NetworkShooterImpactPropSnapshot
            {
                PropNetworkId = 77,
                Position = Vector3.one,
                Rotation = Quaternion.identity,
                ServerTime = now - 1f
            });
            prop.ApplySnapshot(new NetworkShooterImpactPropSnapshot
            {
                PropNetworkId = 77,
                Position = Vector3.one * 9f,
                Rotation = Quaternion.Euler(0f, 90f, 0f),
                ServerTime = now - 2f
            });
            Assert.That(propObject.transform.position, Is.EqualTo(Vector3.one));

            prop.ApplySnapshot(new NetworkShooterImpactPropSnapshot
            {
                PropNetworkId = 77,
                HasActiveMotion = true,
                ActiveMotion = new NetworkShooterImpactMotion
                {
                    PropNetworkId = 77,
                    StartPosition = Vector3.zero,
                    StartRotation = Quaternion.identity,
                    TargetPosition = Vector3.right * 10f,
                    TargetRotation = Quaternion.Euler(0f, 90f, 0f),
                    StartTime = now - 0.5f,
                    Duration = 1f,
                    ImpactDirection = Vector3.right,
                    ImpactStrength = 1f
                },
                ServerTime = now
            });

            Assert.That(propObject.transform.position.x, Is.GreaterThan(0f));
            Assert.That(propObject.transform.position.x, Is.LessThanOrEqualTo(10f));
            NetworkShooterImpactPropSnapshot captured = prop.CaptureSnapshot(now);
            Assert.That(captured.PropNetworkId, Is.EqualTo(77));
            Assert.That(captured.HasActiveMotion, Is.True);

            Vector3 authoritativePose = Vector3.one * 20f;
            prop.ApplySnapshot(new NetworkShooterImpactPropSnapshot
            {
                PropNetworkId = 77,
                Position = authoritativePose,
                Rotation = Quaternion.identity,
                ServerTime = now + 0.1f
            });
            prop.ApplyImpactMotion(new NetworkShooterImpactMotion
            {
                PropNetworkId = 77,
                StartPosition = Vector3.one * 90f,
                StartRotation = Quaternion.identity,
                TargetPosition = Vector3.one * 100f,
                TargetRotation = Quaternion.identity,
                StartTime = now - 0.1f,
                Duration = 10f
            });
            Assert.That(propObject.transform.position, Is.EqualTo(authoritativePose));
        }

        [Test]
        public void PendingPersistentState_ReplacesOlderValueForSameCharacter()
        {
            GameObject bridgeObject = Track(new GameObject("Shooter Bridge Test"));
            bridgeObject.SetActive(false);
            PurrNetShooterTransportBridge bridge =
                bridgeObject.AddComponent<PurrNetShooterTransportBridge>();
            SetPrivateField(bridge, "m_LogDiagnostics", false);

            InvokePrivate(bridge, "ApplyWeaponState", new GC2ShooterWeaponStatePacket
            {
                characterNetworkId = 91,
                state = new NetworkWeaponState { WeaponHash = 100 }
            });
            InvokePrivate(bridge, "ApplyWeaponState", new GC2ShooterWeaponStatePacket
            {
                characterNetworkId = 91,
                state = new NetworkWeaponState { WeaponHash = 200 }
            });

            IDictionary pendingWeapons = GetPrivateField<IDictionary>(bridge, "m_PendingWeaponStates");
            Assert.That(pendingWeapons.Count, Is.EqualTo(1));
            var weapon = (GC2ShooterWeaponStatePacket)pendingWeapons[91u];
            Assert.That(weapon.state.WeaponHash, Is.EqualTo(200));

            InvokePrivate(bridge, "ApplyAimState", new GC2ShooterAimStatePacket
            {
                characterNetworkId = 91,
                state = new NetworkAimState { AimPoint = Vector3.left }
            });
            InvokePrivate(bridge, "ApplyAimState", new GC2ShooterAimStatePacket
            {
                characterNetworkId = 91,
                state = new NetworkAimState { AimPoint = Vector3.right }
            });

            IDictionary pendingAim = GetPrivateField<IDictionary>(bridge, "m_PendingAimStates");
            Assert.That(pendingAim.Count, Is.EqualTo(1));
            var aim = (GC2ShooterAimStatePacket)pendingAim[91u];
            Assert.That(aim.state.AimPoint, Is.EqualTo(Vector3.right));
        }

        [Test]
        public void LegacyWeaponRegistry_IsUsedWhenStructuredRegistryIsEmpty()
        {
            GameObject bridgeObject = Track(new GameObject("Shooter Registry Test"));
            bridgeObject.SetActive(false);
            PurrNetShooterTransportBridge bridge =
                bridgeObject.AddComponent<PurrNetShooterTransportBridge>();
            ShooterWeapon weapon = Track(ScriptableObject.CreateInstance<ShooterWeapon>());
            weapon.name = "Legacy Test Weapon";

            SetPrivateField(bridge, "m_WeaponRegistrations", new ShooterWeaponRegistration[0]);
            SetPrivateField(bridge, "m_RegisterWeapons", new[] { weapon });
            SetPrivateField(bridge, "m_RegisterWeaponPrefabs", new GameObject[] { null });
            SetPrivateField(bridge, "m_RegisterWeaponHandles", new Handle[] { null });
            SetPrivateField(bridge, "m_LogDiagnostics", false);

            InvokePrivate(bridge, "RegisterConfiguredAssets");

            IDictionary registered = GetPrivateField<IDictionary>(bridge, "m_WeaponAssets");
            Assert.That(registered.Count, Is.EqualTo(1));
            Assert.That(registered.Contains(weapon.Id.Hash), Is.True);
        }

        [Test]
        public void TransientShotQueue_ExpiresInsteadOfReplayingToLateController()
        {
            NetworkShooterManager manager = CreateManager(false, true);
            SetPrivateField(manager, "m_TransientBroadcastLifetime", 0.05f);
            manager.ReceiveShotBroadcast(new NetworkShotBroadcast
            {
                ShooterNetworkId = 123,
                WeaponHash = 404,
                MuzzlePosition = Vector3.zero,
                HitPoint = Vector3.forward
            });

            IList pending = GetPrivateField<IList>(manager, "m_PendingShotBroadcasts");
            Assert.That(pending.Count, Is.EqualTo(1));

            object entry = pending[0];
            FieldInfo receivedTime = entry.GetType().GetField(
                "ReceivedTime",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(receivedTime, Is.Not.Null);
            receivedTime.SetValue(entry, Time.unscaledTime - 1f);
            pending[0] = entry;

            InvokePrivate(manager, "FlushPendingTransientBroadcasts");
            Assert.That(pending, Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HitRouting_ClientAndHost_CreatesOneSharedImpact(bool isHost)
        {
            NetworkShooterManager manager = CreateManager(isHost, true);
            NetworkShooterController shooter = CreateController("Shooter Attacker", isHost, false);
            NetworkShooterController target = CreateController("Shooter Target", isHost, false);
            manager.RegisterController(1, shooter);
            manager.RegisterController(2, target);

            int shooterConfirmations = 0;
            int targetConfirmations = 0;
            shooter.OnHitConfirmed += _ => shooterConfirmations++;
            target.OnHitConfirmed += _ => targetConfirmations++;

            manager.ReceiveHitBroadcast(new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 1,
                TargetNetworkId = 2,
                WeaponHash = 99999,
                HitPoint = new Vector3(2f, 0f, 0f),
                HitNormal = Vector3.up,
                MaterialHash = 501
            });

            Assert.That(shooterConfirmations, Is.EqualTo(1));
            Assert.That(targetConfirmations, Is.EqualTo(1));
            Assert.That(CountImpactObjects(), Is.EqualTo(1));
        }

        [Test]
        public void HitRouting_SelfHit_CreatesOneImpactAndOneNotification()
        {
            NetworkShooterManager manager = CreateManager(false, true);
            NetworkShooterController controller = CreateController("Shooter Self Hit", false, false);
            manager.RegisterController(5, controller);

            int confirmations = 0;
            controller.OnHitConfirmed += _ => confirmations++;
            manager.ReceiveHitBroadcast(new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 5,
                TargetNetworkId = 5,
                WeaponHash = 99999,
                HitPoint = Vector3.zero,
                HitNormal = Vector3.forward
            });

            Assert.That(confirmations, Is.EqualTo(1));
            Assert.That(CountImpactObjects(), Is.EqualTo(1));
        }

        [Test]
        public void MissingHitAsset_DefaultFallbackDisabled_RemainsNonVisual()
        {
            NetworkShooterManager manager = CreateManager(false, true);
            NetworkShooterController controller = Track(
                new GameObject("Shooter No Fallback")).AddComponent<NetworkShooterController>();
            SetPrivateField(controller, "m_LogDiagnostics", false);
            controller.Initialize(false, false);
            manager.RegisterController(46, controller);

            manager.ReceiveHitBroadcast(new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 46,
                WeaponHash = 987654,
                HitPoint = Vector3.one,
                HitNormal = Vector3.up
            });

            Assert.That(controller.UseGeneratedFallbackPresentation, Is.False);
            Assert.That(CountImpactObjects(), Is.Zero);
        }

        [Test]
        public void LocalShotWithoutMatchingOptimism_PlaysConfirmedFallbackTracer()
        {
            NetworkShooterManager manager = CreateManager(false, true);

            GameObject shooterObject = Track(new GameObject("Shooter Confirmed Local Shot"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter character = shooterObject.AddComponent<NetworkCharacter>();
            character.SetManualNetworkId(47);
            NetworkShooterController controller = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(controller, "m_LogDiagnostics", false);
            controller.Initialize(false, true);
            controller.UseGeneratedFallbackPresentation = true;
            manager.RegisterController(47, controller);

            manager.ReceiveShotBroadcast(new NetworkShotBroadcast
            {
                ShooterNetworkId = 47,
                WeaponHash = 555123,
                MuzzlePosition = Vector3.zero,
                ShotDirection = Vector3.forward,
                HitPoint = Vector3.forward * 10f
            });

            Assert.That(CountTracerObjects(), Is.EqualTo(1));
        }

        [Test]
        public void LocalOptimisticHit_SuppressesOnlyItsMatchingConfirmedImpact()
        {
            NetworkShooterManager manager = CreateManager(false, true);

            GameObject shooterObject = Track(new GameObject("Shooter Optimistic Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(51);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(false, true);
            shooter.OptimisticHitEffects = true;
            shooter.UseGeneratedFallbackPresentation = true;
            manager.RegisterController(51, shooter);

            GameObject environmentTarget = Track(new GameObject("Shooter Optimistic Environment"));
            Vector3 hitPoint = new Vector3(3f, 2f, 1f);
            bool processNative = shooter.InterceptHit(
                environmentTarget,
                hitPoint,
                Vector3.up,
                3f,
                null,
                0);

            Assert.That(processNative, Is.False);
            Assert.That(CountImpactObjects(), Is.EqualTo(1));
            manager.ReceiveHitBroadcast(new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 51,
                TargetNetworkId = 0,
                WeaponHash = 0,
                HitPoint = hitPoint,
                HitNormal = Vector3.up
            });
            Assert.That(CountImpactObjects(), Is.EqualTo(1));

            manager.ReceiveHitBroadcast(new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 51,
                TargetNetworkId = 0,
                WeaponHash = 0,
                HitPoint = hitPoint + Vector3.right * 2f,
                HitNormal = Vector3.up
            });
            Assert.That(CountImpactObjects(), Is.EqualTo(2));
        }

        [Test]
        public void ConfirmedEnvironmentPhysics_RunsWhenOptimisticPresentationIsReconciled()
        {
            NetworkShooterManager manager = CreateManager(false, true);

            GameObject shooterObject = Track(new GameObject("Shooter Optimistic Physics Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(52);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(false, true);
            shooter.OptimisticHitEffects = true;
            shooter.UseGeneratedFallbackPresentation = true;
            manager.RegisterController(52, shooter);

            ShooterWeapon weapon = CreateForceWeapon("Optimistic Physics Weapon", 10f);
            GameObject target = CreateDynamicTarget("Shooter Optimistic Physics Target", Vector3.forward * 3f);
            Rigidbody rigidbody = target.GetComponent<Rigidbody>();
            rigidbody.Sleep();
            Physics.SyncTransforms();

            Vector3 hitPoint = target.transform.position;
            Assert.That(
                shooter.InterceptHit(
                    target,
                    hitPoint,
                    Vector3.back,
                    3f,
                    weapon,
                    0),
                Is.False);
            Assert.That(CountImpactObjects(), Is.EqualTo(1));

            manager.ReceiveHitBroadcast(new NetworkShooterHitBroadcast
            {
                ShooterNetworkId = 52,
                TargetNetworkId = 0,
                WeaponHash = weapon.Id.Hash,
                HitPoint = hitPoint,
                HitNormal = Vector3.back
            });

            Assert.That(
                rigidbody.IsSleeping(),
                Is.False,
                "Confirmed physics must run even though the matching optimistic VFX is consumed.");
            Assert.That(CountImpactObjects(), Is.EqualTo(1));
        }

        [Test]
        public void ConfirmedEnvironmentPhysics_ResolvesCustomLayerChildColliderToParentRigidbody()
        {
            NetworkShooterController shooter = CreateController(
                "Shooter Child Collider Observer",
                false,
                false);
            ShooterWeapon weapon = CreateForceWeapon("Child Collider Physics Weapon", 7f);

            GameObject parent = Track(new GameObject("Shooter Parent Rigidbody"));
            parent.transform.position = Vector3.right * 4f;
            Rigidbody rigidbody = parent.AddComponent<Rigidbody>();
            rigidbody.useGravity = false;
            rigidbody.Sleep();

            GameObject child = Track(new GameObject("Shooter Child Collider"));
            child.transform.SetParent(parent.transform, false);
            child.layer = 2; // IgnoreRaycast/custom weapon layers must still resolve confirmed physics.
            child.AddComponent<BoxCollider>();
            Physics.SyncTransforms();

            bool applied = InvokePrivate<bool>(
                shooter,
                "ApplyConfirmedEnvironmentImpact",
                new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = 99,
                    TargetNetworkId = 0,
                    WeaponHash = weapon.Id.Hash,
                    HitPoint = parent.transform.position,
                    HitNormal = Vector3.left
                });

            Assert.That(applied, Is.True);
            Assert.That(rigidbody.IsSleeping(), Is.False);
        }

        [Test]
        public void ConfirmedEnvironmentPhysics_ExcludesCharactersAndImpactProps()
        {
            NetworkShooterController shooter = CreateController(
                "Shooter Environment Exclusion Observer",
                false,
                false);
            ShooterWeapon weapon = CreateForceWeapon("Environment Exclusion Weapon", 8f);

            GameObject characterTarget = Track(new GameObject("Shooter Character Physics Target"));
            characterTarget.transform.position = Vector3.left * 4f;
            characterTarget.AddComponent<Character>();
            characterTarget.AddComponent<BoxCollider>();
            Rigidbody characterBody = characterTarget.AddComponent<Rigidbody>();
            characterBody.useGravity = false;

            GameObject impactPropTarget = Track(new GameObject("Shooter Impact Prop Physics Target"));
            impactPropTarget.transform.position = Vector3.right * 4f;
            impactPropTarget.AddComponent<BoxCollider>();
            Rigidbody impactPropBody = impactPropTarget.AddComponent<Rigidbody>();
            impactPropBody.useGravity = false;
            impactPropTarget.AddComponent<NetworkShooterImpactProp>();
            Physics.SyncTransforms();

            bool characterApplied = InvokePrivate<bool>(
                shooter,
                "ApplyConfirmedEnvironmentImpact",
                new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = 100,
                    WeaponHash = weapon.Id.Hash,
                    HitPoint = characterTarget.transform.position,
                    HitNormal = Vector3.forward
                });
            bool impactPropApplied = InvokePrivate<bool>(
                shooter,
                "ApplyConfirmedEnvironmentImpact",
                new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = 100,
                    WeaponHash = weapon.Id.Hash,
                    HitPoint = impactPropTarget.transform.position,
                    HitNormal = Vector3.forward
                });

            Assert.That(characterApplied, Is.False);
            Assert.That(impactPropApplied, Is.False);
        }

        [Test]
        public void HostConfirmedEnvironmentPhysics_ConsumesMatchingPostNativeMarker()
        {
            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Native Physics Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            bridge.Client = true;
            bridge.Host = true;
            NetworkShooterManager manager = CreateManager(true, true);

            GameObject shooterObject = Track(new GameObject("Shooter Native Physics Host"));
            Character character = shooterObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = shooterObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(53);
            SetNetworkCharacterRuntimeRole(
                networkCharacter,
                isServer: true,
                isOwner: true,
                isHost: true);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, true);
            manager.RegisterController(53, shooter);
            bridge.ResolvedCharacter = character;

            ShooterWeapon weapon = CreateForceWeapon("Native Physics Host Weapon", 12f);
            GameObject target = CreateDynamicTarget("Shooter Native Physics Target", Vector3.forward * 3f);
            Vector3 hitPoint = target.transform.position;
            Physics.SyncTransforms();

            Assert.That(
                InvokePrivate<bool>(
                    shooter,
                    "InterceptHit",
                    target,
                    hitPoint,
                    Vector3.back,
                    3f,
                    weapon,
                    (byte)0,
                    true,
                    false),
                Is.True);

            var data = new ShotData(
                character,
                weapon,
                default,
                null,
                shooterObject.transform.position,
                Vector3.forward,
                null,
                null,
                1,
                0f,
                0f,
                0f);
            data.UpdateHit(target, hitPoint, 3f, 0);
            InvokePrivate(
                shooter,
                "NotifyPatchedHitResolved",
                data,
                BlockType.None,
                ReactionOutput.None,
                float.NaN);

            Assert.That(
                GetPrivateField<IList>(shooter, "m_RecentServerNativeEnvironmentImpacts").Count,
                Is.EqualTo(1));

            Rigidbody targetBody = target.GetComponent<Rigidbody>();
            Assert.That(
                InvokePrivate<bool>(
                    shooter,
                    "TryConsumeServerNativeEnvironmentImpactMarker",
                    targetBody,
                    weapon.Id.Hash,
                    hitPoint + Vector3.right),
                Is.False,
                "A marker at a different hit point must not suppress a later confirmed impulse.");

            bool appliedAgain = InvokePrivate<bool>(
                shooter,
                "ApplyConfirmedEnvironmentImpact",
                new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = 53,
                    WeaponHash = weapon.Id.Hash,
                    HitPoint = hitPoint,
                    HitNormal = Vector3.back
                });

            Assert.That(
                appliedAgain,
                Is.False,
                "The host must retain the native GC2 impulse instead of applying the loopback force twice.");
            Assert.That(
                GetPrivateField<IList>(shooter, "m_RecentServerNativeEnvironmentImpacts").Count,
                Is.Zero);

            InvokePrivate(shooter, "RecordServerNativeEnvironmentImpactMarker", data);
            IList expiredMarkers = GetPrivateField<IList>(
                shooter,
                "m_RecentServerNativeEnvironmentImpacts");
            object expiredMarker = expiredMarkers[0];
            FieldInfo expiryField = expiredMarker.GetType().GetField(
                "ExpiresAt",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(expiryField, Is.Not.Null);
            expiryField.SetValue(expiredMarker, Time.time - 1f);
            expiredMarkers[0] = expiredMarker;

            Assert.That(
                InvokePrivate<bool>(
                    shooter,
                    "ApplyConfirmedEnvironmentImpact",
                    new NetworkShooterHitBroadcast
                    {
                        ShooterNetworkId = 53,
                        WeaponHash = weapon.Id.Hash,
                        HitPoint = hitPoint,
                        HitNormal = Vector3.back
                    }),
                Is.True,
                "An expired native marker must not suppress a confirmed impulse.");
            Assert.That(expiredMarkers.Count, Is.Zero);
        }

        [Test]
        public void ClientHitWithoutRequestRoute_FailsClosedAndDoesNotLeakPendingState()
        {
            GameObject shooterObject = Track(new GameObject("Shooter Missing Route Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(57);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(false, true);

            bool processNative = shooter.InterceptHit(
                Track(new GameObject("Shooter Missing Route Target")),
                Vector3.one,
                Vector3.up,
                1f,
                null,
                0);

            Assert.That(processNative, Is.False);
            Assert.That(
                GetPrivateField<IDictionary>(shooter, "m_PendingHits").Count,
                Is.Zero);
        }

        [Test]
        public void DedicatedServerConditionFallback_QueuesManagerOwnedHitAndSuppressesNativeHit()
        {
            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            NetworkShooterManager manager = CreateManager(true, false);

            GameObject shooterObject = Track(new GameObject("Shooter Server Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(61);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, false);
            manager.RegisterController(61, shooter);
            bridge.ResolvedCharacter = shooterObject.GetComponent<Character>();

            GameObject targetObject = Track(new GameObject("Shooter Server Environment"));
            bool processNative = shooter.InterceptHit(
                targetObject,
                Vector3.one,
                Vector3.up,
                1f,
                null,
                0);

            Assert.That(processNative, Is.False);
            IEnumerable queue = GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue");
            IEnumerator enumerator = queue.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            object queued = enumerator.Current;
            FieldInfo trustedField = queued.GetType().GetField(
                "TrustedServerOrigin",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo damageField = queued.GetType().GetField(
                "NativeHitWillContinue",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(trustedField, Is.Not.Null);
            Assert.That(damageField, Is.Not.Null);
            Assert.That((bool)trustedField.GetValue(queued), Is.True);
            Assert.That((bool)damageField.GetValue(queued), Is.False);
        }

        [Test]
        public void LegacyHitCondition_DefersToCancellableSourcePatch()
        {
            Assert.That(
                InvokePrivateStatic<bool>(
                    typeof(ConditionNetworkShooterHit),
                    "ShouldDeferToCancellablePatch",
                    true),
                Is.True,
                "A legacy Can Hit condition runs before ShooterWeapon.OnHit and must let the " +
                "cancellable validator own the request when the current patch is installed.");
            Assert.That(
                InvokePrivateStatic<bool>(
                    typeof(ConditionNetworkShooterHit),
                    "ShouldDeferToCancellablePatch",
                    false),
                Is.False,
                "The legacy condition must remain operational when no cancellable patch exists.");
        }

        [Test]
        public void PatchedServerHit_QueuesNativeContinuationAndConsumesTokenAtMostOnce()
        {
            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Patched Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            NetworkShooterManager manager = CreateManager(true, false);

            GameObject shooterObject = Track(new GameObject("Shooter Patched Server Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(63);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, false);
            manager.RegisterController(63, shooter);
            bridge.ResolvedCharacter = shooterObject.GetComponent<Character>();

            GameObject target = Track(new GameObject("Shooter Patched Server Target"));
            bool processNative = InvokePrivate<bool>(
                shooter,
                "InterceptHit",
                target,
                Vector3.one,
                Vector3.up,
                1f,
                null,
                (byte)0,
                true,
                false);

            Assert.That(processNative, Is.True);
            Assert.That(
                InvokePrivate<bool>(shooter, "TryConsumeServerNativeHitContinuation", target),
                Is.True);
            Assert.That(
                InvokePrivate<bool>(shooter, "TryConsumeServerNativeHitContinuation", target),
                Is.False);

            var resolvedData = new ShotData(
                shooterObject.GetComponent<Character>(),
                null,
                default,
                null,
                Vector3.zero,
                Vector3.forward,
                null,
                null,
                1,
                0f,
                0f,
                0f);
            resolvedData.UpdateHit(target, Vector3.one, 1f, 0);
            InvokePrivate(
                shooter,
                "NotifyPatchedHitResolved",
                resolvedData,
                BlockType.Block,
                ReactionOutput.None,
                4.25f);

            GameObject conditionFreeTarget = Track(new GameObject("Shooter Patched Condition-Free Target"));
            Assert.That(
                InvokePrivate<bool>(
                    shooter,
                    "InterceptHit",
                    conditionFreeTarget,
                    Vector3.right,
                    Vector3.up,
                    1f,
                    null,
                    (byte)0,
                    true,
                    false),
                Is.True);
            var conditionFreeData = new ShotData(
                shooterObject.GetComponent<Character>(),
                null,
                default,
                null,
                Vector3.zero,
                Vector3.forward,
                null,
                null,
                1,
                0f,
                0f,
                0f);
            conditionFreeData.UpdateHit(conditionFreeTarget, Vector3.right, 1f, 0);
            InvokePrivate(
                shooter,
                "NotifyPatchedHitResolved",
                conditionFreeData,
                BlockType.Parry,
                ReactionOutput.None,
                5.5f);
            Assert.That(
                InvokePrivate<bool>(shooter, "TryConsumeServerNativeHitContinuation", conditionFreeTarget),
                Is.False,
                "The post-hit callback must clear a continuation token when no legacy Condition exists.");

            GameObject breakTarget = Track(new GameObject("Shooter Patched Block-Break Target"));
            Assert.That(
                InvokePrivate<bool>(
                    shooter,
                    "InterceptHit",
                    breakTarget,
                    Vector3.forward,
                    Vector3.up,
                    1f,
                    null,
                    (byte)0,
                    true,
                    false),
                Is.True);
            var breakData = new ShotData(
                shooterObject.GetComponent<Character>(),
                null,
                default,
                null,
                Vector3.zero,
                Vector3.forward,
                null,
                null,
                1,
                0f,
                0f,
                0f);
            breakData.UpdateHit(breakTarget, Vector3.forward, 1f, 0);
            InvokePrivate(
                shooter,
                "NotifyPatchedHitResolved",
                breakData,
                BlockType.Break,
                ReactionOutput.None,
                6.75f);
            Assert.That(
                InvokePrivate<bool>(shooter, "TryConsumeServerNativeHitContinuation", breakTarget),
                Is.False);

            IDictionary outcomes = GetPrivateField<IDictionary>(manager, "m_TrustedNativeHitOutcomes");
            Assert.That(outcomes.Count, Is.EqualTo(3));
            CollectionAssert.Contains(outcomes.Values, NetworkBlockResult.Blocked);
            CollectionAssert.Contains(outcomes.Values, NetworkBlockResult.Parried);
            CollectionAssert.Contains(outcomes.Values, NetworkBlockResult.BlockBroken);

            IDictionary powers = GetPrivateField<IDictionary>(manager, "m_TrustedNativeReactionPowers");
            Assert.That(powers.Count, Is.EqualTo(3));
            CollectionAssert.Contains(powers.Values, 4.25f);
            CollectionAssert.Contains(powers.Values, 5.5f);
            CollectionAssert.Contains(powers.Values, 6.75f);

            IEnumerable queue = GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue");
            IEnumerator enumerator = queue.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            object queued = enumerator.Current;
            FieldInfo continuationField = queued.GetType().GetField(
                "NativeHitWillContinue",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(continuationField, Is.Not.Null);
            Assert.That((bool)continuationField.GetValue(queued), Is.True);
        }

        [Test]
        public void TrustedNativeHit_UsesPreHitValidationAfterLethalTargetRemoval()
        {
            NetworkShooterManager manager = CreateManager(true, false);

            GameObject shooterObject = Track(new GameObject("Shooter Prevalidation Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(68);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            shooter.Initialize(true, false);
            manager.RegisterController(68, shooter);

            GameObject targetObject = Track(new GameObject("Shooter Prevalidation Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter targetCharacter = targetObject.AddComponent<NetworkCharacter>();
            targetCharacter.SetManualNetworkId(69);
            manager.GetCharacterByNetworkIdFunc = id => id == 69 ? targetCharacter : null;
            manager.ComputeDamageFunc = _ => 73f;

            var request = new NetworkShooterHitRequest
            {
                RequestId = 7,
                ActorNetworkId = 68,
                ShooterNetworkId = 68,
                CorrelationId = 0x440007,
                ClientTimestamp = Time.time,
                TargetNetworkId = 69,
                IsCharacterHit = true
            };

            Assert.That(manager.TryServerQueueTrustedHit(request, true), Is.True);

            IEnumerable queue = GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue");
            IEnumerator enumerator = queue.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            object queued = enumerator.Current;
            FieldInfo hasPrevalidatedField = queued.GetType().GetField(
                "HasPrevalidatedResponse",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo prevalidatedField = queued.GetType().GetField(
                "PrevalidatedResponse",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(hasPrevalidatedField, Is.Not.Null);
            Assert.That(prevalidatedField, Is.Not.Null);
            Assert.That((bool)hasPrevalidatedField.GetValue(queued), Is.True);
            NetworkShooterHitResponse prevalidated =
                (NetworkShooterHitResponse)prevalidatedField.GetValue(queued);
            Assert.That(prevalidated.Validated, Is.True);
            Assert.That(prevalidated.Damage, Is.EqualTo(73f));

            Assert.That(
                manager.TryServerRecordNativeHitOutcome(
                    68,
                    request.CorrelationId,
                    NetworkBlockResult.None,
                    29.75f),
                Is.True);

            int broadcasts = 0;
            NetworkShooterHitBroadcast observedBroadcast = default;
            manager.OnHitValidated += value =>
            {
                broadcasts++;
                observedBroadcast = value;
            };

            // Simulate a lethal authored OnHit instruction removing the target before Update.
            Object.DestroyImmediate(targetObject);
            InvokePrivate(manager, "ProcessServerHitQueue");

            Assert.That(broadcasts, Is.EqualTo(1));
            Assert.That(observedBroadcast.ReactionPower, Is.EqualTo(29.75f));
            Assert.That(
                CountEnumerable(GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue")),
                Is.Zero);
        }

        [Test]
        public void PatchedServerHit_WhenTrustedQueueIsFull_SuppressesNativeGameplay()
        {
            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Full Queue Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            NetworkShooterManager manager = CreateManager(true, false);
            SetPrivateField(manager, "m_MaxHitQueueLength", 1);

            GameObject shooterObject = Track(new GameObject("Shooter Full Queue Server Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(72);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, false);
            manager.RegisterController(72, shooter);
            bridge.ResolvedCharacter = shooterObject.GetComponent<Character>();

            GameObject acceptedTarget = Track(new GameObject("Shooter Accepted Native Target"));
            GameObject rejectedTarget = Track(new GameObject("Shooter Rejected Native Target"));

            bool firstContinues = InvokePrivate<bool>(
                shooter,
                "InterceptHit",
                acceptedTarget,
                Vector3.one,
                Vector3.up,
                1f,
                null,
                (byte)0,
                true,
                false);
            bool secondContinues = InvokePrivate<bool>(
                shooter,
                "InterceptHit",
                rejectedTarget,
                Vector3.right,
                Vector3.up,
                1f,
                null,
                (byte)0,
                true,
                false);

            Assert.That(firstContinues, Is.True);
            Assert.That(
                secondContinues,
                Is.False,
                "A native server hit must fail closed when it cannot enter the trusted queue.");
            Assert.That(
                CountEnumerable(GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue")),
                Is.EqualTo(1));
            Assert.That(
                InvokePrivate<bool>(
                    shooter,
                    "TryConsumeServerNativeHitContinuation",
                    rejectedTarget),
                Is.False,
                "A rejected hit must not leak a native continuation token.");
        }

        [Test]
        public void ClientOwnedServerReplica_WaitsForAuthenticatedOwnerHitClaim()
        {
            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Client-Owned Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            NetworkShooterManager manager = CreateManager(true, false);

            GameObject shooterObject = Track(new GameObject("Shooter Client-Owned Server Replica"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(65);
            bridge.SetCharacterOwner(65, 900);

            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, false);
            manager.RegisterController(65, shooter);
            bridge.ResolvedCharacter = shooterObject.GetComponent<Character>();

            bool processNative = InvokePrivate<bool>(
                shooter,
                "InterceptHit",
                Track(new GameObject("Shooter Client-Owned Target")),
                Vector3.one,
                Vector3.up,
                1f,
                null,
                (byte)0,
                true,
                false);

            Assert.That(processNative, Is.False);
            Assert.That(
                CountEnumerable(GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue")),
                Is.Zero);
        }

        [Test]
        public void SharedMasterLocalHit_QueuesTrustedNativeHit()
        {
            const uint actorNetworkId = 73;
            const uint localClientId = 41;

            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Shared Authority Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            bridge.Server = true;
            bridge.Client = true;
            bridge.Host = false;
            bridge.LocalClientId = localClientId;
            NetworkShooterManager manager = CreateManager(true, true);

            GameObject shooterObject = Track(new GameObject("Shooter Shared Authority Attacker"));
            Character character = shooterObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = shooterObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(actorNetworkId);
            SetNetworkCharacterRuntimeRole(
                networkCharacter,
                isServer: true,
                isOwner: true,
                isHost: true);
            bridge.ResolvedCharacter = character;
            bridge.SetCharacterOwner(actorNetworkId, localClientId);

            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, true);
            manager.RegisterController(actorNetworkId, shooter);

            bool processNative = InvokePrivate<bool>(
                shooter,
                "InterceptHit",
                Track(new GameObject("Shooter Shared Authority Target")),
                Vector3.one,
                Vector3.up,
                1f,
                null,
                (byte)0,
                true,
                false);

            Assert.That(
                processNative,
                Is.True,
                "A Shared master is the logical gameplay authority even though transport IsHost is false.");
            Assert.That(
                CountEnumerable(GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue")),
                Is.EqualTo(1),
                "The trusted Shared-master hit must enter the authoritative queue and broadcast path.");
        }

        [Test]
        public void SharedMasterLocalHit_WithMismatchedRegisteredOwner_FailsClosed()
        {
            const uint actorNetworkId = 74;
            const uint localClientId = 51;

            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Shared Owner Mismatch Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            bridge.Server = true;
            bridge.Client = true;
            bridge.Host = false;
            bridge.LocalClientId = localClientId;
            NetworkShooterManager manager = CreateManager(true, true);

            GameObject shooterObject = Track(new GameObject("Shooter Shared Owner Mismatch Attacker"));
            Character character = shooterObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = shooterObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(actorNetworkId);
            SetNetworkCharacterRuntimeRole(
                networkCharacter,
                isServer: true,
                isOwner: true,
                isHost: true);
            bridge.ResolvedCharacter = character;
            bridge.SetCharacterOwner(actorNetworkId, localClientId + 1);

            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, true);
            manager.RegisterController(actorNetworkId, shooter);

            bool processNative = InvokePrivate<bool>(
                shooter,
                "InterceptHit",
                Track(new GameObject("Shooter Shared Owner Mismatch Target")),
                Vector3.one,
                Vector3.up,
                1f,
                null,
                (byte)0,
                true,
                false);

            Assert.That(processNative, Is.False);
            Assert.That(
                CountEnumerable(GetPrivateField<IEnumerable>(manager, "m_ServerHitQueue")),
                Is.Zero,
                "A stale or mismatched Shared owner must not be promoted to trusted authority.");
        }

        [Test]
        public void DamageHandler_DoesNotSuppressAuthoritativeReactionHandler()
        {
            NetworkShooterManager manager = CreateManager(true, false);
            GameObject targetObject = Track(new GameObject("Shooter Reaction Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter target = targetObject.AddComponent<NetworkCharacter>();
            target.SetManualNetworkId(64);
            manager.GetCharacterByNetworkIdFunc = id => id == 64 ? target : null;

            int damageCalls = 0;
            int reactionCalls = 0;
            var callOrder = new List<string>();
            manager.TryApplyDamageFunc = (_, _) =>
            {
                damageCalls++;
                callOrder.Add("damage");
                return true;
            };
            manager.TryApplyAuthoritativeReactionFunc = _ =>
            {
                reactionCalls++;
                callOrder.Add("reaction");
                return true;
            };

            var request = new NetworkShooterHitRequest
            {
                ActorNetworkId = 60,
                ShooterNetworkId = 60,
                TargetNetworkId = 64,
                IsCharacterHit = true,
                HitNormal = Vector3.back,
                WeaponHash = 404
            };

            InvokePrivate(
                manager,
                "ApplyAuthoritativeReactionOnServer",
                request,
                12f,
                NetworkBlockResult.None);
            InvokePrivate(manager, "ApplyDamageOnServer", request, 12f);

            Assert.That(damageCalls, Is.EqualTo(1));
            Assert.That(reactionCalls, Is.EqualTo(1));
            CollectionAssert.AreEqual(
                new[] { "reaction", "damage" },
                callOrder,
                "Reaction must start before a custom damage adapter can make a lethal hit suppress it.");
        }

        [TestCase(BlockType.None, NetworkBlockResult.None)]
        [TestCase(BlockType.Block, NetworkBlockResult.Blocked)]
        [TestCase(BlockType.Parry, NetworkBlockResult.Parried)]
        [TestCase(BlockType.Break, NetworkBlockResult.BlockBroken)]
        public void ManagerFallbackBlockMapping_PreservesAuthoredOutcome(
            BlockType blockType,
            NetworkBlockResult expected)
        {
            NetworkBlockResult result = InvokePrivateStatic<NetworkBlockResult>(
                typeof(NetworkShooterManager),
                "MapBlockType",
                blockType);

            Assert.That(result, Is.EqualTo(expected));
        }

        [TestCase(NetworkBlockResult.Blocked)]
        [TestCase(NetworkBlockResult.Parried)]
        public void FullyDefendedManagerOwnedHit_DoesNotInvokeReactionOverride(
            NetworkBlockResult blockResult)
        {
            NetworkShooterManager manager = CreateManager(true, false);
            GameObject targetObject = Track(new GameObject("Shooter Defended Reaction Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter target = targetObject.AddComponent<NetworkCharacter>();
            target.SetManualNetworkId(68);
            manager.GetCharacterByNetworkIdFunc = id => id == 68 ? target : null;

            int reactionCalls = 0;
            manager.TryApplyAuthoritativeReactionFunc = _ =>
            {
                reactionCalls++;
                return true;
            };

            var request = new NetworkShooterHitRequest
            {
                ActorNetworkId = 67,
                ShooterNetworkId = 67,
                TargetNetworkId = 68,
                IsCharacterHit = true,
                HitNormal = Vector3.back
            };

            NetworkBlockResult result = InvokePrivate<NetworkBlockResult>(
                manager,
                "ApplyAuthoritativeReactionOnServer",
                request,
                10f,
                blockResult);

            Assert.That(result, Is.EqualTo(blockResult));
            Assert.That(reactionCalls, Is.Zero);
        }

        [TestCase(NetworkBlockResult.None)]
        [TestCase(NetworkBlockResult.BlockBroken)]
        public void UndefendedOrBrokenManagerOwnedHit_InvokesReactionOverrideOnce(
            NetworkBlockResult blockResult)
        {
            NetworkShooterManager manager = CreateManager(true, false);
            GameObject targetObject = Track(new GameObject("Shooter Reacting Target"));
            targetObject.AddComponent<Character>();
            NetworkCharacter target = targetObject.AddComponent<NetworkCharacter>();
            target.SetManualNetworkId(70);
            manager.GetCharacterByNetworkIdFunc = id => id == 70 ? target : null;

            int reactionCalls = 0;
            manager.TryApplyAuthoritativeReactionFunc = context =>
            {
                reactionCalls++;
                Assert.That(context.BlockResult, Is.EqualTo(blockResult));
                Assert.That(
                    context.ReactionInput.Power,
                    Is.EqualTo(10f),
                    "Fallback reactions must expose the same authoritative power later broadcast to clients.");
                return true;
            };

            var request = new NetworkShooterHitRequest
            {
                ActorNetworkId = 69,
                ShooterNetworkId = 69,
                TargetNetworkId = 70,
                IsCharacterHit = true,
                HitNormal = Vector3.back
            };

            NetworkBlockResult result = InvokePrivate<NetworkBlockResult>(
                manager,
                "ApplyAuthoritativeReactionOnServer",
                request,
                10f,
                blockResult);

            Assert.That(result, Is.EqualTo(blockResult));
            Assert.That(reactionCalls, Is.EqualTo(1));
        }

        [Test]
        public void DedicatedServerShot_QueuesOnceAndNativeNotificationReusesPendingRequest()
        {
            NetworkShooterManager manager = CreateManager(true, false);

            GameObject shooterObject = Track(new GameObject("Shooter Server Shot Actor"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(66);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, false);
            manager.RegisterController(66, shooter);

            ShooterWeapon weapon = Track(ScriptableObject.CreateInstance<ShooterWeapon>());
            weapon.name = "Trusted Server Shot Weapon";
            Vector3 muzzle = new Vector3(1f, 2f, 3f);
            Vector3 direction = Vector3.forward;

            bool processNative = shooter.InterceptShot(
                muzzle,
                direction,
                weapon,
                0f,
                0,
                1);

            Assert.That(processNative, Is.True);
            IEnumerable queue = GetPrivateField<IEnumerable>(manager, "m_ServerShotQueue");
            Assert.That(CountEnumerable(queue), Is.EqualTo(1));

            InvokePrivate(shooter, "NotifyShotFired", muzzle, direction, weapon, 0f);

            Assert.That(CountEnumerable(queue), Is.EqualTo(1));
            IEnumerator enumerator = queue.GetEnumerator();
            Assert.That(enumerator.MoveNext(), Is.True);
            object queued = enumerator.Current;
            FieldInfo trustedField = queued.GetType().GetField(
                "TrustedServerOrigin",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(trustedField, Is.Not.Null);
            Assert.That((bool)trustedField.GetValue(queued), Is.True);
        }

        [Test]
        public void HostDirectHit_UsesLoopbackAndSuppressesNativeHit()
        {
            ShooterTestTransportBridge bridge = Track(
                    new GameObject("Shooter Host Test Transport"))
                .AddComponent<ShooterTestTransportBridge>();
            bridge.Client = true;
            bridge.Host = true;
            NetworkShooterManager manager = CreateManager(true, true);

            GameObject shooterObject = Track(new GameObject("Shooter Host Attacker"));
            shooterObject.AddComponent<Character>();
            NetworkCharacter shooterCharacter = shooterObject.AddComponent<NetworkCharacter>();
            shooterCharacter.SetManualNetworkId(71);
            SetNetworkCharacterRuntimeRole(
                shooterCharacter,
                isServer: true,
                isOwner: true,
                isHost: true);
            NetworkShooterController shooter = shooterObject.AddComponent<NetworkShooterController>();
            SetPrivateField(shooter, "m_LogDiagnostics", false);
            shooter.Initialize(true, true);
            shooter.OptimisticHitEffects = true;
            manager.RegisterController(71, shooter);
            bridge.ResolvedCharacter = shooterObject.GetComponent<Character>();

            int requests = 0;
            shooter.OnHitDetected += _ => requests++;
            bool processNative = shooter.InterceptHit(
                Track(new GameObject("Shooter Host Environment")),
                Vector3.forward,
                Vector3.up,
                1f,
                null,
                0);

            Assert.That(processNative, Is.False);
            Assert.That(requests, Is.EqualTo(1));
        }

        [Test]
        public void PrunedShooterController_ClearsPersistentStateCaches()
        {
            NetworkShooterManager manager = CreateManager(true, false);
            GameObject bridgeObject = Track(new GameObject("Shooter Cache Prune Bridge"));
            bridgeObject.SetActive(false);
            PurrNetShooterTransportBridge bridge =
                bridgeObject.AddComponent<PurrNetShooterTransportBridge>();
            SetPrivateField(bridge, "m_LogDiagnostics", false);

            NetworkShooterController staleController = Track(
                new GameObject("Shooter Stale Controller")).AddComponent<NetworkShooterController>();
            IDictionary registered = GetPrivateField<IDictionary>(bridge, "m_RegisteredControllers");
            registered[81u] = staleController;
            GetPrivateField<IDictionary>(bridge, "m_LatestWeaponStates")[81u] =
                new GC2ShooterWeaponStatePacket { characterNetworkId = 81 };
            GetPrivateField<IDictionary>(bridge, "m_LatestAimStates")[81u] =
                new GC2ShooterAimStatePacket { characterNetworkId = 81 };
            GetPrivateField<IDictionary>(bridge, "m_PendingWeaponStates")[81u] =
                new GC2ShooterWeaponStatePacket { characterNetworkId = 81 };
            GetPrivateField<IDictionary>(bridge, "m_PendingAimStates")[81u] =
                new GC2ShooterAimStatePacket { characterNetworkId = 81 };

            InvokePrivate(bridge, "PruneControllerRegistry", manager);

            Assert.That(registered.Contains(81u), Is.False);
            Assert.That(GetPrivateField<IDictionary>(bridge, "m_LatestWeaponStates").Contains(81u), Is.False);
            Assert.That(GetPrivateField<IDictionary>(bridge, "m_LatestAimStates").Contains(81u), Is.False);
            Assert.That(GetPrivateField<IDictionary>(bridge, "m_PendingWeaponStates").Contains(81u), Is.False);
            Assert.That(GetPrivateField<IDictionary>(bridge, "m_PendingAimStates").Contains(81u), Is.False);
        }

        [Test]
        public void RemoteWeaponApplyVersion_AdvancesForEachNewState()
        {
            GameObject controllerObject = Track(new GameObject("Shooter Version Test"));
            controllerObject.AddComponent<Character>();
            NetworkShooterController controller =
                controllerObject.AddComponent<NetworkShooterController>();
            SetPrivateField(controller, "m_LogDiagnostics", false);
            controller.Initialize(false, false);

            controller.ApplyRemoteWeaponState(NetworkWeaponState.None, null, null, null);
            var second = NetworkWeaponState.None;
            second.LeanAmount = 9f;
            controller.ApplyRemoteWeaponState(second, null, null, null);

            int version = GetPrivateField<int>(controller, "m_RemoteWeaponApplyVersion");
            NetworkWeaponState applied = GetPrivateField<NetworkWeaponState>(
                controller,
                "m_LastWeaponState");
            Assert.That(version, Is.EqualTo(2));
            Assert.That(applied.LeanAmount, Is.EqualTo(9f));
        }

        private NetworkShooterManager CreateManager(bool isServer, bool isClient)
        {
            GameObject managerObject = Track(new GameObject("Shooter Manager Test"));
            NetworkShooterManager manager = managerObject.AddComponent<NetworkShooterManager>();
            SetPrivateField(manager, "m_LogDiagnostics", false);
            manager.Initialize(isServer, isClient);
            return manager;
        }

        private NetworkShooterController CreateController(
            string name,
            bool isServer,
            bool isLocalClient)
        {
            GameObject controllerObject = Track(new GameObject(name));
            NetworkShooterController controller =
                controllerObject.AddComponent<NetworkShooterController>();
            SetPrivateField(controller, "m_LogDiagnostics", false);
            controller.UseGeneratedFallbackPresentation = true;
            controller.Initialize(isServer, isLocalClient);
            return controller;
        }

        private ShooterWeapon CreateForceWeapon(string name, float force)
        {
            ShooterWeapon weapon = Track(ScriptableObject.CreateInstance<ShooterWeapon>());
            weapon.name = name;

            FieldInfo forceField = typeof(Fire).GetField(
                "m_Force",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(forceField, Is.Not.Null, "Shooter Fire.m_Force field was not found");
            forceField.SetValue(
                weapon.Fire,
                new GameCreator.Runtime.Common.EnablerFloat(true, force));

            NetworkShooterManager.RegisterShooterWeapon(weapon);
            return weapon;
        }

        private GameObject CreateDynamicTarget(string name, Vector3 position)
        {
            GameObject target = Track(new GameObject(name));
            target.transform.position = position;
            target.AddComponent<BoxCollider>();
            Rigidbody rigidbody = target.AddComponent<Rigidbody>();
            rigidbody.useGravity = false;
            return target;
        }

        private static int CountImpactObjects()
        {
            int count = 0;
            Transform[] transforms = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == "Network Shooter Impact") count++;
            }
            return count;
        }

        private static int CountTracerObjects()
        {
            int count = 0;
            Transform[] transforms = UnityEngine.Object.FindObjectsByType<Transform>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null && transforms[i].name == "Network Shooter Tracer") count++;
            }
            return count;
        }

        private static int CountEnumerable(IEnumerable values)
        {
            int count = 0;
            IEnumerator enumerator = values.GetEnumerator();
            while (enumerator.MoveNext()) count++;
            return count;
        }

        private static NetworkShooterCharacterSnapshot RoundTrip(
            NetworkShooterCharacterSnapshot value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetShooterValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkShooterCharacterSnapshot result = default;
            PurrNetShooterValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkShooterImpactPropSnapshot RoundTrip(
            NetworkShooterImpactPropSnapshot value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetShooterValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkShooterImpactPropSnapshot result = default;
            PurrNetShooterValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkShooterHitResponse RoundTrip(
            NetworkShooterHitResponse value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetShooterValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkShooterHitResponse result = default;
            PurrNetShooterValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkShooterHitBroadcast RoundTrip(
            NetworkShooterHitBroadcast value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetShooterValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkShooterHitBroadcast result = default;
            PurrNetShooterValuePackers.Read(packer, ref result);
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

        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }

        private static void SetNetworkCharacterRuntimeRole(
            NetworkCharacter character,
            bool isServer,
            bool isOwner,
            bool isHost)
        {
            SetPrivateField(character, "m_RuntimeIsServer", isServer);
            SetPrivateField(character, "m_RuntimeIsOwner", isOwner);
            SetPrivateField(character, "m_RuntimeIsHost", isHost);
        }

        private static void InvokePrivate(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {name}");
            method.Invoke(target, arguments);
        }

        private static T InvokePrivate<T>(object target, string name, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {name}");
            return (T)method.Invoke(target, arguments);
        }

        private static T InvokePrivateStatic<T>(System.Type type, string name, params object[] arguments)
        {
            MethodInfo method = type.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing static method {name}");
            return (T)method.Invoke(null, arguments);
        }
    }
}
#endif
