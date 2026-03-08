using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using Arawn.GameCreator2.Networking.Security;

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
        /// <summary>
        /// Sentinel value used when an inbound transport client ID cannot be represented in this layer.
        /// </summary>
        public const uint InvalidClientId = uint.MaxValue;

        [Header("Global Session Profile")]
        [SerializeField] private NetworkSessionProfile m_GlobalSessionProfile;

        [Header("Input Ownership")]
        [Tooltip("When true, unknown character ownership can be learned from first valid input (compatibility mode). Disable for strict server-validated ownership.")]
        [SerializeField] private bool m_AllowOwnershipLearningWhenMissing = false;

        [Tooltip("When true, ownership learning is allowed only while no server-initialized NetworkSecurityManager is present. Keeps competitive authoritative sessions strict by default.")]
        [SerializeField] private bool m_AllowOwnershipLearningOnlyWithoutSecurityManager = true;

        [Header("Character Registry")]
        [Tooltip("When enabled, character resolution only uses the runtime registry (O(1)). Missing entries are not recovered through scene scans.")]
        [SerializeField] private bool m_StrictRegistryLookup = true;

        [Tooltip("When enabled on server, characters receive transport-issued runtime IDs when available .")]
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

        /// <summary>
        /// Convert a transport sender ID into the GC2 networking client ID domain.
        /// Use this for every inbound transport callback before validation.
        /// </summary>
        public static bool TryConvertSenderClientId(ulong rawSenderClientId, out uint senderClientId)
        {
            if (rawSenderClientId > uint.MaxValue)
            {
                senderClientId = InvalidClientId;
                return false;
            }

            senderClientId = (uint)rawSenderClientId;
            return true;
        }

        /// <summary>
        /// Returns true when a client ID is representable and usable by this layer.
        /// Client ID 0 is valid (for zero-based transports); only <see cref="InvalidClientId"/> is rejected.
        /// </summary>
        public static bool IsValidClientId(uint clientId)
        {
            return clientId != InvalidClientId;
        }

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

            foreach (uint characterNetworkId in m_CharacterOwners.Keys)
            {
                SecurityIntegration.UnregisterActorOwnership(characterNetworkId);
            }

            foreach (uint characterNetworkId in m_CharacterRegistry.Keys)
            {
                NetworkCorrelation.ClearComposeState(characterNetworkId);
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

        /// <summary>
        /// Authoritatively verify ownership for an actor against transport state.
        /// Override in transport implementations to query native owner state when
        /// ownership caches are not yet warmed up.
        /// </summary>
        public virtual bool TryVerifyActorOwnership(uint senderClientId, uint actorNetworkId, out uint ownerClientId)
        {
            ownerClientId = 0;
            if (!IsValidClientId(senderClientId) || actorNetworkId == 0) return false;

            if (!TryGetCharacterOwner(actorNetworkId, out ownerClientId) || !IsValidClientId(ownerClientId))
            {
                return false;
            }

            return ownerClientId == senderClientId;
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

        /// <summary>
        /// Set/refresh owner mapping for a character.
        /// Client ID 0 is valid; pass <see cref="InvalidClientId"/> (or call <see cref="ClearCharacterOwner"/>)
        /// to clear ownership.
        /// </summary>
        public void SetCharacterOwner(uint characterNetworkId, uint ownerClientId)
        {
            if (characterNetworkId == 0) return;
            if (!IsValidClientId(ownerClientId))
            {
                ClearCharacterOwner(characterNetworkId);
                return;
            }

            if (m_CharacterOwners.TryGetValue(characterNetworkId, out uint previousOwner))
            {
                if (previousOwner == ownerClientId)
                {
                    m_UnknownOwnershipWarned.Remove(characterNetworkId);
                    SecurityIntegration.RegisterActorOwnership(characterNetworkId, ownerClientId);
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
            SecurityIntegration.RegisterActorOwnership(characterNetworkId, ownerClientId);
        }

        public void ClearCharacterOwner(uint characterNetworkId)
        {
            if (characterNetworkId == 0) return;

            if (!m_CharacterOwners.TryGetValue(characterNetworkId, out uint previousOwner))
            {
                m_UnknownOwnershipWarned.Remove(characterNetworkId);
                NetworkCorrelation.ClearComposeState(characterNetworkId);
                return;
            }

            m_CharacterOwners.Remove(characterNetworkId);
            RemoveOwnedCharacter(previousOwner, characterNetworkId);
            m_UnknownOwnershipWarned.Remove(characterNetworkId);
            SecurityIntegration.UnregisterActorOwnership(characterNetworkId);
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

            if (m_AllowOwnershipLearningOnlyWithoutSecurityManager)
            {
                NetworkSecurityManager securityManager = NetworkSecurityManager.Instance;
                if (securityManager != null && securityManager.IsServer)
                {
                    if (m_UnknownOwnershipWarned.Add(characterNetworkId))
                    {
                        Debug.LogWarning(
                            $"[NetworkTransportBridge] Rejected ownership learning for character {characterNetworkId} " +
                            $"from client {senderClientId} because NetworkSecurityManager is active in server mode.");
                    }

                    return false;
                }
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
