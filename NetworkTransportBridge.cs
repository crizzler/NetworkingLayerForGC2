using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    public delegate bool NetworkRecipientFilter(uint targetClientId, uint characterNetworkId, NetworkPositionState state, float serverTime);

    public interface INetworkTransportBridge
    {
        bool IsServer { get; }
        bool IsClient { get; }
        bool IsHost { get; }
        float ServerTime { get; }

        void SendToServer(uint characterNetworkId, NetworkInputState[] inputs);
        void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime);
        void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null
        );

        bool TryGetCharacterOwner(uint characterNetworkId, out uint ownerClientId);
        bool TryGetRepresentativeCharacterId(uint ownerClientId, out uint characterNetworkId);
        void SetCharacterOwner(uint characterNetworkId, uint ownerClientId);
        void ClearCharacterOwner(uint characterNetworkId);

        Character ResolveCharacter(uint networkId);
    }

    [DefaultExecutionOrder(-400)]
    public abstract class NetworkTransportBridge : MonoBehaviour, INetworkTransportBridge
    {
        private static NetworkTransportBridge s_Active;

        [Header("Global Session Profile")]
        [SerializeField] private NetworkSessionProfile m_GlobalSessionProfile;

        [Header("Input Ownership")]
        [Tooltip("When true, unknown character ownership can be learned from first valid input (compatibility mode). Disable for strict server-validated ownership.")]
        [SerializeField] private bool m_AllowOwnershipLearningWhenMissing = false;

        [Header("Character Registry")]
        [Tooltip("When enabled, character resolution only uses the runtime registry (O(1)). Missing entries are not recovered through scene scans.")]
        [SerializeField] private bool m_StrictRegistryLookup = true;

        [Tooltip("When enabled on server, characters receive transport-issued runtime IDs when available (for NGO this maps to NetworkObjectId).")]
        [SerializeField] private bool m_UseServerIssuedIdsWhenAvailable = true;

        [Tooltip("If no transport-issued ID is available, server can allocate runtime IDs from this bridge. Leave OFF unless your custom transport also replicates these IDs to clients.")]
        [SerializeField] private bool m_AllocateServerIssuedIdsWhenTransportMissing = false;

        [Tooltip("Starting value for bridge-allocated server runtime IDs.")]
        [Min(1)]
        [SerializeField] private uint m_ServerIssuedIdStart = 1;

        private readonly Dictionary<uint, NetworkCharacter> m_CharacterRegistry = new Dictionary<uint, NetworkCharacter>(128);
        private readonly Dictionary<NetworkCharacter, uint> m_RegisteredCharacterIds = new Dictionary<NetworkCharacter, uint>(128);
        private readonly Dictionary<uint, uint> m_CharacterOwners = new Dictionary<uint, uint>(128);
        private readonly Dictionary<uint, HashSet<uint>> m_OwnedCharactersByClient = new Dictionary<uint, HashSet<uint>>(32);
        private readonly HashSet<uint> m_UnknownOwnershipWarned = new HashSet<uint>();
        private readonly Dictionary<NetworkCharacter, uint> m_ServerIssuedIds = new Dictionary<NetworkCharacter, uint>(128);
        private uint m_NextServerIssuedNetworkId = 1;

        public static NetworkTransportBridge Active
        {
            get
            {
                if (s_Active == null)
                {
                    s_Active = FindFirstObjectByType<NetworkTransportBridge>();
                }

                return s_Active;
            }
        }

        public static bool HasActive => Active != null;

        public NetworkSessionProfile GlobalSessionProfile => m_GlobalSessionProfile;
        public Func<uint, uint, bool> RecipientRelevanceFilter { get; set; }

        public event Action<uint, uint, NetworkInputState[]> OnInputReceivedServer;
        public event Action<uint, NetworkPositionState, float> OnStateReceivedClient;

        public abstract bool IsServer { get; }
        public abstract bool IsClient { get; }
        public abstract bool IsHost { get; }
        public abstract float ServerTime { get; }

        public abstract void SendToServer(uint characterNetworkId, NetworkInputState[] inputs);
        public abstract void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime);
        public abstract void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null
        );

        protected virtual void Awake()
        {
            m_NextServerIssuedNetworkId = m_ServerIssuedIdStart == 0 ? 1u : m_ServerIssuedIdStart;

            if (s_Active == null)
            {
                s_Active = this;
                return;
            }

            if (s_Active != this)
            {
                Debug.LogWarning($"[NetworkTransportBridge] Multiple bridge instances detected. Active: {s_Active.name}, Ignored: {name}");
            }
        }

        protected virtual void OnDestroy()
        {
            if (s_Active == this)
            {
                s_Active = null;
            }

            m_CharacterRegistry.Clear();
            m_RegisteredCharacterIds.Clear();
            m_CharacterOwners.Clear();
            m_OwnedCharactersByClient.Clear();
            m_UnknownOwnershipWarned.Clear();
            m_ServerIssuedIds.Clear();
        }

        public virtual void RegisterCharacter(NetworkCharacter networkCharacter)
        {
            if (networkCharacter == null) return;

            if (IsServer && m_UseServerIssuedIdsWhenAvailable)
            {
                uint authoritativeId = ResolveServerIssuedNetworkId(networkCharacter);
                if (authoritativeId != 0)
                {
                    networkCharacter.ApplyServerIssuedNetworkId(authoritativeId);
                }
            }

            uint networkId = networkCharacter.NetworkId;
            if (networkId == 0) return;

            if (m_RegisteredCharacterIds.TryGetValue(networkCharacter, out uint previousId) &&
                previousId != 0 &&
                previousId != networkId)
            {
                if (m_CharacterRegistry.TryGetValue(previousId, out var previousCharacter) &&
                    previousCharacter == networkCharacter)
                {
                    m_CharacterRegistry.Remove(previousId);
                }

                ClearCharacterOwner(previousId);
            }

            if (m_CharacterRegistry.TryGetValue(networkId, out var existingCharacter))
            {
                if (existingCharacter == null)
                {
                    m_CharacterRegistry.Remove(networkId);
                }
                else if (existingCharacter != networkCharacter)
                {
                    Debug.LogWarning($"[NetworkTransportBridge] Duplicate NetworkId {networkId} for '{networkCharacter.name}' and '{existingCharacter.name}'. Registration skipped.");
                    return;
                }
            }

            m_CharacterRegistry[networkId] = networkCharacter;
            m_RegisteredCharacterIds[networkCharacter] = networkId;

            if (TryResolveOwnerClientId(networkCharacter, out uint ownerClientId))
            {
                SetCharacterOwner(networkId, ownerClientId);
            }
        }

        public virtual void UnregisterCharacter(NetworkCharacter networkCharacter)
        {
            if (networkCharacter == null) return;

            uint networkId = networkCharacter.NetworkId;
            if (m_RegisteredCharacterIds.TryGetValue(networkCharacter, out uint registeredId) && registeredId != 0)
            {
                networkId = registeredId;
            }

            if (networkId == 0) return;

            if (m_CharacterRegistry.TryGetValue(networkId, out var existing) && existing == networkCharacter)
            {
                m_CharacterRegistry.Remove(networkId);
            }

            m_RegisteredCharacterIds.Remove(networkCharacter);
            m_ServerIssuedIds.Remove(networkCharacter);
            ClearCharacterOwner(networkId);
        }

        public virtual Character ResolveCharacter(uint networkId)
        {
            return TryResolveNetworkCharacter(networkId, out var networkCharacter) ? networkCharacter.Character : null;
        }

        public bool TryGetCharacterOwner(uint characterNetworkId, out uint ownerClientId)
        {
            return m_CharacterOwners.TryGetValue(characterNetworkId, out ownerClientId);
        }

        public bool TryGetRepresentativeCharacterId(uint ownerClientId, out uint characterNetworkId)
        {
            characterNetworkId = 0;

            if (!m_OwnedCharactersByClient.TryGetValue(ownerClientId, out var ownedCharacters))
            {
                return false;
            }

            foreach (uint ownedId in ownedCharacters)
            {
                if (ownedId == 0) continue;
                if (!m_CharacterRegistry.TryGetValue(ownedId, out var networkCharacter)) continue;
                if (networkCharacter == null) continue;

                characterNetworkId = ownedId;
                return true;
            }

            return false;
        }

        public void SetCharacterOwner(uint characterNetworkId, uint ownerClientId)
        {
            if (characterNetworkId == 0) return;

            if (m_CharacterOwners.TryGetValue(characterNetworkId, out uint previousOwner))
            {
                if (previousOwner == ownerClientId)
                {
                    m_UnknownOwnershipWarned.Remove(characterNetworkId);
                    return;
                }

                RemoveOwnedCharacter(previousOwner, characterNetworkId);
            }

            m_CharacterOwners[characterNetworkId] = ownerClientId;
            if (!m_OwnedCharactersByClient.TryGetValue(ownerClientId, out var ownedCharacters))
            {
                ownedCharacters = new HashSet<uint>();
                m_OwnedCharactersByClient[ownerClientId] = ownedCharacters;
            }

            ownedCharacters.Add(characterNetworkId);
            m_UnknownOwnershipWarned.Remove(characterNetworkId);
        }

        public void ClearCharacterOwner(uint characterNetworkId)
        {
            if (characterNetworkId == 0) return;

            if (!m_CharacterOwners.TryGetValue(characterNetworkId, out uint previousOwner))
            {
                m_UnknownOwnershipWarned.Remove(characterNetworkId);
                return;
            }

            m_CharacterOwners.Remove(characterNetworkId);
            RemoveOwnedCharacter(previousOwner, characterNetworkId);
            m_UnknownOwnershipWarned.Remove(characterNetworkId);
        }

        protected bool TryResolveNetworkCharacter(uint networkId, out NetworkCharacter networkCharacter)
        {
            networkCharacter = null;
            if (networkId == 0) return false;

            if (m_CharacterRegistry.TryGetValue(networkId, out networkCharacter))
            {
                if (networkCharacter != null)
                {
                    return true;
                }

                m_CharacterRegistry.Remove(networkId);
            }

            if (m_StrictRegistryLookup)
            {
                return false;
            }

            var characters = FindObjectsByType<NetworkCharacter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < characters.Length; i++)
            {
                var candidate = characters[i];
                if (candidate == null) continue;
                if (candidate.NetworkId != networkId) continue;

                m_CharacterRegistry[networkId] = candidate;
                m_RegisteredCharacterIds[candidate] = networkId;
                networkCharacter = candidate;
                return true;
            }

            return false;
        }

        protected bool TryAcceptInputFromSender(uint senderClientId, uint characterNetworkId)
        {
            if (!IsServer) return true;
            if (characterNetworkId == 0) return false;

            if (TryGetCharacterOwner(characterNetworkId, out uint ownerClientId))
            {
                if (ownerClientId == senderClientId) return true;

                Debug.LogWarning($"[NetworkTransportBridge] Rejected input for character {characterNetworkId} from client {senderClientId}. Expected owner: {ownerClientId}");
                return false;
            }

            if (!m_AllowOwnershipLearningWhenMissing)
            {
                if (m_UnknownOwnershipWarned.Add(characterNetworkId))
                {
                    Debug.LogWarning($"[NetworkTransportBridge] Rejected input for character {characterNetworkId} from client {senderClientId}. Owner is unknown.");
                }

                return false;
            }

            SetCharacterOwner(characterNetworkId, senderClientId);
            if (m_UnknownOwnershipWarned.Add(characterNetworkId))
            {
                Debug.LogWarning($"[NetworkTransportBridge] Learned ownership for character {characterNetworkId} from input sender {senderClientId}. Consider disabling compatibility learning in competitive sessions.");
            }

            return true;
        }

        protected virtual bool TryResolveServerIssuedNetworkId(NetworkCharacter networkCharacter, out uint networkId)
        {
            networkId = 0;
            return false;
        }

        private uint ResolveServerIssuedNetworkId(NetworkCharacter networkCharacter)
        {
            if (networkCharacter == null) return 0;

            if (TryResolveServerIssuedNetworkId(networkCharacter, out uint transportIssuedId) && transportIssuedId != 0)
            {
                m_ServerIssuedIds[networkCharacter] = transportIssuedId;
                return transportIssuedId;
            }

            if (!m_AllocateServerIssuedIdsWhenTransportMissing)
            {
                return 0;
            }

            if (m_ServerIssuedIds.TryGetValue(networkCharacter, out uint existingIssuedId) && existingIssuedId != 0)
            {
                return existingIssuedId;
            }

            uint allocatedId = AllocateServerIssuedNetworkId();
            m_ServerIssuedIds[networkCharacter] = allocatedId;
            return allocatedId;
        }

        private uint AllocateServerIssuedNetworkId()
        {
            while (m_NextServerIssuedNetworkId == 0 ||
                   m_CharacterRegistry.ContainsKey(m_NextServerIssuedNetworkId))
            {
                m_NextServerIssuedNetworkId++;
                if (m_NextServerIssuedNetworkId == 0)
                {
                    m_NextServerIssuedNetworkId = 1;
                }
            }

            uint allocatedId = m_NextServerIssuedNetworkId;
            m_NextServerIssuedNetworkId++;
            if (m_NextServerIssuedNetworkId == 0)
            {
                m_NextServerIssuedNetworkId = 1;
            }

            return allocatedId;
        }

        protected virtual bool TryResolveOwnerClientId(NetworkCharacter networkCharacter, out uint ownerClientId)
        {
            ownerClientId = 0;
            return false;
        }

        protected bool ShouldSendToClient(uint targetClientId, uint characterNetworkId, NetworkPositionState state, float serverTime, NetworkRecipientFilter relevanceFilter)
        {
            if (relevanceFilter != null && !relevanceFilter(targetClientId, characterNetworkId, state, serverTime))
            {
                return false;
            }

            var globalFilter = RecipientRelevanceFilter;
            if (globalFilter != null && !globalFilter(targetClientId, characterNetworkId))
            {
                return false;
            }

            return true;
        }

        protected void RaiseInputReceivedServer(uint senderClientId, uint characterNetworkId, NetworkInputState[] inputs)
        {
            OnInputReceivedServer?.Invoke(senderClientId, characterNetworkId, inputs);
        }

        protected void RaiseStateReceivedClient(uint characterNetworkId, NetworkPositionState state, float serverTime)
        {
            OnStateReceivedClient?.Invoke(characterNetworkId, state, serverTime);
        }

        private void RemoveOwnedCharacter(uint ownerClientId, uint characterNetworkId)
        {
            if (!m_OwnedCharactersByClient.TryGetValue(ownerClientId, out var ownedCharacters)) return;

            ownedCharacters.Remove(characterNetworkId);
            if (ownedCharacters.Count == 0)
            {
                m_OwnedCharactersByClient.Remove(ownerClientId);
            }
        }
    }
}
