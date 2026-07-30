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
            if (target == null)
            {
                WarnHitRoutingInvariant(
                    $"Suppressed a melee strike with no target " +
                    $"(skill='{(skill != null ? skill.name : "null")}').");
                return false;
            }

            Character candidateCharacter =
                target.Get<Character>() ?? target.GetComponentInParent<Character>();

            // Remote clients don't process hits - they receive broadcasts
            if (m_IsRemoteClient && !m_IsServer)
            {
                LogMeleeSync(
                    $"InterceptHit ignored on remote target={target.name} skill={(skill != null ? skill.name : "null")}");
                return false;
            }

            // GC2 raises strike callbacks from Character.LateUpdate. A newly consumed combo
            // input can therefore reach its first active frame before this controller's next
            // Update has flushed the skill request. Flush it here so ReliableOrdered transport
            // always sees the attack authorization before its dependent hit.
            if (m_IsLocalClient)
            {
                FlushQueuedSkillInput();
            }

            // Character combat advances in LateUpdate, after this controller's ordinary Update.
            // Refresh the live phase/identity before deduplication so a chained attack cannot
            // inherit the previous strike's target set on its first active frame.
            if (m_MeleeStance != null)
            {
                NetworkAttackState liveAttackState = m_LastAttackState;
                liveAttackState.Phase = (byte)m_MeleeStance.CurrentPhase;
                TryGetCurrentSkillInfo(ref liveAttackState);
                m_LastAttackState = liveAttackState;
            }

            int skillHash = skill != null ? StableHashUtility.GetStableHash(skill.name) : 0;
            int weaponHash = m_LastAttackState.WeaponHash;
            int comboNodeId = m_LastAttackState.ComboNodeId;
            uint attackCorrelationId = ResolveLocalAttackCorrelationId(
                skillHash,
                weaponHash,
                comboNodeId);
            if (attackCorrelationId == 0 && m_IsServer && !m_IsLocalClient)
            {
                attackCorrelationId = GetOrCreateTrustedAttackCorrelationId();
            }

            PrepareProcessedHitOperation(
                attackCorrelationId,
                skillHash,
                weaponHash,
                comboNodeId);

            // GC2 deliberately treats scene geometry and CanHit objects differently from a
            // Character. Those contacts do not have a character network id and must not be
            // reported as a networking-initialization failure. Clients suppress their native
            // gameplay copy; the authoritative server copy may still run GC2's trigger/CanHit
            // branch once.
            Character hitCharacter = candidateCharacter;
            int targetId = hitCharacter != null
                ? hitCharacter.gameObject.GetInstanceID()
                : target.GetInstanceID();
            if (!m_ProcessedHits.Add(targetId))
            {
                return false;
            }

            if (hitCharacter == null || hitCharacter.IsDead)
            {
                LogMeleeSync(
                    $"strike contacted non-character target={target.name} layer={target.layer}; " +
                    $"nativeServerHandling={m_IsServer}");
                return m_IsServer;
            }

            target = hitCharacter.gameObject;

            // Local client - send to server
            var targetNetworkChar = target.GetComponentInParent<NetworkCharacter>();
            uint targetNetworkId = targetNetworkChar != null ? targetNetworkChar.NetworkId : 0;
            uint attackerNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            NetworkMeleeManager manager = NetworkMeleeManager.Instance;

            if (attackerNetworkId == 0 || targetNetworkId == 0 || manager == null)
            {
                WarnHitRoutingInvariant(
                    $"Suppressed a networked melee hit because its routing context is not ready " +
                    $"(attacker={attackerNetworkId}, target={targetNetworkId}, manager={manager != null}). " +
                    "Wait for NetworkCharacter and NetworkMeleeManager initialization before enabling attacks.");
                return false;
            }


            if (m_IsLocalClient && skillHash != 0 && attackCorrelationId == 0)
            {
                bool followsRecentRejection =
                    Time.time - m_LastRejectedAttackTime <= MaximumAttackAuthorizationLifetime &&
                    m_LastRejectedAttackSkillHash == skillHash &&
                    m_LastRejectedAttackWeaponHash == weaponHash &&
                    m_LastRejectedAttackComboNodeId == comboNodeId;
                string rejectionContext = followsRecentRejection
                    ? $" The matching request was rejected (corr={m_LastRejectedAttackCorrelationId}, " +
                      $"reason={m_LastRejectedAttackReason})."
                    : string.Empty;
                WarnHitRoutingInvariant(
                    $"Suppressed melee hit on '{target.name}' because no matching server-authorized " +
                    $"attack request exists (skillHash={skillHash}, weaponHash={weaponHash}, " +
                    $"combo={comboNodeId}).{rejectionContext} Ensure attacks enter through the patched GC2 melee " +
                    "input/strike hooks before their active frame.");
                return false;
            }

            var request = new NetworkMeleeHitRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                AttackCorrelationId = attackCorrelationId,
                ClientTimestamp = manager.GetNetworkTimeFunc?.Invoke() ?? Time.time,
                AttackerNetworkId = attackerNetworkId,
                TargetNetworkId = targetNetworkId,
                HitPoint = hitPoint,
                StrikeDirection = direction,
                SkillHash = skillHash,
                WeaponHash = weaponHash,
                ComboNodeId = comboNodeId,
                AttackPhase = m_LastAttackState.Phase
            };

            // A dedicated-server actor has no owning client from which to receive this request.
            // Queue it as a trusted server observation and suppress GC2's native hit so damage,
            // block evaluation, reactions, and presentation all come from the same authoritative
            // path. Host-owned actors continue through the ordinary loopback request below.
            if (m_IsServer && !m_IsLocalClient)
            {
                NetworkTransportBridge transport = NetworkTransportBridge.Active;
                if (transport == null)
                {
                    WarnHitRoutingInvariant(
                        $"Suppressed server-observed melee hit {request.RequestId} because the " +
                        "transport ownership registry is not ready. Trusted hits require an " +
                        "active NetworkTransportBridge that confirms the actor has no client owner.");
                    return false;
                }

                Character registeredActor = transport.ResolveCharacter(attackerNetworkId);
                if (registeredActor == null || !ReferenceEquals(registeredActor, m_Character))
                {
                    WarnHitRoutingInvariant(
                        $"Suppressed server-observed melee hit {request.RequestId} because actor " +
                        $"{attackerNetworkId} is not ready in the transport character registry. " +
                        "This avoids treating an ownership-registration race as a server-owned actor.");
                    return false;
                }

                if (transport.TryGetCharacterOwner(attackerNetworkId, out uint ownerClientId) &&
                    NetworkTransportBridge.IsValidClientId(ownerClientId))
                {
                    // This is the server replica of a client-owned actor. Its owning client sends
                    // the authoritative request; queueing the server-observed strike as trusted
                    // would apply the same hit twice.
                    LogMeleeSync(
                        $"suppressed server-replica hit req={request.RequestId} actor={attackerNetworkId} " +
                        $"ownerClient={ownerClientId}; waiting for owner request");
                    return false;
                }

                RecordTrustedAttackLease(request, Time.time);

                if (manager.TryServerQueueTrustedHit(request))
                {
                    LogMeleeSync(
                        $"queued trusted server hit req={request.RequestId} target={request.TargetNetworkId} " +
                        $"skillHash={request.SkillHash}");
                    return false;
                }

                WarnHitRoutingInvariant(
                    $"Suppressed trusted server melee hit {request.RequestId} because the " +
                    "authoritative queue rejected it. Native GC2 gameplay was not allowed to " +
                    "run, preventing an unvalidated duplicate hit.");
                return false;
            }

            LogReactionDiagnostics(
                $"hit request built req={request.RequestId} target={targetNetworkId} " +
                $"skill={(skill != null ? skill.name : "null")} skillHash={request.SkillHash} " +
                $"strikeLocal={FormatVector(direction)} vertical={direction.normalized.y:F3} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} hitPoint={FormatVector(hitPoint)}");

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                WarnHitRoutingInvariant(
                    "Suppressed a melee hit with an invalid actor/correlation context.");
                return false;
            }

            int maxBufferedHits = Mathf.Max(1, m_MaxHitBuffer);
            if (m_PendingHits.Count >= maxBufferedHits)
            {
                WarnHitRoutingInvariant(
                    $"Suppressed a melee hit because the pending queue is full ({maxBufferedHits}).");
                return false;
            }

            if (OnHitDetected == null)
            {
                WarnHitRoutingInvariant(
                    "Suppressed a melee hit because no manager/transport is subscribed to OnHitDetected.");
                return false;
            }

            bool playOptimistically = m_OptimisticEffects && !m_IsServer;
            m_PendingHits[pendingKey] = new PendingHit
            {
                Request = request,
                SentTime = Time.time,
                // Optimistic playback uses the presentation-only manager registry. Native
                // Skill.OnHit must remain suppressed because it may contain gameplay actions.
                OptimisticPlayed = playOptimistically
            };

            if (playOptimistically)
            {
                bool customHandled = manager.RequestHitPresentation(new NetworkMeleeHitBroadcast
                {
                    AttackerNetworkId = request.AttackerNetworkId,
                    TargetNetworkId = request.TargetNetworkId,
                    HitPoint = request.HitPoint,
                    StrikeDirection = request.StrikeDirection,
                    SkillHash = request.SkillHash,
                    BlockResult = (byte)NetworkBlockResult.None,
                    PoiseBroken = false
                }, includeSkillAudio: false);

                PendingHit optimisticPending = m_PendingHits[pendingKey];
                optimisticPending.OptimisticCustomHandled = customHandled;
                m_PendingHits[pendingKey] = optimisticPending;
            }

            // Raise event for network layer to send
            LogMeleeSync(
                $"sending hit request req={request.RequestId} corr={request.CorrelationId} " +
                $"target={request.TargetNetworkId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} " +
                $"phase={request.AttackPhase}");
            OnHitDetected.Invoke(request);

            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Hit request sent: {target.name} at {hitPoint}");
            }

            // All networked peers suppress native hit gameplay. Optimistic feedback above is
            // presentation-only and confirmation reconciliation consumes it exactly once.
            return false;
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

        /// <summary>Whether this server replica can author defense and reaction state safely.</summary>
        internal bool IsReadyForAuthoritativeHit(Character expectedTarget)
        {
            if (!m_IsServer ||
                !isActiveAndEnabled ||
                m_Character == null ||
                expectedTarget == null ||
                !ReferenceEquals(expectedTarget, m_Character) ||
                NetworkId == 0)
            {
                return false;
            }

            if (m_MeleeStance == null)
            {
                TryGetMeleeStance();
            }

            return m_MeleeStance != null;
        }

        /// <summary>
        /// Applies one validated reaction to this target on the authoritative server. The
        /// target's actual phase transition is responsible for broadcasting the reaction.
        /// </summary>
        internal bool TryApplyAuthoritativeReaction(NetworkMeleeReactionContext context)
        {
            if (!m_IsServer || context == null || m_Character == null || m_MeleeStance == null)
            {
                WarnHitRoutingInvariant(
                    "Could not apply an authoritative melee reaction because the target " +
                    "controller is not server-ready.");
                return false;
            }

            if (context.Target != null && !ReferenceEquals(context.Target, m_Character))
            {
                WarnHitRoutingInvariant(
                    $"Rejected authoritative melee reaction for target " +
                    $"{context.Request.TargetNetworkId}: controller/Character mismatch.");
                return false;
            }

            if (context.Skill == null)
            {
                WarnHitRoutingInvariant(
                    $"Could not resolve registered Skill hash {context.Request.SkillHash}; " +
                    "damage processing continues but no fallback reaction can be authored.");
                return false;
            }

            // Synchronized reactions are started with the attack's MotionWarp sequence. GC2's
            // MeleeStance.Hit intentionally performs no hit-time reaction for these skills.
            if (context.Skill.SyncReaction != null)
            {
                context.ReactionStarted = m_MeleeStance.CurrentPhase == MeleePhase.Reaction;
                return true;
            }

            if (context.Attacker == null)
            {
                WarnHitRoutingInvariant(
                    $"Could not resolve attacker {context.Request.ActorNetworkId} for the " +
                    "authoritative melee reaction.");
                return false;
            }

            MeleePhase phaseBefore = m_MeleeStance.CurrentPhase;
            bool wasAttacking = phaseBefore == MeleePhase.Anticipation ||
                                phaseBefore == MeleePhase.Strike ||
                                phaseBefore == MeleePhase.Recovery;

            if (!TryInvokeMeleeStanceDirect(
                    s_HitDirectMethod,
                    "HitDirect",
                    context.Attacker,
                    context.Input,
                    context.Skill))
            {
                // Old patched projects do not have HitDirect. On the server, the ordinary Hit
                // path preserves the same poise and SyncReaction rules and is a safe migration
                // fallback until the required patch is upgraded.
                m_MeleeStance.Hit(context.Attacker, context.Input, context.Skill);
            }

            context.ReactionStarted = m_MeleeStance.CurrentPhase == MeleePhase.Reaction;
            context.PoiseBroken = wasAttacking && context.ReactionStarted;

            LogReactionDiagnostics(
                $"authoritative reaction target={NetworkId} attacker={context.Request.ActorNetworkId} " +
                $"skill={context.Skill.name} block={context.BlockResult} phase={phaseBefore}->{m_MeleeStance.CurrentPhase} " +
                $"direction={FormatVector(context.Input.Direction)} power={context.Input.Power:F3} " +
                $"poiseBroken={context.PoiseBroken}");
            return context.ReactionStarted;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Server] Process a hit request from a client.
        /// </summary>
        public NetworkMeleeHitResponse ProcessHitRequest(NetworkMeleeHitRequest request, uint clientNetworkId)
        {
            return ProcessHitRequest(request, clientNetworkId, false);
        }

        /// <summary>
        /// Server validation entry point that distinguishes client-authorized attacks from
        /// trusted server/AI observations. Client hits use an accepted attack lease; trusted
        /// server hits retain the strict live GC2 phase check.
        /// </summary>
        internal NetworkMeleeHitResponse ProcessHitRequest(
            NetworkMeleeHitRequest request,
            uint clientNetworkId,
            bool trustedServerOrigin)
        {
            if (!m_IsServer)
            {
                WarnHitRoutingInvariant(
                    "Rejected ProcessHitRequest because the controller is not server-ready.");
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.CheatSuspected
                };
            }

            MeleeHitRejectionReason attackContextRejection = ValidateAuthoritativeAttackContext(
                request,
                trustedServerOrigin,
                out Skill authoritativeSkill,
                out MeleeWeapon authoritativeWeapon);
            if (attackContextRejection != MeleeHitRejectionReason.None)
            {
                WarnHitRoutingInvariant(
                    $"Authoritative melee hit {request.RequestId} was rejected before lag " +
                    $"compensation (reason={attackContextRejection}, actor={request.ActorNetworkId}, " +
                    $"target={request.TargetNetworkId}, skillHash={request.SkillHash}, " +
                    $"weaponHash={request.WeaponHash}, clientPhase={(MeleePhase)request.AttackPhase}, " +
                    $"serverPhase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}).");
                LogMeleeSyncWarning(
                    $"hit rejected against authoritative attack state req={request.RequestId} " +
                    $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} " +
                    $"phase={(MeleePhase)request.AttackPhase} reason={attackContextRejection}");
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = attackContextRejection
                };
            }

            // Find target character
            var targetNetworkChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null)
            {
                WarnHitRoutingInvariant(
                    $"Authoritative melee hit {request.RequestId} could not resolve network target " +
                    $"{request.TargetNetworkId}.");
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
                WarnHitRoutingInvariant(
                    $"Authoritative melee hit {request.RequestId} resolved target " +
                    $"{request.TargetNetworkId}, but it has no GC2 Character.");
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
                skill: authoritativeSkill,
                weapon: authoritativeWeapon
            );

            if (!validationResult.IsValid)
            {
                if (validationResult.RejectionReason == CombatValidationRejectionReason.InternalError ||
                    validationResult.RejectionReason == CombatValidationRejectionReason.TargetNotRegistered ||
                    validationResult.RejectionReason == CombatValidationRejectionReason.NoHistoryAvailable)
                {
                    bool managerReady = LagCompensationManager.IsInitialized;
                    LagCompensationManager lagManager = managerReady
                        ? LagCompensationManager.Instance
                        : null;
                    bool targetRegistered = managerReady &&
                                            lagManager.IsRegistered(request.TargetNetworkId);
                    WarnHitRoutingInvariant(
                        $"Authoritative melee hit {request.RequestId} cannot be validated: " +
                        $"reason={validationResult.RejectionReason}, managerReady={managerReady}, " +
                        $"targetRegistered={targetRegistered}, target={request.TargetNetworkId}, " +
                        $"details={validationResult.RejectionDetails ?? "none"}. Ensure the server " +
                        "lag-compensation bootstrap and CharacterLagCompensation registration are active.");
                }

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

            if (request.AttackCorrelationId != 0)
            {
                AttackAuthorizationStatus authorizationStatus = EvaluateAuthoritativeAttackAuthorization(
                    request,
                    Time.time,
                    true,
                    out MeleeHitRejectionReason authorizationRejection);
                if (authorizationStatus != AttackAuthorizationStatus.Authorized)
                {
                    WarnHitRoutingInvariant(
                        $"Authoritative melee hit {request.RequestId} could not consume attack " +
                        $"authorization {request.AttackCorrelationId} (status={authorizationStatus}, " +
                        $"reason={authorizationRejection}).");
                    return new NetworkMeleeHitResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Validated = false,
                        RejectionReason = authorizationRejection
                    };
                }
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

        private MeleeHitRejectionReason ValidateAuthoritativeAttackContext(
            NetworkMeleeHitRequest request,
            bool trustedServerOrigin,
            out Skill skill,
            out MeleeWeapon weapon)
        {
            skill = request.SkillHash != 0
                ? NetworkMeleeManager.GetSkillByHash(request.SkillHash)
                : null;
            if (skill == null)
            {
                weapon = null;
                return MeleeHitRejectionReason.SkillMismatch;
            }

            weapon = request.WeaponHash != 0
                ? NetworkMeleeManager.GetMeleeWeaponByHash(request.WeaponHash)
                : null;
            if (weapon == null)
            {
                return MeleeHitRejectionReason.WeaponMismatch;
            }

            if (!trustedServerOrigin || request.AttackCorrelationId != 0)
            {
                AttackAuthorizationStatus authorizationStatus = EvaluateAuthoritativeAttackAuthorization(
                    request,
                    Time.time,
                    false,
                    out MeleeHitRejectionReason authorizationRejection);
                return authorizationStatus == AttackAuthorizationStatus.Authorized
                    ? MeleeHitRejectionReason.None
                    : authorizationRejection;
            }

            if (m_MeleeStance == null)
            {
                TryGetMeleeStance();
            }

            if (m_MeleeStance == null || m_MeleeStance.CurrentPhase != MeleePhase.Strike)
            {
                return MeleeHitRejectionReason.InvalidPhase;
            }

            NetworkAttackState authoritativeState = m_LastAttackState;
            authoritativeState.Phase = (byte)m_MeleeStance.CurrentPhase;
            TryGetCurrentSkillInfo(ref authoritativeState);

            if (authoritativeState.SkillHash == 0 ||
                authoritativeState.SkillHash != request.SkillHash)
            {
                return MeleeHitRejectionReason.SkillMismatch;
            }

            if (authoritativeState.WeaponHash == 0 ||
                authoritativeState.WeaponHash != request.WeaponHash)
            {
                return MeleeHitRejectionReason.WeaponMismatch;
            }

            // Hashes route packets, while object identity protects against a local duplicate-hash
            // registry entry replacing the asset that is actually executing in GC2.
            if (s_AttacksField == null) return MeleeHitRejectionReason.InvalidPhase;
            try
            {
                var attacks = s_AttacksField.GetValue(m_MeleeStance) as Attacks;
                if (attacks?.ComboSkill == null || !ReferenceEquals(attacks.ComboSkill, skill))
                {
                    return MeleeHitRejectionReason.SkillMismatch;
                }

                if (attacks.Weapon == null || !ReferenceEquals(attacks.Weapon, weapon))
                {
                    return MeleeHitRejectionReason.WeaponMismatch;
                }
            }
            catch (Exception exception)
            {
                LogMeleeSyncWarning(
                    $"could not inspect the live authoritative attack for hit {request.RequestId}: " +
                    exception.GetBaseException().Message);
                return MeleeHitRejectionReason.InvalidPhase;
            }

            return MeleeHitRejectionReason.None;
        }

        internal enum AttackAuthorizationStatus : byte
        {
            Authorized = 0,
            Pending = 1,
            Rejected = 2
        }

        private uint ResolveLocalAttackCorrelationId(
            int skillHash,
            int weaponHash,
            int comboNodeId)
        {
            if (!m_IsLocalClient || m_CurrentLocalAttackCorrelationId == 0) return 0;
            if (m_CurrentLocalAttackSkillHash != skillHash) return 0;
            if (m_CurrentLocalAttackWeaponHash != weaponHash) return 0;
            if (m_CurrentLocalAttackComboNodeId != comboNodeId) return 0;
            return m_CurrentLocalAttackCorrelationId;
        }

        private uint GetOrCreateTrustedAttackCorrelationId()
        {
            if (m_CurrentTrustedAttackCorrelationId != 0)
            {
                return m_CurrentTrustedAttackCorrelationId;
            }

            uint actorNetworkId = NetworkId;
            if (actorNetworkId == 0) return 0;
            m_CurrentTrustedAttackCorrelationId = NetworkCorrelation.Next(
                actorNetworkId,
                ref m_NextTrustedAttackOperationCounter);
            return m_CurrentTrustedAttackCorrelationId;
        }

        private void PrepareProcessedHitOperation(
            uint attackCorrelationId,
            int skillHash,
            int weaponHash,
            int comboNodeId)
        {
            ulong operationId = attackCorrelationId != 0
                ? (1UL << 63) | attackCorrelationId
                : ((ulong)unchecked((uint)HashCode.Combine(skillHash, weaponHash)) << 32) |
                  unchecked((uint)comboNodeId);
            if (operationId == 0) operationId = 1;
            if (m_ProcessedHitsOperationId == operationId) return;

            m_ProcessedHitsOperationId = operationId;
            m_ProcessedHits.Clear();
        }

        private void RecordTrustedAttackLease(NetworkMeleeHitRequest request, float acceptedAt)
        {
            if (!m_IsServer || request.AttackCorrelationId == 0) return;
            if (m_ServerAttackLeases.ContainsKey(request.AttackCorrelationId)) return;

            RecordAuthoritativeAttackLease(
                new NetworkSkillRequest
                {
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.AttackCorrelationId,
                    ClientTimestamp = NetworkMeleeManager.Instance?.GetNetworkTimeFunc?.Invoke()
                                      ?? acceptedAt,
                    TargetNetworkId = request.TargetNetworkId,
                    SkillHash = request.SkillHash,
                    WeaponHash = request.WeaponHash,
                    ComboNodeId = request.ComboNodeId,
                    PreviousComboNodeId = ComboTree.NODE_INVALID
                },
                acceptedAt);
        }

        /// <summary>
        /// Records the exact operation the server accepted. The lease lasts through the
        /// authored attack plus transport/rewind grace, so validation is independent of the
        /// server replica happening to be in the same animation frame as the owner.
        /// </summary>
        private void RecordAuthoritativeAttackLease(NetworkSkillRequest request, float acceptedAt)
        {
            if (!m_IsServer ||
                request.ActorNetworkId == 0 ||
                request.CorrelationId == 0 ||
                !NetworkCorrelation.MatchesActor(request.CorrelationId, request.ActorNetworkId))
            {
                return;
            }

            Skill skill = request.SkillHash != 0
                ? NetworkMeleeManager.GetSkillByHash(request.SkillHash)
                : null;
            float lifetime = CalculateAttackAuthorizationLifetime(
                skill,
                request.TargetNetworkId,
                out float comboActiveLifetime);
            float comboTimelineStart = ResolveSkillTimelineTime(request, acceptedAt);

            PruneAuthoritativeAttackLeases(acceptedAt);
            if (m_ServerAttackLeases.Count >= MaxServerAttackLeases)
            {
                uint oldestToken = 0;
                ulong oldestAcceptVersion = ulong.MaxValue;
                foreach (KeyValuePair<uint, ServerAttackLease> pair in m_ServerAttackLeases)
                {
                    if (pair.Value.AcceptVersion >= oldestAcceptVersion) continue;
                    oldestAcceptVersion = pair.Value.AcceptVersion;
                    oldestToken = pair.Key;
                }

                if (oldestToken != 0) m_ServerAttackLeases.Remove(oldestToken);
            }

            m_NextServerAttackAcceptVersion++;
            if (m_NextServerAttackAcceptVersion == 0)
            {
                m_NextServerAttackAcceptVersion = 1;
            }

            m_ServerAttackLeases[request.CorrelationId] = new ServerAttackLease
            {
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                SkillHash = request.SkillHash,
                WeaponHash = request.WeaponHash,
                ComboNodeId = request.ComboNodeId,
                AcceptedAt = acceptedAt,
                AcceptVersion = m_NextServerAttackAcceptVersion,
                ComboActiveUntil = comboTimelineStart + comboActiveLifetime,
                ExpiresAt = acceptedAt + lifetime,
                ConsumedTargetIds = new HashSet<uint>()
            };

            LogMeleeSync(
                $"recorded attack authorization token={request.CorrelationId} " +
                $"acceptVersion={m_NextServerAttackAcceptVersion} " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} " +
                $"combo={request.ComboNodeId} previousCombo={request.PreviousComboNodeId} " +
                $"comboLifetime={comboActiveLifetime:F3}s hitLifetime={lifetime:F3}s");
        }

        private bool IsAuthorizedServerComboContinuation(
            NetworkSkillRequest request,
            MeleeWeapon weapon,
            Skill skill,
            float now)
        {
            if (!m_IsServer || weapon == null || skill == null) return false;

            ComboTree comboTree = weapon.Combo;
            ComboItem requestedItem = request.ComboNodeId != ComboTree.NODE_INVALID
                ? comboTree?.Get(request.ComboNodeId)
                : null;
            if (requestedItem == null || !ReferenceEquals(requestedItem.Skill, skill))
            {
                return false;
            }

            // A validated charge-start request authorizes releasing the same input while the
            // server replica remains in Charge/Busy. Node/Skill identity was checked above and
            // duration limits are checked by ProcessSkillRequest.
            if (request.IsChargeRelease)
            {
                return m_ChargeState.IsCharging &&
                       m_ChargeState.InputKey == request.InputKey;
            }

            float transitionTime = ResolveSkillTimelineTime(request, now);
            ServerAttackLease previous = null;
            foreach (KeyValuePair<uint, ServerAttackLease> pair in m_ServerAttackLeases)
            {
                ServerAttackLease candidate = pair.Value;
                if (candidate == null) continue;
                if (candidate.ActorNetworkId != request.ActorNetworkId ||
                    candidate.WeaponHash != request.WeaponHash)
                {
                    continue;
                }

                if (previous == null || candidate.AcceptVersion > previous.AcceptVersion)
                {
                    previous = candidate;
                }
            }

            bool hasActivePrevious =
                previous != null && transitionTime < previous.ComboActiveUntil;
            if (request.PreviousComboNodeId == ComboTree.NODE_INVALID)
            {
                // GC2 only considers InOrder roots after the previous combo has returned to
                // NODE_INVALID. The hit lease deliberately remains alive beyond that point, so
                // the absence of a combo-active predecessor (not lease expiry) identifies a
                // legitimate fresh chain. Only the newest accepted operation can be the combo
                // predecessor: an older, longer attack was superseded when the newer operation
                // was accepted and must never re-lock the chain afterward.
                return previous != null &&
                       !hasActivePrevious &&
                       comboTree.IsRoot(request.ComboNodeId);
            }

            if (!hasActivePrevious ||
                previous.ComboNodeId != request.PreviousComboNodeId)
            {
                return false;
            }

            List<int> children = comboTree.Children(previous.ComboNodeId);
            if (children.Contains(request.ComboNodeId)) return true;

            return request.ComboNodeId != previous.ComboNodeId &&
                   comboTree.IsRoot(request.ComboNodeId) &&
                   requestedItem.When == MeleeExecute.AnyTime;
        }

        internal AttackAuthorizationStatus EvaluateAuthoritativeAttackAuthorization(
            NetworkMeleeHitRequest request,
            float now,
            bool consume,
            out MeleeHitRejectionReason rejectionReason)
        {
            rejectionReason = MeleeHitRejectionReason.InvalidPhase;

            if (request.AttackCorrelationId == 0 ||
                request.ActorNetworkId == 0 ||
                request.ActorNetworkId != request.AttackerNetworkId ||
                !NetworkCorrelation.MatchesActor(
                    request.AttackCorrelationId,
                    request.ActorNetworkId))
            {
                rejectionReason = MeleeHitRejectionReason.CheatSuspected;
                return AttackAuthorizationStatus.Rejected;
            }

            if (!m_ServerAttackLeases.TryGetValue(
                    request.AttackCorrelationId,
                    out ServerAttackLease lease))
            {
                // Skill and hit packets use ReliableOrdered, but transport callbacks can still
                // enqueue their manager work across adjacent frames. The manager briefly holds
                // this hit while the earlier skill packet establishes its lease.
                return AttackAuthorizationStatus.Pending;
            }

            if (now > lease.ExpiresAt)
            {
                return AttackAuthorizationStatus.Rejected;
            }

            if (lease.ActorNetworkId != request.ActorNetworkId ||
                lease.CorrelationId != request.AttackCorrelationId)
            {
                rejectionReason = MeleeHitRejectionReason.CheatSuspected;
                return AttackAuthorizationStatus.Rejected;
            }

            if (lease.SkillHash != request.SkillHash)
            {
                rejectionReason = MeleeHitRejectionReason.SkillMismatch;
                return AttackAuthorizationStatus.Rejected;
            }

            if (lease.WeaponHash != request.WeaponHash)
            {
                rejectionReason = MeleeHitRejectionReason.WeaponMismatch;
                return AttackAuthorizationStatus.Rejected;
            }

            if (lease.ComboNodeId != request.ComboNodeId)
            {
                return AttackAuthorizationStatus.Rejected;
            }

            if (lease.ConsumedTargetIds.Contains(request.TargetNetworkId))
            {
                rejectionReason = MeleeHitRejectionReason.AlreadyHit;
                return AttackAuthorizationStatus.Rejected;
            }

            if (consume)
            {
                lease.ConsumedTargetIds.Add(request.TargetNetworkId);
            }

            rejectionReason = MeleeHitRejectionReason.None;
            return AttackAuthorizationStatus.Authorized;
        }

        private float CalculateAttackAuthorizationLifetime(
            Skill skill,
            uint targetNetworkId,
            out float comboActiveLifetime)
        {
            const float DefaultLifetime = 1.5f;
            const float DefaultComboActiveLifetime =
                DefaultLifetime - AttackAuthorizationNetworkGrace;
            comboActiveLifetime = DefaultComboActiveLifetime;
            if (skill == null || m_Character == null) return DefaultLifetime;

            try
            {
                GameObject target = NetworkMeleeManager.Instance
                    ?.GetCharacterByNetworkId(targetNetworkId)
                    ?.gameObject;
                Args args = new Args(m_Character.gameObject, target);
                AttackSpeed speed = skill.GetSpeed(args);
                AttackDuration duration = skill.GetDuration(speed, args);
                float authoredExit = Mathf.Max(
                    duration.Anticipation + duration.Strike,
                    duration.Total - skill.TransitionOut);
                if (float.IsNaN(authoredExit) || float.IsInfinity(authoredExit) || authoredExit <= 0f)
                {
                    return DefaultLifetime;
                }

                comboActiveLifetime = Mathf.Clamp(
                    authoredExit,
                    0.05f,
                    MaximumAttackAuthorizationLifetime - AttackAuthorizationNetworkGrace);
                return Mathf.Clamp(
                    comboActiveLifetime + AttackAuthorizationNetworkGrace,
                    MinimumAttackAuthorizationLifetime,
                    MaximumAttackAuthorizationLifetime);
            }
            catch (Exception exception)
            {
                LogMeleeSyncWarning(
                    $"could not calculate attack authorization lifetime for Skill " +
                    $"'{skill.name}': {exception.GetBaseException().Message}");
                return DefaultLifetime;
            }
        }

        private static float ResolveSkillTimelineTime(
            NetworkSkillRequest request,
            float fallbackTime)
        {
            return request.ClientTimestamp > 0f &&
                   !float.IsNaN(request.ClientTimestamp) &&
                   !float.IsInfinity(request.ClientTimestamp)
                ? request.ClientTimestamp
                : fallbackTime;
        }

        private bool IsSkillTimelineTimestampValid(
            NetworkSkillRequest request,
            out string details)
        {
            details = null;

            // Zero remains a compatibility value for trusted/direct server callers. Current
            // owner-generated requests always carry synchronized network time.
            if (request.ClientTimestamp == 0f) return true;
            if (float.IsNaN(request.ClientTimestamp) ||
                float.IsInfinity(request.ClientTimestamp) ||
                request.ClientTimestamp < 0f)
            {
                details = $"invalid skill timeline timestamp {request.ClientTimestamp}";
                return false;
            }

            float serverTime = NetworkMeleeManager.Instance?.GetNetworkTimeFunc?.Invoke()
                               ?? Time.time;
            float age = serverTime - request.ClientTimestamp;
            double maxRewind = m_ValidationConfig?.MaxRewindTime ?? 0.5d;
            double futureTolerance = m_ValidationConfig?.FutureTimestampTolerance ?? 0.05d;
            if (age > maxRewind || age < -futureTolerance)
            {
                details =
                    $"skill timeline timestamp is outside the authoritative window " +
                    $"(age={age:F3}s, maxRewind={maxRewind:F3}s, futureTolerance={futureTolerance:F3}s)";
                return false;
            }

            return true;
        }

        private void PruneAuthoritativeAttackLeases(float now)
        {
            s_SharedAttackLeaseRemovalBuffer.Clear();
            foreach (KeyValuePair<uint, ServerAttackLease> pair in m_ServerAttackLeases)
            {
                // Retain the expired token briefly so late/replayed dependent hits reject
                // immediately instead of being mistaken for a packet-ordering race.
                if (now <= pair.Value.ExpiresAt + ExpiredAttackAuthorizationRetention) continue;
                s_SharedAttackLeaseRemovalBuffer.Add(pair.Key);
            }

            for (int i = 0; i < s_SharedAttackLeaseRemovalBuffer.Count; i++)
            {
                m_ServerAttackLeases.Remove(s_SharedAttackLeaseRemovalBuffer[i]);
            }
            s_SharedAttackLeaseRemovalBuffer.Clear();
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
                if (pending.OptimisticPlayed)
                {
                    m_RecentOptimisticHitPresentations.Add(new RecentOptimisticHitPresentation
                    {
                        Request = pending.Request,
                        ExpiresAt = Time.time + 2f,
                        CustomHandled = pending.OptimisticCustomHandled
                    });
                }

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
        /// Compatibility entry point for transports that do not perform role-specific routing.
        /// New integrations should route the attacker and target roles separately through the
        /// manager so presentation is emitted exactly once.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkMeleeHitBroadcast broadcast)
        {
            bool isAttacker = broadcast.AttackerNetworkId == NetworkId;
            bool isTarget = broadcast.TargetNetworkId == NetworkId;

            if (isAttacker)
            {
                ReceiveHitBroadcastAsAttacker(broadcast, isTarget);
            }
            else if (isTarget)
            {
                ReceiveHitBroadcastAsTarget(broadcast);
            }
            else
            {
                NotifyHitConfirmed(broadcast);
            }
        }

        /// <summary>
        /// Receive the attacker side of a confirmed hit. This side alone owns shared impact
        /// presentation and optimistic-effect reconciliation.
        /// </summary>
        internal void ReceiveHitBroadcastAsAttacker(
            NetworkMeleeHitBroadcast broadcast,
            bool alsoTarget)
        {
            NotifyHitConfirmed(broadcast);

            if (alsoTarget)
            {
                ApplyTargetHitReconciliation(broadcast);
            }

            NetworkMeleeManager manager = NetworkMeleeManager.Instance;
            if (manager?.IsClient == true)
            {
                bool optimisticVisualAlreadyPlayed = HasMatchingOptimisticPresentation(
                    broadcast,
                    out bool customHandled);
                if (optimisticVisualAlreadyPlayed)
                {
                    if (!customHandled)
                    {
                        manager.RequestConfirmedHitAudio(broadcast);
                    }
                }
                else
                {
                    manager.RequestHitPresentation(broadcast);
                }
            }
        }

        private bool HasMatchingOptimisticPresentation(
            NetworkMeleeHitBroadcast broadcast,
            out bool customHandled)
        {
            customHandled = false;
            ulong matchedPendingKey = 0;
            foreach (KeyValuePair<ulong, PendingHit> pair in m_PendingHits)
            {
                PendingHit pending = pair.Value;
                if (!pending.OptimisticPlayed) continue;
                if (!MatchesConfirmedBroadcast(pending.Request, broadcast)) continue;
                matchedPendingKey = pair.Key;
                break;
            }

            if (matchedPendingKey != 0 && m_PendingHits.TryGetValue(matchedPendingKey, out PendingHit matchedPending))
            {
                // Mark consumed so a later response cannot add the same optimistic presentation
                // to the short response-before-broadcast cache.
                matchedPending.OptimisticPlayed = false;
                m_PendingHits[matchedPendingKey] = matchedPending;
                customHandled = matchedPending.OptimisticCustomHandled;
                return true;
            }

            float now = Time.time;
            for (int i = m_RecentOptimisticHitPresentations.Count - 1; i >= 0; i--)
            {
                RecentOptimisticHitPresentation recent = m_RecentOptimisticHitPresentations[i];
                if (recent.ExpiresAt < now)
                {
                    m_RecentOptimisticHitPresentations.RemoveAt(i);
                    continue;
                }

                if (!MatchesConfirmedBroadcast(recent.Request, broadcast)) continue;
                m_RecentOptimisticHitPresentations.RemoveAt(i);
                customHandled = recent.CustomHandled;
                return true;
            }

            return false;
        }

        private static bool MatchesConfirmedBroadcast(
            NetworkMeleeHitRequest request,
            NetworkMeleeHitBroadcast broadcast)
        {
            return request.AttackerNetworkId == broadcast.AttackerNetworkId &&
                   request.TargetNetworkId == broadcast.TargetNetworkId &&
                   request.SkillHash == broadcast.SkillHash &&
                   (request.HitPoint - broadcast.HitPoint).sqrMagnitude <= 0.25f;
        }

        /// <summary>
        /// Receive the target side of a confirmed hit. Target routing intentionally does not
        /// instantiate the shared impact effect.
        /// </summary>
        internal void ReceiveHitBroadcastAsTarget(NetworkMeleeHitBroadcast broadcast)
        {
            NotifyHitConfirmed(broadcast);
            ApplyTargetHitReconciliation(broadcast);
        }

        private void NotifyHitConfirmed(NetworkMeleeHitBroadcast broadcast)
        {
            OnHitConfirmed?.Invoke(broadcast);
            LogMeleeSync(
                $"received hit broadcast attacker={broadcast.AttackerNetworkId} target={broadcast.TargetNetworkId} " +
                $"skillHash={broadcast.SkillHash} block={broadcast.BlockResult} poiseBroken={broadcast.PoiseBroken}");
        }

        private void ApplyTargetHitReconciliation(NetworkMeleeHitBroadcast broadcast)
        {
            if (broadcast.TargetNetworkId == NetworkId)
            {
                SuppressLocalOwnerReconciliation(OwnerHitPreReactionReconciliationSuppression);
            }
        }

    }
}
#endif
