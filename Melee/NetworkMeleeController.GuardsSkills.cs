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
        // BLOCK/SHIELD NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to raise block/guard.
        /// </summary>
        public void RequestBlockStart()
        {
            if (!m_IsLocalClient) return;
            if (m_IsBlockingLocally) return;
            
            // Get shield from current weapon
            var weapon = GetCurrentMeleeWeapon();
            int shieldHash = weapon?.Shield != null ? StableHashUtility.GetStableHash(weapon.Shield.Name) : 0;
            
            var request = new NetworkBlockRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                ClientTimestamp = Time.time,
                Action = NetworkBlockAction.Raise,
                ShieldHash = shieldHash
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkMeleeController] Ignoring block-start request with invalid actor/correlation context.");
                return;
            }

            m_PendingBlockRequests[pendingKey] = new PendingBlockRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            // Optimistically start blocking locally for responsiveness
            m_IsBlockingLocally = true;
            m_BlockStartTime = Time.time;
            m_CurrentShieldHash = shieldHash;
            
            OnBlockRequested?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Block start requested");
            }
        }
        
        /// <summary>
        /// [Client] Request to lower block/guard.
        /// </summary>
        public void RequestBlockStop()
        {
            if (!m_IsLocalClient) return;
            if (!m_IsBlockingLocally) return;
            
            var request = new NetworkBlockRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                ClientTimestamp = Time.time,
                Action = NetworkBlockAction.Lower,
                ShieldHash = m_CurrentShieldHash
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkMeleeController] Ignoring block-stop request with invalid actor/correlation context.");
                return;
            }

            m_PendingBlockRequests[pendingKey] = new PendingBlockRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            // Optimistically stop blocking locally
            m_IsBlockingLocally = false;
            
            OnBlockRequested?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Block stop requested");
            }
        }
        
        /// <summary>
        /// [Server] Process a block request from client.
        /// </summary>
        public NetworkBlockResponse ProcessBlockRequest(NetworkBlockRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.CheatSuspected
                };
            }
            
            // Check if character is busy (attacking, reacting, etc.)
            if (m_Character.Busy.IsBusy && request.Action == NetworkBlockAction.Raise)
            {
                return new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.CharacterBusy
                };
            }
            
            // Check shield is equipped
            var weapon = GetCurrentMeleeWeapon();
            if (weapon?.Shield == null && request.Action == NetworkBlockAction.Raise)
            {
                return new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.NoShieldEquipped
                };
            }
            
            uint charNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            float serverTime = Time.time;
            
            if (request.Action == NetworkBlockAction.Raise)
            {
                // Get shield properties
                var shield = weapon.Shield;
                var args = new Args(m_Character.gameObject);
                float defense = shield.GetDefense(args);
                float parryTime = 0.25f; // Default, could get from shield
                
                // Update server block state
                m_ServerBlockStates[charNetworkId] = new ServerBlockState
                {
                    IsBlocking = true,
                    BlockStartTime = serverTime,
                    ShieldHash = request.ShieldHash,
                    ParryWindowEnd = serverTime + parryTime,
                    CurrentDefense = defense,
                    MaxDefense = defense
                };
                
                // Actually raise guard on server
                m_Character.Combat.Block.RaiseGuard();
            }
            else
            {
                // Lower guard
                if (m_ServerBlockStates.ContainsKey(charNetworkId))
                {
                    var state = m_ServerBlockStates[charNetworkId];
                    state.IsBlocking = false;
                    m_ServerBlockStates[charNetworkId] = state;
                }
                
                m_Character.Combat.Block.LowerGuard();
            }
            
            return new NetworkBlockResponse
            {
                RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                Validated = true,
                RejectionReason = BlockRejectionReason.None,
                ServerBlockStartTime = serverTime
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to block request.
        /// </summary>
        public void ReceiveBlockResponse(NetworkBlockResponse response)
        {
            if (!TryTakePending(m_PendingBlockRequests, response.ActorNetworkId, response.CorrelationId, out _) && m_LogHits)
            {
                Debug.LogWarning($"[NetworkMeleeController] Block response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (!response.Validated)
            {
                // Revert optimistic block state
                m_IsBlockingLocally = !m_IsBlockingLocally;
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Block rejected: {response.RejectionReason}");
                }
            }
            else
            {
                // Sync block start time with server for accurate parry window
                m_BlockStartTime = response.ServerBlockStartTime;
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts block state change.
        /// </summary>
        public void ReceiveBlockBroadcast(NetworkBlockBroadcast broadcast)
        {
            OnBlockStateChanged?.Invoke(broadcast);
            
            // If this is for our character, sync state
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            if (broadcast.CharacterNetworkId == ourNetworkId && m_IsRemoteClient)
            {
                // Apply block state from server
                if (broadcast.Action == NetworkBlockAction.Raise)
                {
                    m_Character.Combat.Block.RaiseGuard();
                }
                else
                {
                    m_Character.Combat.Block.LowerGuard();
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SKILL EXECUTION NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a skill execution request.
        /// </summary>
        public NetworkSkillResponse ProcessSkillRequest(NetworkSkillRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.CheatSuspected
                };
            }
            
            // Check if character is in valid state for skill
            if (m_Character.Busy.IsBusy)
            {
                // Allow during certain phases (recovery allows combo transitions)
                MeleePhase phase = m_MeleeStance?.CurrentPhase ?? MeleePhase.None;
                if (phase != MeleePhase.Recovery && phase != MeleePhase.None)
                {
                    return new NetworkSkillResponse
                    {
                        RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                        Validated = false,
                        RejectionReason = SkillRejectionReason.CharacterBusy
                    };
                }
            }
            
            // Validate weapon is equipped
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null || weapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.WeaponNotEquipped
                };
            }
            
            // Validate charge if this is a charge release
            if (request.IsChargeRelease)
            {
                uint charNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
                if (!m_ServerBlockStates.ContainsKey(charNetworkId))
                {
                    // Check charge state tracking (separate from block states)
                    // For now, trust client charge duration within limits
                    if (request.ChargeDuration < 0.1f || request.ChargeDuration > 10f)
                    {
                        return new NetworkSkillResponse
                        {
                            RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                            Validated = false,
                            RejectionReason = SkillRejectionReason.ChargeNotValid
                        };
                    }
                }
            }
            
            if (request.ComboNodeId < -1)
            {
                return new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.InvalidComboTransition
                };
            }

            if (m_LastAttackState.ComboNodeId >= 0 && request.ComboNodeId >= 0 &&
                request.ComboNodeId < m_LastAttackState.ComboNodeId - 1)
            {
                return new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.InvalidComboTransition
                };
            }

            float now = Time.time;
            if (now - m_LastValidatedSkillRequestTime < 0.05f)
            {
                return new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.OnCooldown
                };
            }
            m_LastValidatedSkillRequestTime = now;
            
            // Skill validated - execute on server
            // The actual skill execution happens through normal GC2 flow,
            // we just validate it was legal
            
            return new NetworkSkillResponse
            {
                RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                Validated = true,
                RejectionReason = SkillRejectionReason.None,
                ComboNodeId = m_LastAttackState.ComboNodeId
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to skill request.
        /// </summary>
        public void ReceiveSkillResponse(NetworkSkillResponse response)
        {
            if (!TryTakePending(m_PendingSkillRequests, response.ActorNetworkId, response.CorrelationId, out _) && m_LogHits)
            {
                Debug.LogWarning($"[NetworkMeleeController] Skill response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (!response.Validated)
            {
                // Could cancel the optimistic skill execution
                // For now just log - in practice you might want to interrupt
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Skill rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts skill execution.
        /// </summary>
        public void ReceiveSkillBroadcast(NetworkSkillBroadcast broadcast)
        {
            OnSkillExecuted?.Invoke(broadcast);
            
            // Remote clients need to play the skill
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            if (broadcast.CharacterNetworkId == ourNetworkId && m_IsRemoteClient)
            {
                Skill skill = NetworkMeleeManager.GetSkillByHash(broadcast.SkillHash);
                MeleeWeapon weapon = NetworkMeleeManager.GetMeleeWeaponByHash(broadcast.WeaponHash);
                
                if (skill != null && m_MeleeStance != null)
                {
                    // Skill playback on remote client — the MeleeStance handles animation
                    // and VFX through its normal combo/attack pipeline via the patch hooks.
                    // The broadcast confirms the server validated this skill execution.
                    if (m_LogHits)
                    {
                        Debug.Log($"[NetworkMeleeController] Remote skill broadcast: {skill.name}" +
                                  (weapon != null ? $" with {weapon.name}" : ""));
                    }
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARGE ATTACK NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a charge start request.
        /// </summary>
        public NetworkChargeResponse ProcessChargeRequest(NetworkChargeRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false
                };
            }
            
            // Validate weapon
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null || weapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false
                };
            }
            
            // Resolve charge skill hash from the weapon's combo tree.
            // GC2's combo tree selects skills based on input key and combo state.
            // On the server we don't replay the full combo graph, so we trust the
            // client's input key and record it for later charge-release validation.
            int chargeSkillHash = request.WeaponHash; // Use weapon hash as proxy; actual skill resolves on release
            
            float serverTime = Time.time;
            
            // Track charge state on server
            m_ChargeState = new NetworkChargeState
            {
                IsCharging = true,
                InputKey = request.InputKey,
                ChargeSkillHash = chargeSkillHash,
                ChargeStartTime = serverTime,
                ChargeComboNodeId = -1
            };
            
            return new NetworkChargeResponse
            {
                RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                Validated = true,
                ServerChargeStartTime = serverTime,
                ChargeSkillHash = chargeSkillHash
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to charge request.
        /// </summary>
        public void ReceiveChargeResponse(NetworkChargeResponse response)
        {
            if (!TryTakePending(m_PendingChargeRequests, response.ActorNetworkId, response.CorrelationId, out _) && m_LogHits)
            {
                Debug.LogWarning($"[NetworkMeleeController] Charge response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
            }
            
            if (response.Validated)
            {
                // Sync charge start time with server
                m_ChargeState = new NetworkChargeState
                {
                    IsCharging = true,
                    InputKey = m_ChargeState.InputKey,
                    ChargeSkillHash = response.ChargeSkillHash,
                    ChargeStartTime = response.ServerChargeStartTime,
                    ChargeComboNodeId = -1
                };
            }
            else
            {
                // Clear optimistic charge
                m_ChargeState = NetworkChargeState.None;
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts charge state change.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            OnChargeStateChanged?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // REACTION NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Broadcast a reaction to all clients.
        /// </summary>
        public NetworkReactionBroadcast CreateReactionBroadcast(
            uint targetNetworkId, 
            uint attackerNetworkId, 
            Vector3 direction, 
            float power, 
            IReaction reaction)
        {
            return new NetworkReactionBroadcast
            {
                CharacterNetworkId = targetNetworkId,
                FromNetworkId = attackerNetworkId,
                ReactionHash = reaction != null ? StableHashUtility.GetStableHash(reaction.GetType().FullName) : 0,
                Direction = NetworkReactionBroadcast.CompressDirection(direction),
                Power = NetworkReactionBroadcast.CompressPower(power)
            };
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a reaction.
        /// </summary>
        public void ReceiveReactionBroadcast(NetworkReactionBroadcast broadcast)
        {
            OnReactionReceived?.Invoke(broadcast);
            
            // Play reaction on this character if it's the target
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            if (broadcast.CharacterNetworkId == ourNetworkId)
            {
                PlayReactionFromBroadcast(broadcast);
            }
        }
        
        private void PlayReactionFromBroadcast(NetworkReactionBroadcast broadcast)
        {
            if (m_MeleeStance == null) return;
            
            // Get attacker GameObject
            GameObject fromObject = null;
            if (broadcast.FromNetworkId != 0)
            {
                var attackerNetChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(broadcast.FromNetworkId);
                if (attackerNetChar != null) fromObject = attackerNetChar.gameObject;
            }
            
            // Build reaction input
            Vector3 direction = broadcast.GetDirection();
            float power = broadcast.GetPower();
            var reactionInput = new ReactionInput(direction, power);
            
            // Play the reaction.
            // GC2 reactions are resolved by direction + power matching against the weapon's
            // reaction list, not by a specific hash. PlayReaction internally selects the
            // appropriate reaction animation based on the ReactionInput parameters.
            m_MeleeStance.PlayReaction(fromObject, reactionInput, null, true);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BLOCK RESULT CALCULATION (Server-side)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Evaluate if target is blocking and determine block result.
        /// </summary>
        public BlockEvaluationResult EvaluateBlock(
            uint targetNetworkId, 
            Vector3 attackDirection, 
            float attackPower,
            int skillHash)
        {
            if (!m_IsServer) return BlockEvaluationResult.NoBlock;
            
            // Get target's block state
            if (!m_ServerBlockStates.TryGetValue(targetNetworkId, out var blockState))
            {
                return BlockEvaluationResult.NoBlock;
            }
            
            if (!blockState.IsBlocking)
            {
                return BlockEvaluationResult.NoBlock;
            }
            
            // Get target character for angle check
            var targetNetChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(targetNetworkId);
            if (targetNetChar == null) return BlockEvaluationResult.NoBlock;
            
            var targetCharacter = targetNetChar.GetComponent<Character>();
            if (targetCharacter == null) return BlockEvaluationResult.NoBlock;
            
            // Check attack angle vs block direction (default 180 degree coverage)
            Vector3 targetForward = targetCharacter.transform.forward;
            Vector3 flatAttackDir = new Vector3(attackDirection.x, 0f, attackDirection.z).normalized;
            float angle = Vector3.Angle(-flatAttackDir, targetForward);
            
            const float DefaultBlockAngle = 90f; // Half of 180 degree coverage
            if (angle > DefaultBlockAngle)
            {
                // Attack came from outside block arc
                return BlockEvaluationResult.NoBlock;
            }
            
            float serverTime = Time.time;
            
            // Check for parry (within parry window)
            if (serverTime <= blockState.ParryWindowEnd)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Attack PARRIED by {targetNetworkId}");
                }
                return BlockEvaluationResult.Parried;
            }
            
            // Check for block break
            float newDefense = blockState.CurrentDefense - attackPower;
            
            if (newDefense <= 0f)
            {
                // Block broken!
                var updatedState = blockState;
                updatedState.IsBlocking = false;
                updatedState.CurrentDefense = 0f;
                m_ServerBlockStates[targetNetworkId] = updatedState;
                
                // Force lower guard
                targetCharacter.Combat.Block.LowerGuard();
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Block BROKEN for {targetNetworkId}");
                }
                return BlockEvaluationResult.BlockBroken;
            }
            
            // Normal block - reduce defense
            var state = blockState;
            state.CurrentDefense = newDefense;
            m_ServerBlockStates[targetNetworkId] = state;
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Attack BLOCKED by {targetNetworkId}, defense remaining: {newDefense}");
            }
            return BlockEvaluationResult.Blocked(newDefense);
        }
        
        /// <summary>
        /// [Server] Reset block defense (e.g., after cooldown).
        /// </summary>
        public void ResetBlockDefense(uint networkId, float defense)
        {
            if (!m_IsServer) return;
            
            if (m_ServerBlockStates.TryGetValue(networkId, out var state))
            {
                state.CurrentDefense = defense;
                state.MaxDefense = defense;
                m_ServerBlockStates[networkId] = state;
            }
        }
        
    }
}
#endif
