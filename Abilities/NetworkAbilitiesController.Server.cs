using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using DaimahouGames.Runtime.Core.Common;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkAbilitiesController
    {
        private static NetworkRequestContext BuildContext(uint actorNetworkId, uint correlationId)
        {
            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private bool ValidateAbilitiesRequest(uint senderClientId, uint actorNetworkId, uint correlationId, string requestType)
        {
            return SecurityIntegration.ValidateModuleRequest(
                senderClientId,
                BuildContext(actorNetworkId, correlationId),
                "Abilities",
                requestType);
        }

        private static AbilityCastRejectReason GetCastSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? AbilityCastRejectReason.ProtocolMismatch
                : AbilityCastRejectReason.SecurityViolation;
        }

        private static AbilityLearnRejectReason GetLearnSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? AbilityLearnRejectReason.ProtocolMismatch
                : AbilityLearnRejectReason.SecurityViolation;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS CAST REQUEST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Process incoming cast request from a client.
        /// Called by network manager when server receives cast request.
        /// </summary>
        public void ProcessCastRequest(uint clientId, NetworkAbilityCastRequest request)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesController] ProcessCastRequest called on non-server.");
                return;
            }
            
            OnCastRequestReceived?.Invoke(clientId, request);
            m_Stats.TotalCastRequests++;

            if (!ValidateAbilitiesRequest(clientId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkAbilityCastRequest)))
            {
                SendCastRejection(
                    clientId,
                    request.RequestId,
                    request.ActorNetworkId,
                    request.CorrelationId,
                    GetCastSecurityRejection(request.ActorNetworkId, request.CorrelationId));
                return;
            }

            if (request.CasterNetworkId == 0 || request.CasterNetworkId != request.ActorNetworkId)
            {
                SendCastRejection(
                    clientId,
                    request.RequestId,
                    request.ActorNetworkId,
                    request.CorrelationId,
                    AbilityCastRejectReason.ProtocolMismatch);
                return;
            }

            uint casterNetworkId = request.ActorNetworkId;
            
            // Validate caster exists
            Pawn casterPawn = GetPawnByNetworkId?.Invoke(casterNetworkId);
            if (casterPawn == null)
            {
                SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.CasterNotFound);
                return;
            }
            
            // Get or create caster state
            if (!m_CasterStates.TryGetValue(casterNetworkId, out var casterState))
            {
                casterState = new CasterState
                {
                    NetworkId = casterNetworkId,
                    Pawn = casterPawn,
                    Caster = casterPawn.GetFeature<Caster>()
                };
                m_CasterStates[casterNetworkId] = casterState;
            }
            
            if (casterState.Caster == null)
            {
                SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.CasterNotFound);
                return;
            }
            
            // Validate ability
            Ability ability = GetAbilityByHash?.Invoke(request.AbilityIdHash);
            if (ability == null)
            {
                SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.AbilityNotKnown);
                return;
            }
            
            // Check if ability is known (in a slot)
            bool abilityKnown = false;
            for (int i = 0; i < 10; i++) // Check up to 10 slots
            {
                var slottedAbility = casterState.Caster.GetSlottedAbility(i);
                if (slottedAbility != null && slottedAbility.ID.Hash == request.AbilityIdHash)
                {
                    abilityKnown = true;
                    break;
                }
            }
            
            if (!abilityKnown)
            {
                SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.AbilityNotKnown);
                m_Stats.RejectedRequirements++;
                return;
            }
            
            // Check cooldown
            var cooldownKey = (casterNetworkId, request.AbilityIdHash);
            if (m_Cooldowns.TryGetValue(cooldownKey, out var cooldownData))
            {
                float serverTime = GetServerTime?.Invoke() ?? Time.time;
                if (serverTime < cooldownData.EndTime)
                {
                    SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.OnCooldown);
                    m_Stats.RejectedOnCooldown++;
                    return;
                }
            }
            
            // Check cast queue
            if (casterState.CastQueueCount >= m_MaxCastQueueSize)
            {
                SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.AlreadyCasting);
                m_Stats.RejectedAlreadyCasting++;
                return;
            }
            
            // Validate requirements using runtime ability
            RuntimeAbility runtimeAbility = casterState.Caster.GetRuntimeAbility(ability);
            var args = new ExtendedArgs(casterState.Pawn.gameObject);
            args.Set(runtimeAbility);
            
            if (!runtimeAbility.CanUse(args, out var failedRequirement))
            {
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast rejected: requirement not met - {failedRequirement?.Title}");
                }
                SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.RequirementNotMet);
                m_Stats.RejectedRequirements++;
                return;
            }
            
            // Validate target if needed
            if (request.TargetType > 0)
            {
                Target target = ReconstructTarget(request);
                if (IsValidTarget(target))
                {
                    args.Set(target);
                }
                
                // Range check
                if (runtimeAbility.Targeting.ShouldCheckRange)
                {
                    float range = (float)runtimeAbility.GetRange(args) + m_RangeValidationGrace;
                    float distance = Vector3.Distance(casterState.Pawn.Position, request.TargetPosition);
                    
                    if (distance > range)
                    {
                        SendCastRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityCastRejectReason.OutOfRange);
                        m_Stats.RejectedOutOfRange++;
                        return;
                    }
                }
            }
            
            // === APPROVED ===
            
            uint castInstanceId = m_NextCastInstanceId++;
            float serverTime2 = GetServerTime?.Invoke() ?? Time.time;
            
            // Create active cast tracking
            var activeCast = new ActiveCast
            {
                CastInstanceId = castInstanceId,
                CasterNetworkId = request.CasterNetworkId,
                AbilityIdHash = request.AbilityIdHash,
                Ability = ability,
                RuntimeAbility = runtimeAbility,
                State = AbilityCastState.Started,
                StartTime = serverTime2,
                TargetType = request.TargetType,
                TargetPosition = request.TargetPosition,
                TargetNetworkId = request.TargetNetworkId
            };
            
            m_ActiveCasts[castInstanceId] = activeCast;
            casterState.ActiveCastIds.Add(castInstanceId);
            casterState.CastQueueCount++;
            
            // Send approval
            var response = new NetworkAbilityCastResponse
            {
                RequestId = request.RequestId,
                CastInstanceId = castInstanceId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Approved = true,
                RejectReason = AbilityCastRejectReason.None,
                CooldownEndTime = 0 // Will be set after cast completes
            };
            
            SendCastResponseToClient?.Invoke(clientId, response);
            m_Stats.ApprovedCasts++;
            
            // Broadcast cast start to all clients
            var castBroadcast = new NetworkAbilityCastBroadcast
            {
                CasterNetworkId = request.CasterNetworkId,
                CastInstanceId = castInstanceId,
                AbilityIdHash = request.AbilityIdHash,
                ServerTime = serverTime2,
                TargetType = request.TargetType,
                TargetPosition = request.TargetPosition,
                TargetNetworkId = request.TargetNetworkId,
                CastState = AbilityCastState.Started
            };
            
            BroadcastCastToClients?.Invoke(castBroadcast);
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Cast approved: {ability.name} (CastID: {castInstanceId})");
            }
            
            // Execute the cast on server (fire-and-forget; lifecycle managed internally)
            _ = ExecuteServerCast(activeCast, casterState);
        }
        
        private void SendCastRejection(
            uint clientId,
            ushort requestId,
            uint actorNetworkId,
            uint correlationId,
            AbilityCastRejectReason reason)
        {
            var response = new NetworkAbilityCastResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                CastInstanceId = 0,
                Approved = false,
                RejectReason = reason,
                CooldownEndTime = 0
            };
            
            SendCastResponseToClient?.Invoke(clientId, response);
            m_Stats.RejectedCasts++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Cast rejected: {reason} (ReqID: {requestId})");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: EXECUTE CAST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private async Task ExecuteServerCast(ActiveCast activeCast, CasterState casterState)
        {
            try
            {
                RuntimeAbility runtimeAbility = activeCast.RuntimeAbility;
                runtimeAbility.Reset();
                
                var args = new ExtendedArgs(casterState.Pawn.gameObject);
                args.Set(runtimeAbility);
                args.Set(new AbiltySource(casterState.Pawn.gameObject));
                
                // Set target
                if (activeCast.TargetType > 0)
                {
                    Target target = ReconstructTarget(activeCast);
                    if (IsValidTarget(target))
                    {
                        args.Set(target);
                    }
                }
                
                // Mark as auto-confirm for server execution
                args.Set(new AutoConfirmInput());
                
                // Subscribe to trigger events to broadcast effects
                var triggerReceipt = runtimeAbility.OnTrigger.Subscribe(triggerArgs =>
                {
                    OnAbilityTriggered(activeCast, triggerArgs);
                });
                
                // Execute via Caster.Cast (this handles state machine)
                bool success = await casterState.Caster.Cast(activeCast.Ability, args);
                
                triggerReceipt.Dispose();
                
                // Update cast state
                activeCast.State = runtimeAbility.IsCanceled ? AbilityCastState.Canceled : AbilityCastState.Completed;
                
                // Broadcast completion/cancellation
                BroadcastCastStateChange(activeCast);
                
                // Handle cooldown on success
                if (success && !runtimeAbility.IsCanceled)
                {
                    ApplyServerCooldown(casterState.NetworkId, activeCast.AbilityIdHash, args);
                }
                
                // Cleanup
                casterState.ActiveCastIds.Remove(activeCast.CastInstanceId);
                casterState.CastQueueCount = Mathf.Max(0, casterState.CastQueueCount - 1);
                m_ActiveCasts.Remove(activeCast.CastInstanceId);
                
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast completed: {activeCast.Ability.name} " +
                              $"(Success: {success}, Canceled: {runtimeAbility.IsCanceled})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAbilitiesController] Cast execution error: {ex.Message}\n{ex.StackTrace}");
                
                // Cleanup on error
                activeCast.State = AbilityCastState.Canceled;
                BroadcastCastStateChange(activeCast);
                
                casterState.ActiveCastIds.Remove(activeCast.CastInstanceId);
                casterState.CastQueueCount = Mathf.Max(0, casterState.CastQueueCount - 1);
                m_ActiveCasts.Remove(activeCast.CastInstanceId);
            }
        }
        
        private void OnAbilityTriggered(ActiveCast activeCast, ExtendedArgs args)
        {
            activeCast.State = AbilityCastState.Triggered;
            
            // Broadcast trigger to clients
            BroadcastCastStateChange(activeCast);
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Ability triggered: {activeCast.Ability.name}");
            }
        }
        
        private void BroadcastCastStateChange(ActiveCast activeCast)
        {
            var stateBroadcast = new NetworkAbilityCastBroadcast
            {
                CasterNetworkId = activeCast.CasterNetworkId,
                CastInstanceId = activeCast.CastInstanceId,
                AbilityIdHash = activeCast.AbilityIdHash,
                ServerTime = GetServerTime?.Invoke() ?? Time.time,
                TargetType = activeCast.TargetType,
                TargetPosition = activeCast.TargetPosition,
                TargetNetworkId = activeCast.TargetNetworkId,
                CastState = activeCast.State
            };
            
            BroadcastCastToClients?.Invoke(stateBroadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: COOLDOWN MANAGEMENT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void ApplyServerCooldown(uint casterNetworkId, int abilityIdHash, ExtendedArgs args)
        {
            RuntimeAbility runtimeAbility = args.Get<RuntimeAbility>();
            if (runtimeAbility == null) return;
            
            // Find cooldown requirement
            float cooldownDuration = 0f;
            foreach (var requirement in runtimeAbility.Requirements)
            {
                if (requirement is AbilityRequirementCooldown cooldownReq)
                {
                    // Commit the requirement to apply the cooldown locally
                    cooldownReq.Commit(args);
                    
                    // Get the cooldown duration (hacky but works)
                    var caster = runtimeAbility.Caster;
                    if (caster != null)
                    {
                        var cooldowns = caster.Get<Cooldowns>();
                        if (cooldowns != null)
                        {
                            float endTime = cooldowns.GetCooldown(runtimeAbility.ID);
                            cooldownDuration = endTime - Time.time;
                        }
                    }
                    break;
                }
            }
            
            if (cooldownDuration <= 0) return;
            
            // Add server-side buffer
            cooldownDuration += m_CooldownBuffer;
            
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            float cooldownEndTime = serverTime + cooldownDuration;
            
            var cooldownKey = (casterNetworkId, abilityIdHash);
            m_Cooldowns[cooldownKey] = new CooldownData
            {
                EndTime = cooldownEndTime,
                TotalDuration = cooldownDuration
            };
            
            // Broadcast cooldown to clients
            var cooldownBroadcast = new NetworkCooldownBroadcast
            {
                CharacterNetworkId = casterNetworkId,
                AbilityIdHash = abilityIdHash,
                CooldownEndTime = cooldownEndTime,
                TotalDuration = cooldownDuration,
                Reason = CooldownChangeReason.AbilityCast
            };
            
            BroadcastCooldownToClients?.Invoke(cooldownBroadcast);
            m_Stats.CooldownsSet++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Cooldown set: hash={abilityIdHash}, duration={cooldownDuration}s");
            }
        }
        
        /// <summary>
        /// Server method to reset a cooldown.
        /// </summary>
        public void ServerResetCooldown(uint casterNetworkId, int abilityIdHash)
        {
            if (!m_IsServer) return;
            
            var cooldownKey = (casterNetworkId, abilityIdHash);
            m_Cooldowns.Remove(cooldownKey);
            
            // Broadcast cleared cooldown
            var resetBroadcast = new NetworkCooldownBroadcast
            {
                CharacterNetworkId = casterNetworkId,
                AbilityIdHash = abilityIdHash,
                CooldownEndTime = 0,
                TotalDuration = 0,
                Reason = CooldownChangeReason.ServerReset
            };
            
            BroadcastCooldownToClients?.Invoke(resetBroadcast);
            m_Stats.CooldownsCleared++;
        }
        
        /// <summary>
        /// Server method to modify a cooldown duration.
        /// </summary>
        public void ServerModifyCooldown(uint casterNetworkId, int abilityIdHash, float newDuration, bool extend)
        {
            if (!m_IsServer) return;
            
            var cooldownKey = (casterNetworkId, abilityIdHash);
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            
            float newEndTime = serverTime + newDuration;
            
            m_Cooldowns[cooldownKey] = new CooldownData
            {
                EndTime = newEndTime,
                TotalDuration = newDuration
            };
            
            var modifyBroadcast = new NetworkCooldownBroadcast
            {
                CharacterNetworkId = casterNetworkId,
                AbilityIdHash = abilityIdHash,
                CooldownEndTime = newEndTime,
                TotalDuration = newDuration,
                Reason = extend ? CooldownChangeReason.ServerExtended : CooldownChangeReason.ServerReduced
            };
            
            BroadcastCooldownToClients?.Invoke(modifyBroadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROJECTILE SPAWNING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server spawns a projectile and broadcasts to clients.
        /// Call this from ability effects instead of direct Projectile.Get().
        /// </summary>
        public RuntimeProjectile ServerSpawnProjectile(
            uint castInstanceId,
            Projectile projectileSO,
            Vector3 spawnPosition,
            Vector3 direction,
            Vector3 targetPosition,
            uint targetNetworkId = 0,
            ExtendedArgs args = null)
        {
            if (!m_IsServer) return null;
            
            // Check limit per caster
            if (m_ActiveCasts.TryGetValue(castInstanceId, out var activeCast))
            {
                if (m_CasterStates.TryGetValue(activeCast.CasterNetworkId, out var casterState))
                {
                    if (casterState.ActiveProjectileIds.Count >= m_MaxProjectilesPerCharacter)
                    {
                        if (m_DebugLog)
                        {
                            Debug.LogWarning($"[NetworkAbilitiesController] Max projectiles reached for caster {casterState.NetworkId}");
                        }
                        return null;
                    }
                }
            }
            
            uint projectileId = m_NextProjectileId++;
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Spawn the projectile on server
            RuntimeProjectile instance = projectileSO.Get(
                args ?? new Args((GameObject)null),
                spawnPosition,
                Quaternion.LookRotation(direction)
            );
            
            if (instance == null) return null;
            
            // Track it
            var activeProjectile = new ActiveProjectile
            {
                ProjectileId = projectileId,
                CastInstanceId = castInstanceId,
                OwnerNetworkId = activeCast?.CasterNetworkId ?? 0,
                ProjectileHash = StableHashUtility.GetStableHash(projectileSO),
                Instance = instance,
                SpawnTime = serverTime,
                PierceCount = 0
            };
            
            m_ActiveProjectiles[projectileId] = activeProjectile;
            
            if (m_CasterStates.TryGetValue(activeProjectile.OwnerNetworkId, out var ownerState))
            {
                ownerState.ActiveProjectileIds.Add(projectileId);
            }
            
            // Broadcast spawn
            var spawnBroadcast = new NetworkProjectileSpawnBroadcast
            {
                ProjectileId = projectileId,
                CastInstanceId = castInstanceId,
                ProjectileHash = StableHashUtility.GetStableHash(projectileSO),
                SpawnPosition = spawnPosition,
                Direction = direction,
                TargetPosition = targetPosition,
                TargetNetworkId = targetNetworkId,
                ServerTime = serverTime
            };
            
            BroadcastProjectileSpawnToClients?.Invoke(spawnBroadcast);
            m_Stats.ProjectilesSpawned++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Projectile spawned: {projectileSO.name} (ID: {projectileId})");
            }
            
            return instance;
        }
        
        /// <summary>
        /// Server reports projectile hit.
        /// </summary>
        public void ServerReportProjectileHit(uint projectileId, Vector3 position, uint hitTargetNetworkId = 0)
        {
            if (!m_IsServer) return;
            if (!m_ActiveProjectiles.TryGetValue(projectileId, out var projectile)) return;
            
            var hitBroadcast = new NetworkProjectileEventBroadcast
            {
                ProjectileId = projectileId,
                EventType = ProjectileEventType.Hit,
                Position = position,
                HitTargetNetworkId = hitTargetNetworkId,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastProjectileEventToClients?.Invoke(hitBroadcast);
            m_Stats.ProjectileHits++;
        }
        
        /// <summary>
        /// Server destroys a projectile.
        /// </summary>
        public void ServerDestroyProjectile(uint projectileId, ProjectileEventType reason = ProjectileEventType.Destroyed)
        {
            if (!m_IsServer) return;
            if (!m_ActiveProjectiles.TryGetValue(projectileId, out var projectile)) return;
            
            Vector3 position = projectile.Instance != null ? projectile.Instance.transform.position : Vector3.zero;
            
            var destroyBroadcast = new NetworkProjectileEventBroadcast
            {
                ProjectileId = projectileId,
                EventType = reason,
                Position = position,
                HitTargetNetworkId = 0,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastProjectileEventToClients?.Invoke(destroyBroadcast);
            
            // Cleanup
            if (m_CasterStates.TryGetValue(projectile.OwnerNetworkId, out var ownerState))
            {
                ownerState.ActiveProjectileIds.Remove(projectileId);
            }
            
            if (projectile.Instance != null)
            {
                projectile.Instance.gameObject.SetActive(false);
            }
            
            m_ActiveProjectiles.Remove(projectileId);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: IMPACT SPAWNING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server spawns an impact and broadcasts to clients.
        /// </summary>
        public RuntimeImpact ServerSpawnImpact(
            uint castInstanceId,
            Impact impactSO,
            Vector3 position,
            Quaternion rotation,
            ExtendedArgs args = null)
        {
            if (!m_IsServer) return null;
            
            if (m_ActiveImpacts.Count >= m_MaxConcurrentImpacts)
            {
                if (m_DebugLog)
                {
                    Debug.LogWarning("[NetworkAbilitiesController] Max concurrent impacts reached.");
                }
                return null;
            }
            
            uint impactId = m_NextImpactId++;
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Spawn impact on server
            RuntimeImpact instance = impactSO.Get(
                args ?? new Args((GameObject)null),
                position,
                rotation
            );
            
            if (instance == null) return null;
            
            // Track it
            var activeImpact = new ActiveImpact
            {
                ImpactId = impactId,
                CastInstanceId = castInstanceId,
                ImpactHash = StableHashUtility.GetStableHash(impactSO),
                Instance = instance,
                SpawnTime = serverTime
            };
            
            m_ActiveImpacts[impactId] = activeImpact;
            
            // Broadcast spawn
            var impactBroadcast = new NetworkImpactSpawnBroadcast
            {
                ImpactId = impactId,
                CastInstanceId = castInstanceId,
                ImpactHash = StableHashUtility.GetStableHash(impactSO),
                Position = position,
                Rotation = rotation,
                ServerTime = serverTime
            };
            
            BroadcastImpactSpawnToClients?.Invoke(impactBroadcast);
            m_Stats.ImpactsSpawned++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Impact spawned: {impactSO.name} (ID: {impactId})");
            }
            
            return instance;
        }
        
        /// <summary>
        /// Server reports impact hits.
        /// </summary>
        public void ServerReportImpactHits(uint impactId, uint[] hitTargetNetworkIds)
        {
            if (!m_IsServer) return;
            if (!m_ActiveImpacts.ContainsKey(impactId)) return;
            
            var impactHitBroadcast = new NetworkImpactHitBroadcast
            {
                ImpactId = impactId,
                ServerTime = GetServerTime?.Invoke() ?? Time.time,
                TargetCount = (byte)Mathf.Min(hitTargetNetworkIds.Length, 16)
            };
            
            // Fill in targets
            if (hitTargetNetworkIds.Length > 0) impactHitBroadcast.Target0 = hitTargetNetworkIds[0];
            if (hitTargetNetworkIds.Length > 1) impactHitBroadcast.Target1 = hitTargetNetworkIds[1];
            if (hitTargetNetworkIds.Length > 2) impactHitBroadcast.Target2 = hitTargetNetworkIds[2];
            if (hitTargetNetworkIds.Length > 3) impactHitBroadcast.Target3 = hitTargetNetworkIds[3];
            if (hitTargetNetworkIds.Length > 4) impactHitBroadcast.Target4 = hitTargetNetworkIds[4];
            if (hitTargetNetworkIds.Length > 5) impactHitBroadcast.Target5 = hitTargetNetworkIds[5];
            if (hitTargetNetworkIds.Length > 6) impactHitBroadcast.Target6 = hitTargetNetworkIds[6];
            if (hitTargetNetworkIds.Length > 7) impactHitBroadcast.Target7 = hitTargetNetworkIds[7];
            if (hitTargetNetworkIds.Length > 8) impactHitBroadcast.Target8 = hitTargetNetworkIds[8];
            if (hitTargetNetworkIds.Length > 9) impactHitBroadcast.Target9 = hitTargetNetworkIds[9];
            if (hitTargetNetworkIds.Length > 10) impactHitBroadcast.Target10 = hitTargetNetworkIds[10];
            if (hitTargetNetworkIds.Length > 11) impactHitBroadcast.Target11 = hitTargetNetworkIds[11];
            if (hitTargetNetworkIds.Length > 12) impactHitBroadcast.Target12 = hitTargetNetworkIds[12];
            if (hitTargetNetworkIds.Length > 13) impactHitBroadcast.Target13 = hitTargetNetworkIds[13];
            if (hitTargetNetworkIds.Length > 14) impactHitBroadcast.Target14 = hitTargetNetworkIds[14];
            if (hitTargetNetworkIds.Length > 15) impactHitBroadcast.Target15 = hitTargetNetworkIds[15];
            
            BroadcastImpactHitToClients?.Invoke(impactHitBroadcast);
            m_Stats.ImpactTargetsHit += hitTargetNetworkIds.Length;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS LEARN REQUEST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Process learn request on server.
        /// </summary>
        public void ProcessLearnRequest(uint clientId, NetworkAbilityLearnRequest request)
        {
            if (!m_IsServer) return;
            
            OnLearnRequestReceived?.Invoke(clientId, request);

            if (!ValidateAbilitiesRequest(clientId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkAbilityLearnRequest)))
            {
                SendLearnRejection(
                    clientId,
                    request.RequestId,
                    request.ActorNetworkId,
                    request.CorrelationId,
                    GetLearnSecurityRejection(request.ActorNetworkId, request.CorrelationId));
                return;
            }

            if (request.CharacterNetworkId == 0 || request.CharacterNetworkId != request.ActorNetworkId)
            {
                SendLearnRejection(
                    clientId,
                    request.RequestId,
                    request.ActorNetworkId,
                    request.CorrelationId,
                    AbilityLearnRejectReason.ProtocolMismatch);
                return;
            }
            
            // Validate character
            Pawn pawn = GetPawnByNetworkId?.Invoke(request.CharacterNetworkId);
            if (pawn == null)
            {
                SendLearnRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityLearnRejectReason.CharacterNotFound);
                return;
            }
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null)
            {
                SendLearnRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityLearnRejectReason.CharacterNotFound);
                return;
            }
            
            if (request.IsLearning)
            {
                // Learn ability
                Ability ability = GetAbilityByHash?.Invoke(request.AbilityIdHash);
                if (ability == null)
                {
                    SendLearnRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityLearnRejectReason.AbilityNotFound);
                    return;
                }
                
                if (request.Slot < 0)
                {
                    SendLearnRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityLearnRejectReason.SlotInvalid);
                    return;
                }
                
                // Apply
                caster.Learn(ability, request.Slot);
                m_Stats.AbilitiesLearned++;
                
                // Send response
                var learnResponse = new NetworkAbilityLearnResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = true,
                    RejectReason = AbilityLearnRejectReason.None
                };
                SendLearnResponseToClient?.Invoke(clientId, learnResponse);
                
                // Broadcast
                var learnBroadcast = new NetworkAbilityLearnBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    AbilityIdHash = request.AbilityIdHash,
                    Slot = request.Slot,
                    IsLearned = true
                };
                BroadcastLearnToClients?.Invoke(learnBroadcast);
            }
            else
            {
                // Unlearn ability
                if (request.Slot < 0)
                {
                    SendLearnRejection(clientId, request.RequestId, request.ActorNetworkId, request.CorrelationId, AbilityLearnRejectReason.SlotInvalid);
                    return;
                }
                
                Ability slottedAbility = caster.GetSlottedAbility(request.Slot);
                
                caster.UnLearn(request.Slot);
                m_Stats.AbilitiesUnlearned++;
                
                var unlearnResponse = new NetworkAbilityLearnResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = true,
                    RejectReason = AbilityLearnRejectReason.None
                };
                SendLearnResponseToClient?.Invoke(clientId, unlearnResponse);
                
                var unlearnBroadcast = new NetworkAbilityLearnBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    AbilityIdHash = slottedAbility?.ID.Hash ?? 0,
                    Slot = request.Slot,
                    IsLearned = false
                };
                BroadcastLearnToClients?.Invoke(unlearnBroadcast);
            }
        }
        
        private void SendLearnRejection(
            uint clientId,
            ushort requestId,
            uint actorNetworkId,
            uint correlationId,
            AbilityLearnRejectReason reason)
        {
            var learnRejectResponse = new NetworkAbilityLearnResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = false,
                RejectReason = reason
            };
            SendLearnResponseToClient?.Invoke(clientId, learnRejectResponse);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: COOLDOWN / CANCEL REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Process cooldown query request on server.
        /// </summary>
        public void ProcessCooldownRequest(uint clientId, NetworkCooldownRequest request)
        {
            if (!m_IsServer) return;

            if (!ValidateAbilitiesRequest(clientId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkCooldownRequest)) ||
                request.CasterNetworkId == 0 ||
                request.CasterNetworkId != request.ActorNetworkId)
            {
                SendCooldownResponseToClient?.Invoke(clientId, new NetworkCooldownResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    IsOnCooldown = false,
                    CooldownEndTime = 0f,
                    TotalDuration = 0f
                });
                return;
            }

            uint casterNetworkId = request.ActorNetworkId;
            Pawn casterPawn = GetPawnByNetworkId?.Invoke(casterNetworkId);
            if (casterPawn == null)
            {
                SendCooldownResponseToClient?.Invoke(clientId, new NetworkCooldownResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    IsOnCooldown = false,
                    CooldownEndTime = 0f,
                    TotalDuration = 0f
                });
                return;
            }

            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            var cooldownKey = (casterNetworkId, request.AbilityIdHash);
            bool isOnCooldown = false;
            float cooldownEndTime = 0f;
            float totalDuration = 0f;

            if (m_Cooldowns.TryGetValue(cooldownKey, out CooldownData cooldownData))
            {
                if (serverTime < cooldownData.EndTime)
                {
                    isOnCooldown = true;
                    cooldownEndTime = cooldownData.EndTime;
                    totalDuration = cooldownData.TotalDuration;
                }
                else
                {
                    m_Cooldowns.Remove(cooldownKey);
                }
            }

            SendCooldownResponseToClient?.Invoke(clientId, new NetworkCooldownResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                IsOnCooldown = isOnCooldown,
                CooldownEndTime = cooldownEndTime,
                TotalDuration = totalDuration
            });
        }

        /// <summary>
        /// Process cast cancel request on server.
        /// </summary>
        public void ProcessCancelRequest(uint clientId, NetworkCastCancelRequest request)
        {
            if (!m_IsServer) return;

            if (!ValidateAbilitiesRequest(clientId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkCastCancelRequest)) ||
                request.CasterNetworkId == 0 ||
                request.CasterNetworkId != request.ActorNetworkId)
            {
                SendCancelResponseToClient?.Invoke(clientId, new NetworkCastCancelResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    CastInstanceId = 0
                });
                return;
            }

            uint casterNetworkId = request.ActorNetworkId;
            if (!m_CasterStates.TryGetValue(casterNetworkId, out CasterState casterState) ||
                casterState == null ||
                casterState.Caster == null)
            {
                SendCancelResponseToClient?.Invoke(clientId, new NetworkCastCancelResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Approved = false,
                    CastInstanceId = 0
                });
                return;
            }

            uint castInstanceId = request.CastInstanceId;
            ActiveCast activeCast = null;
            if (castInstanceId != 0)
            {
                if (!m_ActiveCasts.TryGetValue(castInstanceId, out activeCast) ||
                    activeCast == null ||
                    activeCast.CasterNetworkId != casterNetworkId)
                {
                    SendCancelResponseToClient?.Invoke(clientId, new NetworkCastCancelResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Approved = false,
                        CastInstanceId = 0
                    });
                    return;
                }
            }
            else
            {
                for (int i = casterState.ActiveCastIds.Count - 1; i >= 0; i--)
                {
                    uint activeId = casterState.ActiveCastIds[i];
                    if (!m_ActiveCasts.TryGetValue(activeId, out ActiveCast candidate) ||
                        candidate == null)
                    {
                        continue;
                    }

                    activeCast = candidate;
                    castInstanceId = activeId;
                    break;
                }

                if (activeCast == null)
                {
                    SendCancelResponseToClient?.Invoke(clientId, new NetworkCastCancelResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Approved = false,
                        CastInstanceId = 0
                    });
                    return;
                }
            }

            if (casterState.Pawn != null)
            {
                CastState castState = casterState.Pawn.GetState<CastState>();
                if (castState != null)
                {
                    castState.Cancel();
                }
                else
                {
                    activeCast.RuntimeAbility?.Cancel();
                }
            }
            else
            {
                activeCast.RuntimeAbility?.Cancel();
            }

            SendCancelResponseToClient?.Invoke(clientId, new NetworkCastCancelResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Approved = true,
                CastInstanceId = castInstanceId
            });
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private byte GetTargetType(Target target)
        {
            if (!IsValidTarget(target)) return 0;
            
            // Check if target has a Pawn (character target)
            if (target.Pawn != null) return 2;
            
            // Check if target has a GameObject (object target)
            if (target.GameObject != null) return 3;
            
            // Position target
            return 1;
        }
        
        private uint GetTargetNetworkId(Target target)
        {
            if (!IsValidTarget(target)) return 0;
            
            Pawn pawn = target.Pawn;
            if (pawn != null)
            {
                return GetNetworkIdForPawn?.Invoke(pawn) ?? 0;
            }
            
            return 0;
        }
        
        private Target ReconstructTarget(NetworkAbilityCastRequest request)
        {
            return ReconstructTargetFromData(request.TargetType, request.TargetPosition, request.TargetNetworkId);
        }
        
        private Target ReconstructTarget(ActiveCast cast)
        {
            return ReconstructTargetFromData(cast.TargetType, cast.TargetPosition, cast.TargetNetworkId);
        }
        
        private Target ReconstructTargetFromData(byte targetType, Vector3 position, uint networkId)
        {
            switch (targetType)
            {
                case 0:
                    return default;
                    
                case 1: // Position
                    return new Target(position);
                    
                case 2: // Character
                    Pawn pawn = GetPawnByNetworkId?.Invoke(networkId);
                    return pawn != null ? new Target(pawn) : new Target(position);
                    
                case 3: // Object
                    // For now, treat as position
                    return new Target(position);
                    
                default:
                    return default;
            }
        }
        
        /// <summary>
        /// Checks if a Target struct has a valid target (either GameObject or explicit position).
        /// </summary>
        private bool IsValidTarget(Target target)
        {
            return target.GameObject != null || target.HasPosition;
        }
        
        private void CleanupExpiredEntries(float currentTime)
        {
            // Cleanup old cooldowns
            var expiredCooldowns = new List<(uint, int)>();
            foreach (var kvp in m_Cooldowns)
            {
                if (currentTime >= kvp.Value.EndTime)
                {
                    expiredCooldowns.Add(kvp.Key);
                }
            }
            
            foreach (var key in expiredCooldowns)
            {
                m_Cooldowns.Remove(key);
            }
            
            // Cleanup stale projectiles (should be destroyed by game logic, but safety)
            var staleProjectiles = new List<uint>();
            foreach (var kvp in m_ActiveProjectiles)
            {
                if (currentTime - kvp.Value.SpawnTime > 30f) // 30 second max lifetime
                {
                    staleProjectiles.Add(kvp.Key);
                }
            }
            
            foreach (var id in staleProjectiles)
            {
                ServerDestroyProjectile(id, ProjectileEventType.Destroyed);
            }
            
            // Cleanup stale impacts
            var staleImpacts = new List<uint>();
            foreach (var kvp in m_ActiveImpacts)
            {
                if (currentTime - kvp.Value.SpawnTime > 10f) // 10 second max
                {
                    staleImpacts.Add(kvp.Key);
                }
            }
            
            foreach (var id in staleImpacts)
            {
                m_ActiveImpacts.Remove(id);
            }
        }
    }
}
