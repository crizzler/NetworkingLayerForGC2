using System;
using System.Collections.Generic;
using UnityEngine;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using DaimahouGames.Runtime.Core.Common;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkAbilitiesController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: REQUEST ABILITY CAST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request to cast an ability. Called by client.
        /// </summary>
        /// <param name="casterNetworkId">Network ID of the caster.</param>
        /// <param name="ability">The ability to cast.</param>
        /// <param name="target">Optional target.</param>
        /// <param name="callback">Optional callback when response received.</param>
        public void RequestCastAbility(
            uint casterNetworkId,
            Ability ability,
            Target target = default,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Not initialized.");
                return;
            }
            
            if (ability == null)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Cannot cast null ability.");
                return;
            }
            
            ushort requestId = m_NextRequestId++;
            
            var request = new NetworkAbilityCastRequest
            {
                RequestId = requestId,
                ActorNetworkId = casterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(casterNetworkId, requestId),
                CasterNetworkId = casterNetworkId,
                AbilityIdHash = ability.ID.Hash,
                ClientTime = Time.time,
                TargetType = GetTargetType(target),
                TargetPosition = target.Position,
                TargetNetworkId = GetTargetNetworkId(target),
                AutoConfirm = false
            };
            
            if (m_IsClient && !m_IsServer)
            {
                // Client-only: store pending and send to server
                m_PendingCastRequests[request.CorrelationId] = new PendingCastRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback,
                    Ability = ability
                };
                
                SendCastRequestToServer?.Invoke(request);
                OnCastRequestSent?.Invoke(request);
                
                m_Stats.TotalCastRequests++;
                
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast request sent: {ability.name} (ID: {requestId})");
                }
            }
            else if (m_IsServer)
            {
                // Server or Host: process directly
                ProcessCastRequest(casterNetworkId, request);
            }
        }
        
        /// <summary>
        /// Request to cast ability with auto-confirm (for AI/instructions).
        /// </summary>
        public void RequestCastAbilityAutoConfirm(
            uint casterNetworkId,
            Ability ability,
            Vector3 targetPosition,
            uint targetNetworkId = 0,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Not initialized.");
                return;
            }
            
            ushort requestId = m_NextRequestId++;
            
            byte targetType = (byte)(targetNetworkId != 0 ? 2 : 1);
            
            var request = new NetworkAbilityCastRequest
            {
                RequestId = requestId,
                ActorNetworkId = casterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(casterNetworkId, requestId),
                CasterNetworkId = casterNetworkId,
                AbilityIdHash = ability.ID.Hash,
                ClientTime = Time.time,
                TargetType = targetType,
                TargetPosition = targetPosition,
                TargetNetworkId = targetNetworkId,
                AutoConfirm = true
            };
            
            if (m_IsClient && !m_IsServer)
            {
                m_PendingCastRequests[request.CorrelationId] = new PendingCastRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback,
                    Ability = ability
                };
                
                SendCastRequestToServer?.Invoke(request);
                OnCastRequestSent?.Invoke(request);
                m_Stats.TotalCastRequests++;
            }
            else if (m_IsServer)
            {
                ProcessCastRequest(casterNetworkId, request);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: ABILITY LEARNING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request to learn an ability into a slot.
        /// </summary>
        public void RequestLearnAbility(
            uint characterNetworkId,
            Ability ability,
            int slot,
            Action<NetworkAbilityLearnResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer) return;
            if (ability == null) return;
            
            ushort requestId = m_NextRequestId++;
            
            var request = new NetworkAbilityLearnRequest
            {
                RequestId = requestId,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, requestId),
                CharacterNetworkId = characterNetworkId,
                AbilityIdHash = ability.ID.Hash,
                Slot = (sbyte)slot,
                IsLearning = true
            };
            
            if (m_IsClient && !m_IsServer)
            {
                m_PendingLearnRequests[request.CorrelationId] = new PendingLearnRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback
                };
                
                SendLearnRequestToServer?.Invoke(request);
                OnLearnRequestSent?.Invoke(request);
            }
            else if (m_IsServer)
            {
                ProcessLearnRequest(characterNetworkId, request);
            }
        }
        
        /// <summary>
        /// Request to unlearn an ability from a slot.
        /// </summary>
        public void RequestUnlearnAbility(
            uint characterNetworkId,
            int slot,
            Action<NetworkAbilityLearnResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer) return;
            
            ushort requestId = m_NextRequestId++;
            
            var request = new NetworkAbilityLearnRequest
            {
                RequestId = requestId,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, requestId),
                CharacterNetworkId = characterNetworkId,
                AbilityIdHash = 0,
                Slot = (sbyte)slot,
                IsLearning = false
            };
            
            if (m_IsClient && !m_IsServer)
            {
                m_PendingLearnRequests[request.CorrelationId] = new PendingLearnRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback
                };
                
                SendLearnRequestToServer?.Invoke(request);
            }
            else if (m_IsServer)
            {
                ProcessLearnRequest(characterNetworkId, request);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVE RESPONSES AND BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Client receives cast response from server.
        /// </summary>
        public void ReceiveCastResponse(NetworkAbilityCastResponse response)
        {
            if (!m_IsClient) return;
            
            OnCastResponseReceived?.Invoke(response);
            
            uint pendingKey = response.CorrelationId != 0 ? response.CorrelationId : response.RequestId;
            if (m_PendingCastRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingCastRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
                
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast response received: " +
                              $"Approved={response.Approved}, Reason={response.RejectReason}");
                }
            }
        }
        
        /// <summary>
        /// Client receives cast broadcast (for other players' casts).
        /// </summary>
        public void ReceiveCastBroadcast(NetworkAbilityCastBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnCastBroadcastReceived?.Invoke(broadcast);
            
            // If this is for a remote player, play animations/VFX
            uint localId = GetLocalPlayerNetworkId?.Invoke() ?? 0;
            if (broadcast.CasterNetworkId != localId)
            {
                // Remote player casting - trigger visual representation
                HandleRemoteCast(broadcast);
            }
        }
        
        private void HandleRemoteCast(NetworkAbilityCastBroadcast broadcast)
        {
            // Get the pawn/character
            Pawn pawn = GetPawnByNetworkId?.Invoke(broadcast.CasterNetworkId);
            if (pawn == null) return;
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return;
            
            Ability ability = GetAbilityByHash?.Invoke(broadcast.AbilityIdHash);
            if (ability == null) return;
            
            switch (broadcast.CastState)
            {
                case AbilityCastState.Started:
                    // Play cast start animation/effects on remote
                    if (m_DebugLog)
                    {
                        Debug.Log($"[NetworkAbilitiesController] Remote cast started: {ability.name}");
                    }
                    break;
                    
                case AbilityCastState.Triggered:
                    // Effects triggered
                    break;
                    
                case AbilityCastState.Completed:
                case AbilityCastState.Canceled:
                    // Cleanup
                    break;
            }
        }
        
        /// <summary>
        /// Client receives cooldown broadcast.
        /// </summary>
        public void ReceiveCooldownBroadcast(NetworkCooldownBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnCooldownBroadcastReceived?.Invoke(broadcast);
            
            // Sync cooldown locally
            Pawn pawn = GetPawnByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (pawn == null) return;
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return;
            
            Cooldowns cooldowns = caster.Get<Cooldowns>();
            if (cooldowns == null) return;
            
            Ability ability = GetAbilityByHash?.Invoke(broadcast.AbilityIdHash);
            if (ability == null) return;
            
            // Apply the server's cooldown
            if (broadcast.CooldownEndTime > 0)
            {
                float remainingDuration = broadcast.CooldownEndTime - (GetServerTime?.Invoke() ?? Time.time);
                if (remainingDuration > 0)
                {
                    cooldowns.AddCooldown(ability.ID, remainingDuration);
                }
            }
        }
        
        /// <summary>
        /// Client receives learn response.
        /// </summary>
        public void ReceiveLearnResponse(NetworkAbilityLearnResponse response)
        {
            if (!m_IsClient) return;
            
            OnLearnResponseReceived?.Invoke(response);
            
            uint pendingKey = response.CorrelationId != 0 ? response.CorrelationId : response.RequestId;
            if (m_PendingLearnRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingLearnRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
        }
        
        /// <summary>
        /// Client receives learn broadcast.
        /// </summary>
        public void ReceiveLearnBroadcast(NetworkAbilityLearnBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnLearnBroadcastReceived?.Invoke(broadcast);
            
            // Sync locally
            Pawn pawn = GetPawnByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (pawn == null) return;
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return;
            
            if (broadcast.IsLearned)
            {
                Ability ability = GetAbilityByHash?.Invoke(broadcast.AbilityIdHash);
                if (ability != null)
                {
                    caster.Learn(ability, broadcast.Slot);
                }
            }
            else
            {
                caster.UnLearn(broadcast.Slot);
            }
        }
        
        /// <summary>
        /// Client receives projectile spawn broadcast.
        /// </summary>
        public void ReceiveProjectileSpawnBroadcast(NetworkProjectileSpawnBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnProjectileSpawnReceived?.Invoke(broadcast);
            
            // Spawn visual projectile on client
            Projectile projectileSO = GetProjectileByHash?.Invoke(broadcast.ProjectileHash);
            if (projectileSO == null) return;
            
            // Calculate lag-compensated position
            float timeSinceSpawn = (GetServerTime?.Invoke() ?? Time.time) - broadcast.ServerTime;
            Vector3 compensatedPosition = broadcast.SpawnPosition + broadcast.Direction * timeSinceSpawn * 5f; // Estimate speed
            
            RuntimeProjectile instance = projectileSO.Get(
                new Args((GameObject)null),
                compensatedPosition,
                Quaternion.LookRotation(broadcast.Direction)
            );
            
            if (instance != null)
            {
                // Initialize with broadcast data
                var extendedArgs = new ExtendedArgs(instance.gameObject);
                
                if (broadcast.TargetNetworkId != 0)
                {
                    Pawn targetPawn = GetPawnByNetworkId?.Invoke(broadcast.TargetNetworkId);
                    if (targetPawn != null)
                    {
                        extendedArgs.Set(new Target(targetPawn));
                    }
                }
                else
                {
                    extendedArgs.Set(new Target(broadcast.TargetPosition));
                }
                
                instance.Initialize(extendedArgs, broadcast.Direction);
            }
        }
        
        /// <summary>
        /// Client receives impact spawn broadcast.
        /// </summary>
        public void ReceiveImpactSpawnBroadcast(NetworkImpactSpawnBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnImpactSpawnReceived?.Invoke(broadcast);
            
            Impact impactSO = GetImpactByHash?.Invoke(broadcast.ImpactHash);
            if (impactSO == null) return;
            
            RuntimeImpact instance = impactSO.Get(
                new Args((GameObject)null),
                broadcast.Position,
                broadcast.Rotation
            );
            
            // Client-side impacts are visual only - server handles actual effects
        }
    }
}
