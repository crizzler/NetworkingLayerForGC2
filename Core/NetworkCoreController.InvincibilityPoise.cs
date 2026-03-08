using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkCoreController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVINCIBILITY - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to set invincibility.
        /// </summary>
        public void RequestSetInvincibility(uint characterNetworkId, float duration,
            Action<NetworkInvincibilityResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkInvincibilityRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                Duration = duration,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingInvincibilityRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingInvincibilityRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.InvincibilityRequestsSent++;
            SendInvincibilityRequestToServer?.Invoke(request);
            OnInvincibilityRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVINCIBILITY - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process invincibility request from client.
        /// </summary>
        public void ProcessInvincibilityRequest(uint senderNetworkId, NetworkInvincibilityRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.InvincibilityRequestsReceived++;
            OnInvincibilityRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendInvincibilityResponse(senderNetworkId, request.RequestId, false, 
                    InvincibilityRejectReason.CharacterNotFound, 0, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Check cooldown
            if (m_InvincibilityCooldowns.TryGetValue(request.CharacterNetworkId, out float cooldownEnd) 
                && currentTime < cooldownEnd)
            {
                SendInvincibilityResponse(senderNetworkId, request.RequestId, false,
                    InvincibilityRejectReason.OnCooldown, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InvincibilityRejected++;
                return;
            }
            
            // Validate duration
            float approvedDuration = Mathf.Clamp(request.Duration, 0, m_MaxInvincibilityDuration);
            if (approvedDuration <= 0)
            {
                // Cancelling invincibility
                if (!character.Combat.Invincibility.IsInvincible)
                {
                    SendInvincibilityResponse(senderNetworkId, request.RequestId, false,
                        InvincibilityRejectReason.NotInvincible, 0, request.ActorNetworkId, request.CorrelationId);
                    m_Stats.InvincibilityRejected++;
                    return;
                }
            }
            
            // Apply invincibility
            character.Combat.Invincibility.Set(approvedDuration);
            
            // Update cooldown
            m_InvincibilityCooldowns[request.CharacterNetworkId] = currentTime + approvedDuration + m_InvincibilityCooldown;
            
            // Send response
            SendInvincibilityResponse(senderNetworkId, request.RequestId, true,
                InvincibilityRejectReason.None, approvedDuration, request.ActorNetworkId, request.CorrelationId);
            m_Stats.InvincibilityApproved++;
            
            // Broadcast
            var broadcast = new NetworkInvincibilityBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                IsInvincible = approvedDuration > 0,
                StartTime = currentTime,
                Duration = approvedDuration
            };
            
            BroadcastInvincibilityToClients?.Invoke(broadcast);
        }
        
        private void SendInvincibilityResponse(uint clientId, ushort requestId, bool approved,
            InvincibilityRejectReason reason, float duration, uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkInvincibilityResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                ApprovedDuration = duration
            };
            
            SendInvincibilityResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle invincibility response from server.
        /// </summary>
        public void ReceiveInvincibilityResponse(NetworkInvincibilityResponse response)
        {
            if (!m_IsClient) return;
            
            ulong pendingKey = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingInvincibilityRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingInvincibilityRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnInvincibilityResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle invincibility broadcast from server.
        /// </summary>
        public void ReceiveInvincibilityBroadcast(NetworkInvincibilityBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            character.Combat.Invincibility.Set(broadcast.Duration);
            OnInvincibilityBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // POISE - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to damage poise.
        /// </summary>
        public void RequestPoiseDamage(uint characterNetworkId, float damage,
            Action<NetworkPoiseResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPoiseRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ActionType = PoiseActionType.Damage,
                Value = damage,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingPoiseRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingPoiseRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PoiseRequestsSent++;
            SendPoiseRequestToServer?.Invoke(request);
            OnPoiseRequestSent?.Invoke(request);
        }
        
        /// <summary>
        /// [Client] Request to reset poise.
        /// </summary>
        public void RequestPoiseReset(uint characterNetworkId, float value = -1,
            Action<NetworkPoiseResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPoiseRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ActionType = value < 0 ? PoiseActionType.Reset : PoiseActionType.Set,
                Value = value,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingPoiseRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingPoiseRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PoiseRequestsSent++;
            SendPoiseRequestToServer?.Invoke(request);
            OnPoiseRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // POISE - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process poise request from client.
        /// </summary>
        public void ProcessPoiseRequest(uint senderNetworkId, NetworkPoiseRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.PoiseRequestsReceived++;
            OnPoiseRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendPoiseResponse(senderNetworkId, request.RequestId, false,
                    PoiseRejectReason.CharacterNotFound, 0, false, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            var poise = character.Combat.Poise;
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Apply poise action
            switch (request.ActionType)
            {
                case PoiseActionType.Damage:
                    if (m_ValidatePoiseDamage && request.Value < 0)
                    {
                        SendPoiseResponse(senderNetworkId, request.RequestId, false,
                            PoiseRejectReason.InvalidValue, poise.Current, poise.IsBroken, request.ActorNetworkId, request.CorrelationId);
                        m_Stats.PoiseRejected++;
                        return;
                    }
                    poise.Damage(request.Value);
                    break;
                    
                case PoiseActionType.Set:
                    poise.Set(request.Value);
                    break;
                    
                case PoiseActionType.Reset:
                    poise.Reset(poise.Maximum);
                    break;
                    
                case PoiseActionType.Add:
                    poise.Set(poise.Current + request.Value);
                    break;
            }
            
            // Send response
            SendPoiseResponse(senderNetworkId, request.RequestId, true,
                PoiseRejectReason.None, poise.Current, poise.IsBroken, request.ActorNetworkId, request.CorrelationId);
            m_Stats.PoiseApproved++;
            
            // Broadcast
            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                CurrentPoise = poise.Current,
                MaximumPoise = poise.Maximum,
                IsBroken = poise.IsBroken,
                ServerTime = currentTime
            };
            
            BroadcastPoiseToClients?.Invoke(broadcast);
        }
        
        private void SendPoiseResponse(uint clientId, ushort requestId, bool approved,
            PoiseRejectReason reason, float currentPoise, bool isBroken, uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkPoiseResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                CurrentPoise = currentPoise,
                IsBroken = isBroken
            };
            
            SendPoiseResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle poise response from server.
        /// </summary>
        public void ReceivePoiseResponse(NetworkPoiseResponse response)
        {
            if (!m_IsClient) return;
            
            ulong pendingKey = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingPoiseRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingPoiseRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnPoiseResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle poise broadcast from server.
        /// </summary>
        public void ReceivePoiseBroadcast(NetworkPoiseBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            var poise = character.Combat.Poise;
            poise.Set(broadcast.CurrentPoise);
            
            OnPoiseBroadcastReceived?.Invoke(broadcast);
        }
        
    }
}
