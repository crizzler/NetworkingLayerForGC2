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
        // BUSY - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to set busy state on limbs.
        /// </summary>
        public void RequestSetBusy(uint characterNetworkId, BusyLimbs limbs, bool setBusy,
            float timeout = 0, Action<NetworkBusyResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkBusyRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                Limbs = limbs,
                SetBusy = setBusy,
                Timeout = timeout
            };
            
            m_PendingBusyRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingBusyRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            SendBusyRequestToServer?.Invoke(request);
            OnBusyRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BUSY - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process busy request from client.
        /// </summary>
        public void ProcessBusyRequest(uint senderNetworkId, NetworkBusyRequest request)
        {
            if (!m_IsServer) return;
            
            OnBusyRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendBusyResponse(senderNetworkId, request.RequestId, false, BusyRejectReason.CharacterNotFound, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            var busy = character.Busy;
            
            // Apply busy state using GC2's Busy system
            // Convert our BusyLimbs to GC2's Busy.Limb enum
            GameCreator.Runtime.Characters.Busy.Limb gc2Limbs = 0;
            if ((request.Limbs & BusyLimbs.ArmLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmLeft;
            if ((request.Limbs & BusyLimbs.ArmRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmRight;
            if ((request.Limbs & BusyLimbs.LegLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegLeft;
            if ((request.Limbs & BusyLimbs.LegRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegRight;
            
            if (request.SetBusy)
            {
                if (request.Timeout > 0)
                {
                    _ = busy.Timeout(gc2Limbs, request.Timeout); // Fire-and-forget async
                }
                else
                {
                    busy.AddState(gc2Limbs);
                }
            }
            else
            {
                busy.RemoveState(gc2Limbs);
            }
            
            // Send response
            SendBusyResponse(senderNetworkId, request.RequestId, true, BusyRejectReason.None, request.ActorNetworkId, request.CorrelationId);
            
            // Broadcast
            BusyLimbs currentBusy = 0;
            if (busy.AreArmsBusy) currentBusy |= BusyLimbs.Arms;
            if (busy.AreLegsBusy) currentBusy |= BusyLimbs.Legs;
            
            var broadcast = new NetworkBusyBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                CurrentBusyLimbs = currentBusy,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastBusyToClients?.Invoke(broadcast);
        }
        
        private void SendBusyResponse(uint clientId, ushort requestId, bool approved, BusyRejectReason reason,
            uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkBusyResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason
            };
            
            SendBusyResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle busy response from server.
        /// </summary>
        public void ReceiveBusyResponse(NetworkBusyResponse response)
        {
            if (!m_IsClient) return;
            
            ulong pendingKey = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingBusyRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingBusyRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnBusyResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle busy broadcast from server.
        /// </summary>
        public void ReceiveBusyBroadcast(NetworkBusyBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            var busy = character.Busy;
            
            // Sync busy state
            GameCreator.Runtime.Characters.Busy.Limb gc2Limbs = 0;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.ArmLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmLeft;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.ArmRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmRight;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.LegLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegLeft;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.LegRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegRight;
            
            // Clear all then set current
            busy.RemoveState(GameCreator.Runtime.Characters.Busy.Limb.Every);
            if (gc2Limbs != 0)
            {
                busy.AddState(gc2Limbs);
            }
            
            OnBusyBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTERACTION - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to interact with a target.
        /// </summary>
        public void RequestInteraction(uint characterNetworkId, uint targetNetworkId, int targetHash,
            Vector3 interactionPosition, Action<NetworkInteractionResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkInteractionRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                TargetNetworkId = targetNetworkId,
                TargetHash = targetHash,
                InteractionPosition = interactionPosition,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingInteractionRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingInteractionRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.InteractionRequestsSent++;
            SendInteractionRequestToServer?.Invoke(request);
            OnInteractionRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTERACTION - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process interaction request from client.
        /// </summary>
        public void ProcessInteractionRequest(uint senderNetworkId, NetworkInteractionRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.InteractionRequestsReceived++;
            OnInteractionRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.CharacterNotFound, 0, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Check interaction cooldown
            var cooldownKey = (request.CharacterNetworkId, request.TargetNetworkId);
            if (m_InteractionCooldowns.TryGetValue(cooldownKey, out float cooldownEnd) && currentTime < cooldownEnd)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.OnCooldown, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InteractionRejected++;
                return;
            }
            
            // Validate range
            float distance = Vector3.Distance(character.transform.position, request.InteractionPosition);
            if (distance > m_MaxInteractionRange)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.OutOfRange, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InteractionRejected++;
                return;
            }
            
            // Check if character can interact
            if (!character.Interaction.CanInteract)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.CharacterBusy, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InteractionRejected++;
                return;
            }
            
            // Perform interaction
            // Note: Full implementation would resolve target and call character.Interaction.Interact()
            // This is simplified - actual interaction target resolution depends on game implementation
            
            // Update cooldown
            m_InteractionCooldowns[cooldownKey] = currentTime + m_InteractionCooldown;
            
            // Send response
            SendInteractionResponse(senderNetworkId, request.RequestId, true,
                InteractionRejectReason.None, 0, request.ActorNetworkId, request.CorrelationId);
            m_Stats.InteractionApproved++;
            
            // Broadcast
            var broadcast = new NetworkInteractionBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                TargetNetworkId = request.TargetNetworkId,
                TargetHash = request.TargetHash,
                InteractionType = InteractionType.Generic,
                ServerTime = currentTime
            };
            
            BroadcastInteractionToClients?.Invoke(broadcast);
        }
        
        private void SendInteractionResponse(uint clientId, ushort requestId, bool approved,
            InteractionRejectReason reason, int resultData, uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkInteractionResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                ResultData = resultData
            };
            
            SendInteractionResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle interaction response from server.
        /// </summary>
        public void ReceiveInteractionResponse(NetworkInteractionResponse response)
        {
            if (!m_IsClient) return;
            
            ulong pendingKey = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingInteractionRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingInteractionRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnInteractionResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle interaction broadcast from server.
        /// </summary>
        public void ReceiveInteractionBroadcast(NetworkInteractionBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            // Interaction effects/animations can be triggered here
            OnInteractionBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-AUTHORITATIVE DIRECT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Directly start ragdoll and broadcast (bypasses client request).
        /// </summary>
        public void ServerStartRagdoll(uint characterNetworkId, Vector3 force = default, Vector3 forcePoint = default)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            var actionType = force != default ? RagdollActionType.StartRagdollWithForce : RagdollActionType.StartRagdoll;
            
            var request = new NetworkRagdollRequest
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = actionType,
                Force = force,
                ForcePoint = forcePoint
            };
            
            ApplyRagdollAction(character, request);
            
            var broadcast = new NetworkRagdollBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = actionType,
                ServerTime = GetServerTime?.Invoke() ?? Time.time,
                Force = force,
                ForcePoint = forcePoint
            };
            
            BroadcastRagdollToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Server] Directly set invincibility and broadcast.
        /// </summary>
        public void ServerSetInvincibility(uint characterNetworkId, float duration)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            character.Combat.Invincibility.Set(duration);
            
            var broadcast = new NetworkInvincibilityBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                IsInvincible = duration > 0,
                StartTime = GetServerTime?.Invoke() ?? Time.time,
                Duration = duration
            };
            
            BroadcastInvincibilityToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Server] Directly damage poise and broadcast.
        /// </summary>
        public void ServerDamagePoise(uint characterNetworkId, float damage)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            var poise = character.Combat.Poise;
            poise.Damage(damage);
            
            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                CurrentPoise = poise.Current,
                MaximumPoise = poise.Maximum,
                IsBroken = poise.IsBroken,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastPoiseToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Server] Directly reset poise and broadcast.
        /// </summary>
        public void ServerResetPoise(uint characterNetworkId)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            var poise = character.Combat.Poise;
            poise.Reset(poise.Maximum);
            
            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                CurrentPoise = poise.Current,
                MaximumPoise = poise.Maximum,
                IsBroken = poise.IsBroken,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastPoiseToClients?.Invoke(broadcast);
        }
    }
}
