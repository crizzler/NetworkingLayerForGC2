using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionWireSerializerTests
    {
        private struct GoldenValue
        {
            public ushort RequestId;
            public uint ActorId;
            public bool Approved;
        }

        [Test]
        public void Serialize_UsesLittleEndianDeclarationOrder()
        {
            byte[] bytes = FusionWireSerializer.Serialize(new GoldenValue
            {
                RequestId = 0x1234,
                ActorId = 0x89ABCDEF,
                Approved = true
            });

            CollectionAssert.AreEqual(
                new byte[] { 0x34, 0x12, 0xEF, 0xCD, 0xAB, 0x89, 0x01 },
                bytes);
        }

        [Test]
        public void VariableRequest_RoundTripsUnicodeAndNull()
        {
            var source = new NetworkVariableRequest
            {
                RequestId = 17,
                ActorNetworkId = 42,
                CorrelationId = 99,
                TargetNetworkId = 7,
                Scope = NetworkVariableScope.LocalList,
                Operation = NetworkVariableOperation.Insert,
                ProfileHash = 1234,
                VariableHash = 5678,
                VariableName = "体力-🙂",
                Index = 3,
                IndexTo = 8,
                SerializedValue = null,
                ClientTime = 12.5f
            };

            NetworkVariableRequest result =
                FusionWireSerializer.Deserialize<NetworkVariableRequest>(
                    FusionWireSerializer.Serialize(source));

            Assert.AreEqual(source.RequestId, result.RequestId);
            Assert.AreEqual(source.ActorNetworkId, result.ActorNetworkId);
            Assert.AreEqual(source.CorrelationId, result.CorrelationId);
            Assert.AreEqual(source.TargetNetworkId, result.TargetNetworkId);
            Assert.AreEqual(source.Scope, result.Scope);
            Assert.AreEqual(source.Operation, result.Operation);
            Assert.AreEqual(source.ProfileHash, result.ProfileHash);
            Assert.AreEqual(source.VariableHash, result.VariableHash);
            Assert.AreEqual(source.VariableName, result.VariableName);
            Assert.AreEqual(source.Index, result.Index);
            Assert.AreEqual(source.IndexTo, result.IndexTo);
            Assert.IsNull(result.SerializedValue);
            Assert.AreEqual(source.ClientTime, result.ClientTime);
        }

        [Test]
        public void VariableSnapshot_PreservesEstablishedTimeBeforeChangesOrder()
        {
            byte[] bytes = FusionWireSerializer.Serialize(new NetworkVariableSnapshot
            {
                Changes = Array.Empty<NetworkVariableBroadcast>(),
                ServerTime = 1f
            });

            CollectionAssert.AreEqual(
                new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x00 },
                bytes);
        }

        [Test]
        public void CoreSnapshot_RoundTripsNestedArray()
        {
            var source = new NetworkCoreSnapshot
            {
                State = new NetworkCoreState
                {
                    CharacterNetworkId = 77,
                    DeltaFlags = CoreStateDeltaFlags.All,
                    IsRagdoll = true,
                    IsInvincible = true,
                    InvincibilityEndTime = 8.25f,
                    CurrentPoise = 12f,
                    MaximumPoise = 50f,
                    IsPoiseBroken = false,
                    BusyLimbs = BusyLimbs.Every,
                    ServerTime = 3.5f
                },
                Props = new[]
                {
                    new NetworkPropAttachmentState
                    {
                        CharacterNetworkId = 77,
                        PropInstanceId = 9,
                        PropHash = 10,
                        BoneHash = 11,
                        LocalPosition = new UnityEngine.Vector3(1f, 2f, 3f),
                        RotationX = 4,
                        RotationY = 5,
                        RotationZ = 6
                    }
                }
            };

            NetworkCoreSnapshot result =
                FusionWireSerializer.Deserialize<NetworkCoreSnapshot>(
                    FusionWireSerializer.Serialize(source));

            Assert.AreEqual(source.State.CharacterNetworkId, result.State.CharacterNetworkId);
            Assert.AreEqual(source.State.DeltaFlags, result.State.DeltaFlags);
            Assert.AreEqual(source.State.BusyLimbs, result.State.BusyLimbs);
            Assert.AreEqual(1, result.Props.Length);
            Assert.AreEqual(source.Props[0].PropInstanceId, result.Props[0].PropInstanceId);
            Assert.AreEqual(source.Props[0].LocalPosition, result.Props[0].LocalPosition);
        }

        [Test]
        public void InteractionPackets_RoundTripEveryFieldAndRetainSchemaSize()
        {
            var request = new NetworkInteractionRequest
            {
                RequestId = 16,
                ActorNetworkId = 106,
                CorrelationId = 207,
                CharacterNetworkId = 106,
                TargetNetworkId = 501,
                TargetHash = 6002,
                InteractionType = InteractionType.Open,
                InteractionPosition = new Vector3(3f, 4f, -5f),
                ClientTime = 21.25f
            };
            byte[] requestBytes = FusionWireSerializer.Serialize(request);
            NetworkInteractionRequest requestActual =
                FusionWireSerializer.Deserialize<NetworkInteractionRequest>(requestBytes);

            Assert.That(requestBytes, Has.Length.EqualTo(NetworkInteractionRequest.SIZE_BYTES));
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.TargetNetworkId, Is.EqualTo(request.TargetNetworkId));
            Assert.That(requestActual.TargetHash, Is.EqualTo(request.TargetHash));
            Assert.That(requestActual.InteractionType, Is.EqualTo(request.InteractionType));
            Assert.That(requestActual.InteractionPosition, Is.EqualTo(request.InteractionPosition));
            Assert.That(requestActual.ClientTime, Is.EqualTo(request.ClientTime));

            var response = new NetworkInteractionResponse
            {
                RequestId = 16,
                ActorNetworkId = 106,
                CorrelationId = 207,
                CharacterNetworkId = 106,
                TargetNetworkId = 501,
                TargetHash = 6002,
                InteractionType = InteractionType.Open,
                Approved = true,
                RejectReason = InteractionRejectReason.None,
                ResultData = 808
            };
            byte[] responseBytes = FusionWireSerializer.Serialize(response);
            NetworkInteractionResponse responseActual =
                FusionWireSerializer.Deserialize<NetworkInteractionResponse>(responseBytes);

            Assert.That(responseBytes, Has.Length.EqualTo(NetworkInteractionResponse.SIZE_BYTES));
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.CharacterNetworkId, Is.EqualTo(response.CharacterNetworkId));
            Assert.That(responseActual.TargetNetworkId, Is.EqualTo(response.TargetNetworkId));
            Assert.That(responseActual.TargetHash, Is.EqualTo(response.TargetHash));
            Assert.That(responseActual.InteractionType, Is.EqualTo(response.InteractionType));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));
            Assert.That(responseActual.ResultData, Is.EqualTo(response.ResultData));

            var broadcast = new NetworkInteractionBroadcast
            {
                RequestId = 16,
                ActorNetworkId = 106,
                CorrelationId = 207,
                CharacterNetworkId = 106,
                TargetNetworkId = 501,
                TargetHash = 6002,
                InteractionType = InteractionType.Open,
                ResultData = 808,
                ServerTime = 100.75f
            };
            byte[] broadcastBytes = FusionWireSerializer.Serialize(broadcast);
            NetworkInteractionBroadcast broadcastActual =
                FusionWireSerializer.Deserialize<NetworkInteractionBroadcast>(broadcastBytes);

            Assert.That(broadcastBytes, Has.Length.EqualTo(NetworkInteractionBroadcast.SIZE_BYTES));
            Assert.That(broadcastActual.RequestId, Is.EqualTo(broadcast.RequestId));
            Assert.That(broadcastActual.ActorNetworkId, Is.EqualTo(broadcast.ActorNetworkId));
            Assert.That(broadcastActual.CorrelationId, Is.EqualTo(broadcast.CorrelationId));
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.TargetNetworkId, Is.EqualTo(broadcast.TargetNetworkId));
            Assert.That(broadcastActual.TargetHash, Is.EqualTo(broadcast.TargetHash));
            Assert.That(broadcastActual.InteractionType, Is.EqualTo(broadcast.InteractionType));
            Assert.That(broadcastActual.ResultData, Is.EqualTo(broadcast.ResultData));
            Assert.That(broadcastActual.ServerTime, Is.EqualTo(broadcast.ServerTime));
        }

        [Test]
        public void Deserialize_RejectsTruncatedAndTrailingPayloads()
        {
            byte[] valid = FusionWireSerializer.Serialize(new GoldenValue
            {
                RequestId = 1,
                ActorId = 2,
                Approved = true
            });

            byte[] truncated = new byte[valid.Length - 1];
            Buffer.BlockCopy(valid, 0, truncated, 0, truncated.Length);
            Assert.Throws<EndOfStreamException>(
                () => FusionWireSerializer.Deserialize<GoldenValue>(truncated));

            byte[] trailing = new byte[valid.Length + 1];
            Buffer.BlockCopy(valid, 0, trailing, 0, valid.Length);
            Assert.Throws<InvalidDataException>(
                () => FusionWireSerializer.Deserialize<GoldenValue>(trailing));
        }
    }
}
