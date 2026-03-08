#if GC2_STATS
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;

namespace Arawn.GameCreator2.Networking.Stats
{
    public partial class NetworkStatsController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVE RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Receive stat modification response from server.
        /// </summary>
        public void ReceiveStatModifyResponse(NetworkStatModifyResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingStatMods.TryGetValue(key, out var pending))
                return;
            
            m_PendingStatMods.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkStatsController] Stat modify rejected: {response.RejectionReason}");
                }
                
                // Rollback optimistic update
                if (m_RollbackOnReject && m_OptimisticStatValues.TryGetValue(pending.Request.StatHash, out float original))
                {
                    var stat = GetRuntimeStatByHash(pending.Request.StatHash);
                    if (stat != null)
                    {
                        SetStatBaseSilently(stat, original);
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
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingAttrMods.TryGetValue(key, out var pending))
                return;
            
            m_PendingAttrMods.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkStatsController] Attribute modify rejected: {response.RejectionReason}");
                }
                
                // Rollback optimistic update
                if (m_RollbackOnReject && m_OptimisticAttrValues.TryGetValue(pending.Request.AttributeHash, out float original))
                {
                    var attr = GetRuntimeAttributeByHash(pending.Request.AttributeHash);
                    if (attr != null)
                    {
                        SetAttributeValueSilently(attr, original);
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
            m_PendingStatusEffects.Remove(GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId));
            
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
            m_PendingModifierRequests.Remove(GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId));
            
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
            m_PendingClearStatusEffects.Remove(GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId));
            
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
            
            var stat = GetRuntimeStatByHash(broadcast.StatHash);
            if (stat == null) return;
            
            // Apply server state
            SetStatBaseSilently(stat, broadcast.NewBaseValue);
            
            OnStatChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [All] Receive attribute change broadcast from server.
        /// </summary>
        public void ReceiveAttributeChangeBroadcast(NetworkAttributeChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var attr = GetRuntimeAttributeByHash(broadcast.AttributeHash);
            if (attr == null) return;
            
            // Apply server state
            SetAttributeValueSilently(attr, broadcast.NewValue);
            
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
            
            var stat = GetRuntimeStatByHash(broadcast.StatHash);
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
                    var stat = GetRuntimeStatByHash(statValue.StatHash);
                    SetStatBaseSilently(stat, statValue.BaseValue);
                }
            }
            
            // Apply all attributes
            if (snapshot.Attributes != null)
            {
                foreach (var attrValue in snapshot.Attributes)
                {
                    var attr = GetRuntimeAttributeByHash(attrValue.AttributeHash);
                    SetAttributeValueSilently(attr, attrValue.CurrentValue);
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
            foreach (var statHash in EnumerateRuntimeStatHashes())
            {
                var stat = GetRuntimeStatByHash(statHash);
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
            foreach (var attrHash in EnumerateRuntimeAttributeHashes())
            {
                var attr = GetRuntimeAttributeByHash(attrHash);
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
            foreach (var statHash in EnumerateRuntimeStatHashes())
            {
                var stat = GetRuntimeStatByHash(statHash);
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
            foreach (var attrHash in EnumerateRuntimeAttributeHashes())
            {
                var attr = GetRuntimeAttributeByHash(attrHash);
                if (attr == null) continue;
                
                float currentValue = (float)attr.Value;
                bool hasChanged = !m_LastSyncedAttrValues.TryGetValue(attrHash, out float lastValue) ||
                    Math.Abs(currentValue - lastValue) > 0.001f;

                if (!m_SmartDeltaSync || hasChanged)
                {
                    changedAttrs.Add(new NetworkAttributeValue
                    {
                        AttributeHash = attrHash,
                        CurrentValue = currentValue,
                        MaxValue = (float)attr.MaxValue
                    });
                    
                    if (attrIndex < 32) attrMask |= (1u << attrIndex);
                }

                m_LastSyncedAttrValues[attrHash] = currentValue;
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
        
    }
}
#endif
