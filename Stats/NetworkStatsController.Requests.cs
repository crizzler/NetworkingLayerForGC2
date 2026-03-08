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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
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
            m_PendingStatMods[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingStatModify
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
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
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
            m_PendingAttrMods[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingAttributeModify
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
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetNetworkId = NetworkId,
                StatusEffectHash = statusEffectId.Hash,
                Action = action,
                Amount = amount,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingStatusEffects[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingStatusEffectAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatusEffectRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatusEffectRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetNetworkId = NetworkId,
                StatHash = statId.Hash,
                Action = ModifierAction.Add,
                ModifierType = modifierType,
                Value = value,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingModifierRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingStatModifierAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatModifierRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatModifierRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetNetworkId = NetworkId,
                StatHash = statId.Hash,
                Action = ModifierAction.Remove,
                ModifierType = modifierType,
                Value = value,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingModifierRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingStatModifierAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatModifierRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatModifierRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetNetworkId = NetworkId,
                StatHash = 0, // 0 = all stats
                Action = ModifierAction.Clear,
                ModifierType = NetworkModifierType.Constant,
                Value = 0,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingModifierRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingStatModifierAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnStatModifierRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessStatModifierRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
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
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetNetworkId = NetworkId,
                TypeMask = typeMask,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingClearStatusEffects[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingClearStatusEffectsAction
            {
                Request = request,
                SentTime = Time.time
            };
            
            if (m_IsServer)
            {
                var response = ProcessClearStatusEffectsRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveClearStatusEffectsResponse(response);
            }
            else
            {
                NetworkStatsManager.Instance?.SendClearStatusEffectsRequest(request);
            }
        }
        
    }
}
#endif
