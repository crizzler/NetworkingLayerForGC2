using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking
{
    public delegate bool NetworkRecipientFilter(uint targetClientId, uint characterNetworkId, NetworkPositionState state, float serverTime);

    public interface INetworkTransportBridge
    {
        bool IsServer { get; }
        bool IsClient { get; }
        bool IsHost { get; }
        bool IsRunning { get; }
        bool IsStarting { get; }
        float ServerTime { get; }
        NetworkTransportRole Role { get; }
        IReadOnlyCollection<uint> ConnectedClientIds { get; }
        string LastSessionError { get; }
        string LastSessionStopReason { get; }

        bool TryGetLocalClientId(out uint clientId);
        bool TryGetLocalPlayer(out GameObject player);

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
        private readonly HashSet<uint> m_ObservedConnectedClientIds = new HashSet<uint>();
        private readonly List<uint> m_ObservedClientScratch = new List<uint>(32);
        private uint m_NextServerIssuedNetworkId = 1;
        private bool m_LifecycleObservationInitialized;
        private bool m_ObservedRunning;
        private bool m_ObservedAuthority;
        private uint m_ObservedAuthorityEpoch;
        private GameObject m_ObservedLocalPlayer;

        private static readonly IReadOnlyCollection<uint> s_NoConnectedClients =
            Array.Empty<uint>();

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

        public virtual bool IsRunning => IsServer || IsClient;
        public virtual bool IsStarting => false;
        public virtual IReadOnlyCollection<uint> ConnectedClientIds => s_NoConnectedClients;
        public virtual string LastSessionError => string.Empty;
        public virtual string LastSessionStopReason => string.Empty;

        public NetworkTransportRole Role
        {
            get
            {
                if (!IsRunning) return NetworkTransportRole.Offline;
                if (IsHost) return NetworkTransportRole.Host;
                if (IsServer) return NetworkTransportRole.Server;
                return IsClient ? NetworkTransportRole.Client : NetworkTransportRole.Offline;
            }
        }

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

        public virtual bool TryGetLocalClientId(out uint clientId)
        {
            clientId = InvalidClientId;
            return false;
        }

        public virtual bool TryGetLocalPlayer(out GameObject player)
        {
            player = null;
            if (!IsRunning || ShortcutPlayer.Instance == null) return false;

            NetworkCharacter networkCharacter =
                ShortcutPlayer.Instance.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                networkCharacter =
                    ShortcutPlayer.Instance.GetComponentInParent<NetworkCharacter>();
            }

            if (networkCharacter == null || !networkCharacter.IsLocalPlayer) return false;

            player = ShortcutPlayer.Instance;
            return true;
        }

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
            StopLifecycleObservation();

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

        /// <summary>
        /// Observe normalized lifecycle state after native transport callbacks have completed.
        /// Derived bridges should avoid hiding this Unity message; override and call base when
        /// transport-specific late-frame work is required.
        /// </summary>
        protected virtual void LateUpdate()
        {
            ObserveLifecycle();
        }

        protected virtual void OnDisable()
        {
            StopLifecycleObservation();
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

        private void ObserveLifecycle()
        {
            // Only the globally selected bridge represents the active GC2 session. Ignored
            // duplicate bridge components must not generate duplicate visual-script events.
            if (Active != this)
            {
                StopLifecycleObservation();
                return;
            }

            bool running = IsRunning;
            bool authority = running && IsServer;
            GameObject localPlayer = null;
            if (running)
            {
                TryGetLocalPlayer(out localPlayer);
            }

            if (!m_LifecycleObservationInitialized)
            {
                m_LifecycleObservationInitialized = true;
                m_ObservedRunning = false;
                m_ObservedAuthority = false;
                m_ObservedAuthorityEpoch = 0;
            }

            bool sessionStarted = running && !m_ObservedRunning;
            bool sessionStopped = !running && m_ObservedRunning;
            if (sessionStarted || sessionStopped)
            {
                m_ObservedRunning = running;
            }

            if (sessionStarted)
            {
                NetworkLifecycleEvents.RaiseSessionStarted(this);
            }

            // On teardown, publish the payload-bearing loss notifications before the final
            // SessionStopped marker. This matches OnDestroy and lets stopped-session actions
            // inspect the final client, player, and authority contexts consistently.
            if (sessionStopped)
            {
                ObserveLocalPlayer(localPlayer);
                ObserveConnectedClients(false);
                ObserveLogicalAuthority(false);
                NetworkLifecycleEvents.RaiseSessionStopped(this);
                return;
            }

            ObserveConnectedClients(running);
            ObserveLogicalAuthority(authority);
            ObserveLocalPlayer(localPlayer);
        }

        private void ObserveLogicalAuthority(bool authority)
        {
            if (authority == m_ObservedAuthority) return;

            m_ObservedAuthority = authority;
            m_ObservedAuthorityEpoch++;
            if (m_ObservedAuthorityEpoch == 0) m_ObservedAuthorityEpoch = 1;
            NetworkLifecycleEvents.RaiseLogicalAuthorityChanged(
                this,
                authority,
                m_ObservedAuthorityEpoch);
        }

        private void ObserveLocalPlayer(GameObject localPlayer)
        {
            if (!ReferenceEquals(localPlayer, m_ObservedLocalPlayer))
            {
                GameObject previous = m_ObservedLocalPlayer;
                m_ObservedLocalPlayer = localPlayer;

                if (!ReferenceEquals(previous, null))
                {
                    NetworkLifecycleEvents.RaiseLocalPlayerLost(this, previous);
                }

                if (localPlayer != null)
                {
                    NetworkLifecycleEvents.RaiseLocalPlayerReady(this, localPlayer);
                }
            }
        }

        private void ObserveConnectedClients(bool running)
        {
            m_ObservedClientScratch.Clear();
            if (running)
            {
                IReadOnlyCollection<uint> connectedClients = ConnectedClientIds;
                if (connectedClients != null)
                {
                    foreach (uint clientId in connectedClients)
                    {
                        if (!IsValidClientId(clientId)) continue;
                        if (m_ObservedConnectedClientIds.Contains(clientId)) continue;
                        m_ObservedConnectedClientIds.Add(clientId);
                        NetworkLifecycleEvents.RaiseClientConnected(this, clientId);
                    }
                }
            }

            foreach (uint clientId in m_ObservedConnectedClientIds)
            {
                if (running && ContainsClient(ConnectedClientIds, clientId)) continue;
                m_ObservedClientScratch.Add(clientId);
            }

            for (int i = 0; i < m_ObservedClientScratch.Count; i++)
            {
                uint clientId = m_ObservedClientScratch[i];
                m_ObservedConnectedClientIds.Remove(clientId);
                NetworkLifecycleEvents.RaiseClientDisconnected(this, clientId);
            }
        }

        private void StopLifecycleObservation()
        {
            if (!m_LifecycleObservationInitialized) return;

            m_ObservedClientScratch.Clear();

            if (!ReferenceEquals(m_ObservedLocalPlayer, null))
            {
                NetworkLifecycleEvents.RaiseLocalPlayerLost(this, m_ObservedLocalPlayer);
                m_ObservedLocalPlayer = null;
            }

            foreach (uint clientId in m_ObservedConnectedClientIds)
            {
                m_ObservedClientScratch.Add(clientId);
            }

            for (int i = 0; i < m_ObservedClientScratch.Count; i++)
            {
                NetworkLifecycleEvents.RaiseClientDisconnected(
                    this,
                    m_ObservedClientScratch[i]);
            }

            m_ObservedConnectedClientIds.Clear();
            m_ObservedClientScratch.Clear();

            if (m_ObservedAuthority)
            {
                m_ObservedAuthority = false;
                m_ObservedAuthorityEpoch++;
                if (m_ObservedAuthorityEpoch == 0) m_ObservedAuthorityEpoch = 1;
                NetworkLifecycleEvents.RaiseLogicalAuthorityChanged(
                    this,
                    false,
                    m_ObservedAuthorityEpoch);
            }

            if (m_ObservedRunning)
            {
                m_ObservedRunning = false;
                NetworkLifecycleEvents.RaiseSessionStopped(this);
            }

            m_LifecycleObservationInitialized = false;
        }

        private static bool ContainsClient(
            IReadOnlyCollection<uint> connectedClients,
            uint clientId)
        {
            if (connectedClients == null) return false;
            foreach (uint candidate in connectedClients)
            {
                if (candidate == clientId) return true;
            }

            return false;
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
