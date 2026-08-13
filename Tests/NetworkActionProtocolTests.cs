using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Security;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkActionProtocolTests
    {
        private const BindingFlags InstanceNonPublic =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<UnityEngine.Object> m_Cleanup = new();
        private INetworkOwnershipResolver m_OriginalOwnershipResolver;
        private NetworkSecurityManager m_TestSecurityManager;

        [SetUp]
        public void SetUp()
        {
            m_OriginalOwnershipResolver = SecurityIntegration.OwnershipResolver;
            SecurityIntegration.OwnershipResolver = new NetworkOwnershipResolver();
            SecurityIntegration.ClearModuleServerContexts();
            NetworkCorrelation.ResetComposeState();
            if (NetworkSecurityManager.Instance == null)
            {
                var security = Track(new GameObject("Network Actions Test Security"));
                m_TestSecurityManager = security.AddComponent<NetworkSecurityManager>();
                // Unity may omit ordinary MonoBehaviour Awake dispatch in a focused EditMode
                // run. Claim the singleton slot explicitly only when that happened.
                if (NetworkSecurityManager.Instance == null)
                {
                    MethodInfo awake = typeof(NetworkSingleton<NetworkSecurityManager>).GetMethod(
                        "Awake", InstanceNonPublic);
                    Assert.That(awake, Is.Not.Null);
                    awake.Invoke(m_TestSecurityManager, null);
                }
            }
            else m_TestSecurityManager = NetworkSecurityManager.Instance;

            Assert.That(NetworkSecurityManager.Instance, Is.SameAs(m_TestSecurityManager));
            m_TestSecurityManager.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
            m_TestSecurityManager = null;
            SecurityIntegration.OwnershipResolver = m_OriginalOwnershipResolver;
            SecurityIntegration.ClearModuleServerContexts();
            NetworkCorrelation.ResetComposeState();
        }

        [Test]
        public void PayloadValidation_RejectsWrongDiscriminatorAndNonFiniteValues()
        {
            NetworkActionDefinition number = CreateDefinition(
                "tests.number", NetworkActionPayloadType.Number);
            Assert.That(number.TryValidatePayload(
                NetworkActionPayload.FromBoolean(true), out NetworkActionRejectReason wrongType),
                Is.False);
            Assert.That(wrongType, Is.EqualTo(NetworkActionRejectReason.InvalidPayload));
            Assert.That(number.TryValidatePayload(
                NetworkActionPayload.FromNumber(float.NaN), out NetworkActionRejectReason nan),
                Is.False);
            Assert.That(nan, Is.EqualTo(NetworkActionRejectReason.InvalidPayload));
            Assert.That(number.TryValidatePayload(
                NetworkActionPayload.FromNumber(float.PositiveInfinity), out _), Is.False);

            NetworkActionDefinition vector = CreateDefinition(
                "tests.vector", NetworkActionPayloadType.Vector3);
            Assert.That(vector.TryValidatePayload(
                NetworkActionPayload.FromVector3(new Vector3(1f, float.NegativeInfinity, 3f)),
                out NetworkActionRejectReason infiniteVector), Is.False);
            Assert.That(infiniteVector, Is.EqualTo(NetworkActionRejectReason.InvalidPayload));
        }

        [Test]
        public void StringPayload_IsBoundedOnValidationAndNormalization()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "tests.string", NetworkActionPayloadType.String);
            string oversized = new('x', NetworkActionManager.MaxPayloadStringCharacters + 17);
            NetworkActionPayload payload = NetworkActionPayload.FromString(oversized);

            Assert.That(definition.TryValidatePayload(payload, out NetworkActionRejectReason reason),
                Is.False);
            Assert.That(reason, Is.EqualTo(NetworkActionRejectReason.InvalidPayload));

            NetworkActionPayload normalized = definition.NormalizePayload(payload);
            Assert.That(normalized.Type, Is.EqualTo(NetworkActionPayloadType.String));
            Assert.That(normalized.StringValue.Length,
                Is.EqualTo(NetworkActionManager.MaxPayloadStringCharacters));
        }

        [Test]
        public void InactiveStringField_IsAlsoBoundedBeforePayloadAdmission()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "tests.boolean-bounds", NetworkActionPayloadType.Boolean);
            NetworkActionPayload payload = NetworkActionPayload.FromBoolean(true);
            payload.StringValue = new string(
                'x', NetworkActionManager.MaxPayloadStringCharacters + 1);

            Assert.That(definition.TryValidatePayload(
                    payload, out NetworkActionRejectReason reason),
                Is.False,
                "The wire union always carries StringValue, so inactive fields must not bypass bounds.");
            Assert.That(reason, Is.EqualTo(NetworkActionRejectReason.InvalidPayload));
        }

        [Test]
        public void PersistentDefinition_ForcesReliableObserverVisibleContract()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "tests.persistent-contract",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.OwnerRequest,
                NetworkActionRecipientPolicy.Requester,
                false);
            MethodInfo onValidate = typeof(NetworkActionDefinition).GetMethod(
                "OnValidate", InstanceNonPublic);
            Assert.That(onValidate, Is.Not.Null);

            onValidate.Invoke(definition, null);

            Assert.That(definition.Reliable, Is.True);
            Assert.That(definition.RecipientPolicy,
                Is.EqualTo(NetworkActionRecipientPolicy.RelevantObservers));
        }

        [Test]
        public void ContextScopes_AreNestedAndRestoreThePreviousExecution()
        {
            var outerBroadcast = new NetworkActionBroadcast
            {
                ActionId = "outer",
                Payload = NetworkActionPayload.FromBoolean(true)
            };
            var innerBroadcast = new NetworkActionBroadcast
            {
                ActionId = "inner",
                Payload = NetworkActionPayload.FromNumber(9f)
            };
            NetworkActionExecutionContext outer =
                NetworkActionExecutionContext.FromBroadcast(in outerBroadcast, null, null);
            NetworkActionExecutionContext inner =
                NetworkActionExecutionContext.FromBroadcast(in innerBroadcast, null, null);

            Assert.That(NetworkActionContext.HasCurrent, Is.False);
            using (NetworkActionContext.Push(in outer))
            {
                Assert.That(NetworkActionContext.Current.ActionId, Is.EqualTo("outer"));
                using (NetworkActionContext.Push(in inner))
                {
                    Assert.That(NetworkActionContext.Current.ActionId, Is.EqualTo("inner"));
                    Assert.That(NetworkActionContext.Current.Payload.NumberValue, Is.EqualTo(9f));
                }

                Assert.That(NetworkActionContext.Current.ActionId, Is.EqualTo("outer"));
            }

            Assert.That(NetworkActionContext.HasCurrent, Is.False);
        }

        [Test]
        public async Task ContextScopes_AreIsolatedAcrossConcurrentAsyncFlows()
        {
            async Task<string> ObserveAsync(string actionId)
            {
                var broadcast = new NetworkActionBroadcast
                {
                    ActionId = actionId,
                    Payload = NetworkActionPayload.FromString(actionId)
                };
                NetworkActionExecutionContext context =
                    NetworkActionExecutionContext.FromBroadcast(in broadcast, null, null);
                using (NetworkActionContext.Push(in context))
                {
                    await Task.Yield();
                    Assert.That(NetworkActionContext.HasCurrent, Is.True);
                    Assert.That(NetworkActionContext.Current.Payload.StringValue,
                        Is.EqualTo(actionId));
                    return NetworkActionContext.Current.ActionId;
                }
            }

            Task<string> first = ObserveAsync("async.first");
            Task<string> second = ObserveAsync("async.second");
            string[] observed = await Task.WhenAll(first, second);

            Assert.That(observed, Is.EqualTo(new[] { "async.first", "async.second" }));
            Assert.That(NetworkActionContext.HasCurrent, Is.False);
        }

        [Test]
        public void OwnerRequest_AllowsOwnedActorToOperateUnownedSceneEndpoint()
        {
            NetworkActionManager manager = CreateManager(false);
            NetworkActionEndpoint unownedDoor = CreateEndpoint(
                manager, "Unowned Door", 2001, Array.Empty<NetworkActionDefinition>());
            NetworkActionDefinition ownerRequest = CreateDefinition(
                "door.toggle",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.OwnerRequest);

            MethodInfo validate = typeof(NetworkActionManager).GetMethod(
                "ValidateAuthorityPolicy", InstanceNonPublic);
            Assert.That(validate, Is.Not.Null);
            object[] ownerArgs =
            {
                false, 7u, unownedDoor, ownerRequest, NetworkActionRejectReason.None
            };
            Assert.That((bool)validate.Invoke(manager, ownerArgs), Is.True);
            Assert.That(ownerArgs[4], Is.EqualTo(NetworkActionRejectReason.None));

            NetworkActionDefinition authorityOnly = CreateDefinition(
                "door.authority-only",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.AuthorityOnly);
            object[] authorityArgs =
            {
                false, 7u, unownedDoor, authorityOnly, NetworkActionRejectReason.None
            };
            Assert.That((bool)validate.Invoke(manager, authorityArgs), Is.False);
            Assert.That(authorityArgs[4], Is.EqualTo(NetworkActionRejectReason.NotAuthorized));
        }

        [Test]
        public void RemoteOwnedActor_RequestIsApprovedForUnownedSceneEndpoint()
        {
            const uint clientId = 7;
            const uint actorId = 1001;
            NetworkActionManager manager = CreateManager(true);
            CreateEndpoint(manager, "Remote Actor", actorId,
                Array.Empty<NetworkActionDefinition>());
            SecurityIntegration.RegisterActorOwnership(actorId, clientId);
            NetworkActionDefinition definition = CreateDefinition(
                "door.remote-open",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.OwnerRequest,
                NetworkActionRecipientPolicy.AllClients,
                true);
            NetworkActionEndpoint door = CreateEndpoint(
                manager, "Unowned Scene Door", 2001, new[] { definition });

            NetworkActionResponse response = default;
            NetworkActionBroadcast broadcast = default;
            manager.OnSendActionResponse = (recipient, value) =>
            {
                Assert.That(recipient, Is.EqualTo(clientId));
                response = value;
            };
            manager.OnBroadcastAction = value => broadcast = value;
            ushort requestId = 1;
            var request = new NetworkActionRequest
            {
                RequestId = requestId,
                ActorNetworkId = actorId,
                CorrelationId = NetworkCorrelation.Compose(actorId, requestId),
                TargetNetworkId = door.NetworkId,
                EndpointHash = door.EndpointHash,
                ActionHash = definition.ActionHash,
                ActionId = definition.ActionId,
                SchemaVersion = definition.SchemaVersion,
                Payload = NetworkActionPayload.FromBoolean(true),
                ClientTime = 1f
            };

            manager.ReceiveActionRequest(request, clientId);

            Assert.That(response.Authorized, Is.True);
            Assert.That(response.RejectReason, Is.EqualTo(NetworkActionRejectReason.None));
            Assert.That(broadcast.TargetNetworkId, Is.EqualTo(door.NetworkId));
            Assert.That(broadcast.Payload.BooleanValue, Is.True);
        }

        [Test]
        public void SecurityManager_InitializesDedicatedActionsRateLimiter()
        {
            CreateManager(true);
            NetworkSecurityManager security = NetworkSecurityManager.Instance;
            Assert.That(security, Is.Not.Null);
            FieldInfo field = typeof(NetworkSecurityManager).GetField(
                "m_RateLimiters", InstanceNonPublic);
            Assert.That(field, Is.Not.Null);
            var limiters = (IDictionary)field.GetValue(security);

            Assert.That(limiters.Contains("Actions"), Is.True,
                "Network Actions must not bypass the shared module request limiter.");
        }

        [Test]
        public void AuthorityLocalRequest_CompletesCallbackOnceAndClearsPendingEntry()
        {
            NetworkActionManager manager = CreateManager(true);
            CreateEndpoint(manager, "Actor", 1001, Array.Empty<NetworkActionDefinition>());
            NetworkActionDefinition definition = CreateDefinition(
                "door.toggle",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.OwnerRequest,
                NetworkActionRecipientPolicy.AllClients,
                true);
            NetworkActionEndpoint door = CreateEndpoint(
                manager, "Door", 2001, new[] { definition });

            int callbackCount = 0;
            NetworkActionResponse response = default;
            NetworkActionBroadcast broadcast = default;
            manager.OnBroadcastAction = value => broadcast = value;

            bool submitted = manager.RequestAction(
                1001,
                door,
                definition,
                NetworkActionPayload.FromBoolean(true),
                callback: value =>
                {
                    callbackCount++;
                    response = value;
                });

            Assert.That(submitted, Is.True);
            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(response.Authorized, Is.True);
            Assert.That(response.Revision, Is.EqualTo(2u));
            Assert.That(broadcast.Payload.BooleanValue, Is.True);
            Assert.That(broadcast.Reliable, Is.True);
            Assert.That(GetPendingCount(manager), Is.Zero);
        }

        [Test]
        public void AuthorityOnlyRequest_RemainsLocalWhenTransportDelegateIsWired()
        {
            NetworkActionManager manager = CreateManager(true);
            CreateEndpoint(manager, "Authority Actor", 1001,
                Array.Empty<NetworkActionDefinition>());
            NetworkActionDefinition definition = CreateDefinition(
                "authority.reset",
                NetworkActionPayloadType.None,
                NetworkActionEffectKind.TransientEvent,
                NetworkActionAuthorityPolicy.AuthorityOnly,
                NetworkActionRecipientPolicy.AllClients,
                true);
            NetworkActionEndpoint endpoint = CreateEndpoint(
                manager, "Authority Target", 2001, new[] { definition });
            int outboundRequestCount = 0;
            int callbackCount = 0;
            NetworkActionResponse response = default;
            manager.OnSendActionRequest = _ => outboundRequestCount++;

            Assert.That(manager.RequestAction(
                1001,
                endpoint,
                definition,
                NetworkActionPayload.None,
                callback: value =>
                {
                    callbackCount++;
                    response = value;
                }), Is.True);

            Assert.That(outboundRequestCount, Is.Zero,
                "Logical authority must not loop an AuthorityOnly request through a client route.");
            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(response.Authorized, Is.True);
            Assert.That(response.RejectReason, Is.EqualTo(NetworkActionRejectReason.None));
        }

        [Test]
        public async Task PersistentAction_BuildsTargetedLateJoinSnapshotAndClientAppliesIt()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.open",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.OwnerRequest,
                NetworkActionRecipientPolicy.RelevantObservers,
                true);

            NetworkActionManager authority = CreateManager(true);
            NetworkActionEndpoint authorityActor = CreateEndpoint(
                authority, "Authority Actor", 1001, Array.Empty<NetworkActionDefinition>());
            NetworkActionEndpoint authorityDoor = CreateEndpoint(
                authority, "Authority Door", 2001, new[] { definition });
            Assert.That(authorityActor, Is.Not.Null);
            Assert.That(authority.RequestAction(
                1001, authorityDoor, definition, NetworkActionPayload.FromBoolean(true)), Is.True);
            await DrainDeferredActionExecution();

            uint targetClient = 73;
            uint sentClient = 0;
            NetworkActionSnapshot sentSnapshot = default;
            authority.OnSendSnapshotToClient = (clientId, snapshot) =>
            {
                sentClient = clientId;
                sentSnapshot = snapshot;
            };
            authority.SendInitialState(targetClient);

            Assert.That(sentClient, Is.EqualTo(targetClient));
            Assert.That(sentSnapshot.Entries, Has.Length.EqualTo(1));
            Assert.That(sentSnapshot.Entries[0].IsSnapshot, Is.True);
            Assert.That(sentSnapshot.Entries[0].Revision, Is.EqualTo(2u));
            Assert.That(sentSnapshot.Entries[0].Payload.BooleanValue, Is.True);

            DestroyTrackedSceneObjects();

            NetworkActionManager client = CreateManager(false);
            CreateEndpoint(client, "Client Door", 2001, new[] { definition });
            NetworkActionExecutionContext appliedContext = default;
            Action<NetworkActionExecutionContext> listener = value => appliedContext = value;
            NetworkActionEvents.Applied += listener;
            try
            {
                client.ReceiveActionSnapshot(sentSnapshot);
                await DrainDeferredActionExecution();
            }
            finally
            {
                NetworkActionEvents.Applied -= listener;
            }

            Assert.That(client.TryGetPersistentState(
                GetEndpoint(client, 2001), definition, out NetworkActionPayload payload, out uint revision), Is.True);
            Assert.That(payload.BooleanValue, Is.True);
            Assert.That(revision, Is.EqualTo(2u));
            Assert.That(appliedContext.Phase,
                Is.EqualTo(NetworkActionContextPhase.SnapshotApplied));
            Assert.That(appliedContext.IsSnapshot, Is.True);
        }

        [Test]
        public void SetAuthorityEpoch_RebasesAuthoredPersistentStateBeforeSnapshot()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.epoch", NetworkActionPayloadType.Boolean);
            NetworkActionManager manager = CreateManager(true);
            CreateEndpoint(manager, "Door", 2001, new[] { definition });

            manager.SetAuthorityEpoch(9);
            NetworkActionSnapshot snapshot = manager.BuildSnapshot(4f);

            Assert.That(manager.AuthorityEpoch, Is.EqualTo(9u));
            Assert.That(snapshot.AuthorityEpoch, Is.EqualTo(9u));
            Assert.That(snapshot.Entries, Has.Length.EqualTo(1));
            Assert.That(snapshot.Entries[0].AuthorityEpoch, Is.EqualTo(9u));
        }

        [Test]
        public void ResetSessionState_ClearsStateAndCompletesPendingCallbacks()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.reset", NetworkActionPayloadType.Boolean);
            NetworkActionManager manager = CreateManager(true);
            CreateEndpoint(manager, "Actor", 1001, Array.Empty<NetworkActionDefinition>());
            NetworkActionEndpoint door = CreateEndpoint(
                manager, "Door", 2001, new[] { definition });
            Assert.That(manager.TryGetPersistentState(
                door, definition, out _, out _), Is.True);

            manager.ResetSessionState();
            Assert.That(manager.TryGetPersistentState(
                door, definition, out _, out _), Is.False);

            // In client role a transport delegate owns delivery, so the callback remains
            // pending until the session reset completes it.
            manager.OnSendActionRequest = _ => { };
            int callbackCount = 0;
            NetworkActionResponse response = default;
            Assert.That(manager.RequestAction(
                1001,
                door,
                definition,
                NetworkActionPayload.FromBoolean(true),
                callback: value =>
                {
                    callbackCount++;
                    response = value;
                }), Is.True);
            Assert.That(GetPendingCount(manager), Is.EqualTo(1));

            manager.ResetSessionState();

            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(response.Authorized, Is.False);
            Assert.That(response.RejectReason, Is.EqualTo(NetworkActionRejectReason.NotRunning));
            Assert.That(GetPendingCount(manager), Is.Zero);
            Assert.That(manager.AuthorityEpoch, Is.Zero);
            Assert.That(manager.IsServer, Is.False);

            manager.IsServer = true;
            Assert.That(manager.TryGetPersistentState(
                door, definition, out NetworkActionPayload initial, out uint revision), Is.True);
            Assert.That(initial.BooleanValue, Is.False);
            Assert.That(revision, Is.EqualTo(1u));
        }

        [Test]
        public void ClientPersistentState_RejectsOlderRevisionButAcceptsNewAuthorityEpoch()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.state", NetworkActionPayloadType.Boolean);
            NetworkActionManager client = CreateManager(false);
            CreateEndpoint(client, "Client Door", 2001, new[] { definition });

            client.ReceiveActionBroadcast(CreateBroadcast(
                definition, false, revision: 5, epoch: 3));
            client.ReceiveActionBroadcast(CreateBroadcast(
                definition, true, revision: 4, epoch: 3));
            AssertPersistent(client, definition, false, 5);

            client.ReceiveActionBroadcast(CreateBroadcast(
                definition, true, revision: 1, epoch: 4));
            AssertPersistent(client, definition, true, 1);
        }

        [Test]
        public void LateJoinSnapshot_ArrivingBeforeEndpointRegistration_IsAppliedAfterRegistration()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.deferred", NetworkActionPayloadType.Boolean);
            NetworkActionManager client = CreateManager(false);
            NetworkActionBroadcast state = CreateBroadcast(
                definition, true, revision: 7, epoch: 3);

            client.ReceiveActionSnapshot(new NetworkActionSnapshot
            {
                Entries = new[] { state },
                AuthorityEpoch = 3,
                ServerTime = 1f
            });

            NetworkActionEndpoint endpoint = CreateEndpoint(
                client, "Deferred Door", 2001, new[] { definition });

            Assert.That(client.TryGetPersistentState(
                endpoint, definition, out NetworkActionPayload payload, out uint revision),
                Is.True);
            Assert.That(payload.BooleanValue, Is.True);
            Assert.That(revision, Is.EqualTo(7u));
        }

        [Test]
        public void ReusedNetworkId_DoesNotApplyStateFromDifferentEndpointIdentity()
        {
            const string staleEndpointId = "test.endpoint.stale-generation";
            const string currentEndpointId = "test.endpoint.current-generation";
            NetworkActionDefinition definition = CreateDefinition(
                "door.reused-id", NetworkActionPayloadType.Boolean);
            NetworkActionManager client = CreateManager(false);

            client.ReceiveActionBroadcast(CreateBroadcast(
                definition, true, revision: 9, epoch: 2, staleEndpointId));
            NetworkActionEndpoint endpoint = CreateEndpoint(
                client,
                "Current Door",
                2001,
                new[] { definition },
                currentEndpointId);

            Assert.That(client.TryGetPersistentState(endpoint, definition, out _, out _),
                Is.False,
                "A deferred state from a previous endpoint identity must be purged on registration.");

            client.ReceiveActionBroadcast(CreateBroadcast(
                definition, true, revision: 10, epoch: 2, staleEndpointId));
            Assert.That(client.TryGetPersistentState(endpoint, definition, out _, out _),
                Is.False,
                "A stale broadcast must not mutate a different endpoint sharing its Network ID.");

            client.ReceiveActionBroadcast(CreateBroadcast(
                definition, true, revision: 1, epoch: 3, currentEndpointId));
            Assert.That(client.TryGetPersistentState(
                endpoint, definition, out NetworkActionPayload payload, out uint revision),
                Is.True);
            Assert.That(payload.BooleanValue, Is.True);
            Assert.That(revision, Is.EqualTo(1u));
        }

        [Test]
        public void TerminalCompositeUnregister_SamePrefabNetworkIdReuseStartsFromInitialState()
        {
            const string endpointId = "test.endpoint.same-prefab-generation";
            NetworkActionDefinition definition = CreateDefinition(
                "door.same-prefab-reuse", NetworkActionPayloadType.Boolean);
            NetworkActionManager authority = CreateManager(true);
            CreateEndpoint(
                authority, "Actor", 1001, Array.Empty<NetworkActionDefinition>());
            NetworkActionEndpoint first = CreateEndpoint(
                authority, "First Door", 2001, new[] { definition }, endpointId);

            Assert.That(authority.RequestAction(
                1001, first, definition, NetworkActionPayload.FromBoolean(true)), Is.True);
            Assert.That(authority.TryGetPersistentState(
                first, definition, out NetworkActionPayload changed, out uint changedRevision),
                Is.True);
            Assert.That(changed.BooleanValue, Is.True);
            Assert.That(changedRevision, Is.EqualTo(2u));

            var generation = new NetworkActionEndpointKey(
                first.NetworkId, first.EndpointHash);
            UnityEngine.Object.DestroyImmediate(first.gameObject);
            authority.UnregisterEndpoint(generation, true);

            NetworkActionEndpoint replacement = CreateEndpoint(
                authority, "Replacement Door", 2001, new[] { definition }, endpointId);
            Assert.That(authority.TryGetPersistentState(
                replacement,
                definition,
                out NetworkActionPayload initial,
                out uint initialRevision), Is.True);
            Assert.That(initial.BooleanValue, Is.False,
                "A new object generation must not inherit the despawned door's state.");
            Assert.That(initialRevision, Is.EqualTo(1u));
        }

        [Test]
        public async Task DestroyedEndpoint_DoesNotRaiseAppliedAfterAsyncYield()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.destroyed-async", NetworkActionPayloadType.Boolean);
            NetworkActionManager client = CreateManager(false);
            NetworkActionEndpoint endpoint = CreateEndpoint(
                client, "Async Door", 2001, new[] { definition });
            int appliedCount = 0;
            void OnApplied(NetworkActionExecutionContext _) => appliedCount++;
            NetworkActionEvents.Applied += OnApplied;
            try
            {
                client.ReceiveActionBroadcast(CreateBroadcast(
                    definition, true, revision: 2, epoch: 1));
                UnityEngine.Object.DestroyImmediate(endpoint.gameObject);
                await Task.Yield();
                await Task.Yield();

                Assert.That(appliedCount, Is.Zero,
                    "An async action must not fire On Applied after its endpoint was destroyed.");
            }
            finally
            {
                NetworkActionEvents.Applied -= OnApplied;
            }
        }

        [Test]
        public void ClientBroadcast_RejectsPayloadThatViolatesRegisteredSchema()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.boolean", NetworkActionPayloadType.Boolean);
            NetworkActionManager client = CreateManager(false);
            CreateEndpoint(client, "Client Door", 2001, new[] { definition });
            NetworkActionBroadcast malformed = CreateBroadcast(
                definition, false, revision: 1, epoch: 1);
            malformed.Payload = NetworkActionPayload.FromString(
                new string('x', NetworkActionManager.MaxPayloadStringCharacters + 1));

            client.ReceiveActionBroadcast(malformed);

            Assert.That(client.TryGetPersistentState(
                GetEndpoint(client, 2001), definition, out _, out _), Is.False,
                "Wire data must be validated against the endpoint definition before state mutation.");
        }

        [Test]
        public void ClientBroadcast_RejectsEffectAndRecipientMetadataMismatch()
        {
            NetworkActionDefinition definition = CreateDefinition(
                "door.contract",
                NetworkActionPayloadType.Boolean,
                NetworkActionEffectKind.PersistentState,
                NetworkActionAuthorityPolicy.OwnerRequest,
                NetworkActionRecipientPolicy.RelevantObservers);
            NetworkActionManager client = CreateManager(false);
            CreateEndpoint(client, "Client Door", 2001, new[] { definition });
            int appliedCount = 0;
            Action<NetworkActionExecutionContext> listener = _ => appliedCount++;
            NetworkActionEvents.Applied += listener;
            try
            {
                NetworkActionBroadcast wrongEffect = CreateBroadcast(
                    definition, true, revision: 1, epoch: 1);
                wrongEffect.EffectKind = NetworkActionEffectKind.TransientEvent;
                client.ReceiveActionBroadcast(wrongEffect);

                NetworkActionBroadcast wrongRecipients = CreateBroadcast(
                    definition, true, revision: 1, epoch: 1);
                wrongRecipients.RecipientPolicy = NetworkActionRecipientPolicy.Requester;
                client.ReceiveActionBroadcast(wrongRecipients);
            }
            finally
            {
                NetworkActionEvents.Applied -= listener;
            }

            Assert.That(appliedCount, Is.Zero);
            Assert.That(client.TryGetPersistentState(
                GetEndpoint(client, 2001), definition, out _, out _), Is.False);
        }

        [Test]
        public void TransientReliabilityFlag_IsCopiedIntoCanonicalBroadcast()
        {
            NetworkActionManager manager = CreateManager(true);
            CreateEndpoint(manager, "Actor", 1001, Array.Empty<NetworkActionDefinition>());
            NetworkActionDefinition definition = CreateDefinition(
                "fx.spark",
                NetworkActionPayloadType.None,
                NetworkActionEffectKind.TransientEvent,
                NetworkActionAuthorityPolicy.OwnerRequest,
                NetworkActionRecipientPolicy.AllClients,
                false);
            NetworkActionEndpoint endpoint = CreateEndpoint(
                manager, "Spark", 2001, new[] { definition });
            NetworkActionBroadcast sent = default;
            manager.OnBroadcastAction = value => sent = value;

            Assert.That(manager.RequestAction(
                1001, endpoint, definition, NetworkActionPayload.None), Is.True);
            Assert.That(sent.ActionId, Is.EqualTo(definition.ActionId));
            Assert.That(sent.EffectKind, Is.EqualTo(NetworkActionEffectKind.TransientEvent));
            Assert.That(sent.Reliable, Is.False);
            Assert.That(sent.Revision, Is.Zero);
        }

        private NetworkActionManager CreateManager(bool isServer)
        {
            var gameObject = Track(new GameObject(isServer
                ? "Network Actions Authority"
                : "Network Actions Client"));
            NetworkActionManager manager = gameObject.AddComponent<NetworkActionManager>();
            manager.IsServer = isServer;
            return manager;
        }

        private NetworkActionEndpoint CreateEndpoint(
            NetworkActionManager manager,
            string name,
            uint networkId,
            NetworkActionDefinition[] definitions,
            string endpointId = null)
        {
            var gameObject = Track(new GameObject(name));
            NetworkActionEndpoint endpoint = gameObject.AddComponent<NetworkActionEndpoint>();
            SetPrivateField(
                endpoint,
                "m_EndpointId",
                endpointId ?? $"test.endpoint.{networkId}");
            var bindings = new NetworkActionBinding[definitions.Length];
            for (int i = 0; i < definitions.Length; i++)
                bindings[i] = new NetworkActionBinding(definitions[i]);
            SetPrivateField(endpoint, "m_Actions", bindings);
            endpoint.ApplyTransportNetworkIdentity(
                networkId, false, NetworkTransportBridge.InvalidClientId, false);
            Assert.That(manager.RegisterEndpoint(networkId, endpoint), Is.True);
            return endpoint;
        }

        private NetworkActionDefinition CreateDefinition(
            string actionId,
            NetworkActionPayloadType payloadType,
            NetworkActionEffectKind effectKind = NetworkActionEffectKind.PersistentState,
            NetworkActionAuthorityPolicy authorityPolicy = NetworkActionAuthorityPolicy.OwnerRequest,
            NetworkActionRecipientPolicy recipientPolicy = NetworkActionRecipientPolicy.RelevantObservers,
            bool reliable = true)
        {
            NetworkActionDefinition definition = Track(
                ScriptableObject.CreateInstance<NetworkActionDefinition>());
            SetPrivateField(definition, "m_ActionId", actionId);
            SetPrivateField(definition, "m_SchemaVersion", (ushort)1);
            SetPrivateField(definition, "m_PayloadType", payloadType);
            SetPrivateField(definition, "m_EffectKind", effectKind);
            SetPrivateField(definition, "m_AuthorityPolicy", authorityPolicy);
            SetPrivateField(definition, "m_RecipientPolicy", recipientPolicy);
            SetPrivateField(definition, "m_Reliable", reliable);
            SetPrivateField(definition, "m_Cooldown", 0f);
            SetPrivateField(definition, "m_MaximumDistance", 0f);
            SetPrivateField(definition, "m_RequireLineOfSight", false);
            SetPrivateField(definition, "m_IncludeInLateJoinSnapshot", true);
            SetPrivateField(definition, "m_InitialPayload", payloadType switch
            {
                NetworkActionPayloadType.None => NetworkActionPayload.None,
                NetworkActionPayloadType.Boolean => NetworkActionPayload.FromBoolean(false),
                NetworkActionPayloadType.Number => NetworkActionPayload.FromNumber(0f),
                NetworkActionPayloadType.String => NetworkActionPayload.FromString(string.Empty),
                NetworkActionPayloadType.Vector3 => NetworkActionPayload.FromVector3(Vector3.zero),
                _ => default
            });
            return definition;
        }

        private static NetworkActionBroadcast CreateBroadcast(
            NetworkActionDefinition definition,
            bool value,
            uint revision,
            uint epoch,
            string endpointId = null)
        {
            return new NetworkActionBroadcast
            {
                TargetNetworkId = 2001,
                EndpointHash = StableHashUtility.GetStableHash(
                    endpointId ?? "test.endpoint.2001"),
                ActionHash = definition.ActionHash,
                ActionId = definition.ActionId,
                SchemaVersion = definition.SchemaVersion,
                EffectKind = NetworkActionEffectKind.PersistentState,
                RecipientPolicy = NetworkActionRecipientPolicy.RelevantObservers,
                Reliable = true,
                Payload = NetworkActionPayload.FromBoolean(value),
                Revision = revision,
                AuthorityEpoch = epoch,
                ServerTime = 1f
            };
        }

        private static void AssertPersistent(
            NetworkActionManager manager,
            NetworkActionDefinition definition,
            bool expected,
            uint revision)
        {
            Assert.That(manager.TryGetPersistentState(
                GetEndpoint(manager, 2001), definition, out NetworkActionPayload payload, out uint actualRevision),
                Is.True);
            Assert.That(payload.BooleanValue, Is.EqualTo(expected));
            Assert.That(actualRevision, Is.EqualTo(revision));
        }

        private static NetworkActionEndpoint GetEndpoint(
            NetworkActionManager manager,
            uint networkId)
        {
            Assert.That(manager.TryGetEndpoint(networkId, out NetworkActionEndpoint endpoint),
                Is.True);
            return endpoint;
        }

        private static int GetPendingCount(NetworkActionManager manager)
        {
            FieldInfo field = typeof(NetworkActionManager).GetField(
                "m_PendingRequests", InstanceNonPublic);
            Assert.That(field, Is.Not.Null);
            return ((IDictionary)field.GetValue(manager)).Count;
        }

        private void DestroyTrackedSceneObjects()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object value = m_Cleanup[i];
                if (value == null || value is ScriptableObject) continue;
                if (value is GameObject gameObject &&
                    gameObject.GetComponent<NetworkSecurityManager>() != null)
                {
                    // This helper separates authority and client objects inside one test. The
                    // fixture-owned security boundary must span both halves of that test.
                    continue;
                }
                UnityEngine.Object.DestroyImmediate(value);
                m_Cleanup.RemoveAt(i);
            }
        }

        private static async Task DrainDeferredActionExecution()
        {
            // Endpoint execution intentionally yields before invoking project-authored GC2
            // instructions. The manager raises Applied only after that task completes.
            await Task.Yield();
            await Task.Yield();
            await Task.Yield();
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, InstanceNonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }
    }
}
