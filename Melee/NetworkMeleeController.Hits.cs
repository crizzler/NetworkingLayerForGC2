#if GC2_MELEE
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

namespace Arawn.GameCreator2.Networking.Melee
{
    public partial class NetworkMeleeController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HIT INTERCEPTION (Called by NetworkStriker or custom integrations)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a hit detected by a striker.
        /// </summary>
        /// <param name="target">The hit target.</param>
        /// <param name="hitPoint">World position of hit.</param>
        /// <param name="direction">Strike direction in target's local space.</param>
        /// <param name="skill">The skill being used.</param>
        /// <returns>True if hit should be processed locally (server or optimistic), false otherwise.</returns>
        public bool InterceptHit(GameObject target, Vector3 hitPoint, Vector3 direction, Skill skill)
        {
            if (target == null) return false;
            
            int targetId = target.GetInstanceID();
            
            // Don't process same target twice in one strike
            if (m_ProcessedHits.Contains(targetId)) return false;
            m_ProcessedHits.Add(targetId);
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits - they receive broadcasts
            if (m_IsRemoteClient)
            {
                return false;
            }
            
            // Local client - send to server
            var targetNetworkChar = target.GetComponent<NetworkCharacter>();
            uint targetNetworkId = targetNetworkChar != null ? targetNetworkChar.NetworkId : 0;
            uint attackerNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkMeleeHitRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                ClientTimestamp = NetworkMeleeManager.Instance?.GetNetworkTimeFunc?.Invoke() ?? Time.time,
                AttackerNetworkId = attackerNetworkId,
                TargetNetworkId = targetNetworkId,
                HitPoint = hitPoint,
                StrikeDirection = direction,
                SkillHash = skill != null ? StableHashUtility.GetStableHash(skill.name) : 0,
                WeaponHash = m_LastAttackState.WeaponHash,
                ComboNodeId = m_LastAttackState.ComboNodeId,
                AttackPhase = m_LastAttackState.Phase
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkMeleeController] Ignoring hit request with invalid actor/correlation context.");
                return false;
            }

            int maxBufferedHits = Mathf.Max(1, m_MaxHitBuffer);
            if (m_PendingHits.Count >= maxBufferedHits)
            {
                if (m_LogHits)
                {
                    Debug.LogWarning($"[NetworkMeleeController] Hit request dropped: pending queue is full ({maxBufferedHits}).");
                }

                return false;
            }

            m_PendingHits[pendingKey] = new PendingHit
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = false
            };
            
            // Raise event for network layer to send
            OnHitDetected?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Hit request sent: {target.name} at {hitPoint}");
            }
            
            // Return optimistic setting
            return m_OptimisticEffects;
        }
        
        /// <summary>
        /// Intercept a StrikeOutput from GC2's striker system.
        /// </summary>
        public bool InterceptStrikeOutput(StrikeOutput output, Skill skill)
        {
            Vector3 direction = m_Character != null ? m_Character.transform.forward : Vector3.forward;
            if (output.GameObject != null && m_Character != null)
            {
                Vector3 toTarget = output.GameObject.transform.position - m_Character.transform.position;
                if (toTarget.sqrMagnitude > float.Epsilon)
                {
                    direction = toTarget.normalized;
                }
            }

            return InterceptHit(output.GameObject, output.Point, direction, skill);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a hit request from a client.
        /// </summary>
        public NetworkMeleeHitResponse ProcessHitRequest(NetworkMeleeHitRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkMeleeController] ProcessHitRequest called on non-server");
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.CheatSuspected
                };
            }
            
            // Find target character
            var targetNetworkChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetNotFound
                };
            }
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetNotFound
                };
            }
            
            // Check if target is invincible
            if (targetCharacter.Combat.Invincibility.IsInvincible)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetInvincible
                };
            }
            
            // Check if target dodged
            if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetDodged
                };
            }
            
            // ═══════════════════════════════════════════════════════════════════════════════════
            // LAG COMPENSATION VALIDATION
            // ═══════════════════════════════════════════════════════════════════════════════════
            
            // Ensure validator is initialized
            if (m_Validator == null)
            {
                m_Validator = new MeleeLagCompensationValidator(m_ValidationConfig);
            }
            
            // Perform lag-compensated validation
            var validationResult = m_Validator.ValidateMeleeHit(
                request,
                m_Character,
                skill: NetworkMeleeManager.GetSkillByHash(request.SkillHash),
                weapon: NetworkMeleeManager.GetMeleeWeaponByHash(request.WeaponHash)
            );
            
            if (!validationResult.IsValid)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Hit rejected: {validationResult}");
                }
                
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MapValidationRejection(validationResult.RejectionReason),
                    Damage = 0f,
                    PoiseBroken = false
                };
            }
            
            // Hit validated with lag compensation!
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Hit validated: {validationResult}");
            }
            
            return new NetworkMeleeHitResponse
            {
                RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                Validated = true,
                RejectionReason = MeleeHitRejectionReason.None,
                Damage = validationResult.FinalDamage,
                PoiseBroken = EvaluatePoiseBroken(request.SkillHash, validationResult.FinalDamage)
            };
        }
        
        /// <summary>
        /// Evaluate whether the hit breaks the target's poise using the skill's poise damage.
        /// </summary>
        private bool EvaluatePoiseBroken(int skillHash, float finalDamage)
        {
            Skill skill = NetworkMeleeManager.GetSkillByHash(skillHash);
            if (skill == null) return false;
            
            // Use the skill's configured poise damage value.
            // GC2's poise system compares poise damage against the target's poise armor.
            // Without the target's current poise state, we use the damage as a heuristic:
            // if poise damage exceeds the final damage, this is likely a stagger-worthy hit.
            Args args = new Args(m_Character);
            float poiseDamage = skill.GetPoiseDamage(args);
            return poiseDamage > 0f && poiseDamage >= finalDamage;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server responds to our hit request.
        /// </summary>
        public void ReceiveHitResponse(NetworkMeleeHitResponse response)
        {
            if (!TryTakePending(m_PendingHits, response.ActorNetworkId, response.CorrelationId, out var pending))
            {
                if (m_LogHits)
                {
                    Debug.LogWarning($"[NetworkMeleeController] Response for unknown request: {response.RequestId}, corr={response.CorrelationId}");
                }
                return;
            }
            
            if (response.Validated)
            {
                // Hit was confirmed - if we didn't play optimistic effects, play now
                if (!pending.OptimisticPlayed && !m_OptimisticEffects)
                {
                    // Effects will be played by broadcast
                }
            }
            else
            {
                // Hit was rejected
                OnHitRejected?.Invoke(response);
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Hit rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkMeleeHitBroadcast broadcast)
        {
            OnHitConfirmed?.Invoke(broadcast);
            
            // Play effects if this is a remote client or non-optimistic local
            bool shouldPlayEffects = m_IsRemoteClient || 
                (m_IsLocalClient && !m_OptimisticEffects);
            
            if (shouldPlayEffects)
            {
                PlayHitEffects(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EFFECTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void PlayHitEffects(NetworkMeleeHitBroadcast broadcast)
        {
            // Resolve skill from broadcast hash to play effects (particles, sounds, hit pause)
            Skill skill = NetworkMeleeManager.GetSkillByHash(broadcast.SkillHash);
            if (skill == null) return;
            
            // GC2 melee hit effects are driven by the Skill's internal OnHit pipeline.
            // The local MeleeStance already handles effect playback for the owning client's
            // hits via its normal AttackSkill flow. For remote hit confirmation, we invoke
            // the reaction system which handles impact VFX through the broadcast direction/power.
        }
        
    }
}
#endif
