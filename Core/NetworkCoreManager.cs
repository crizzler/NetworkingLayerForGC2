using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network message routing manager for GC2 Core features.
    /// Provides message type definitions and routing delegates for network implementations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This manager coordinates network message routing between clients and server for:
    /// - Ragdoll state synchronization
    /// - Props attachment/detachment
    /// - Invincibility state
    /// - Poise damage and recovery
    /// - Busy limb states
    /// - Interaction validation
    /// </para>
    /// <para>
    /// Network implementations (transport, Mirror, etc.) should subscribe to the send delegates
    /// and call the receive methods when messages arrive.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Network Core Manager")]
    public partial class NetworkCoreManager : NetworkSingleton<NetworkCoreManager>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;
        
        /// <summary>
        /// Message type identifiers for network routing.
        /// Reserve range 200-229 for Core features.
        /// </summary>
        public static class MessageTypes
        {
            // Ragdoll (200-204)
            public const byte RagdollRequest = 200;
            public const byte RagdollResponse = 201;
            public const byte RagdollBroadcast = 202;
            
            // Props (205-209)
            public const byte PropRequest = 205;
            public const byte PropResponse = 206;
            public const byte PropBroadcast = 207;
            
            // Invincibility (210-214)
            public const byte InvincibilityRequest = 210;
            public const byte InvincibilityResponse = 211;
            public const byte InvincibilityBroadcast = 212;
            
            // Poise (215-219)
            public const byte PoiseRequest = 215;
            public const byte PoiseResponse = 216;
            public const byte PoiseBroadcast = 217;
            
            // Busy (220-224)
            public const byte BusyRequest = 220;
            public const byte BusyResponse = 221;
            public const byte BusyBroadcast = 222;
            
            // Interaction (225-229)
            public const byte InteractionRequest = 225;
            public const byte InteractionResponse = 226;
            public const byte InteractionBroadcast = 227;
            public const byte InteractionFocusBroadcast = 228;
            
            // Core State Sync
            public const byte CoreStateSync = 229;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("References")]
        [SerializeField] private NetworkCoreController m_CoreController;
        
        [Header("Prop Registry")]
        [Tooltip("Prop prefabs that can be attached over the network")]
        [SerializeField] private PropRegistryEntry[] m_PropRegistry;
        
        [Header("Debug")]
        [SerializeField] private bool m_DebugLog = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROP REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Serializable]
        public class PropRegistryEntry
        {
            public string PropId;
            public GameObject Prefab;
            
            [HideInInspector]
            public int Hash;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SEND DELEGATES (Assign these in your network implementation)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // Ragdoll
        public Action<NetworkRagdollRequest> SendRagdollRequestToServer;
        public Action<uint, NetworkRagdollResponse> SendRagdollResponseToClient;
        public Action<NetworkRagdollBroadcast> BroadcastRagdoll;
        
        // Props
        public Action<NetworkPropRequest> SendPropRequestToServer;
        public Action<uint, NetworkPropResponse> SendPropResponseToClient;
        public Action<NetworkPropBroadcast> BroadcastProp;
        
        // Invincibility
        public Action<NetworkInvincibilityRequest> SendInvincibilityRequestToServer;
        public Action<uint, NetworkInvincibilityResponse> SendInvincibilityResponseToClient;
        public Action<NetworkInvincibilityBroadcast> BroadcastInvincibility;
        
        // Poise
        public Action<NetworkPoiseRequest> SendPoiseRequestToServer;
        public Action<uint, NetworkPoiseResponse> SendPoiseResponseToClient;
        public Action<NetworkPoiseBroadcast> BroadcastPoise;
        
        // Busy
        public Action<NetworkBusyRequest> SendBusyRequestToServer;
        public Action<uint, NetworkBusyResponse> SendBusyResponseToClient;
        public Action<NetworkBusyBroadcast> BroadcastBusy;
        
        // Interaction
        public Action<NetworkInteractionRequest> SendInteractionRequestToServer;
        public Action<uint, NetworkInteractionResponse> SendInteractionResponseToClient;
        public Action<NetworkInteractionBroadcast> BroadcastInteraction;
        public Action<NetworkInteractionFocusBroadcast> BroadcastInteractionFocus;

        // Persistent state snapshots (server -> one client)
        public Action<uint, NetworkCoreSnapshot> SendCoreSnapshotToClient;
        
        // Utility delegates
        public Func<float> GetServerTime;
        public Func<uint, Character> GetCharacterByNetworkId;
        public Func<uint> GetLocalPlayerNetworkId;
        public Func<GameObject, uint> GetNetworkIdForGameObject;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Dictionary<int, GameObject> m_PropHashToPrefab;
        private Dictionary<(int CharacterInstanceId, int BoneHash), Transform> m_BoneHashCache;
        private readonly HashSet<int> m_LegacyControllerWarnings = new();
        private NetworkCorePatchHooks m_PatchHooks;
        private bool m_IsInitialized;
        private bool m_IsServer;
        private bool m_IsClient;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public NetworkCoreController CoreController => m_CoreController;
        public bool IsInitialized => m_IsInitialized;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override void OnSingletonAwake()
        {
            InitializePropRegistry();
        }
        
        protected override void OnDestroy()
        {
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false);
            }
            base.OnDestroy();
            UnwireController();
        }
        
        private void OnEnable()
        {
            if (m_CoreController != null)
            {
                WireController();
            }

            SyncPatchHooks();
        }
        
        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Core", false);
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false);
            }
            UnwireController();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;

            if (m_CoreController != null && m_CoreController.gameObject != gameObject)
            {
                Debug.LogWarning(
                    $"[NetworkCoreManager] The assigned Core controller on '{m_CoreController.name}' is a legacy " +
                    "per-character endpoint. A manager-owned controller will be used instead.", this);
                m_CoreController.enabled = false;
                m_CoreController = null;
            }

            if (m_CoreController == null)
            {
                m_CoreController = GetComponent<NetworkCoreController>();
                if (m_CoreController == null)
                {
                    m_CoreController = gameObject.AddComponent<NetworkCoreController>();
                }
            }

            DisableLegacyCoreControllers();
            NetworkCoreController.ClaimManagerOwnedSingleton(m_CoreController);
            
            m_CoreController.Initialize(isServer, isClient);
            SecurityIntegration.SetModuleServerContext("Core", isServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(isServer, () => GetServerTime?.Invoke() ?? Time.time);
            WireController();
            SyncPatchHooks();
            
            m_IsInitialized = true;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkCoreManager] Initialized - Server: {isServer}, Client: {isClient}");
            }
        }
        
        private void InitializePropRegistry()
        {
            m_PropHashToPrefab = new Dictionary<int, GameObject>();
            
            if (m_PropRegistry != null)
            {
                foreach (var entry in m_PropRegistry)
                {
                    if (entry.Prefab != null && !string.IsNullOrEmpty(entry.PropId))
                    {
                        entry.Hash = StableHashUtility.GetStableHash(entry.PropId);
                        m_PropHashToPrefab[entry.Hash] = entry.Prefab;
                    }
                }
            }
            
            m_BoneHashCache = new Dictionary<(int, int), Transform>();
        }
        
        private void WireController()
        {
            if (m_CoreController == null) return;
            
            // Wire send delegates
            m_CoreController.SendRagdollRequestToServer = req => SendRagdollRequestToServer?.Invoke(req);
            m_CoreController.SendRagdollResponseToClient = (id, resp) => SendRagdollResponseToClient?.Invoke(id, resp);
            m_CoreController.BroadcastRagdollToClients = bc => BroadcastRagdoll?.Invoke(bc);
            
            m_CoreController.SendPropRequestToServer = req => SendPropRequestToServer?.Invoke(req);
            m_CoreController.SendPropResponseToClient = (id, resp) => SendPropResponseToClient?.Invoke(id, resp);
            m_CoreController.BroadcastPropToClients = bc => BroadcastProp?.Invoke(bc);
            
            m_CoreController.SendInvincibilityRequestToServer = req => SendInvincibilityRequestToServer?.Invoke(req);
            m_CoreController.SendInvincibilityResponseToClient = (id, resp) => SendInvincibilityResponseToClient?.Invoke(id, resp);
            m_CoreController.BroadcastInvincibilityToClients = bc => BroadcastInvincibility?.Invoke(bc);
            
            m_CoreController.SendPoiseRequestToServer = req => SendPoiseRequestToServer?.Invoke(req);
            m_CoreController.SendPoiseResponseToClient = (id, resp) => SendPoiseResponseToClient?.Invoke(id, resp);
            m_CoreController.BroadcastPoiseToClients = bc => BroadcastPoise?.Invoke(bc);
            
            m_CoreController.SendBusyRequestToServer = req => SendBusyRequestToServer?.Invoke(req);
            m_CoreController.SendBusyResponseToClient = (id, resp) => SendBusyResponseToClient?.Invoke(id, resp);
            m_CoreController.BroadcastBusyToClients = bc => BroadcastBusy?.Invoke(bc);
            
            m_CoreController.SendInteractionRequestToServer = req => SendInteractionRequestToServer?.Invoke(req);
            m_CoreController.SendInteractionResponseToClient = (id, resp) => SendInteractionResponseToClient?.Invoke(id, resp);
            m_CoreController.BroadcastInteractionToClients = bc => BroadcastInteraction?.Invoke(bc);
            m_CoreController.BroadcastInteractionFocusToClients = bc => BroadcastInteractionFocus?.Invoke(bc);
            
            // Wire utility delegates
            m_CoreController.GetServerTime = () => GetServerTime?.Invoke() ?? Time.time;
            m_CoreController.GetCharacterByNetworkId = id => GetCharacterByNetworkId?.Invoke(id);
            m_CoreController.GetLocalPlayerNetworkId = () => GetLocalPlayerNetworkId?.Invoke() ?? 0;
            m_CoreController.GetNetworkIdForGameObject = gameObject =>
                GetNetworkIdForGameObject?.Invoke(gameObject) ?? 0;
            m_CoreController.GetPropPrefabByHash = GetPropPrefabByHash;
            m_CoreController.GetBoneByHashForCharacter = GetBoneByHash;
        }
        
        private void UnwireController()
        {
            if (m_CoreController == null) return;
            
            m_CoreController.SendRagdollRequestToServer = null;
            m_CoreController.SendRagdollResponseToClient = null;
            m_CoreController.BroadcastRagdollToClients = null;
            
            m_CoreController.SendPropRequestToServer = null;
            m_CoreController.SendPropResponseToClient = null;
            m_CoreController.BroadcastPropToClients = null;
            
            m_CoreController.SendInvincibilityRequestToServer = null;
            m_CoreController.SendInvincibilityResponseToClient = null;
            m_CoreController.BroadcastInvincibilityToClients = null;
            
            m_CoreController.SendPoiseRequestToServer = null;
            m_CoreController.SendPoiseResponseToClient = null;
            m_CoreController.BroadcastPoiseToClients = null;
            
            m_CoreController.SendBusyRequestToServer = null;
            m_CoreController.SendBusyResponseToClient = null;
            m_CoreController.BroadcastBusyToClients = null;
            
            m_CoreController.SendInteractionRequestToServer = null;
            m_CoreController.SendInteractionResponseToClient = null;
            m_CoreController.BroadcastInteractionToClients = null;
            m_CoreController.BroadcastInteractionFocusToClients = null;
            
            m_CoreController.GetServerTime = null;
            m_CoreController.GetCharacterByNetworkId = null;
            m_CoreController.GetLocalPlayerNetworkId = null;
            m_CoreController.GetNetworkIdForGameObject = null;
            m_CoreController.GetPropPrefabByHash = null;
            m_CoreController.GetBoneByHashForCharacter = null;
        }

        private void SyncPatchHooks()
        {
            if (!m_IsServer)
            {
                if (m_PatchHooks != null)
                {
                    m_PatchHooks.Initialize(false, false);
                }
                return;
            }

            if (m_PatchHooks == null)
            {
                m_PatchHooks = GetComponent<NetworkCorePatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkCorePatchHooks>();
                }
            }

            m_PatchHooks.Initialize(m_IsServer, m_IsClient);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROP REGISTRY HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private GameObject GetPropPrefabByHash(int hash)
        {
            return m_PropHashToPrefab.TryGetValue(hash, out var prefab) ? prefab : null;
        }
        
        private Transform GetBoneByHash(Character character, int hash)
        {
            if (character == null) return null;
            if (hash == 0) return character.transform;

            var key = (character.GetInstanceID(), hash);
            if (m_BoneHashCache.TryGetValue(key, out Transform cached) && cached != null &&
                (cached == character.transform || cached.IsChildOf(character.transform)))
            {
                return cached;
            }

            Transform[] bones = character.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < bones.Length; i++)
            {
                Transform candidate = bones[i];
                if (StableHashUtility.GetStableHash(candidate.name) != hash) continue;
                m_BoneHashCache[key] = candidate;
                return candidate;
            }

            return null;
        }

        private void DisableLegacyCoreControllers()
        {
            NetworkCoreController[] controllers = FindObjectsByType<NetworkCoreController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
            {
                NetworkCoreController controller = controllers[i];
                if (controller == null || controller == m_CoreController) continue;
                controller.enabled = false;
                if (m_LegacyControllerWarnings.Add(controller.GetInstanceID()))
                {
                    Debug.LogWarning(
                        $"[NetworkCoreManager] Disabled legacy per-character NetworkCoreController on " +
                        $"'{controller.name}'. Remove it from the character prefab; Core routing is manager-owned.",
                        controller);
                }
            }
        }
        
        /// <summary>
        /// Register a prop prefab at runtime.
        /// </summary>
        public void RegisterPropPrefab(string propId, GameObject prefab)
        {
            if (prefab == null || string.IsNullOrEmpty(propId)) return;
            
            int hash = StableHashUtility.GetStableHash(propId);
            m_PropHashToPrefab[hash] = prefab;
        }
        
        /// <summary>
        /// Get hash for a prop ID.
        /// </summary>
        public static int GetPropHash(string propId)
        {
            return StableHashUtility.GetStableHash(propId);
        }
        
        /// <summary>
        /// Get hash for a bone name.
        /// </summary>
        public static int GetBoneHash(string boneName)
        {
            return StableHashUtility.GetStableHash(boneName);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVE METHODS (Call these from your network implementation)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // RAGDOLL
        // ─────────────────────────────────────────────────────────────────────────────────────────
        
        /// <summary>
        /// [Server] Called when ragdoll request is received from client.
        /// </summary>
        public void ReceiveRagdollRequest(uint senderNetworkId, NetworkRagdollRequest request)
        {
            if (m_CoreController == null) return;
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCoreManager] ReceiveRagdollRequest called on non-server.");
                return;
            }
            PrimeCoreOwnershipForInboundRequest(request.ActorNetworkId, request.CharacterNetworkId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderNetworkId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Core",
                    nameof(NetworkRagdollRequest)))
            {
                SendRagdollResponseToClient?.Invoke(senderNetworkId, new NetworkRagdollResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = IsProtocolMismatch(request.ActorNetworkId, request.CorrelationId)
                        ? RagdollRejectReason.ProtocolMismatch
                        : RagdollRejectReason.SecurityViolation
                });
                return;
            }
            if (!TryValidateCoreActorBinding(
                    senderNetworkId,
                    request.ActorNetworkId,
                    request.CharacterNetworkId,
                    nameof(NetworkRagdollRequest),
                    out bool ragdollProtocolMismatch))
            {
                SendRagdollResponseToClient?.Invoke(senderNetworkId, new NetworkRagdollResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = ragdollProtocolMismatch
                        ? RagdollRejectReason.ProtocolMismatch
                        : RagdollRejectReason.NotOwner
                });
                return;
            }
            m_CoreController.ProcessRagdollRequest(senderNetworkId, request);
        }
        
        /// <summary>
        /// [Client] Called when ragdoll response is received from server.
        /// </summary>
        public void ReceiveRagdollResponse(NetworkRagdollResponse response)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveRagdollResponse(response);
        }
        
        /// <summary>
        /// [Client] Called when ragdoll broadcast is received from server.
        /// </summary>
        public void ReceiveRagdollBroadcast(NetworkRagdollBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveRagdollBroadcast(broadcast);
        }
        
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // PROPS
        // ─────────────────────────────────────────────────────────────────────────────────────────
        
        /// <summary>
        /// [Server] Called when prop request is received from client.
        /// </summary>
        public void ReceivePropRequest(uint senderNetworkId, NetworkPropRequest request)
        {
            if (m_CoreController == null) return;
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCoreManager] ReceivePropRequest called on non-server.");
                return;
            }
            PrimeCoreOwnershipForInboundRequest(request.ActorNetworkId, request.CharacterNetworkId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderNetworkId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Core",
                    nameof(NetworkPropRequest)))
            {
                SendPropResponseToClient?.Invoke(senderNetworkId, new NetworkPropResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = IsProtocolMismatch(request.ActorNetworkId, request.CorrelationId)
                        ? PropRejectReason.ProtocolMismatch
                        : PropRejectReason.SecurityViolation,
                    PropInstanceId = 0
                });
                return;
            }
            if (!TryValidateCoreActorBinding(
                    senderNetworkId,
                    request.ActorNetworkId,
                    request.CharacterNetworkId,
                    nameof(NetworkPropRequest),
                    out bool propProtocolMismatch))
            {
                SendPropResponseToClient?.Invoke(senderNetworkId, new NetworkPropResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = propProtocolMismatch
                        ? PropRejectReason.ProtocolMismatch
                        : PropRejectReason.NotOwner,
                    PropInstanceId = 0
                });
                return;
            }
            m_CoreController.ProcessPropRequest(senderNetworkId, request);
        }
        
        /// <summary>
        /// [Client] Called when prop response is received from server.
        /// </summary>
        public void ReceivePropResponse(NetworkPropResponse response)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceivePropResponse(response);
        }
        
        /// <summary>
        /// [Client] Called when prop broadcast is received from server.
        /// </summary>
        public void ReceivePropBroadcast(NetworkPropBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceivePropBroadcast(broadcast);
        }
        
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // INVINCIBILITY
        // ─────────────────────────────────────────────────────────────────────────────────────────
        
        /// <summary>
        /// [Server] Called when invincibility request is received from client.
        /// </summary>
        public void ReceiveInvincibilityRequest(uint senderNetworkId, NetworkInvincibilityRequest request)
        {
            if (m_CoreController == null) return;
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCoreManager] ReceiveInvincibilityRequest called on non-server.");
                return;
            }
            PrimeCoreOwnershipForInboundRequest(request.ActorNetworkId, request.CharacterNetworkId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderNetworkId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Core",
                    nameof(NetworkInvincibilityRequest)))
            {
                SendInvincibilityResponseToClient?.Invoke(senderNetworkId, new NetworkInvincibilityResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = IsProtocolMismatch(request.ActorNetworkId, request.CorrelationId)
                        ? InvincibilityRejectReason.ProtocolMismatch
                        : InvincibilityRejectReason.SecurityViolation,
                    ApprovedDuration = 0f
                });
                return;
            }
            if (!TryValidateCoreActorBinding(
                    senderNetworkId,
                    request.ActorNetworkId,
                    request.CharacterNetworkId,
                    nameof(NetworkInvincibilityRequest),
                    out bool invincibilityProtocolMismatch))
            {
                SendInvincibilityResponseToClient?.Invoke(senderNetworkId, new NetworkInvincibilityResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = invincibilityProtocolMismatch
                        ? InvincibilityRejectReason.ProtocolMismatch
                        : InvincibilityRejectReason.NotOwner,
                    ApprovedDuration = 0f
                });
                return;
            }
            m_CoreController.ProcessInvincibilityRequest(senderNetworkId, request);
        }
        
        /// <summary>
        /// [Client] Called when invincibility response is received from server.
        /// </summary>
        public void ReceiveInvincibilityResponse(NetworkInvincibilityResponse response)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveInvincibilityResponse(response);
        }
        
        /// <summary>
        /// [Client] Called when invincibility broadcast is received from server.
        /// </summary>
        public void ReceiveInvincibilityBroadcast(NetworkInvincibilityBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveInvincibilityBroadcast(broadcast);
        }
        
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // POISE
        // ─────────────────────────────────────────────────────────────────────────────────────────
        
        /// <summary>
        /// [Server] Called when poise request is received from client.
        /// </summary>
        public void ReceivePoiseRequest(uint senderNetworkId, NetworkPoiseRequest request)
        {
            if (m_CoreController == null) return;
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCoreManager] ReceivePoiseRequest called on non-server.");
                return;
            }
            PrimeCoreOwnershipForInboundRequest(request.ActorNetworkId, request.CharacterNetworkId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderNetworkId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Core",
                    nameof(NetworkPoiseRequest)))
            {
                SendPoiseResponseToClient?.Invoke(senderNetworkId, new NetworkPoiseResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = IsProtocolMismatch(request.ActorNetworkId, request.CorrelationId)
                        ? PoiseRejectReason.ProtocolMismatch
                        : PoiseRejectReason.SecurityViolation,
                    CurrentPoise = 0f,
                    IsBroken = false
                });
                return;
            }
            if (!TryValidateCoreActorBinding(
                    senderNetworkId,
                    request.ActorNetworkId,
                    request.CharacterNetworkId,
                    nameof(NetworkPoiseRequest),
                    out bool poiseProtocolMismatch))
            {
                SendPoiseResponseToClient?.Invoke(senderNetworkId, new NetworkPoiseResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = poiseProtocolMismatch
                        ? PoiseRejectReason.ProtocolMismatch
                        : PoiseRejectReason.NotOwner,
                    CurrentPoise = 0f,
                    IsBroken = false
                });
                return;
            }
            m_CoreController.ProcessPoiseRequest(senderNetworkId, request);
        }
        
        /// <summary>
        /// [Client] Called when poise response is received from server.
        /// </summary>
        public void ReceivePoiseResponse(NetworkPoiseResponse response)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceivePoiseResponse(response);
        }
        
        /// <summary>
        /// [Client] Called when poise broadcast is received from server.
        /// </summary>
        public void ReceivePoiseBroadcast(NetworkPoiseBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceivePoiseBroadcast(broadcast);
        }
        
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // BUSY
        // ─────────────────────────────────────────────────────────────────────────────────────────
        
        /// <summary>
        /// [Server] Called when busy request is received from client.
        /// </summary>
        public void ReceiveBusyRequest(uint senderNetworkId, NetworkBusyRequest request)
        {
            if (m_CoreController == null) return;
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCoreManager] ReceiveBusyRequest called on non-server.");
                return;
            }
            PrimeCoreOwnershipForInboundRequest(request.ActorNetworkId, request.CharacterNetworkId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderNetworkId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Core",
                    nameof(NetworkBusyRequest)))
            {
                SendBusyResponseToClient?.Invoke(senderNetworkId, new NetworkBusyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = IsProtocolMismatch(request.ActorNetworkId, request.CorrelationId)
                        ? BusyRejectReason.ProtocolMismatch
                        : BusyRejectReason.SecurityViolation
                });
                return;
            }
            if (!TryValidateCoreActorBinding(
                    senderNetworkId,
                    request.ActorNetworkId,
                    request.CharacterNetworkId,
                    nameof(NetworkBusyRequest),
                    out bool busyProtocolMismatch))
            {
                SendBusyResponseToClient?.Invoke(senderNetworkId, new NetworkBusyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = busyProtocolMismatch
                        ? BusyRejectReason.ProtocolMismatch
                        : BusyRejectReason.NotOwner
                });
                return;
            }
            m_CoreController.ProcessBusyRequest(senderNetworkId, request);
        }
        
        /// <summary>
        /// [Client] Called when busy response is received from server.
        /// </summary>
        public void ReceiveBusyResponse(NetworkBusyResponse response)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveBusyResponse(response);
        }
        
        /// <summary>
        /// [Client] Called when busy broadcast is received from server.
        /// </summary>
        public void ReceiveBusyBroadcast(NetworkBusyBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveBusyBroadcast(broadcast);
        }
        
        // ─────────────────────────────────────────────────────────────────────────────────────────
        // INTERACTION
        // ─────────────────────────────────────────────────────────────────────────────────────────
        
        /// <summary>
        /// [Server] Called when interaction request is received from client.
        /// </summary>
        public void ReceiveInteractionRequest(uint senderNetworkId, NetworkInteractionRequest request)
        {
            if (m_CoreController == null) return;
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCoreManager] ReceiveInteractionRequest called on non-server.");
                return;
            }
            PrimeCoreOwnershipForInboundRequest(request.ActorNetworkId, request.CharacterNetworkId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderNetworkId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Core",
                    nameof(NetworkInteractionRequest)))
            {
                SendInteractionResponseToClient?.Invoke(senderNetworkId, new NetworkInteractionResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = IsProtocolMismatch(request.ActorNetworkId, request.CorrelationId)
                        ? InteractionRejectReason.ProtocolMismatch
                        : InteractionRejectReason.SecurityViolation,
                    ResultData = 0
                });
                return;
            }
            if (!TryValidateCoreActorBinding(
                    senderNetworkId,
                    request.ActorNetworkId,
                    request.CharacterNetworkId,
                    nameof(NetworkInteractionRequest),
                    out bool interactionProtocolMismatch))
            {
                SendInteractionResponseToClient?.Invoke(senderNetworkId, new NetworkInteractionResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    RejectReason = interactionProtocolMismatch
                        ? InteractionRejectReason.ProtocolMismatch
                        : InteractionRejectReason.NotOwner,
                    ResultData = 0
                });
                return;
            }
            m_CoreController.ProcessInteractionRequest(senderNetworkId, request);
        }
        
        /// <summary>
        /// [Client] Called when interaction response is received from server.
        /// </summary>
        public void ReceiveInteractionResponse(NetworkInteractionResponse response)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveInteractionResponse(response);
        }
        
        /// <summary>
        /// [Client] Called when interaction broadcast is received from server.
        /// </summary>
        public void ReceiveInteractionBroadcast(NetworkInteractionBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveInteractionBroadcast(broadcast);
        }

        /// <summary>[Client] Called when an interaction focus/blur broadcast arrives.</summary>
        public void ReceiveInteractionFocusBroadcast(NetworkInteractionFocusBroadcast broadcast)
        {
            if (m_CoreController == null) return;
            m_CoreController.ReceiveInteractionFocusBroadcast(broadcast);
        }

        /// <summary>[Client] Apply or retain a full persistent Core snapshot.</summary>
        public bool ReceiveCoreSnapshot(NetworkCoreSnapshot snapshot)
        {
            return m_CoreController != null && m_CoreController.ReceiveCoreSnapshot(snapshot);
        }

        /// <summary>[Server] Capture persistent Core state for one character.</summary>
        public bool TryCaptureCoreSnapshot(uint characterNetworkId, out NetworkCoreSnapshot snapshot)
        {
            snapshot = default;
            return m_IsServer && m_CoreController != null &&
                   m_CoreController.TryCaptureCoreSnapshot(characterNetworkId, out snapshot);
        }

        /// <summary>[Server] Send one character snapshot to one client.</summary>
        public bool SendSnapshotToClient(uint clientNetworkId, uint characterNetworkId)
        {
            if (!TryCaptureCoreSnapshot(characterNetworkId, out NetworkCoreSnapshot snapshot))
                return false;
            if (SendCoreSnapshotToClient == null) return false;

            SendCoreSnapshotToClient.Invoke(clientNetworkId, snapshot);
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONVENIENCE CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start ragdoll on a character.
        /// </summary>
        public void RequestStartRagdoll(uint characterNetworkId, Vector3 force = default, 
            Vector3 forcePoint = default, Action<NetworkRagdollResponse> callback = null)
        {
            m_CoreController?.RequestStartRagdoll(characterNetworkId, force, forcePoint, callback);
        }
        
        /// <summary>
        /// [Client] Request to recover from ragdoll.
        /// </summary>
        public void RequestStartRecover(uint characterNetworkId, bool instant = false,
            Action<NetworkRagdollResponse> callback = null)
        {
            m_CoreController?.RequestStartRecover(characterNetworkId, instant, callback);
        }
        
        /// <summary>
        /// [Client] Request to attach a prop.
        /// </summary>
        public void RequestAttachProp(uint characterNetworkId, string propId, string boneName,
            Vector3 localPosition = default, Quaternion localRotation = default,
            Action<NetworkPropResponse> callback = null)
        {
            int propHash = GetPropHash(propId);
            int boneHash = string.IsNullOrEmpty(boneName) ? 0 : GetBoneHash(boneName);
            m_CoreController?.RequestAttachProp(characterNetworkId, propHash, boneHash, 
                localPosition, localRotation, callback);
        }
        
        /// <summary>
        /// [Client] Request to detach a prop.
        /// </summary>
        public void RequestDetachProp(uint characterNetworkId, string propId,
            Action<NetworkPropResponse> callback = null)
        {
            int propHash = GetPropHash(propId);
            m_CoreController?.RequestDetachProp(characterNetworkId, propHash, callback);
        }

        /// <summary>[Client] Request removal of one exact server-assigned prop.</summary>
        public void RequestDetachPropInstance(uint characterNetworkId, int propInstanceId,
            Action<NetworkPropResponse> callback = null)
        {
            m_CoreController?.RequestDetachPropInstance(characterNetworkId, propInstanceId, callback);
        }

        /// <summary>[Client] Request removal of all network-managed props.</summary>
        public void RequestDetachAllProps(uint characterNetworkId,
            Action<NetworkPropResponse> callback = null)
        {
            m_CoreController?.RequestDetachAllProps(characterNetworkId, callback);
        }
        
        /// <summary>
        /// [Client] Request to set invincibility.
        /// </summary>
        public void RequestSetInvincibility(uint characterNetworkId, float duration,
            Action<NetworkInvincibilityResponse> callback = null)
        {
            m_CoreController?.RequestSetInvincibility(characterNetworkId, duration, callback);
        }
        
        /// <summary>
        /// [Client] Request to damage poise.
        /// </summary>
        public void RequestPoiseDamage(uint characterNetworkId, float damage,
            Action<NetworkPoiseResponse> callback = null)
        {
            m_CoreController?.RequestPoiseDamage(characterNetworkId, damage, callback);
        }
        
        /// <summary>
        /// [Client] Request to reset poise.
        /// </summary>
        public void RequestPoiseReset(uint characterNetworkId,
            Action<NetworkPoiseResponse> callback = null)
        {
            m_CoreController?.RequestPoiseReset(characterNetworkId, -1, callback);
        }
        
        /// <summary>
        /// [Client] Request to set busy limbs.
        /// </summary>
        public void RequestSetBusy(uint characterNetworkId, BusyLimbs limbs, bool setBusy,
            float timeout = 0, Action<NetworkBusyResponse> callback = null)
        {
            m_CoreController?.RequestSetBusy(characterNetworkId, limbs, setBusy, timeout, callback);
        }
        
        /// <summary>
        /// [Client] Request interaction with target.
        /// </summary>
        public void RequestInteraction(uint characterNetworkId, uint targetNetworkId,
            Vector3 interactionPosition, Action<NetworkInteractionResponse> callback = null)
        {
            m_CoreController?.RequestInteraction(characterNetworkId, targetNetworkId, 0, 
                interactionPosition, callback);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONVENIENCE SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Directly start ragdoll and broadcast.
        /// </summary>
        public void ServerStartRagdoll(uint characterNetworkId, Vector3 force = default, 
            Vector3 forcePoint = default)
        {
            m_CoreController?.ServerStartRagdoll(characterNetworkId, force, forcePoint);
        }
        
        /// <summary>
        /// [Server] Directly set invincibility and broadcast.
        /// </summary>
        public void ServerSetInvincibility(uint characterNetworkId, float duration)
        {
            m_CoreController?.ServerSetInvincibility(characterNetworkId, duration);
        }
        
        /// <summary>
        /// [Server] Directly damage poise and broadcast.
        /// </summary>
        public void ServerDamagePoise(uint characterNetworkId, float damage)
        {
            m_CoreController?.ServerDamagePoise(characterNetworkId, damage);
        }
        
        /// <summary>
        /// [Server] Directly reset poise and broadcast.
        /// </summary>
        public void ServerResetPoise(uint characterNetworkId)
        {
            m_CoreController?.ServerResetPoise(characterNetworkId);
        }

        /// <summary>[Server] Attach a registered prop and broadcast its descriptor.</summary>
        public bool TryServerAttachProp(uint characterNetworkId, string propId, string boneName,
            Vector3 localPosition, Quaternion localRotation, out int propInstanceId,
            out PropRejectReason rejectReason)
        {
            return TryServerAttachProp(characterNetworkId, GetPropHash(propId),
                string.IsNullOrEmpty(boneName) ? 0 : GetBoneHash(boneName), localPosition,
                localRotation, out propInstanceId, out rejectReason);
        }

        /// <summary>[Server] Attach a registered prop using precomputed stable hashes.</summary>
        public bool TryServerAttachProp(uint characterNetworkId, int propHash, int boneHash,
            Vector3 localPosition, Quaternion localRotation, out int propInstanceId,
            out PropRejectReason rejectReason)
        {
            propInstanceId = 0;
            rejectReason = PropRejectReason.NotAuthorized;
            return m_CoreController != null && m_CoreController.TryServerAttachProp(
                characterNetworkId, propHash, boneHash, localPosition, localRotation,
                out propInstanceId, out rejectReason);
        }

        /// <summary>[Server] Detach one exact network prop.</summary>
        public bool TryServerDetachProp(uint characterNetworkId, int propInstanceId,
            out PropRejectReason rejectReason)
        {
            rejectReason = PropRejectReason.NotAuthorized;
            return m_CoreController != null && m_CoreController.TryServerDetachProp(
                characterNetworkId, propInstanceId, out rejectReason);
        }

        /// <summary>[Server] Detach every network-managed prop.</summary>
        public bool ServerDetachAllProps(uint characterNetworkId,
            out PropRejectReason rejectReason)
        {
            rejectReason = PropRejectReason.NotAuthorized;
            return m_CoreController != null &&
                   m_CoreController.ServerDetachAllProps(characterNetworkId, out rejectReason);
        }

        /// <summary>
        /// Drop snapshot/attachment state for a despawned character before its network ID can be
        /// reused. Server callers also notify observers with an idempotent detach-all broadcast.
        /// </summary>
        public void ForgetCharacterState(uint characterNetworkId)
        {
            m_CoreController?.ForgetCharacterState(characterNetworkId, m_IsServer);
            if (m_BoneHashCache == null) return;

            var remove = new List<(int CharacterInstanceId, int BoneHash)>();
            foreach (KeyValuePair<(int CharacterInstanceId, int BoneHash), Transform> pair in
                     m_BoneHashCache)
            {
                if (pair.Value == null ||
                    pair.Value.GetComponentInParent<NetworkCharacter>()?.NetworkId == characterNetworkId)
                {
                    remove.Add(pair.Key);
                }
            }

            for (int i = 0; i < remove.Count; i++) m_BoneHashCache.Remove(remove[i]);
        }

        /// <summary>[Server] Broadcast transient focus/blur presentation state.</summary>
        public void ServerBroadcastInteractionFocus(uint characterNetworkId, uint targetNetworkId,
            bool isFocus)
        {
            if (!m_IsServer) return;
            BroadcastInteractionFocus?.Invoke(new NetworkInteractionFocusBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                TargetNetworkId = targetNetworkId,
                IsFocus = isFocus
            });
        }
    }
}
