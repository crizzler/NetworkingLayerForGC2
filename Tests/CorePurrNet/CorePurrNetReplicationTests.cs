using System;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    public sealed class CorePurrNetReplicationTests
    {
        private readonly List<UnityEngine.Object> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
        }

        [Test]
        public void CoreSnapshot_PurrNetRoundTrip_PreservesStateAndProps()
        {
            var prop = new NetworkPropAttachmentState
            {
                CharacterNetworkId = 17,
                PropInstanceId = 41,
                PropHash = 9001,
                BoneHash = 812,
                LocalPosition = new Vector3(1.25f, -2f, 3.5f)
            };
            prop.SetLocalRotation(Quaternion.Euler(15f, 120f, 275f));

            var expected = new NetworkCoreSnapshot
            {
                State = new NetworkCoreState
                {
                    CharacterNetworkId = 17,
                    DeltaFlags = CoreStateDeltaFlags.All,
                    IsRagdoll = true,
                    IsInvincible = true,
                    InvincibilityEndTime = 92.5f,
                    CurrentPoise = 3.25f,
                    MaximumPoise = 12f,
                    IsPoiseBroken = true,
                    BusyLimbs = BusyLimbs.ArmLeft | BusyLimbs.LegRight,
                    ServerTime = 87f
                },
                Props = new[] { prop }
            };

            NetworkCoreSnapshot actual = RoundTrip(expected);

            Assert.That(actual.CharacterNetworkId, Is.EqualTo(expected.CharacterNetworkId));
            Assert.That(actual.State.DeltaFlags, Is.EqualTo(CoreStateDeltaFlags.All));
            Assert.That(actual.State.IsRagdoll, Is.True);
            Assert.That(actual.State.IsInvincible, Is.True);
            Assert.That(actual.State.BusyLimbs, Is.EqualTo(expected.State.BusyLimbs));
            Assert.That(actual.State.InvincibilityEndTime, Is.EqualTo(92.5f));
            Assert.That(actual.Props, Has.Length.EqualTo(1));
            Assert.That(actual.Props[0].PropInstanceId, Is.EqualTo(41));
            Assert.That(actual.Props[0].PropHash, Is.EqualTo(9001));
            Assert.That(actual.Props[0].BoneHash, Is.EqualTo(812));
            Assert.That(actual.Props[0].LocalPosition, Is.EqualTo(prop.LocalPosition));
            Assert.That(
                Quaternion.Angle(actual.Props[0].GetLocalRotation(), prop.GetLocalRotation()),
                Is.LessThan(0.05f));
        }

        [Test]
        public void RagdollPackets_PurrNetRoundTrip_PreserveEveryField()
        {
            var request = new NetworkRagdollRequest
            {
                RequestId = 11,
                ActorNetworkId = 101,
                CorrelationId = 202,
                CharacterNetworkId = 101,
                ClientTime = 12.5f,
                ActionType = RagdollActionType.StartRagdollWithForce,
                Force = new Vector3(1f, 2f, 3f),
                ForcePoint = new Vector3(-4f, 5f, -6f)
            };
            NetworkRagdollRequest requestActual = RoundTrip(
                request,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.ClientTime, Is.EqualTo(request.ClientTime));
            Assert.That(requestActual.ActionType, Is.EqualTo(request.ActionType));
            Assert.That(requestActual.Force, Is.EqualTo(request.Force));
            Assert.That(requestActual.ForcePoint, Is.EqualTo(request.ForcePoint));

            var response = new NetworkRagdollResponse
            {
                RequestId = 11,
                ActorNetworkId = 101,
                CorrelationId = 202,
                Approved = false,
                RejectReason = RagdollRejectReason.Cooldown
            };
            NetworkRagdollResponse responseActual = RoundTrip(
                response,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));

            var broadcast = new NetworkRagdollBroadcast
            {
                CharacterNetworkId = 101,
                ActionType = RagdollActionType.InstantRecover,
                ServerTime = 44.25f,
                Force = Vector3.left,
                ForcePoint = Vector3.forward * 9f
            };
            NetworkRagdollBroadcast broadcastActual = RoundTrip(
                broadcast,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.ActionType, Is.EqualTo(broadcast.ActionType));
            Assert.That(broadcastActual.ServerTime, Is.EqualTo(broadcast.ServerTime));
            Assert.That(broadcastActual.Force, Is.EqualTo(broadcast.Force));
            Assert.That(broadcastActual.ForcePoint, Is.EqualTo(broadcast.ForcePoint));
        }

        [Test]
        public void PropPackets_PurrNetRoundTrip_PreserveInstanceAndRotation()
        {
            var request = new NetworkPropRequest
            {
                RequestId = 12,
                ActorNetworkId = 102,
                CorrelationId = 203,
                CharacterNetworkId = 102,
                ActionType = PropActionType.DetachInstance,
                PropHash = 3001,
                PropInstanceId = 7002,
                BoneHash = 4003,
                LocalPosition = new Vector3(0.25f, -1.5f, 3.75f)
            };
            request.SetLocalRotation(Quaternion.Euler(15f, 125f, 300f));
            NetworkPropRequest requestActual = RoundTrip(
                request,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.ActionType, Is.EqualTo(request.ActionType));
            Assert.That(requestActual.PropHash, Is.EqualTo(request.PropHash));
            Assert.That(requestActual.PropInstanceId, Is.EqualTo(request.PropInstanceId));
            Assert.That(requestActual.BoneHash, Is.EqualTo(request.BoneHash));
            Assert.That(requestActual.LocalPosition, Is.EqualTo(request.LocalPosition));
            Assert.That(requestActual.RotationX, Is.EqualTo(request.RotationX));
            Assert.That(requestActual.RotationY, Is.EqualTo(request.RotationY));
            Assert.That(requestActual.RotationZ, Is.EqualTo(request.RotationZ));
            Assert.That(
                Quaternion.Angle(requestActual.GetLocalRotation(), request.GetLocalRotation()),
                Is.LessThan(0.05f));

            var response = new NetworkPropResponse
            {
                RequestId = 12,
                ActorNetworkId = 102,
                CorrelationId = 203,
                Approved = true,
                RejectReason = PropRejectReason.None,
                PropInstanceId = 7002
            };
            NetworkPropResponse responseActual = RoundTrip(
                response,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));
            Assert.That(responseActual.PropInstanceId, Is.EqualTo(response.PropInstanceId));

            var broadcast = new NetworkPropBroadcast
            {
                CharacterNetworkId = 102,
                ActionType = PropActionType.AttachPrefab,
                PropHash = 3001,
                PropInstanceId = 7002,
                BoneHash = 4003,
                LocalPosition = new Vector3(-0.5f, 2.25f, 4f)
            };
            broadcast.SetLocalRotation(Quaternion.Euler(33f, 166f, 271f));
            NetworkPropBroadcast broadcastActual = RoundTrip(
                broadcast,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.ActionType, Is.EqualTo(broadcast.ActionType));
            Assert.That(broadcastActual.PropHash, Is.EqualTo(broadcast.PropHash));
            Assert.That(broadcastActual.PropInstanceId, Is.EqualTo(broadcast.PropInstanceId));
            Assert.That(broadcastActual.BoneHash, Is.EqualTo(broadcast.BoneHash));
            Assert.That(broadcastActual.LocalPosition, Is.EqualTo(broadcast.LocalPosition));
            Assert.That(broadcastActual.RotationX, Is.EqualTo(broadcast.RotationX));
            Assert.That(broadcastActual.RotationY, Is.EqualTo(broadcast.RotationY));
            Assert.That(broadcastActual.RotationZ, Is.EqualTo(broadcast.RotationZ));
            Assert.That(
                Quaternion.Angle(broadcastActual.GetLocalRotation(), broadcast.GetLocalRotation()),
                Is.LessThan(0.05f));
        }

        [Test]
        public void InvincibilityPackets_PurrNetRoundTrip_PreserveEveryField()
        {
            var request = new NetworkInvincibilityRequest
            {
                RequestId = 13,
                ActorNetworkId = 103,
                CorrelationId = 204,
                CharacterNetworkId = 103,
                Duration = 6.75f,
                ClientTime = 15.5f
            };
            NetworkInvincibilityRequest requestActual = RoundTrip(
                request,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.Duration, Is.EqualTo(request.Duration));
            Assert.That(requestActual.ClientTime, Is.EqualTo(request.ClientTime));

            var response = new NetworkInvincibilityResponse
            {
                RequestId = 13,
                ActorNetworkId = 103,
                CorrelationId = 204,
                Approved = false,
                RejectReason = InvincibilityRejectReason.OnCooldown,
                ApprovedDuration = 2.5f
            };
            NetworkInvincibilityResponse responseActual = RoundTrip(
                response,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));
            Assert.That(responseActual.ApprovedDuration, Is.EqualTo(response.ApprovedDuration));

            var broadcast = new NetworkInvincibilityBroadcast
            {
                CharacterNetworkId = 103,
                IsInvincible = true,
                StartTime = 70.25f,
                Duration = 8.5f
            };
            NetworkInvincibilityBroadcast broadcastActual = RoundTrip(
                broadcast,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.IsInvincible, Is.EqualTo(broadcast.IsInvincible));
            Assert.That(broadcastActual.StartTime, Is.EqualTo(broadcast.StartTime));
            Assert.That(broadcastActual.Duration, Is.EqualTo(broadcast.Duration));
        }

        [Test]
        public void PoisePackets_PurrNetRoundTrip_PreserveEveryField()
        {
            var request = new NetworkPoiseRequest
            {
                RequestId = 14,
                ActorNetworkId = 104,
                CorrelationId = 205,
                CharacterNetworkId = 104,
                ActionType = PoiseActionType.Damage,
                Value = 3.5f,
                ClientTime = 18.75f
            };
            NetworkPoiseRequest requestActual = RoundTrip(
                request,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.ActionType, Is.EqualTo(request.ActionType));
            Assert.That(requestActual.Value, Is.EqualTo(request.Value));
            Assert.That(requestActual.ClientTime, Is.EqualTo(request.ClientTime));

            var response = new NetworkPoiseResponse
            {
                RequestId = 14,
                ActorNetworkId = 104,
                CorrelationId = 205,
                Approved = true,
                RejectReason = PoiseRejectReason.None,
                CurrentPoise = 6.5f,
                IsBroken = true
            };
            NetworkPoiseResponse responseActual = RoundTrip(
                response,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));
            Assert.That(responseActual.CurrentPoise, Is.EqualTo(response.CurrentPoise));
            Assert.That(responseActual.IsBroken, Is.EqualTo(response.IsBroken));

            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = 104,
                CurrentPoise = 6.5f,
                MaximumPoise = 15f,
                IsBroken = true,
                ServerTime = 80.125f
            };
            NetworkPoiseBroadcast broadcastActual = RoundTrip(
                broadcast,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.CurrentPoise, Is.EqualTo(broadcast.CurrentPoise));
            Assert.That(broadcastActual.MaximumPoise, Is.EqualTo(broadcast.MaximumPoise));
            Assert.That(broadcastActual.IsBroken, Is.EqualTo(broadcast.IsBroken));
            Assert.That(broadcastActual.ServerTime, Is.EqualTo(broadcast.ServerTime));
        }

        [Test]
        public void BusyPackets_PurrNetRoundTrip_PreserveEveryField()
        {
            var request = new NetworkBusyRequest
            {
                RequestId = 15,
                ActorNetworkId = 105,
                CorrelationId = 206,
                CharacterNetworkId = 105,
                Limbs = BusyLimbs.ArmRight | BusyLimbs.LegLeft,
                SetBusy = true,
                Timeout = 1.75f
            };
            NetworkBusyRequest requestActual = RoundTrip(
                request,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.Limbs, Is.EqualTo(request.Limbs));
            Assert.That(requestActual.SetBusy, Is.EqualTo(request.SetBusy));
            Assert.That(requestActual.Timeout, Is.EqualTo(request.Timeout));

            var response = new NetworkBusyResponse
            {
                RequestId = 15,
                ActorNetworkId = 105,
                CorrelationId = 206,
                Approved = false,
                RejectReason = BusyRejectReason.NotBusy
            };
            NetworkBusyResponse responseActual = RoundTrip(
                response,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));

            var broadcast = new NetworkBusyBroadcast
            {
                CharacterNetworkId = 105,
                CurrentBusyLimbs = BusyLimbs.Arms | BusyLimbs.LegRight,
                ServerTime = 90.5f
            };
            NetworkBusyBroadcast broadcastActual = RoundTrip(
                broadcast,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.CurrentBusyLimbs, Is.EqualTo(broadcast.CurrentBusyLimbs));
            Assert.That(broadcastActual.ServerTime, Is.EqualTo(broadcast.ServerTime));
        }

        [Test]
        public void InteractionPackets_PurrNetRoundTrip_PreserveEveryField()
        {
            var request = new NetworkInteractionRequest
            {
                RequestId = 16,
                ActorNetworkId = 106,
                CorrelationId = 207,
                CharacterNetworkId = 106,
                TargetNetworkId = 501,
                TargetHash = 6002,
                InteractionPosition = new Vector3(3f, 4f, -5f),
                ClientTime = 21.25f
            };
            NetworkInteractionRequest requestActual = RoundTrip(
                request,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(requestActual.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(requestActual.ActorNetworkId, Is.EqualTo(request.ActorNetworkId));
            Assert.That(requestActual.CorrelationId, Is.EqualTo(request.CorrelationId));
            Assert.That(requestActual.CharacterNetworkId, Is.EqualTo(request.CharacterNetworkId));
            Assert.That(requestActual.TargetNetworkId, Is.EqualTo(request.TargetNetworkId));
            Assert.That(requestActual.TargetHash, Is.EqualTo(request.TargetHash));
            Assert.That(requestActual.InteractionPosition, Is.EqualTo(request.InteractionPosition));
            Assert.That(requestActual.ClientTime, Is.EqualTo(request.ClientTime));

            var response = new NetworkInteractionResponse
            {
                RequestId = 16,
                ActorNetworkId = 106,
                CorrelationId = 207,
                Approved = true,
                RejectReason = InteractionRejectReason.None,
                ResultData = 808
            };
            NetworkInteractionResponse responseActual = RoundTrip(
                response,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(responseActual.RequestId, Is.EqualTo(response.RequestId));
            Assert.That(responseActual.ActorNetworkId, Is.EqualTo(response.ActorNetworkId));
            Assert.That(responseActual.CorrelationId, Is.EqualTo(response.CorrelationId));
            Assert.That(responseActual.Approved, Is.EqualTo(response.Approved));
            Assert.That(responseActual.RejectReason, Is.EqualTo(response.RejectReason));
            Assert.That(responseActual.ResultData, Is.EqualTo(response.ResultData));

            var broadcast = new NetworkInteractionBroadcast
            {
                CharacterNetworkId = 106,
                TargetNetworkId = 501,
                TargetHash = 6002,
                InteractionType = InteractionType.Open,
                ServerTime = 100.75f
            };
            NetworkInteractionBroadcast broadcastActual = RoundTrip(
                broadcast,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(broadcastActual.CharacterNetworkId, Is.EqualTo(broadcast.CharacterNetworkId));
            Assert.That(broadcastActual.TargetNetworkId, Is.EqualTo(broadcast.TargetNetworkId));
            Assert.That(broadcastActual.TargetHash, Is.EqualTo(broadcast.TargetHash));
            Assert.That(broadcastActual.InteractionType, Is.EqualTo(broadcast.InteractionType));
            Assert.That(broadcastActual.ServerTime, Is.EqualTo(broadcast.ServerTime));

            var focus = new NetworkInteractionFocusBroadcast
            {
                CharacterNetworkId = 106,
                TargetNetworkId = 501,
                IsFocus = true
            };
            NetworkInteractionFocusBroadcast focusActual = RoundTrip(
                focus,
                PurrNetCoreValuePackers.Write,
                PurrNetCoreValuePackers.Read);
            Assert.That(focusActual.CharacterNetworkId, Is.EqualTo(focus.CharacterNetworkId));
            Assert.That(focusActual.TargetNetworkId, Is.EqualTo(focus.TargetNetworkId));
            Assert.That(focusActual.IsFocus, Is.EqualTo(focus.IsFocus));
        }

        [Test]
        public void ServerProps_ValidateAndDetachByExactInstance()
        {
            GameObject controllerObject = Track(new GameObject("Core Controller Test"));
            NetworkCoreController controller = controllerObject.AddComponent<NetworkCoreController>();
            controller.Initialize(true, false);

            GameObject characterObject = Track(new GameObject("Core Character Test"));
            Character character = characterObject.AddComponent<Character>();
            GameObject propPrefab = Track(new GameObject("Registered Prop"));

            controller.GetCharacterByNetworkId = id => id == 7 ? character : null;
            controller.GetPropPrefabByHash = hash => hash == 101 ? propPrefab : null;
            controller.GetBoneByHashForCharacter = (_, hash) => hash == 0 ? character.transform : null;

            var broadcasts = new List<NetworkPropBroadcast>();
            controller.BroadcastPropToClients = broadcasts.Add;

            Assert.That(controller.TryServerAttachProp(
                7, 404, 0, Vector3.zero, Quaternion.identity, out _, out PropRejectReason missing), Is.False);
            Assert.That(missing, Is.EqualTo(PropRejectReason.PropNotFound));

            Assert.That(controller.TryServerAttachProp(
                7, 101, 999, Vector3.zero, Quaternion.identity, out _, out PropRejectReason bone), Is.False);
            Assert.That(bone, Is.EqualTo(PropRejectReason.BoneNotFound));

            Assert.That(controller.TryServerAttachProp(
                7, 101, 0, Vector3.one, Quaternion.identity, out int firstId, out PropRejectReason first), Is.True);
            Assert.That(first, Is.EqualTo(PropRejectReason.None));

            Assert.That(controller.TryServerAttachProp(
                7, 101, 0, Vector3.right, Quaternion.identity, out int secondId, out _), Is.True);
            Assert.That(secondId, Is.Not.EqualTo(firstId));

            Assert.That(controller.TryServerDetachProp(7, firstId, out PropRejectReason detached), Is.True);
            Assert.That(detached, Is.EqualTo(PropRejectReason.None));
            Assert.That(broadcasts[^1].ActionType, Is.EqualTo(PropActionType.DetachInstance));
            Assert.That(broadcasts[^1].PropInstanceId, Is.EqualTo(firstId));

            Assert.That(controller.TryServerDetachProp(7, firstId, out PropRejectReason duplicate), Is.False);
            Assert.That(duplicate, Is.EqualTo(PropRejectReason.NotAttached));

            Assert.That(controller.ServerDetachAllProps(7, out PropRejectReason detachAll), Is.True);
            Assert.That(detachAll, Is.EqualTo(PropRejectReason.None));
            Assert.That(broadcasts[^1].ActionType, Is.EqualTo(PropActionType.DetachAll));
            Assert.That(controller.TryServerDetachProp(7, secondId, out PropRejectReason removed), Is.False);
            Assert.That(removed, Is.EqualTo(PropRejectReason.NotAttached));
        }

        [Test]
        public void AttachInstance_IsRejectedAsUnsupported()
        {
            GameObject controllerObject = Track(new GameObject("Core Controller Test"));
            NetworkCoreController controller = controllerObject.AddComponent<NetworkCoreController>();
            controller.Initialize(true, false);

            GameObject characterObject = Track(new GameObject("Core Character Test"));
            Character character = characterObject.AddComponent<Character>();
            controller.GetCharacterByNetworkId = _ => character;

            NetworkPropResponse response = default;
            bool received = false;
            controller.SendPropResponseToClient = (_, value) =>
            {
                response = value;
                received = true;
            };

            controller.ProcessPropRequest(3, new NetworkPropRequest
            {
                RequestId = 8,
                ActorNetworkId = 7,
                CorrelationId = 12,
                CharacterNetworkId = 7,
                ActionType = PropActionType.AttachInstance,
                PropHash = 101
            });

            Assert.That(received, Is.True);
            Assert.That(response.Approved, Is.False);
            Assert.That(response.RejectReason, Is.EqualTo(PropRejectReason.UnsupportedAction));
        }

        [Test]
        public void SnapshotPropReconciliation_IsIdempotentAndFullReplacement()
        {
            GameObject controllerObject = Track(new GameObject("Core Controller Test"));
            NetworkCoreController controller = controllerObject.AddComponent<NetworkCoreController>();
            GameObject characterObject = Track(new GameObject("Core Character Test"));
            Character character = characterObject.AddComponent<Character>();

            var expected = new NetworkPropAttachmentState
            {
                CharacterNetworkId = 7,
                PropInstanceId = 55,
                PropHash = 101,
                BoneHash = 0,
                LocalPosition = Vector3.up
            };

            Dictionary<uint, List<NetworkPropAttachmentState>> tracked = GetPrivateField<
                Dictionary<uint, List<NetworkPropAttachmentState>>>(controller, "m_CharacterProps");
            tracked[7] = new List<NetworkPropAttachmentState> { expected };

            MethodInfo reconcile = typeof(NetworkCoreController).GetMethod(
                "ReconcileSnapshotProps",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(reconcile, Is.Not.Null);

            reconcile.Invoke(controller, new object[] { character, 7u, new[] { expected } });
            reconcile.Invoke(controller, new object[] { character, 7u, new[] { expected } });
            Assert.That(tracked[7], Has.Count.EqualTo(1));
            Assert.That(tracked[7][0].PropInstanceId, Is.EqualTo(55));

            reconcile.Invoke(controller, new object[]
            {
                character,
                7u,
                Array.Empty<NetworkPropAttachmentState>()
            });
            Assert.That(tracked[7], Is.Empty);
        }

        [Test]
        public void NewerQueuedBusyBroadcast_AppliesAfterOlderQueuedSnapshot()
        {
            const uint characterNetworkId = 77;
            GameObject controllerObject = Track(new GameObject("Core Pending State Test"));
            NetworkCoreController controller = controllerObject.AddComponent<NetworkCoreController>();
            controller.Initialize(false, true);

            GameObject characterObject = Track(new GameObject("Core Pending Character Test"));
            Character character = characterObject.AddComponent<Character>();
            bool characterReady = false;
            controller.GetCharacterByNetworkId = id =>
                characterReady && id == characterNetworkId ? character : null;
            controller.GetServerTime = () => 50f;

            var olderSnapshot = new NetworkCoreSnapshot
            {
                State = new NetworkCoreState
                {
                    CharacterNetworkId = characterNetworkId,
                    DeltaFlags = CoreStateDeltaFlags.All,
                    IsRagdoll = false,
                    IsInvincible = false,
                    InvincibilityEndTime = 0f,
                    CurrentPoise = 10f,
                    MaximumPoise = 10f,
                    BusyLimbs = BusyLimbs.ArmLeft,
                    ServerTime = 40f
                },
                Props = Array.Empty<NetworkPropAttachmentState>()
            };
            Assert.That(controller.ReceiveCoreSnapshot(olderSnapshot), Is.False);

            controller.ReceiveBusyBroadcast(new NetworkBusyBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                CurrentBusyLimbs = BusyLimbs.LegRight,
                ServerTime = 45f
            });

            Dictionary<uint, NetworkCoreSnapshot> pendingSnapshots = GetPrivateField<
                Dictionary<uint, NetworkCoreSnapshot>>(controller, "m_PendingCoreSnapshots");
            Dictionary<uint, NetworkBusyBroadcast> pendingBusy = GetPrivateField<
                Dictionary<uint, NetworkBusyBroadcast>>(controller, "m_PendingBusyBroadcasts");
            Assert.That(pendingSnapshots.ContainsKey(characterNetworkId), Is.True);
            Assert.That(pendingBusy[characterNetworkId].CurrentBusyLimbs, Is.EqualTo(BusyLimbs.LegRight));

            characterReady = true;
            InvokePrivate(controller, "RetryPendingCoreState");

            Assert.That(character.Busy.IsArmLeftBusy, Is.False);
            Assert.That(character.Busy.IsLegRightBusy, Is.True);
            Assert.That(pendingSnapshots.ContainsKey(characterNetworkId), Is.False);
            Assert.That(pendingBusy.ContainsKey(characterNetworkId), Is.False);
        }

        [Test]
        public void BasePurrNetBridge_AutoEnsuresReliableOrderedCoreBridge()
        {
            GameObject bridgeObject = Track(new GameObject("PurrNet Bridge Test"));
            bridgeObject.AddComponent<PurrNetTransportBridge>();

            PurrNetCoreTransportBridge coreBridge =
                bridgeObject.GetComponent<PurrNetCoreTransportBridge>();
            Assert.That(coreBridge, Is.Not.Null);

            Channel channel = GetPrivateField<Channel>(coreBridge, "m_Channel");
            Assert.That(channel, Is.EqualTo(Channel.ReliableOrdered));
        }

        private static NetworkCoreSnapshot RoundTrip(NetworkCoreSnapshot value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetCoreValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);

            NetworkCoreSnapshot result = default;
            PurrNetCoreValuePackers.Read(packer, ref result);
            return result;
        }

        private delegate void ReadValue<T>(BitPacker packer, ref T value);

        private static T RoundTrip<T>(
            T value,
            Action<BitPacker, T> write,
            ReadValue<T> read)
        {
            using BitPacker packer = BitPackerPool.Get();
            write.Invoke(packer, value);
            packer.ResetPositionAndMode(true);

            T result = default;
            read.Invoke(packer, ref result);
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

        private static void InvokePrivate(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {name}");
            method.Invoke(target, null);
        }
    }
}
