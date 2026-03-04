#if UNITY_NETCODE
using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Lightweight NGO implementation of the network transport bridge.
    /// Uses named messages to keep gameplay code transport-agnostic.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Netcode Bridge")]
    public class NetcodeGameObjectsTransportBridge : NetworkTransportBridge
    {
        private const string MESSAGE_INPUT = "GC2N/Input";
        private const string MESSAGE_STATE = "GC2N/State";

        private NetworkManager m_BoundManager;
        private bool m_HandlersRegistered;

        public override bool IsServer => NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
        public override bool IsClient => NetworkManager.Singleton != null && NetworkManager.Singleton.IsClient;
        public override bool IsHost => NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost;

        public override float ServerTime
        {
            get
            {
                var manager = NetworkManager.Singleton;
                if (manager == null) return Time.time;
                return (float)manager.ServerTime.Time;
            }
        }

        protected override void Awake()
        {
            base.Awake();
            TryRegisterHandlers();
        }

        private void Update()
        {
            TryRegisterHandlers();
        }

        protected override void OnDestroy()
        {
            UnregisterHandlers();
            base.OnDestroy();
        }

        protected override bool TryResolveOwnerClientId(NetworkCharacter networkCharacter, out uint ownerClientId)
        {
            ownerClientId = 0;
            if (networkCharacter == null) return false;

            var networkObject = networkCharacter.GetComponent<NetworkObject>();
            if (networkObject == null || !networkObject.IsSpawned) return false;

            return TryConvertClientId(networkObject.OwnerClientId, out ownerClientId);
        }

        protected override bool TryResolveServerIssuedNetworkId(NetworkCharacter networkCharacter, out uint networkId)
        {
            networkId = 0;
            if (networkCharacter == null) return false;

            var networkObject = networkCharacter.GetComponent<NetworkObject>();
            if (networkObject == null || !networkObject.IsSpawned) return false;

            networkId = unchecked((uint)networkObject.NetworkObjectId);
            if (networkId == 0)
            {
                networkId = 1;
            }

            return true;
        }

        public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;
            if (!IsClient) return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null) return;

            // Host loopback can bypass serialization.
            if (IsHost)
            {
                TryRefreshCharacterOwner(characterNetworkId);

                if (!TryConvertClientId(manager.LocalClientId, out uint localClientId)) return;
                if (!TryAcceptInputFromSender(localClientId, characterNetworkId)) return;

                RaiseInputReceivedServer(localClientId, characterNetworkId, inputs);
                return;
            }

            if (manager.CustomMessagingManager == null) return;

            int size = sizeof(uint) + sizeof(ushort) + inputs.Length * (sizeof(short) + sizeof(short) + sizeof(ushort) + sizeof(byte) + sizeof(byte));
            using var writer = new FastBufferWriter(size, Allocator.Temp);

            writer.WriteValueSafe(characterNetworkId);
            ushort inputCount = (ushort)Mathf.Clamp(inputs.Length, 0, ushort.MaxValue);
            writer.WriteValueSafe(inputCount);

            for (int i = 0; i < inputCount; i++)
            {
                var input = inputs[i];
                writer.WriteValueSafe(input.inputX);
                writer.WriteValueSafe(input.inputY);
                writer.WriteValueSafe(input.sequenceNumber);
                writer.WriteValueSafe(input.flags);
                writer.WriteValueSafe(input.deltaTimeMs);
            }

            manager.CustomMessagingManager.SendNamedMessage(
                MESSAGE_INPUT,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.UnreliableSequenced
            );
        }

        public override void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime)
        {
            if (!IsServer) return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || manager.CustomMessagingManager == null) return;

            ulong target = ownerClientId;
            if (IsHost && target == manager.LocalClientId)
            {
                RaiseStateReceivedClient(characterNetworkId, state, serverTime);
                return;
            }

            int size = GetStateMessageSize();
            using var writer = new FastBufferWriter(size, Allocator.Temp);
            WriteStatePayload(writer, characterNetworkId, state, serverTime);

            manager.CustomMessagingManager.SendNamedMessage(
                MESSAGE_STATE,
                target,
                writer,
                NetworkDelivery.UnreliableSequenced
            );
        }

        public override void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null
        )
        {
            if (!IsServer) return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || manager.CustomMessagingManager == null) return;

            ulong excluded = excludeClientId == uint.MaxValue ? ulong.MaxValue : excludeClientId;

            // Host local loopback.
            if (IsHost && excluded != manager.LocalClientId && TryConvertClientId(manager.LocalClientId, out uint localClientId))
            {
                if (ShouldSendToClient(localClientId, characterNetworkId, state, serverTime, relevanceFilter))
                {
                    RaiseStateReceivedClient(characterNetworkId, state, serverTime);
                }
            }

            var connectedIds = manager.ConnectedClientsIds;
            if (connectedIds == null) return;

            for (int i = 0; i < connectedIds.Count; i++)
            {
                ulong clientId = connectedIds[i];
                if (clientId == NetworkManager.ServerClientId) continue;
                if (clientId == excluded) continue;
                if (!TryConvertClientId(clientId, out uint targetClientId)) continue;
                if (!ShouldSendToClient(targetClientId, characterNetworkId, state, serverTime, relevanceFilter)) continue;

                int size = GetStateMessageSize();
                using var writer = new FastBufferWriter(size, Allocator.Temp);
                WriteStatePayload(writer, characterNetworkId, state, serverTime);

                manager.CustomMessagingManager.SendNamedMessage(
                    MESSAGE_STATE,
                    clientId,
                    writer,
                    NetworkDelivery.UnreliableSequenced
                );
            }
        }

        private void TryRegisterHandlers()
        {
            if (m_HandlersRegistered) return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || manager.CustomMessagingManager == null) return;

            manager.CustomMessagingManager.RegisterNamedMessageHandler(MESSAGE_INPUT, OnInputMessageReceived);
            manager.CustomMessagingManager.RegisterNamedMessageHandler(MESSAGE_STATE, OnStateMessageReceived);

            m_BoundManager = manager;
            m_HandlersRegistered = true;
        }

        private void UnregisterHandlers()
        {
            if (!m_HandlersRegistered || m_BoundManager == null || m_BoundManager.CustomMessagingManager == null) return;

            m_BoundManager.CustomMessagingManager.UnregisterNamedMessageHandler(MESSAGE_INPUT);
            m_BoundManager.CustomMessagingManager.UnregisterNamedMessageHandler(MESSAGE_STATE);

            m_HandlersRegistered = false;
            m_BoundManager = null;
        }

        private void OnInputMessageReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer) return;
            if (!TryConvertClientId(senderClientId, out uint senderClient)) return;

            reader.ReadValueSafe(out uint characterNetworkId);
            reader.ReadValueSafe(out ushort inputCount);

            if (inputCount == 0)
            {
                return;
            }

            var inputs = new NetworkInputState[inputCount];
            for (int i = 0; i < inputCount; i++)
            {
                reader.ReadValueSafe(out short inputX);
                reader.ReadValueSafe(out short inputY);
                reader.ReadValueSafe(out ushort sequence);
                reader.ReadValueSafe(out byte flags);
                reader.ReadValueSafe(out byte deltaMs);

                inputs[i] = new NetworkInputState
                {
                    inputX = inputX,
                    inputY = inputY,
                    sequenceNumber = sequence,
                    flags = flags,
                    deltaTimeMs = deltaMs
                };
            }

            TryRefreshCharacterOwner(characterNetworkId);
            if (!TryAcceptInputFromSender(senderClient, characterNetworkId)) return;

            RaiseInputReceivedServer(senderClient, characterNetworkId, inputs);
        }

        private void OnStateMessageReceived(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsClient) return;

            reader.ReadValueSafe(out uint characterNetworkId);

            NetworkPositionState state = default;
            reader.ReadValueSafe(out state.positionX);
            reader.ReadValueSafe(out state.positionY);
            reader.ReadValueSafe(out state.positionZ);
            reader.ReadValueSafe(out state.rotationY);
            reader.ReadValueSafe(out state.verticalVelocity);
            reader.ReadValueSafe(out state.flags);
            reader.ReadValueSafe(out state.lastProcessedInput);

            reader.ReadValueSafe(out float serverTime);

            RaiseStateReceivedClient(characterNetworkId, state, serverTime);
        }

        private static int GetStateMessageSize()
        {
            return sizeof(uint) + sizeof(int) + sizeof(int) + sizeof(int) + sizeof(ushort) + sizeof(short) + sizeof(byte) + sizeof(ushort) + sizeof(float);
        }

        private static void WriteStatePayload(FastBufferWriter writer, uint characterNetworkId, NetworkPositionState state, float serverTime)
        {
            writer.WriteValueSafe(characterNetworkId);
            writer.WriteValueSafe(state.positionX);
            writer.WriteValueSafe(state.positionY);
            writer.WriteValueSafe(state.positionZ);
            writer.WriteValueSafe(state.rotationY);
            writer.WriteValueSafe(state.verticalVelocity);
            writer.WriteValueSafe(state.flags);
            writer.WriteValueSafe(state.lastProcessedInput);
            writer.WriteValueSafe(serverTime);
        }

        private void TryRefreshCharacterOwner(uint characterNetworkId)
        {
            if (characterNetworkId == 0) return;
            if (TryGetCharacterOwner(characterNetworkId, out _)) return;
            if (!TryResolveNetworkCharacter(characterNetworkId, out var networkCharacter)) return;

            if (TryResolveOwnerClientId(networkCharacter, out uint ownerClientId))
            {
                SetCharacterOwner(characterNetworkId, ownerClientId);
            }
        }

        private static bool TryConvertClientId(ulong sourceClientId, out uint clientId)
        {
            clientId = 0;
            if (sourceClientId > uint.MaxValue)
            {
                Debug.LogWarning($"[NetcodeBridge] Ignoring client id {sourceClientId} because it exceeds uint range.");
                return false;
            }

            clientId = (uint)sourceClientId;
            return true;
        }
    }
}
#endif
