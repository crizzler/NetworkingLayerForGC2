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
            var stat = GetRuntimeStatByHash(request.StatHash);
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
            var attr = GetRuntimeAttributeByHash(request.AttributeHash);
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
            float oldValue = (float)attr.Value;
            float newValue = ApplyAttributeModification(attr, request.ModificationType, request.Value);
            
            // Broadcast change
            var broadcast = new NetworkAttributeChangeBroadcast
            {
                NetworkId = NetworkId,
                AttributeHash = request.AttributeHash,
                NewValue = (float)attr.Value,
                MaxValue = (float)attr.MaxValue,
                Change = newValue - oldValue
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
            var stat = GetRuntimeStatByHash(request.StatHash);
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
        
    }
}
#endif
