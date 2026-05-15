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
                LogMeleeSync(
                    $"InterceptHit server-local target={target.name} skill={(skill != null ? skill.name : "null")} " +
                    $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");
                return true;
            }
            
            // Remote clients don't process hits - they receive broadcasts
            if (m_IsRemoteClient)
            {
                LogMeleeSync(
                    $"InterceptHit ignored on remote target={target.name} skill={(skill != null ? skill.name : "null")}");
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

            LogReactionDiagnostics(
                $"hit request built req={request.RequestId} target={targetNetworkId} " +
                $"skill={(skill != null ? skill.name : "null")} skillHash={request.SkillHash} " +
                $"strikeLocal={FormatVector(direction)} vertical={direction.normalized.y:F3} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} hitPoint={FormatVector(hitPoint)}");

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
            LogMeleeSync(
                $"sending hit request req={request.RequestId} corr={request.CorrelationId} " +
                $"target={request.TargetNetworkId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} " +
                $"phase={request.AttackPhase}");
            OnHitDetected?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Hit request sent: {target.name} at {hitPoint}");
            }
            
            // Return optimistic setting
            return m_OptimisticEffects;
        }

        /// <summary>
        /// Resolves the same target-local strike direction that GC2's AttackSkill uses when it
        /// builds ShieldInput and ReactionInput. This preserves vertical strikes such as
        /// UpperSlash so network reactions can select FromBottom hit animations.
        /// </summary>
        public Vector3 ResolveStrikeDirectionForTarget(GameObject target, Skill skill)
        {
            if (target == null) return Vector3.zero;

            Transform attacker = m_Character != null ? m_Character.transform : transform;
            Vector3 worldDirection = ResolveSkillWorldStrikeDirection(attacker, target.transform, skill);
            if (worldDirection.sqrMagnitude < 0.0001f) return Vector3.zero;

            Vector3 localDirection = target.transform.InverseTransformDirection(worldDirection).normalized;
            if (ShouldLogReactionDiagnostics &&
                (IsVerticalReactionDirection(localDirection) ||
                 skill == null ||
                 skill.Strike.Direction is MeleeDirection.Upwards or MeleeDirection.Downwards))
            {
                LogReactionDiagnostics(
                    $"resolved strike direction target={target.name} skill={(skill != null ? skill.name : "null")} " +
                    $"skillDir={(skill != null ? skill.Strike.Direction.ToString() : "fallbackToTarget")} " +
                    $"world={FormatVector(worldDirection.normalized)} local={FormatVector(localDirection)} " +
                    $"attackerPos={FormatVector(attacker.position)} targetPos={FormatVector(target.transform.position)}");
            }

            return localDirection;
        }

        private static Vector3 ResolveSkillWorldStrikeDirection(
            Transform attacker,
            Transform target,
            Skill skill)
        {
            if (attacker == null) return Vector3.forward;

            if (skill != null)
            {
                return skill.Strike.Direction switch
                {
                    MeleeDirection.None => Vector3.zero,
                    MeleeDirection.Left => attacker.TransformDirection(Vector3.left),
                    MeleeDirection.Right => attacker.TransformDirection(Vector3.right),
                    MeleeDirection.Forward => attacker.TransformDirection(Vector3.forward),
                    MeleeDirection.Backwards => attacker.TransformDirection(Vector3.back),
                    MeleeDirection.Upwards => attacker.TransformDirection(Vector3.up),
                    MeleeDirection.Downwards => attacker.TransformDirection(Vector3.down),
                    _ => attacker.TransformDirection(Vector3.forward)
                };
            }

            if (target == null) return attacker.TransformDirection(Vector3.forward);

            Vector3 toTarget = target.position - attacker.position;
            return toTarget.sqrMagnitude > float.Epsilon
                ? toTarget.normalized
                : attacker.TransformDirection(Vector3.forward);
        }

        /// <summary>
        /// Intercept a StrikeOutput from GC2's striker system.
        /// </summary>
        public bool InterceptStrikeOutput(StrikeOutput output, Skill skill)
        {
            Vector3 direction = ResolveStrikeDirectionForTarget(output.GameObject, skill);

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
                LogMeleeSyncWarning(
                    $"hit rejected target not found req={request.RequestId} target={request.TargetNetworkId}");
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
                LogMeleeSyncWarning(
                    $"hit rejected target has no GC2 Character req={request.RequestId} target={request.TargetNetworkId}");
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
                LogMeleeSyncWarning(
                    $"hit rejected target invincible req={request.RequestId} target={request.TargetNetworkId}");
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
                LogMeleeSyncWarning(
                    $"hit rejected target dodged req={request.RequestId} target={request.TargetNetworkId}");
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
                LogReactionWarning(
                    $"hit rejected by validator req={request.RequestId} target={request.TargetNetworkId} " +
                    $"strikeLocal={FormatVector(request.StrikeDirection)} reason={validationResult.RejectionReason} " +
                    $"details={validationResult.RejectionDetails}");
                LogMeleeSyncWarning($"hit rejected by validator req={request.RequestId}: {validationResult}");
                
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
            LogReactionDiagnostics(
                $"hit validated req={request.RequestId} target={request.TargetNetworkId} " +
                $"strikeLocal={FormatVector(request.StrikeDirection)} damage={validationResult.FinalDamage:F3} " +
                $"hitPoint={FormatVector(validationResult.ValidatedHitPoint)}");
            LogMeleeSync($"hit validated req={request.RequestId}: {validationResult}");
            
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
                LogMeleeSyncWarning($"hit response for unknown request req={response.RequestId} corr={response.CorrelationId}");
                return;
            }

            LogMeleeSync(
                $"received hit response req={response.RequestId} corr={response.CorrelationId} " +
                $"validated={response.Validated} reason={response.RejectionReason} damage={response.Damage:F2}");
            
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
            LogMeleeSync(
                $"received hit broadcast attacker={broadcast.AttackerNetworkId} target={broadcast.TargetNetworkId} " +
                $"skillHash={broadcast.SkillHash} block={broadcast.BlockResult} poiseBroken={broadcast.PoiseBroken}");

            if (broadcast.TargetNetworkId == NetworkId)
            {
                SuppressLocalOwnerReconciliation(OwnerHitPreReactionReconciliationSuppression);
            }
            
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
