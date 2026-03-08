#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Shooter;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOT INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a shot before it's fired.
        /// Call this from your network-aware shot implementation.
        /// </summary>
        /// <param name="muzzlePosition">Muzzle position.</param>
        /// <param name="shotDirection">Shot direction with spread.</param>
        /// <param name="weapon">The weapon being used.</param>
        /// <param name="chargeRatio">Charge ratio for charged weapons.</param>
        /// <param name="projectileIndex">Index for multi-projectile weapons.</param>
        /// <param name="totalProjectiles">Total projectiles in shot.</param>
        /// <returns>True if shot should proceed locally (server or optimistic).</returns>
        public bool InterceptShot(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon,
            float chargeRatio,
            byte projectileIndex,
            byte totalProjectiles)
        {
            // Server fires immediately
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't fire - they receive broadcasts
            if (m_IsRemoteClient)
            {
                return false;
            }
            
            // Local client - send request to server
            uint shooterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            var sightHash = m_CurrentWeaponData?.SightId.Hash ?? 0;
            ushort requestId = GetNextRequestId();
            m_LastSentShotRequestId = requestId;
            
            var request = new NetworkShotRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                ClientTimestamp = Time.time,
                ShooterNetworkId = shooterNetworkId,
                MuzzlePosition = muzzlePosition,
                ShotDirection = shotDirection,
                WeaponHash = weapon?.Id.Hash ?? 0,
                SightHash = sightHash,
                ChargeRatio = chargeRatio,
                ProjectileIndex = projectileIndex,
                TotalProjectiles = totalProjectiles
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring shot request with invalid actor/correlation context.");
                return false;
            }

            int maxPendingShots = Mathf.Max(1, m_MaxPendingShots);
            if (m_PendingShots.Count >= maxPendingShots)
            {
                if (m_LogShots)
                {
                    Debug.LogWarning($"[NetworkShooterController] Shot request dropped: pending queue is full ({maxPendingShots}).");
                }

                return false;
            }

            m_PendingShots[pendingKey] = new PendingShotRequest
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = false
            };
            
            OnShotRequestSent?.Invoke(request);
            
            if (m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Shot request sent: {request.RequestId}");
            }
            
            return m_OptimisticShotEffects;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HIT INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a hit detected by a shot.
        /// </summary>
        /// <param name="target">The hit target GameObject.</param>
        /// <param name="hitPoint">Hit point in world space.</param>
        /// <param name="hitNormal">Hit normal.</param>
        /// <param name="distance">Distance from muzzle.</param>
        /// <param name="weapon">The weapon used.</param>
        /// <param name="pierceIndex">Pierce index (0 = first hit).</param>
        /// <returns>True if hit should be processed locally.</returns>
        public bool InterceptHit(
            GameObject target,
            Vector3 hitPoint,
            Vector3 hitNormal,
            float distance,
            ShooterWeapon weapon,
            byte pierceIndex)
        {
            if (target == null) return false;
            
            int targetId = target.GetInstanceID();
            
            // Don't process same target twice
            if (m_ProcessedHits.Contains(targetId)) return false;
            m_ProcessedHits.Add(targetId);
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits
            if (m_IsRemoteClient)
            {
                return false;
            }
            
            // Get target network ID
            var targetNetworkChar = target.GetComponent<NetworkCharacter>();
            uint targetNetworkId = targetNetworkChar != null ? targetNetworkChar.NetworkId : 0;
            uint shooterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            ushort requestId = GetNextRequestId();
            ushort sourceShotRequestId = ResolveSourceShotRequestId();
            
            bool isCharacterHit = target.GetComponent<Character>() != null;
            
            var request = new NetworkShooterHitRequest
            {
                RequestId = requestId,
                SourceShotRequestId = sourceShotRequestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                ClientTimestamp = Time.time,
                ShooterNetworkId = shooterNetworkId,
                TargetNetworkId = targetNetworkId,
                HitPoint = hitPoint,
                HitNormal = hitNormal,
                Distance = distance,
                WeaponHash = weapon?.Id.Hash ?? 0,
                PierceIndex = pierceIndex,
                IsCharacterHit = isCharacterHit
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkShooterController] Ignoring hit request with invalid actor/correlation context.");
                return false;
            }

            m_PendingHits[pendingKey] = new PendingHitRequest
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = false
            };
            
            OnHitDetected?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkShooterController] Hit request sent: {target.name} at {hitPoint}");
            }
            
            return m_OptimisticHitEffects;
        }
        
        /// <summary>
        /// Clear the processed hits set. Call when starting a new shot sequence.
        /// </summary>
        public void ClearProcessedHits()
        {
            m_ProcessedHits.Clear();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a shot request from a client.
        /// </summary>
        public NetworkShotResponse ProcessShotRequest(NetworkShotRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.WeaponNotEquipped
                };
            }
            
            // Validate ammo
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null && munition.InMagazine <= 0)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.NoAmmo
                };
            }
            
            // Validate not jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.WeaponJammed
                };
            }
            
            // Validate muzzle position sanity against server character position
            Vector3 characterPosition = m_Character != null ? m_Character.transform.position : request.MuzzlePosition;
            if ((request.MuzzlePosition - characterPosition).sqrMagnitude > 36f) // 6m tolerance
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.InvalidPosition
                };
            }

            // Validate direction vector
            float directionSqMag = request.ShotDirection.sqrMagnitude;
            if (directionSqMag < 0.01f || directionSqMag > 1.5f)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.InvalidDirection
                };
            }

            // Server-side fire-rate validation derived from weapon fire-rate.
            float now = Time.time;
            float minShotInterval = GetServerMinShotInterval();
            if (minShotInterval > 0f && now - m_LastServerValidatedShotTime < minShotInterval)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.RateLimitExceeded
                };
            }

            // Ensure lag-comp validator records this shot so subsequent hit requests can be
            // cryptographically/structurally bound to an authoritative validated shot.
            if (m_Validator == null)
            {
                m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
            }

            ShotValidationResult shotValidation = m_Validator.ValidateShot(
                request,
                m_Character,
                m_CurrentWeapon
            );
            if (!shotValidation.IsValid)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = shotValidation.RejectionReason
                };
            }
            
            // Store validated shot for hit validation
            m_ValidatedShots[request.RequestId] = new ValidatedShot
            {
                Request = request,
                ValidatedTime = Time.time,
                HitsProcessed = 0
            };
            m_LastServerValidatedShotTime = now;
            
            ushort ammoRemaining = munition != null ? (ushort)munition.InMagazine : (ushort)0;
            
            return new NetworkShotResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ShotRejectionReason.None,
                AmmoRemaining = ammoRemaining
            };
        }

        private float GetServerMinShotInterval()
        {
            if (m_CurrentWeapon == null || m_CurrentWeaponData == null)
            {
                return 0.02f;
            }

            float fireRate = m_CurrentWeapon.Fire.FireRate(m_CurrentWeaponData.WeaponArgs);
            if (float.IsNaN(fireRate) || float.IsInfinity(fireRate) || fireRate <= float.Epsilon)
            {
                return 0.02f;
            }

            // Allow slight tolerance for transport jitter while still blocking impossible cadence.
            float interval = 1f / fireRate;
            return Mathf.Clamp(interval * 0.9f, 0.01f, 2f);
        }
        
        /// <summary>
        /// [Server] Process a hit request from a client.
        /// </summary>
        public NetworkShooterHitResponse ProcessHitRequest(NetworkShooterHitRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                };
            }

            if (request.SourceShotRequestId == 0 ||
                !m_ValidatedShots.TryGetValue(request.SourceShotRequestId, out var validatedShot))
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }

            if (validatedShot.Request.ShooterNetworkId != request.ShooterNetworkId ||
                validatedShot.Request.WeaponHash != request.WeaponHash)
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }
            
            // For character hits, validate target
            if (request.IsCharacterHit && request.TargetNetworkId != 0)
            {
                var targetNetworkChar = NetworkShooterManager.Instance?.GetCharacterByNetworkId(request.TargetNetworkId);
                if (targetNetworkChar == null)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }
                
                var targetCharacter = targetNetworkChar.GetComponent<Character>();
                if (targetCharacter == null)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }
                
                // Check invincibility
                if (targetCharacter.Combat.Invincibility.IsInvincible)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetInvincible
                    };
                }
                
                // Check dodge
                if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetDodged
                    };
                }
                
                // ═══════════════════════════════════════════════════════════════════════════════════
                // LAG COMPENSATION VALIDATION
                // ═══════════════════════════════════════════════════════════════════════════════════
                
                // Ensure validator is initialized
                if (m_Validator == null)
                {
                    m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
                }
                
                // Perform lag-compensated validation
                var validationResult = m_Validator.ValidateShotHit(
                    request,
                    m_Character,
                    m_CurrentWeapon
                );
                
                if (!validationResult.IsValid)
                {
                    if (m_LogHits)
                    {
                        Debug.Log($"[NetworkShooterController] Hit rejected: {validationResult}");
                    }
                    
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = MapValidationRejection(validationResult.RejectionReason),
                        Damage = 0f,
                        BlockResult = NetworkBlockResult.None
                    };
                }
                
                // Hit validated with lag compensation!
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Hit validated: {validationResult}");
                }

                MarkValidatedShotHitProcessed(request.SourceShotRequestId);
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = true,
                    RejectionReason = HitRejectionReason.None,
                    Damage = validationResult.FinalDamage,
                    BlockResult = NetworkBlockResult.None
                };
            }
            
            // Environment hit - still validate against the authoritative shot trajectory.
            if (m_Validator == null)
            {
                m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
            }

            var environmentValidation = m_Validator.ValidateShotHit(
                request,
                m_Character,
                m_CurrentWeapon
            );

            if (!environmentValidation.IsValid)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Environment hit rejected: {environmentValidation}");
                }

                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MapValidationRejection(environmentValidation.RejectionReason),
                    Damage = 0f,
                    BlockResult = NetworkBlockResult.None
                };
            }

            MarkValidatedShotHitProcessed(request.SourceShotRequestId);
            return new NetworkShooterHitResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = HitRejectionReason.None,
                Damage = 0f,
                BlockResult = NetworkBlockResult.None
            };
        }

        private ushort ResolveSourceShotRequestId()
        {
            if (m_LastSentShotRequestId != 0)
            {
                return m_LastSentShotRequestId;
            }

            float latestSentTime = float.MinValue;
            ushort latestRequestId = 0;
            foreach (var pending in m_PendingShots.Values)
            {
                if (pending.SentTime > latestSentTime)
                {
                    latestSentTime = pending.SentTime;
                    latestRequestId = pending.Request.RequestId;
                }
            }

            return latestRequestId;
        }

        private void MarkValidatedShotHitProcessed(ushort sourceShotRequestId)
        {
            if (sourceShotRequestId == 0) return;
            if (!m_ValidatedShots.TryGetValue(sourceShotRequestId, out var validatedShot)) return;

            validatedShot.HitsProcessed++;
            m_ValidatedShots[sourceShotRequestId] = validatedShot;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server responds to a shot request.
        /// </summary>
        public void ReceiveShotResponse(NetworkShotResponse response)
        {
            if (!TryTakePending(m_PendingShots, response.ActorNetworkId, response.CorrelationId, out _))
            {
                if (m_LogShots)
                {
                    Debug.LogWarning($"[NetworkShooterController] Shot response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
                }
                return;
            }
            
            if (!response.Validated)
            {
                if (m_LogShots)
                {
                    Debug.Log($"[NetworkShooterController] Shot rejected: {response.RejectionReason}");
                }
                // Optimistic shot effects (muzzle flash, tracer, sound) are fire-and-forget VFX
                // that have already completed by the time the server response arrives.
                // Rolling them back would be visually jarring and offer no gameplay benefit.
            }
        }
        
        /// <summary>
        /// [Client] Called when server responds to a hit request.
        /// </summary>
        public void ReceiveHitResponse(NetworkShooterHitResponse response)
        {
            if (!TryTakePending(m_PendingHits, response.ActorNetworkId, response.CorrelationId, out _))
            {
                if (m_LogHits)
                {
                    Debug.LogWarning($"[NetworkShooterController] Hit response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
                }
                return;
            }
            
            if (!response.Validated)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Hit rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a confirmed shot.
        /// </summary>
        public void ReceiveShotBroadcast(NetworkShotBroadcast broadcast)
        {
            OnShotConfirmed?.Invoke(broadcast);
            
            // Play effects on remote clients
            if (m_IsRemoteClient)
            {
                PlayShotEffects(broadcast);
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkShooterHitBroadcast broadcast)
        {
            OnHitConfirmed?.Invoke(broadcast);
            
            // Play effects on remote clients or non-optimistic locals
            if (m_IsRemoteClient || (m_IsLocalClient && !m_OptimisticHitEffects))
            {
                PlayHitEffects(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EFFECTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void PlayShotEffects(NetworkShotBroadcast broadcast)
        {
            // Resolve weapon from hash to play muzzle flash, tracer, and fire sound.
            // On the local client these are handled optimistically by ShooterStance.
            // This path runs for remote clients observing another player's shots.
            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null) return;
            
            // GC2's ShooterStance drives muzzle VFX and audio through WeaponData internally.
            // For remote clients, the animation sync (via NetworkCharacter) triggers the
            // fire animation which in turn plays the weapon's configured muzzle effects.
        }
        
        private void PlayHitEffects(NetworkShooterHitBroadcast broadcast)
        {
            // Resolve weapon from hash to determine impact effect style.
            // MaterialHash (resolved server-side) can drive surface-specific particles.
            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null) return;
            
            // Impact VFX at the hit point. GC2's shot pipeline handles impact effects
            // internally via the ShotData.OnHit flow. For remote clients, the hit broadcast
            // provides position/normal data for spawning generic impact particles.
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Maps CombatValidationRejectionReason to HitRejectionReason.
        /// </summary>
        private static HitRejectionReason MapValidationRejection(CombatValidationRejectionReason reason)
        {
            return reason switch
            {
                CombatValidationRejectionReason.None => HitRejectionReason.None,
                CombatValidationRejectionReason.AttackerNotFound => HitRejectionReason.ShooterNotFound,
                CombatValidationRejectionReason.TargetNotFound => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.AttackerNotRegistered => HitRejectionReason.ShooterNotFound,
                CombatValidationRejectionReason.TargetNotRegistered => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetNotActive => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetInvincible => HitRejectionReason.TargetInvincible,
                CombatValidationRejectionReason.TargetDodging => HitRejectionReason.TargetDodged,
                CombatValidationRejectionReason.TargetDead => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TimestampTooOld => HitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.TimestampInFuture => HitRejectionReason.CheatSuspected,
                CombatValidationRejectionReason.NoHistoryAvailable => HitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.RaycastMissed => HitRejectionReason.RaycastMissed,
                CombatValidationRejectionReason.OutOfShooterRange => HitRejectionReason.OutOfRange,
                CombatValidationRejectionReason.InvalidTrajectory => HitRejectionReason.InvalidTrajectory,
                CombatValidationRejectionReason.ShotNotValidated => HitRejectionReason.ShotNotValidated,
                CombatValidationRejectionReason.InvalidMuzzlePosition => HitRejectionReason.InvalidPosition,
                CombatValidationRejectionReason.CheatSuspected => HitRejectionReason.CheatSuspected,
                _ => HitRejectionReason.CheatSuspected
            };
        }
        
    }
}
#endif
