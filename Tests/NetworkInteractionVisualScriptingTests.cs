using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using UnityEngine;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkInteractionVisualScriptingTests
    {
        private readonly List<GameObject> m_Objects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Objects.Count - 1; i >= 0; i--)
            {
                if (m_Objects[i] != null) Object.DestroyImmediate(m_Objects[i]);
            }

            m_Objects.Clear();
            NetworkCorrelation.ResetComposeState();
        }

        [Test]
        public void ClientTypedInteractionRequest_PreservesTypeAndCorrelationContext()
        {
            NetworkCoreController controller = CreateController(false, true);
            NetworkInteractionRequest sent = default;
            controller.GetServerTime = () => 12.5f;
            controller.SendInteractionRequestToServer = request => sent = request;

            bool result = controller.TryRequestInteraction(
                101,
                202,
                303,
                new Vector3(1f, 2f, 3f),
                InteractionType.Open,
                out NetworkInteractionRequest generated);

            Assert.That(result, Is.True);
            Assert.That(sent.RequestId, Is.Not.Zero);
            Assert.That(sent, Is.EqualTo(generated));
            Assert.That(sent.ActorNetworkId, Is.EqualTo(101));
            Assert.That(sent.CorrelationId, Is.Not.Zero);
            Assert.That(sent.CharacterNetworkId, Is.EqualTo(101));
            Assert.That(sent.TargetNetworkId, Is.EqualTo(202));
            Assert.That(sent.TargetHash, Is.EqualTo(303));
            Assert.That(sent.InteractionType, Is.EqualTo(InteractionType.Open));
            Assert.That(sent.InteractionPosition, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            Assert.That(sent.ClientTime, Is.EqualTo(12.5f));
            Assert.That(NetworkInteractionRequest.SIZE_BYTES, Is.EqualTo(39));
        }

        [Test]
        public void CharacterNotFoundResponse_EchoesCompleteRequestContext()
        {
            NetworkCoreController controller = CreateController(true, false);
            NetworkInteractionResponse response = default;
            controller.GetCharacterByNetworkId = _ => null;
            controller.SendInteractionResponseToClient = (_, value) => response = value;

            var request = new NetworkInteractionRequest
            {
                RequestId = 41,
                ActorNetworkId = 101,
                CorrelationId = 0x01020304,
                CharacterNetworkId = 101,
                TargetNetworkId = 202,
                TargetHash = 303,
                InteractionType = InteractionType.Close,
                InteractionPosition = Vector3.one,
                ClientTime = 2f
            };

            controller.ProcessInteractionRequest(9, request);

            Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(response.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(response.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(response.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(response.TargetNetworkId, Is.EqualTo(request.TargetNetworkId));
            Assert.That(response.TargetHash, Is.EqualTo(request.TargetHash));
            Assert.That(response.InteractionType, Is.EqualTo(InteractionType.Close));
            Assert.That(response.Approved, Is.False);
            Assert.That(response.RejectReason, Is.EqualTo(InteractionRejectReason.CharacterNotFound));
            Assert.That(response.ResultData, Is.Zero);
            Assert.That(NetworkInteractionResponse.SIZE_BYTES, Is.EqualTo(29));
        }

        [Test]
        public void ApprovedInteraction_BroadcastsRequestedTypeAndResponseContext()
        {
            NetworkCoreController controller = CreateController(true, false);
            GameObject characterObject = Track(new GameObject("Interaction Actor"));
            Character character = characterObject.AddComponent<Character>();
            GameObject targetObject = Track(new GameObject("Interaction Door"));
            InteractionTracker tracker = InteractionTracker.Require(targetObject);

            // ProcessInteractionRequest first consumes the authoritative GC2 focus, then falls
            // back to the runtime spatial hash. EditMode tests do not advance Character.Update,
            // so establish the same focus that a live Character would have selected before the
            // player pressed Interact. This keeps the test about the network protocol instead of
            // depending on an editor-frame spatial-hash update.
            FieldInfo targetField = typeof(Interaction).GetField(
                "<Target>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(targetField, Is.Not.Null);
            targetField.SetValue(character.Interaction, tracker);

            int interactions = 0;
            tracker.EventInteract += (_, _) => interactions++;

            controller.GetCharacterByNetworkId = id => id == 101 ? character : null;
            controller.GetNetworkIdForGameObject = value => value == targetObject ? 202u : 0u;
            controller.GetServerTime = () => 50.25f;

            NetworkInteractionResponse response = default;
            NetworkInteractionBroadcast broadcast = default;
            controller.SendInteractionResponseToClient = (_, value) => response = value;
            controller.BroadcastInteractionToClients = value => broadcast = value;

            var request = new NetworkInteractionRequest
            {
                RequestId = 42,
                ActorNetworkId = 101,
                CorrelationId = 0x05060708,
                CharacterNetworkId = 101,
                TargetNetworkId = 202,
                TargetHash = 0,
                InteractionType = InteractionType.Open,
                InteractionPosition = targetObject.transform.position,
                ClientTime = 49f
            };

            controller.ProcessInteractionRequest(9, request);

            Assert.That(interactions, Is.EqualTo(1));
            Assert.That(response.Approved, Is.True);
            Assert.That(response.RejectReason, Is.EqualTo(InteractionRejectReason.None));
            Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(response.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(response.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(response.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(response.TargetNetworkId, Is.EqualTo(202));
            Assert.That(response.TargetHash, Is.Zero);
            Assert.That(response.InteractionType, Is.EqualTo(InteractionType.Open));

            Assert.That(broadcast.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(broadcast.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(broadcast.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(broadcast.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(broadcast.TargetNetworkId, Is.EqualTo(response.TargetNetworkId));
            Assert.That(broadcast.TargetHash, Is.EqualTo(response.TargetHash));
            Assert.That(broadcast.InteractionType, Is.EqualTo(InteractionType.Open));
            Assert.That(broadcast.ResultData, Is.EqualTo(response.ResultData));
            Assert.That(broadcast.ServerTime, Is.EqualTo(50.25f));
            Assert.That(NetworkInteractionBroadcast.SIZE_BYTES, Is.EqualTo(31));
        }

        [Test]
        public void ReceivedResponseAndBroadcast_UpdateVisualScriptingPayloadContext()
        {
            NetworkCoreController client = CreateController(false, true);
            var response = new NetworkInteractionResponse
            {
                RequestId = 7,
                ActorNetworkId = 11,
                CorrelationId = 12,
                CharacterNetworkId = 11,
                TargetNetworkId = 13,
                TargetHash = 14,
                InteractionType = InteractionType.Talk,
                Approved = false,
                RejectReason = InteractionRejectReason.OutOfRange,
                ResultData = 15
            };

            client.ReceiveInteractionResponse(response);

            Assert.That(NetworkInteractionEvents.LastEventType,
                Is.EqualTo(NetworkInteractionEventType.Rejected));
            Assert.That(NetworkInteractionEvents.LastRejectReason,
                Is.EqualTo(InteractionRejectReason.OutOfRange));
            Assert.That(NetworkInteractionEvents.LastInteractionType,
                Is.EqualTo(InteractionType.Talk));
            Assert.That(NetworkInteractionEvents.LastResultData, Is.EqualTo(15));

            var broadcast = new NetworkInteractionBroadcast
            {
                RequestId = 8,
                ActorNetworkId = 21,
                CorrelationId = 22,
                CharacterNetworkId = 21,
                TargetNetworkId = 23,
                TargetHash = 24,
                InteractionType = InteractionType.Read,
                ResultData = 25,
                ServerTime = 26f
            };

            client.ReceiveInteractionBroadcast(broadcast);

            Assert.That(NetworkInteractionEvents.LastEventType,
                Is.EqualTo(NetworkInteractionEventType.Broadcast));
            Assert.That(NetworkInteractionEvents.LastApproved, Is.True);
            Assert.That(NetworkInteractionEvents.LastInteractionType,
                Is.EqualTo(InteractionType.Read));
            Assert.That(NetworkInteractionEvents.LastCorrelationId, Is.EqualTo(22));
            Assert.That(NetworkInteractionEvents.LastResultData, Is.EqualTo(25));
            Assert.That(NetworkInteractionEvents.LastServerTime, Is.EqualTo(26f));
        }

        [Test]
        public void InteractionVisualScriptingFacade_ExposesExpectedNodeTypes()
        {
            Assert.That(typeof(Instruction).IsAssignableFrom(
                typeof(InstructionNetworkRequestInteraction)), Is.True);
            Assert.That(typeof(Event).IsAssignableFrom(
                typeof(EventNetworkInteractionApproved)), Is.True);
            Assert.That(typeof(Event).IsAssignableFrom(
                typeof(EventNetworkInteractionRejected)), Is.True);
            Assert.That(typeof(Event).IsAssignableFrom(
                typeof(EventNetworkInteractionBroadcast)), Is.True);
            Assert.That(typeof(Condition).IsAssignableFrom(
                typeof(ConditionNetworkInteractionTypeIs)), Is.True);
            Assert.That(typeof(Condition).IsAssignableFrom(
                typeof(ConditionNetworkInteractionRejectReasonIs)), Is.True);
            Assert.That(typeof(PropertyTypeGetGameObject).IsAssignableFrom(
                typeof(GetGameObjectNetworkInteractionTarget)), Is.True);
        }

        private NetworkCoreController CreateController(bool isServer, bool isClient)
        {
            GameObject gameObject = Track(new GameObject("Network Core Interaction Test"));
            NetworkCoreController controller = gameObject.AddComponent<NetworkCoreController>();
            controller.Initialize(isServer, isClient);
            return controller;
        }

        private GameObject Track(GameObject gameObject)
        {
            m_Objects.Add(gameObject);
            return gameObject;
        }
    }
}
