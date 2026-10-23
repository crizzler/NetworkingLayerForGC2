using NUnit.Framework;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.Malbers")]
    public sealed class NetworkMalbersStateSyncTests
    {
        private GameObject m_ManagerObject;

        [TearDown]
        public void TearDown()
        {
            if (m_ManagerObject != null)
            {
                Object.DestroyImmediate(m_ManagerObject);
                m_ManagerObject = null;
            }
        }

        private NetworkMalbersStateManager CreateManager(bool isServer, bool isClient)
        {
            m_ManagerObject = new GameObject("Network Malbers State Manager Test");
            var manager = m_ManagerObject.AddComponent<NetworkMalbersStateManager>();
            manager.Initialize(isServer, isClient);
            return manager;
        }

        [Test]
        public void StateFlags_AreAppendOnly()
        {
            Assert.AreEqual(1 << 0, (int)NetworkMalbersStateFlags.State);
            Assert.AreEqual(1 << 1, (int)NetworkMalbersStateFlags.Mode);
            Assert.AreEqual(1 << 2, (int)NetworkMalbersStateFlags.Stance);
            Assert.AreEqual(1 << 3, (int)NetworkMalbersStateFlags.Grounded);
            Assert.AreEqual(1 << 4, (int)NetworkMalbersStateFlags.Movement);
        }

        [Test]
        public void StateMessage_SetAndHas_AreIdempotent()
        {
            var message = new NetworkMalbersStateMessage();

            Assert.IsFalse(message.Has(NetworkMalbersStateFlags.State));
            message.Set(NetworkMalbersStateFlags.State, true);
            message.Set(NetworkMalbersStateFlags.State, true);
            Assert.IsTrue(message.Has(NetworkMalbersStateFlags.State));
            Assert.IsFalse(message.Has(NetworkMalbersStateFlags.Mode));

            message.Set(NetworkMalbersStateFlags.State, false);
            Assert.IsFalse(message.Has(NetworkMalbersStateFlags.State));
        }

        [Test]
        public void Server_RejectsSnapshotForUnownedCharacter()
        {
            NetworkMalbersStateManager manager = CreateManager(true, true);
            manager.ValidateOwnership = (sender, character) => false;

            int broadcasts = 0;
            manager.BroadcastToClients += _ => broadcasts++;

            manager.ReceiveRequest(
                7u,
                new NetworkMalbersStateMessage
                {
                    CharacterNetworkId = 42,
                    Flags = NetworkMalbersStateFlags.State,
                    StateId = 2
                });

            Assert.AreEqual(0, broadcasts);
        }

        [Test]
        public void Server_AcceptsAndRebroadcastsOwnedSnapshot()
        {
            NetworkMalbersStateManager manager = CreateManager(true, true);
            manager.ValidateOwnership = (sender, character) => sender == 7u && character == 42u;

            NetworkMalbersStateMessage broadcast = default;
            int broadcasts = 0;
            manager.BroadcastToClients += message =>
            {
                broadcast = message;
                broadcasts++;
            };

            manager.ReceiveRequest(
                7u,
                new NetworkMalbersStateMessage
                {
                    CharacterNetworkId = 42,
                    Flags = NetworkMalbersStateFlags.State | NetworkMalbersStateFlags.Mode,
                    StateId = 3,
                    ModeId = 5,
                    AbilityIndex = 1
                });

            Assert.AreEqual(1, broadcasts);
            Assert.AreEqual(42u, broadcast.CharacterNetworkId);
            Assert.AreEqual(3, broadcast.StateId);
            Assert.AreEqual(5, broadcast.ModeId);
            Assert.AreEqual(1, broadcast.AbilityIndex);
        }

        [Test]
        public void Client_ReceivesOnlyBroadcastsWithACharacterId()
        {
            NetworkMalbersStateManager manager = CreateManager(false, true);

            int received = 0;
            manager.StateReceived += _ => received++;

            manager.ReceiveBroadcast(new NetworkMalbersStateMessage { CharacterNetworkId = 0 });
            manager.ReceiveBroadcast(
                new NetworkMalbersStateMessage
                {
                    CharacterNetworkId = 11,
                    Flags = NetworkMalbersStateFlags.State,
                    StateId = 4
                });

            Assert.AreEqual(1, received);
        }

        [Test]
        public void ClientSendFromAuthority_UsesSendToServer()
        {
            NetworkMalbersStateManager manager = CreateManager(false, true);

            int sent = 0;
            manager.SendToServer += _ => sent++;
            manager.BroadcastToClients += _ =>
                Assert.Fail("A client must not broadcast directly.");

            manager.SendFromAuthority(
                new NetworkMalbersStateMessage { CharacterNetworkId = 9, StateId = 1 });

            Assert.AreEqual(1, sent);
        }

        [Test]
        public void ServerSendFromAuthority_BroadcastsDirectly()
        {
            NetworkMalbersStateManager manager = CreateManager(true, true);

            int broadcast = 0;
            manager.BroadcastToClients += _ => broadcast++;
            manager.SendToServer += _ =>
                Assert.Fail("A server must not send a client request to itself.");

            manager.SendFromAuthority(
                new NetworkMalbersStateMessage { CharacterNetworkId = 9, StateId = 1 });

            Assert.AreEqual(1, broadcast);
        }

        [Test]
        public void SendFromAuthority_DropsZeroCharacterId()
        {
            NetworkMalbersStateManager manager = CreateManager(true, true);

            int broadcast = 0;
            manager.BroadcastToClients += _ => broadcast++;

            manager.SendFromAuthority(default);

            Assert.AreEqual(0, broadcast);
        }

        [Test]
        public void FusionBridge_UsesAnAppendOnlyMalbersMessageType()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/AnimationMotion/" +
                "FusionAnimationMotionTransportBridge.cs");

            StringAssert.Contains("MotionResult = 6,", source);
            StringAssert.Contains("MalbersState = 7", source);
            StringAssert.Contains("case MessageType.MalbersState:", source);
            StringAssert.Contains("GetMalbersStateManager()?.ReceiveRequest(", source);
            StringAssert.Contains("GetMalbersStateManager()?.ReceiveBroadcast(", source);
            StringAssert.Contains("SendToAuthority((ushort)MessageType.MalbersState, message)", source);
            StringAssert.Contains("BroadcastCounted((ushort)MessageType.MalbersState, message)", source);
        }

        [Test]
        public void PurrNetBridge_SubscribesAndPacksMalbersState()
        {
            string messages = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/" +
                "PurrNetAnimationMotionMessages.cs");
            StringAssert.Contains("struct GC2MalbersStatePacket : IPackedAuto", messages);

            string packers = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/" +
                "PurrNetAnimationMotionValuePackers.cs");
            StringAssert.Contains("Hasher.PrepareType(typeof(NetworkMalbersStateMessage));", packers);
            StringAssert.Contains("Write(this BitPacker packer, NetworkMalbersStateMessage value)", packers);
            StringAssert.Contains("Read(this BitPacker packer, ref NetworkMalbersStateMessage value)", packers);

            string bridge = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/" +
                "PurrNetAnimationMotionTransportBridge.cs");
            StringAssert.Contains("Subscribe<GC2MalbersStatePacket>", bridge);
            StringAssert.Contains("Unsubscribe<GC2MalbersStatePacket>", bridge);
            StringAssert.Contains("SendToServerOrLoopback(", bridge);
            StringAssert.Contains("ReceiveRequest(senderClientId, data.message)", bridge);
            StringAssert.Contains("ReceiveBroadcast(data.message)", bridge);
        }

        [Test]
        public void MalbersStateBridge_ForcesNativeStateModeAndStance()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2.Malbers/Runtime/NetworkMalbersStateBridge.cs");

            StringAssert.Contains("m_Animal.ActiveStateID", source);
            StringAssert.Contains("m_Animal.State_Force(message.StateId);", source);
            StringAssert.Contains("m_Animal.Mode_ForceActivate(message.ModeId, message.AbilityIndex);", source);
            StringAssert.Contains("m_Animal.Mode_Stop();", source);
            StringAssert.Contains("m_Animal.Stance_Set(message.StanceId);", source);
            StringAssert.Contains("m_Manager.StateReceived += OnStateReceived;", source);
            StringAssert.Contains("message.CharacterNetworkId != m_NetworkCharacter.NetworkId", source);
            StringAssert.Contains("NetworkCharacter.NetworkRole.LocalClient", source);
        }

        [Test]
        public void Policy_ExposesTheOptionalStateBridgeTypeName()
        {
            Assert.AreEqual(
                "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersStateBridge",
                NetworkMalbersIntegrationPolicy.StateBridgeTypeName);

            string partial = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Character/NetworkCharacter.Malbers.cs");
            StringAssert.Contains(
                "NetworkMalbersIntegrationPolicy.EnsureStateBridge(gameObject)",
                partial);
        }

        [Test]
        public void Manager_UsesTransportOwnershipVerification()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Animation/NetworkMalbersStateManager.cs");

            StringAssert.Contains("bridge.TryVerifyActorOwnership(senderClientId, characterNetworkId, out _)", source);
            StringAssert.Contains("NetworkTransportBridge.IsValidClientId(senderClientId)", source);
            StringAssert.Contains("public Func<uint, uint, bool> ValidateOwnership;", source);
        }

        private static string ReadAssetSource(string relativePath)
        {
            string projectRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, ".."));
            string path = System.IO.Path.Combine(projectRoot, "Assets", relativePath);
            Assert.IsTrue(System.IO.File.Exists(path), $"Expected source file was not found: {path}");
            return System.IO.File.ReadAllText(path);
        }
    }
}
