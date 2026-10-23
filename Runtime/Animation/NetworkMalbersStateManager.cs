using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Selects which optional Malbers Animal Controller values are present in a state message.
    /// Older peers may not know every flag, so receivers must ignore bits they do not use.
    /// </summary>
    [Flags]
    public enum NetworkMalbersStateFlags
    {
        None = 0,
        State = 1 << 0,
        Mode = 1 << 1,
        Stance = 1 << 2,
        Grounded = 1 << 3,
        Movement = 1 << 4
    }

    /// <summary>
    /// Transport-agnostic snapshot of the native Malbers Animal Controller state for one
    /// character. GC2 Animim states and gestures travel through
    /// <see cref="NetworkAnimationManager"/>; this message carries the Malbers-owned layer
    /// (state, mode, stance, grounded and movement) that GC2 does not observe.
    /// </summary>
    [Serializable]
    public struct NetworkMalbersStateMessage
    {
        public uint CharacterNetworkId;
        public NetworkMalbersStateFlags Flags;
        public int StateId;
        public int ModeId;
        public int StanceId;
        public int AbilityIndex;
        public float StateTime;
        public float VerticalSpeed;
        public bool Grounded;

        public bool Has(NetworkMalbersStateFlags flag) => (Flags & flag) == flag;

        public void Set(NetworkMalbersStateFlags flag, bool enabled)
        {
            Flags = enabled ? Flags | flag : Flags & ~flag;
        }
    }

    /// <summary>
    /// Routes <see cref="NetworkMalbersStateMessage"/> snapshots between authority and
    /// proxies. Transports wire <see cref="SendToServer"/> and
    /// <see cref="BroadcastToClients"/> and feed received messages back through
    /// <see cref="ReceiveRequest"/> or <see cref="ReceiveBroadcast"/>.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Network Malbers State Manager")]
    public sealed class NetworkMalbersStateManager : NetworkSingleton<NetworkMalbersStateManager>
    {
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;

        /// <summary>Client-to-server transport send. The server validates ownership.</summary>
        public Action<NetworkMalbersStateMessage> SendToServer;

        /// <summary>Server-to-clients transport broadcast.</summary>
        public Action<NetworkMalbersStateMessage> BroadcastToClients;

        /// <summary>Raised on clients after an authoritative broadcast is accepted.</summary>
        public event Action<NetworkMalbersStateMessage> StateReceived;

        /// <summary>Resolves a <see cref="NetworkCharacter"/> for ownership validation.</summary>
        public Func<uint, NetworkCharacter> GetNetworkCharacterById;

        /// <summary>
        /// Test/transport override for ownership validation. When unset the active
        /// transport bridge is used.
        /// </summary>
        public Func<uint, uint, bool> ValidateOwnership;

        [SerializeField] private bool m_LogDiagnostics;

        private bool m_IsServer;
        private bool m_IsClient;

        public bool IsServer => m_IsServer;
        public bool IsClient => m_IsClient;

        /// <summary>Last state ID sent by this authority; zero before the first snapshot.</summary>
        public int LastSentStateId { get; private set; }

        /// <summary>Number of snapshots sent by this authority.</summary>
        public int SentSnapshotCount { get; private set; }

        /// <summary>Last state ID received from the authority; zero before the first broadcast.</summary>
        public int LastReceivedStateId { get; private set; }

        /// <summary>Number of authoritative snapshots received by this client.</summary>
        public int ReceivedSnapshotCount { get; private set; }

        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            Log($"initialized server={m_IsServer} client={m_IsClient}");
        }

        public void Shutdown()
        {
            m_IsServer = false;
            m_IsClient = false;
            StateReceived = null;
        }

        /// <summary>
        /// Sends a snapshot from whichever peer owns the character. The server broadcasts
        /// directly; a client sends to the server, which re-broadcasts after validation.
        /// </summary>
        public void SendFromAuthority(NetworkMalbersStateMessage message)
        {
            if (message.CharacterNetworkId == 0)
            {
                Log("dropped send: character network id is zero");
                return;
            }

            if (m_IsServer)
            {
                LastSentStateId = message.StateId;
                SentSnapshotCount++;
                BroadcastToClients?.Invoke(message);
                return;
            }

            if (!m_IsClient)
            {
                Log("dropped send: manager is neither server nor client");
                return;
            }

            if (SendToServer == null)
            {
                Log("dropped send: SendToServer has no transport listener");
                return;
            }

            SendToServer.Invoke(message);
        }

        /// <summary>
        /// Server entry point for a client snapshot. Rejects snapshots for characters the
        /// sender does not own, then broadcasts the accepted snapshot to every client.
        /// </summary>
        public void ReceiveRequest(uint senderClientId, NetworkMalbersStateMessage message)
        {
            if (!m_IsServer)
            {
                Log($"dropped request: manager is not server sender={senderClientId}");
                return;
            }

            if (!CanAcceptClientCommand(senderClientId, message.CharacterNetworkId))
            {
                Log(
                    $"dropped request: ownership validation failed sender={senderClientId} " +
                    $"character={message.CharacterNetworkId}");
                return;
            }

            Log(
                $"server accepted state character={message.CharacterNetworkId} " +
                $"sender={senderClientId} state={message.StateId} mode={message.ModeId} " +
                $"stance={message.StanceId} flags={message.Flags}");
            BroadcastToClients?.Invoke(message);
        }

        /// <summary>Client entry point for an authoritative snapshot.</summary>
        public void ReceiveBroadcast(NetworkMalbersStateMessage message)
        {
            if (NetworkCiTrace.Enabled)
            {
                NetworkCiTrace.Log(
                    "malbers",
                    "manager-receive",
                    message.CharacterNetworkId,
                    0,
                    $"instance={name} isClient={m_IsClient} state={message.StateId} " +
                    $"received={ReceivedSnapshotCount}");
            }

            if (!m_IsClient)
            {
                Log($"dropped broadcast: manager is not client character={message.CharacterNetworkId}");
                return;
            }

            if (message.CharacterNetworkId == 0)
            {
                Log("dropped broadcast: character network id is zero");
                return;
            }

            LastReceivedStateId = message.StateId;
            ReceivedSnapshotCount++;
            StateReceived?.Invoke(message);
        }

        private bool CanAcceptClientCommand(uint senderClientId, uint characterNetworkId)
        {
            if (characterNetworkId == 0) return false;

            if (ValidateOwnership != null)
            {
                return ValidateOwnership(senderClientId, characterNetworkId);
            }

            if (!NetworkTransportBridge.IsValidClientId(senderClientId)) return false;

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            return bridge != null &&
                   bridge.TryVerifyActorOwnership(senderClientId, characterNetworkId, out _);
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkMalbersStateManager] {message}");
        }
    }
}
