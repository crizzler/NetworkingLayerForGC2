#if GC2_STATS
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;

namespace Arawn.GameCreator2.Networking.Stats
{
    /// <summary>
    /// Server-authoritative stats controller for GC2 Traits.
    /// Intercepts all stat/attribute changes and routes through server validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purpose:</b>
    /// In competitive multiplayer, stats like health, damage, speed, etc. MUST be
    /// server-authoritative to prevent cheating. This component ensures all stat
    /// modifications go through server validation before being applied.
    /// </para>
    /// <para>
    /// <b>Architecture:</b>
    /// - Clients send modification requests to server
    /// - Server validates and applies changes
    /// - Server broadcasts confirmed changes to all clients
    /// - Optimistic updates can be enabled for responsiveness
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Traits))]
    [AddComponentMenu("Game Creator/Network/Stats/Network Stats Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 5)]
    public class NetworkStatsController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Network Settings")]
        [Tooltip("Apply changes optimistically before server confirmation.")]
        [SerializeField] private bool m_OptimisticUpdates = true;
        
        [Tooltip("Rollback optimistic updates if server rejects.")]
        [SerializeField] private bool m_RollbackOnReject = true;
        
        [Header("Sync Settings")]
        [Tooltip("Send full state sync at this interval (seconds). 0 = never.")]
        [SerializeField] private float m_FullSyncInterval = 5f;
        
        [Tooltip("Send delta updates at this interval (seconds).")]
        [SerializeField] private float m_DeltaSyncInterval = 0.1f;
        
        [Tooltip("Only sync attributes that change frequently (health, mana).")]
        [SerializeField] private bool m_SmartDeltaSync = true;
        
        [Header("Validation")]
        [Tooltip("Maximum stat change per second (anti-cheat).")]
        [SerializeField] private float m_MaxChangePerSecond = 1000f;
        
        [Tooltip("Log rejected modifications for debugging.")]
        [SerializeField] private bool m_LogRejections = false;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogAllChanges = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Called when a stat modification is requested.</summary>
        public event Action<NetworkStatModifyRequest> OnStatModifyRequested;
        
        /// <summary>Called when a stat modification is confirmed.</summary>
        public event Action<NetworkStatChangeBroadcast> OnStatChanged;
        
        /// <summary>Called when an attribute modification is requested.</summary>
        public event Action<NetworkAttributeModifyRequest> OnAttributeModifyRequested;
        
        /// <summary>Called when an attribute modification is confirmed.</summary>
        public event Action<NetworkAttributeChangeBroadcast> OnAttributeChanged;
        
        /// <summary>Called when a status effect action is requested.</summary>
        public event Action<NetworkStatusEffectRequest> OnStatusEffectRequested;
        
        /// <summary>Called when a status effect action is confirmed.</summary>
        public event Action<NetworkStatusEffectBroadcast> OnStatusEffectChanged;
        
        /// <summary>Called when a stat modifier action is requested.</summary>
        public event Action<NetworkStatModifierRequest> OnStatModifierRequested;
        
        /// <summary>Called when a stat modifier action is confirmed.</summary>
        public event Action<NetworkStatModifierBroadcast> OnStatModifierChanged;
        
        /// <summary>Called when a modification is rejected by server.</summary>
        public event Action<StatRejectionReason, string> OnModificationRejected;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Traits m_Traits;
        private NetworkCharacter m_NetworkCharacter;
        
        // Network role
        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;
        
        // Request tracking
        private ushort m_NextRequestId = 1;
        private readonly Dictionary<ushort, PendingStatModify> m_PendingStatMods = new(16);
        private readonly Dictionary<ushort, PendingAttributeModify> m_PendingAttrMods = new(16);
        private readonly Dictionary<ushort, PendingStatusEffectAction> m_PendingStatusEffects = new(8);
        private readonly Dictionary<ushort, PendingStatModifierAction> m_PendingModifierRequests = new(8);
        private readonly Dictionary<ushort, PendingClearStatusEffectsAction> m_PendingClearStatusEffects = new(4);
        
        // State tracking for delta sync
        private readonly Dictionary<int, float> m_LastSyncedStatValues = new(16);
        private readonly Dictionary<int, float> m_LastSyncedAttrValues = new(16);
        private float m_LastFullSync;
        private float m_LastDeltaSync;
        
        // Optimistic rollback data
        private readonly Dictionary<int, float> m_OptimisticStatValues = new(16);
        private readonly Dictionary<int, float> m_OptimisticAttrValues = new(16);
        
        // Rate limiting (anti-cheat)
        private readonly Dictionary<int, float> m_StatChangeAccumulator = new(16);
        private readonly Dictionary<int, float> m_AttrChangeAccumulator = new(16);
        private float m_LastRateLimitReset;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingStatModify
        {
            public NetworkStatModifyRequest Request;
            public float OriginalValue;
            public float SentTime;
        }
        
        private struct PendingAttributeModify
        {
            public NetworkAttributeModifyRequest Request;
            public float OriginalValue;
            public float SentTime;
        }
        
        private struct PendingStatusEffectAction
        {
            public NetworkStatusEffectRequest Request;
            public float SentTime;
        }
        
        private struct PendingStatModifierAction
        {
            public NetworkStatModifierRequest Request;
            public float SentTime;
        }
        
        private struct PendingClearStatusEffectsAction
        {
            public NetworkClearStatusEffectsRequest Request;
            public float SentTime;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Traits component.</summary>
        public Traits Traits => m_Traits;
        
        /// <summary>Network ID of this character.</summary>
        public uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServer => m_IsServer;
        
        /// <summary>Whether this is the local player's character.</summary>
        public bool IsLocalClient => m_IsLocalClient;
        
        /// <summary>Whether optimistic updates are enabled.</summary>
        public bool OptimisticUpdates
        {
            get => m_OptimisticUpdates;
            set => m_OptimisticUpdates = value;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Traits = GetComponent<Traits>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }
        
        private void Start()
        {
            // Subscribe to GC2 stat change events (for local detection)
            if (m_Traits.RuntimeStats != null)
            {
                m_Traits.RuntimeStats.EventChange += OnLocalStatChanged;
            }
            
            if (m_Traits.RuntimeAttributes != null)
            {
                m_Traits.RuntimeAttributes.EventChange += OnLocalAttributeChanged;
            }
            
            if (m_Traits.RuntimeStatusEffects != null)
            {
                m_Traits.RuntimeStatusEffects.EventChange += OnLocalStatusEffectChanged;
            }
        }
        
        private void OnDestroy()
        {
            if (m_Traits != null)
            {
                if (m_Traits.RuntimeStats != null)
                    m_Traits.RuntimeStats.EventChange -= OnLocalStatChanged;
                
                if (m_Traits.RuntimeAttributes != null)
                    m_Traits.RuntimeAttributes.EventChange -= OnLocalAttributeChanged;
                
                if (m_Traits.RuntimeStatusEffects != null)
                    m_Traits.RuntimeStatusEffects.EventChange -= OnLocalStatusEffectChanged;
            }
        }
        
        private void Update()
        {
            if (!m_IsServer && !m_IsLocalClient) return;
            
            float currentTime = Time.time;
            
            // Reset rate limiters periodically
            if (currentTime - m_LastRateLimitReset > 1f)
            {
                m_StatChangeAccumulator.Clear();
                m_AttrChangeAccumulator.Clear();
                m_LastRateLimitReset = currentTime;
            }
            
            // Server-side sync
            if (m_IsServer)
            {
                // Full sync
                if (m_FullSyncInterval > 0 && currentTime - m_LastFullSync > m_FullSyncInterval)
                {
                    BroadcastFullState();
                    m_LastFullSync = currentTime;
                }
                
                // Delta sync
                if (m_DeltaSyncInterval > 0 && currentTime - m_LastDeltaSync > m_DeltaSyncInterval)
                {
                    BroadcastDeltaState();
                    m_LastDeltaSync = currentTime;
                }
            }
            
            // Cleanup timed out requests
            CleanupPendingRequests();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the network stats controller with role information.
        /// Called by NetworkCharacter when network role is determined.
        /// </summary>
        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;
            
            // Initialize state tracking
            InitializeStateTracking();
            
            if (m_LogAllChanges)
            {
                string role = m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : "RemoteClient");
                Debug.Log($"[NetworkStatsController] {gameObject.name} initialized as {role}");
            }
        }
        
        private void InitializeStateTracking()
        {
            if (m_Traits.RuntimeStats == null) return;
            
            // Cache initial stat values
            foreach (var statHash in m_Traits.RuntimeStats.StatsKeys)
            {
                var stat = m_Traits.RuntimeStats.Get(statHash);
                if (stat != null)
                {
                    m_LastSyncedStatValues[statHash] = (float)stat.Value;
                }
            }
            
            // Cache initial attribute values
            foreach (var attrHash in m_Traits.RuntimeAttributes.AttributesKeys)
            {
                var attr = m_Traits.RuntimeAttributes.Get(attrHash);
                if (attr != null)
                {
                    m_LastSyncedAttrValues[attrHash] = (float)attr.Value;
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: REQUEST MODIFICATIONS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request a stat base value modification. Client-side only.
        /// </summary>
        public void RequestStatModify(
            IdString statId, 
            StatModificationType modType, 
            float value,
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot modify stats on remote client");
                return;
            }
            
            // Rate limit check
            if (!CheckRateLimit(statId.Hash, value, m_StatChangeAccumulator))
            {
                OnModificationRejected?.Invoke(StatRejectionReason.RateLimitExceeded, $"Stat: {statId.String}");
                return;
            }
            
            var request = new NetworkStatModifyRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                StatHash = statId.Hash,
                ModificationType = modType,
                Value = value,
                Source = source,
                SourceHash = sourceHash
            };
            
            // Store original value for rollback
            var stat = m_Traits.RuntimeStats.Get(statId);
            float originalValue = stat != null ? (float)stat.Base : 0f;
            
            // Optimistic update
            if (m_OptimisticUpdates && !m_IsServer)
            {
                m_OptimisticStatValues[statId.Hash] = originalValue;
                ApplyStatModifyLocally(request);
            }
            
            // Track pending request
            m_PendingStatMods[request.RequestId] = new PendingStatModify
            {
                Request = request,
                OriginalValue = originalValue,
                SentTime = Time.time
            };
            
            OnStatModifyRequested?.Invoke(request);
            
            // If server, process immediately
            if (m_IsServer)
            {
                var response = ProcessStatModifyRequest(request, NetworkId);
                ReceiveStatModifyResponse(response);
            }
            else
            {
                // Send to server via manager
                NetworkStatsManager.Instance?.SendStatModifyRequest(request);
            }
        }
        
        /// <summary>
        /// Request an attribute value modification. Client-side only.
        /// </summary>
        public void RequestAttributeModify(
            IdString attributeId,
            AttributeModificationType modType,
            float value,
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot modify attributes on remote client");
                return;
            }
            
            // Rate limit check
            if (!CheckRateLimit(attributeId.Hash, value, m_AttrChangeAccumulator))
            {
                OnModificationRejected?.Invoke(StatRejectionReason.RateLimitExceeded, $"Attribute: {attributeId.String}");
                return;
            }
            
            var request = new NetworkAttributeModifyRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                AttributeHash = attributeId.Hash,
                ModificationType = modType,
                Value = value,
                Source = source,
                SourceHash = sourceHash
            };
            
            // Store original value for rollback
            var attr = m_Traits.RuntimeAttributes.Get(attributeId);
            float originalValue = attr != null ? (float)attr.Value : 0f;
            
            // Optimistic update
            if (m_OptimisticUpdates && !m_IsServer)
            {
                m_OptimisticAttrValues[attributeId.Hash] = originalValue;
                ApplyAttributeModifyLocally(request);
            }
            
            // Track pending request
            m_PendingAttrMods[request.RequestId] = new PendingAttributeModify
            {
                Request = request,
                OriginalValue = originalValue,
                SentTime = Time.time
            };
            
            OnAttributeModifyRequested?.Invoke(request);
            
            // If server, process immediately
            if (m_IsServer)
            {
                var response = ProcessAttributeModifyRequest(request, NetworkId);
                ReceiveAttributeModifyResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendAttributeModifyRequest(request);
            }
        }
        
        /// <summary>
        /// Request a status effect action.
        /// </summary>
        public void RequestStatusEffectAction(
            IdString statusEffectId,
            StatusEffectAction action,
            byte amount = 1,
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot modify status effects on remote client");
                return;
            }
            
            var request = new NetworkStatusEffectRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                StatusEffectHash = statusEffectId.Hash,
                Action = action,
                Amount = amount,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingStatusEffects[request.RequestId] = new PendingStatusEffectAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatusEffectRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatusEffectRequest(request, NetworkId);
                ReceiveStatusEffectResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendStatusEffectRequest(request);
            }
        }
        
        /// <summary>
        /// Request to add a stat modifier.
        /// </summary>
        public void RequestStatModifierAdd(
            IdString statId,
            NetworkModifierType modifierType,
            float value,
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot add modifiers on remote client");
                return;
            }
            
            var request = new NetworkStatModifierRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                StatHash = statId.Hash,
                Action = ModifierAction.Add,
                ModifierType = modifierType,
                Value = value,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingModifierRequests[request.RequestId] = new PendingStatModifierAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatModifierRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatModifierRequest(request, NetworkId);
                ReceiveStatModifierResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendStatModifierRequest(request);
            }
        }
        
        /// <summary>
        /// Request to remove a stat modifier.
        /// </summary>
        public void RequestStatModifierRemove(
            IdString statId,
            NetworkModifierType modifierType,
            float value,
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot remove modifiers on remote client");
                return;
            }
            
            var request = new NetworkStatModifierRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                StatHash = statId.Hash,
                Action = ModifierAction.Remove,
                ModifierType = modifierType,
                Value = value,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingModifierRequests[request.RequestId] = new PendingStatModifierAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatModifierRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatModifierRequest(request, NetworkId);
                ReceiveStatModifierResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendStatModifierRequest(request);
            }
        }
        
        /// <summary>
        /// Request to clear all stat modifiers.
        /// </summary>
        public void RequestStatModifiersClear(
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot clear modifiers on remote client");
                return;
            }
            
            var request = new NetworkStatModifierRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                StatHash = 0, // 0 = all stats
                Action = ModifierAction.Clear,
                ModifierType = NetworkModifierType.Constant,
                Value = 0,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingModifierRequests[request.RequestId] = new PendingStatModifierAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatModifierRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatModifierRequest(request, NetworkId);
                ReceiveStatModifierResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendStatModifierRequest(request);
            }
        }
        
        /// <summary>
        /// Request to clear status effects by type mask.
        /// </summary>
        public void RequestClearStatusEffectsByType(
            byte typeMask,
            StatModificationSource source = StatModificationSource.Direct,
            int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkStatsController] Cannot clear status effects on remote client");
                return;
            }
            
            var request = new NetworkClearStatusEffectsRequest
            {
                RequestId = m_NextRequestId++,
                TargetNetworkId = NetworkId,
                TypeMask = typeMask,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingClearStatusEffects[request.RequestId] = new PendingClearStatusEffectsAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            if (m_IsServer)
            {
                var response = ProcessClearStatusEffectsRequest(request, NetworkId);
                ReceiveClearStatusEffectsResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendClearStatusEffectsRequest(request);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a stat modification request.
        /// </summary>
        public NetworkStatModifyResponse ProcessStatModifyRequest(NetworkStatModifyRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkStatModifyResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.NotAuthorized
                };
            }
            
            // Validate target
            if (request.TargetNetworkId != NetworkId)
            {
                return new NetworkStatModifyResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.TargetNotFound
                };
            }
            
            // Get stat
            var stat = m_Traits.RuntimeStats.Get(request.StatHash);
            if (stat == null)
            {
                return new NetworkStatModifyResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.StatNotFound
                };
            }
            
            // Apply modification
            float newValue = ApplyStatModification(stat, request.ModificationType, request.Value);
            
            // Broadcast change
            var broadcast = new NetworkStatChangeBroadcast
            {
                NetworkId = NetworkId,
                StatHash = request.StatHash,
                NewBaseValue = (float)stat.Base,
                NewComputedValue = (float)stat.Value
            };
            
            NetworkStatsManager.Instance?.BroadcastStatChange(broadcast);
            OnStatChanged?.Invoke(broadcast);
            
            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkStatsController] Stat modified: hash={request.StatHash}, newBase={stat.Base}, newValue={stat.Value}");
            }
            
            return new NetworkStatModifyResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = StatRejectionReason.None,
                NewValue = newValue
            };
        }
        
        /// <summary>
        /// [Server] Process an attribute modification request.
        /// </summary>
        public NetworkAttributeModifyResponse ProcessAttributeModifyRequest(NetworkAttributeModifyRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkAttributeModifyResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.NotAuthorized
                };
            }
            
            // Validate target
            if (request.TargetNetworkId != NetworkId)
            {
                return new NetworkAttributeModifyResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.TargetNotFound
                };
            }
            
            // Get attribute
            var attr = m_Traits.RuntimeAttributes.Get(request.AttributeHash);
            if (attr == null)
            {
                return new NetworkAttributeModifyResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.AttributeNotFound
                };
            }
            
            // Apply modification
            float newValue = ApplyAttributeModification(attr, request.ModificationType, request.Value);
            
            // Broadcast change
            var broadcast = new NetworkAttributeChangeBroadcast
            {
                NetworkId = NetworkId,
                AttributeHash = request.AttributeHash,
                NewValue = (float)attr.Value,
                MaxValue = (float)attr.MaxValue,
                Change = newValue - (float)attr.Value
            };
            
            NetworkStatsManager.Instance?.BroadcastAttributeChange(broadcast);
            OnAttributeChanged?.Invoke(broadcast);
            
            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkStatsController] Attribute modified: hash={request.AttributeHash}, newValue={attr.Value}/{attr.MaxValue}");
            }
            
            return new NetworkAttributeModifyResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = StatRejectionReason.None,
                NewValue = (float)attr.Value,
                MaxValue = (float)attr.MaxValue
            };
        }
        
        /// <summary>
        /// [Server] Process a status effect request.
        /// </summary>
        public NetworkStatusEffectResponse ProcessStatusEffectRequest(NetworkStatusEffectRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkStatusEffectResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.NotAuthorized
                };
            }
            
            // Get status effect from settings
            var statusEffect = GetStatusEffectById(request.StatusEffectHash);
            if (statusEffect == null)
            {
                return new NetworkStatusEffectResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.StatusEffectNotFound
                };
            }
            
            // Apply action
            switch (request.Action)
            {
                case StatusEffectAction.Add:
                    for (int i = 0; i < request.Amount; i++)
                    {
                        m_Traits.RuntimeStatusEffects.Add(statusEffect);
                    }
                    break;
                    
                case StatusEffectAction.Remove:
                    m_Traits.RuntimeStatusEffects.Remove(statusEffect, request.Amount);
                    break;
                    
                case StatusEffectAction.RemoveAll:
                    m_Traits.RuntimeStatusEffects.Remove(statusEffect, 99);
                    break;
            }
            
            byte stackCount = (byte)m_Traits.RuntimeStatusEffects.GetActiveStackCount(statusEffect.ID);
            
            // Broadcast
            var broadcast = new NetworkStatusEffectBroadcast
            {
                NetworkId = NetworkId,
                StatusEffectHash = request.StatusEffectHash,
                Action = request.Action,
                StackCount = stackCount,
                RemainingDuration = GetStatusEffectRemainingDuration(statusEffect.ID)
            };
            
            NetworkStatsManager.Instance?.BroadcastStatusEffectChange(broadcast);
            OnStatusEffectChanged?.Invoke(broadcast);
            
            return new NetworkStatusEffectResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = StatRejectionReason.None,
                CurrentStackCount = stackCount
            };
        }
        
        /// <summary>
        /// [Server] Process a stat modifier request.
        /// </summary>
        public NetworkStatModifierResponse ProcessStatModifierRequest(NetworkStatModifierRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.NotAuthorized
                };
            }
            
            // Validate target
            if (request.TargetNetworkId != NetworkId)
            {
                return new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.TargetNotFound
                };
            }
            
            // Clear all modifiers
            if (request.Action == ModifierAction.Clear)
            {
                m_Traits.RuntimeStats.ClearModifiers();
                
                return new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    Authorized = true,
                    RejectionReason = StatRejectionReason.None,
                    NewStatValue = 0f
                };
            }
            
            // Get stat
            var stat = m_Traits.RuntimeStats.Get(request.StatHash);
            if (stat == null)
            {
                return new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.StatNotFound
                };
            }
            
            // Convert network modifier type to GC2 type
            var gc2ModType = request.ModifierType == NetworkModifierType.Constant
                ? ModifierType.Constant
                : ModifierType.Percent;
            
            // Apply action
            bool success = true;
            switch (request.Action)
            {
                case ModifierAction.Add:
                    stat.AddModifier(gc2ModType, request.Value);
                    break;
                    
                case ModifierAction.Remove:
                    success = stat.RemoveModifier(gc2ModType, request.Value);
                    break;
            }
            
            if (!success)
            {
                return new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.ModifierNotFound
                };
            }
            
            // Broadcast change
            var broadcast = new NetworkStatModifierBroadcast
            {
                NetworkId = NetworkId,
                StatHash = request.StatHash,
                Action = request.Action,
                ModifierType = request.ModifierType,
                Value = request.Value,
                NewStatValue = (float)stat.Value
            };
            
            NetworkStatsManager.Instance?.BroadcastStatModifierChange(broadcast);
            OnStatModifierChanged?.Invoke(broadcast);
            
            return new NetworkStatModifierResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = StatRejectionReason.None,
                NewStatValue = (float)stat.Value
            };
        }
        
        /// <summary>
        /// [Server] Process a clear status effects request.
        /// </summary>
        public NetworkClearStatusEffectsResponse ProcessClearStatusEffectsRequest(NetworkClearStatusEffectsRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkClearStatusEffectsResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.NotAuthorized
                };
            }
            
            // Validate target
            if (request.TargetNetworkId != NetworkId)
            {
                return new NetworkClearStatusEffectsResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.TargetNotFound
                };
            }
            
            // Clear status effects by type mask
            m_Traits.RuntimeStatusEffects.ClearByType((StatusEffectTypeMask)request.TypeMask);
            
            return new NetworkClearStatusEffectsResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = StatRejectionReason.None
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVE RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Receive stat modification response from server.
        /// </summary>
        public void ReceiveStatModifyResponse(NetworkStatModifyResponse response)
        {
            if (!m_PendingStatMods.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingStatMods.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkStatsController] Stat modify rejected: {response.RejectionReason}");
                }
                
                // Rollback optimistic update
                if (m_RollbackOnReject && m_OptimisticStatValues.TryGetValue(pending.Request.StatHash, out float original))
                {
                    var stat = m_Traits.RuntimeStats.Get(pending.Request.StatHash);
                    if (stat != null)
                    {
                        // Use internal method to avoid triggering network events
                        stat.SetBaseWithoutNotify(original);
                    }
                    m_OptimisticStatValues.Remove(pending.Request.StatHash);
                }
                
                OnModificationRejected?.Invoke(response.RejectionReason, "Stat modification");
            }
            else
            {
                m_OptimisticStatValues.Remove(pending.Request.StatHash);
            }
        }
        
        /// <summary>
        /// [Client] Receive attribute modification response from server.
        /// </summary>
        public void ReceiveAttributeModifyResponse(NetworkAttributeModifyResponse response)
        {
            if (!m_PendingAttrMods.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingAttrMods.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkStatsController] Attribute modify rejected: {response.RejectionReason}");
                }
                
                // Rollback optimistic update
                if (m_RollbackOnReject && m_OptimisticAttrValues.TryGetValue(pending.Request.AttributeHash, out float original))
                {
                    var attr = m_Traits.RuntimeAttributes.Get(pending.Request.AttributeHash);
                    if (attr != null)
                    {
                        attr.SetValueWithoutNotify(original);
                    }
                    m_OptimisticAttrValues.Remove(pending.Request.AttributeHash);
                }
                
                OnModificationRejected?.Invoke(response.RejectionReason, "Attribute modification");
            }
            else
            {
                m_OptimisticAttrValues.Remove(pending.Request.AttributeHash);
            }
        }
        
        /// <summary>
        /// [Client] Receive status effect response from server.
        /// </summary>
        public void ReceiveStatusEffectResponse(NetworkStatusEffectResponse response)
        {
            m_PendingStatusEffects.Remove(response.RequestId);
            
            if (!response.Authorized && m_LogRejections)
            {
                Debug.LogWarning($"[NetworkStatsController] Status effect action rejected: {response.RejectionReason}");
                OnModificationRejected?.Invoke(response.RejectionReason, "Status effect action");
            }
        }
        
        /// <summary>
        /// [Client] Receive stat modifier response from server.
        /// </summary>
        public void ReceiveStatModifierResponse(NetworkStatModifierResponse response)
        {
            m_PendingModifierRequests.Remove(response.RequestId);
            
            if (!response.Authorized && m_LogRejections)
            {
                Debug.LogWarning($"[NetworkStatsController] Stat modifier action rejected: {response.RejectionReason}");
                OnModificationRejected?.Invoke(response.RejectionReason, "Stat modifier action");
            }
        }
        
        /// <summary>
        /// [Client] Receive clear status effects response from server.
        /// </summary>
        public void ReceiveClearStatusEffectsResponse(NetworkClearStatusEffectsResponse response)
        {
            m_PendingClearStatusEffects.Remove(response.RequestId);
            
            if (!response.Authorized && m_LogRejections)
            {
                Debug.LogWarning($"[NetworkStatsController] Clear status effects rejected: {response.RejectionReason}");
                OnModificationRejected?.Invoke(response.RejectionReason, "Clear status effects");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ALL CLIENTS: RECEIVE BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [All] Receive stat change broadcast from server.
        /// </summary>
        public void ReceiveStatChangeBroadcast(NetworkStatChangeBroadcast broadcast)
        {
            if (m_IsServer) return; // Server already has authoritative state
            
            var stat = m_Traits.RuntimeStats.Get(broadcast.StatHash);
            if (stat == null) return;
            
            // Apply server state
            stat.SetBaseWithoutNotify(broadcast.NewBaseValue);
            
            OnStatChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [All] Receive attribute change broadcast from server.
        /// </summary>
        public void ReceiveAttributeChangeBroadcast(NetworkAttributeChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var attr = m_Traits.RuntimeAttributes.Get(broadcast.AttributeHash);
            if (attr == null) return;
            
            // Apply server state
            attr.SetValueWithoutNotify(broadcast.NewValue);
            
            OnAttributeChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [All] Receive status effect change broadcast from server.
        /// </summary>
        public void ReceiveStatusEffectBroadcast(NetworkStatusEffectBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            // Sync status effect state
            OnStatusEffectChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [All] Receive stat modifier change broadcast from server.
        /// </summary>
        public void ReceiveStatModifierBroadcast(NetworkStatModifierBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var stat = m_Traits.RuntimeStats.Get(broadcast.StatHash);
            if (stat == null) return;
            
            // Convert network modifier type to GC2 type
            var gc2ModType = broadcast.ModifierType == NetworkModifierType.Constant
                ? ModifierType.Constant
                : ModifierType.Percent;
            
            // Apply modifier action
            switch (broadcast.Action)
            {
                case ModifierAction.Add:
                    stat.AddModifier(gc2ModType, broadcast.Value);
                    break;
                    
                case ModifierAction.Remove:
                    stat.RemoveModifier(gc2ModType, broadcast.Value);
                    break;
                    
                case ModifierAction.Clear:
                    stat.ClearModifiers();
                    break;
            }
            
            OnStatModifierChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [All] Receive full state snapshot (initial sync or reconnect).
        /// </summary>
        public void ReceiveFullSnapshot(NetworkStatsSnapshot snapshot)
        {
            if (m_IsServer) return;
            
            // Apply all stats
            if (snapshot.Stats != null)
            {
                foreach (var statValue in snapshot.Stats)
                {
                    var stat = m_Traits.RuntimeStats.Get(statValue.StatHash);
                    stat?.SetBaseWithoutNotify(statValue.BaseValue);
                }
            }
            
            // Apply all attributes
            if (snapshot.Attributes != null)
            {
                foreach (var attrValue in snapshot.Attributes)
                {
                    var attr = m_Traits.RuntimeAttributes.Get(attrValue.AttributeHash);
                    attr?.SetValueWithoutNotify(attrValue.CurrentValue);
                }
            }
            
            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkStatsController] Received full snapshot: {snapshot.Stats?.Length ?? 0} stats, {snapshot.Attributes?.Length ?? 0} attributes");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Get full stats snapshot for initial sync.
        /// </summary>
        public NetworkStatsSnapshot GetFullSnapshot()
        {
            var statValues = new List<NetworkStatValue>();
            var attrValues = new List<NetworkAttributeValue>();
            var statusValues = new List<NetworkStatusEffectValue>();
            
            // Collect stats
            foreach (var statHash in m_Traits.RuntimeStats.StatsKeys)
            {
                var stat = m_Traits.RuntimeStats.Get(statHash);
                if (stat != null)
                {
                    statValues.Add(new NetworkStatValue
                    {
                        StatHash = statHash,
                        BaseValue = (float)stat.Base,
                        ComputedValue = (float)stat.Value
                    });
                }
            }
            
            // Collect attributes
            foreach (var attrHash in m_Traits.RuntimeAttributes.AttributesKeys)
            {
                var attr = m_Traits.RuntimeAttributes.Get(attrHash);
                if (attr != null)
                {
                    attrValues.Add(new NetworkAttributeValue
                    {
                        AttributeHash = attrHash,
                        CurrentValue = (float)attr.Value,
                        MaxValue = (float)attr.MaxValue
                    });
                }
            }
            
            // Collect status effects
            foreach (var statusId in m_Traits.RuntimeStatusEffects.GetActiveList())
            {
                int count = m_Traits.RuntimeStatusEffects.GetActiveStackCount(statusId);
                if (count > 0)
                {
                    statusValues.Add(new NetworkStatusEffectValue
                    {
                        StatusEffectHash = statusId.Hash,
                        StackCount = (byte)count,
                        RemainingDuration = GetStatusEffectRemainingDuration(statusId)
                    });
                }
            }
            
            return new NetworkStatsSnapshot
            {
                NetworkId = NetworkId,
                Timestamp = Time.time,
                Stats = statValues.ToArray(),
                Attributes = attrValues.ToArray(),
                StatusEffects = statusValues.ToArray()
            };
        }
        
        private void BroadcastFullState()
        {
            var snapshot = GetFullSnapshot();
            NetworkStatsManager.Instance?.BroadcastFullSnapshot(snapshot);
        }
        
        private void BroadcastDeltaState()
        {
            var changedStats = new List<NetworkStatValue>();
            var changedAttrs = new List<NetworkAttributeValue>();
            uint statMask = 0;
            uint attrMask = 0;
            int statIndex = 0;
            int attrIndex = 0;
            
            // Check for changed stats
            foreach (var statHash in m_Traits.RuntimeStats.StatsKeys)
            {
                var stat = m_Traits.RuntimeStats.Get(statHash);
                if (stat == null) continue;
                
                float currentValue = (float)stat.Value;
                if (!m_LastSyncedStatValues.TryGetValue(statHash, out float lastValue) ||
                    Math.Abs(currentValue - lastValue) > 0.001f)
                {
                    changedStats.Add(new NetworkStatValue
                    {
                        StatHash = statHash,
                        BaseValue = (float)stat.Base,
                        ComputedValue = currentValue
                    });
                    
                    if (statIndex < 32) statMask |= (1u << statIndex);
                    m_LastSyncedStatValues[statHash] = currentValue;
                }
                statIndex++;
            }
            
            // Check for changed attributes
            foreach (var attrHash in m_Traits.RuntimeAttributes.AttributesKeys)
            {
                var attr = m_Traits.RuntimeAttributes.Get(attrHash);
                if (attr == null) continue;
                
                float currentValue = (float)attr.Value;
                if (!m_LastSyncedAttrValues.TryGetValue(attrHash, out float lastValue) ||
                    Math.Abs(currentValue - lastValue) > 0.001f)
                {
                    changedAttrs.Add(new NetworkAttributeValue
                    {
                        AttributeHash = attrHash,
                        CurrentValue = currentValue,
                        MaxValue = (float)attr.MaxValue
                    });
                    
                    if (attrIndex < 32) attrMask |= (1u << attrIndex);
                    m_LastSyncedAttrValues[attrHash] = currentValue;
                }
                attrIndex++;
            }
            
            // Only broadcast if something changed
            if (changedStats.Count > 0 || changedAttrs.Count > 0)
            {
                var delta = new NetworkStatsDelta
                {
                    NetworkId = NetworkId,
                    Timestamp = Time.time,
                    StatChangeMask = statMask,
                    AttributeChangeMask = attrMask,
                    ChangedStats = changedStats.ToArray(),
                    ChangedAttributes = changedAttrs.ToArray()
                };
                
                NetworkStatsManager.Instance?.BroadcastDelta(delta);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void ApplyStatModifyLocally(NetworkStatModifyRequest request)
        {
            var stat = m_Traits.RuntimeStats.Get(request.StatHash);
            if (stat == null) return;
            
            ApplyStatModification(stat, request.ModificationType, request.Value);
        }
        
        private float ApplyStatModification(RuntimeStatData stat, StatModificationType modType, float value)
        {
            float newValue;
            
            switch (modType)
            {
                case StatModificationType.SetBase:
                    stat.Base = value;
                    newValue = value;
                    break;
                    
                case StatModificationType.AddToBase:
                    stat.Base = stat.Base + value;
                    newValue = (float)stat.Base;
                    break;
                    
                case StatModificationType.MultiplyBase:
                    stat.Base = stat.Base * value;
                    newValue = (float)stat.Base;
                    break;
                    
                default:
                    newValue = (float)stat.Base;
                    break;
            }
            
            return newValue;
        }
        
        private void ApplyAttributeModifyLocally(NetworkAttributeModifyRequest request)
        {
            var attr = m_Traits.RuntimeAttributes.Get(request.AttributeHash);
            if (attr == null) return;
            
            ApplyAttributeModification(attr, request.ModificationType, request.Value);
        }
        
        private float ApplyAttributeModification(RuntimeAttributeData attr, AttributeModificationType modType, float value)
        {
            switch (modType)
            {
                case AttributeModificationType.Set:
                    attr.Value = value;
                    break;
                    
                case AttributeModificationType.Add:
                    attr.Value += value;
                    break;
                    
                case AttributeModificationType.SetPercent:
                    attr.Value = attr.MinValue + (attr.MaxValue - attr.MinValue) * value;
                    break;
                    
                case AttributeModificationType.AddPercent:
                    attr.Value += (attr.MaxValue - attr.MinValue) * value;
                    break;
            }
            
            return (float)attr.Value;
        }
        
        private bool CheckRateLimit(int hash, float change, Dictionary<int, float> accumulator)
        {
            if (!accumulator.TryGetValue(hash, out float accumulated))
                accumulated = 0f;
            
            accumulated += Math.Abs(change);
            accumulator[hash] = accumulated;
            
            return accumulated <= m_MaxChangePerSecond;
        }
        
        private void CleanupPendingRequests()
        {
            float timeout = 5f;
            float currentTime = Time.time;
            
            // Cleanup stat requests
            var statKeysToRemove = new List<ushort>();
            foreach (var kvp in m_PendingStatMods)
            {
                if (currentTime - kvp.Value.SentTime > timeout)
                    statKeysToRemove.Add(kvp.Key);
            }
            foreach (var key in statKeysToRemove)
                m_PendingStatMods.Remove(key);
            
            // Cleanup attribute requests
            var attrKeysToRemove = new List<ushort>();
            foreach (var kvp in m_PendingAttrMods)
            {
                if (currentTime - kvp.Value.SentTime > timeout)
                    attrKeysToRemove.Add(kvp.Key);
            }
            foreach (var key in attrKeysToRemove)
                m_PendingAttrMods.Remove(key);
        }
        
        private StatusEffect GetStatusEffectById(int hash)
        {
            // Look up from StatsRepository settings
            return StatsRepository.Get.StatusEffects.Find(hash);
        }
        
        /// <summary>
        /// Gets the remaining duration for the first (oldest) stack of a status effect.
        /// Returns -1 if the effect has no duration or isn't active.
        /// </summary>
        private float GetStatusEffectRemainingDuration(IdString statusEffectId)
        {
            var value = m_Traits.RuntimeStatusEffects.GetActiveAt(statusEffectId, 0);
            return value.HasDuration ? value.TimeRemaining : -1f;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // LOCAL CHANGE DETECTION (for debug/logging)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnLocalStatChanged(IdString statId)
        {
            // This fires when GC2 changes a stat locally
            // On server, this is fine. On client, it should have gone through network
            if (m_LogAllChanges && !m_IsServer)
            {
                Debug.Log($"[NetworkStatsController] Local stat changed: {statId.String}");
            }
        }
        
        private void OnLocalAttributeChanged(IdString attributeId)
        {
            if (m_LogAllChanges && !m_IsServer)
            {
                Debug.Log($"[NetworkStatsController] Local attribute changed: {attributeId.String}");
            }
        }
        
        private void OnLocalStatusEffectChanged(IdString statusEffectId)
        {
            if (m_LogAllChanges && !m_IsServer)
            {
                Debug.Log($"[NetworkStatsController] Local status effect changed: {statusEffectId.String}");
            }
        }
    }
}
#endif
