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
    public partial class NetworkStatsController : MonoBehaviour
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
        private ushort m_LastIssuedRequestId = 1;
        private static readonly List<ulong> s_SharedKeyBuffer = new(16);
        private readonly Dictionary<ulong, PendingStatModify> m_PendingStatMods = new(16);
        private readonly Dictionary<ulong, PendingAttributeModify> m_PendingAttrMods = new(16);
        private readonly Dictionary<ulong, PendingStatusEffectAction> m_PendingStatusEffects = new(8);
        private readonly Dictionary<ulong, PendingStatModifierAction> m_PendingModifierRequests = new(8);
        private readonly Dictionary<ulong, PendingClearStatusEffectsAction> m_PendingClearStatusEffects = new(4);
        
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
            foreach (var statHash in EnumerateRuntimeStatHashes())
            {
                var stat = GetRuntimeStatByHash(statHash);
                if (stat != null)
                {
                    m_LastSyncedStatValues[statHash] = (float)stat.Value;
                }
            }
            
            // Cache initial attribute values
            foreach (var attrHash in EnumerateRuntimeAttributeHashes())
            {
                var attr = GetRuntimeAttributeByHash(attrHash);
                if (attr != null)
                {
                    m_LastSyncedAttrValues[attrHash] = (float)attr.Value;
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void ApplyStatModifyLocally(NetworkStatModifyRequest request)
        {
            var stat = GetRuntimeStatByHash(request.StatHash);
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
            var attr = GetRuntimeAttributeByHash(request.AttributeHash);
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

        private ushort GetNextRequestId()
        {
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            ushort requestId = m_NextRequestId;
            m_NextRequestId++;
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            m_LastIssuedRequestId = requestId;
            return requestId;
        }

        private static ulong GetPendingKey(uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }
        
        private void CleanupPendingRequests()
        {
            float timeout = 5f;
            float currentTime = Time.time;
            
            // Cleanup stat requests (pooled list avoids per-frame GC allocation)
            s_SharedKeyBuffer.Clear();
            foreach (var kvp in m_PendingStatMods)
            {
                if (currentTime - kvp.Value.SentTime > timeout)
                    s_SharedKeyBuffer.Add(kvp.Key);
            }
            foreach (var key in s_SharedKeyBuffer)
                m_PendingStatMods.Remove(key);
            
            // Cleanup attribute requests
            s_SharedKeyBuffer.Clear();
            foreach (var kvp in m_PendingAttrMods)
            {
                if (currentTime - kvp.Value.SentTime > timeout)
                    s_SharedKeyBuffer.Add(kvp.Key);
            }
            foreach (var key in s_SharedKeyBuffer)
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
