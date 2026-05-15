#if GC2_MELEE
using System;
using System.Collections;
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
            if (!m_IsLocalClient)
            {
                LogMeleeSync($"ignored block start request: controller is not local client.");
                return;
            }

            if (m_IsBlockingLocally)
            {
                LogMeleeSync(
                    $"ignored block start request: already blocking locally. " +
                    $"gc2Blocking={m_Character?.Combat.Block.IsBlocking}");
                return;
            }
            
            // Get shield from current weapon
            var weapon = GetCurrentMeleeWeapon();
            int shieldHash = weapon?.Shield != null ? StableHashUtility.GetStableHash(weapon.Shield.Name) : 0;
            LogMeleeSync(
                $"block start requested weapon={(weapon != null ? weapon.name : "null")} " +
                $"hasShield={(weapon?.Shield != null)} shieldHash={shieldHash} gc2Blocking={m_Character?.Combat.Block.IsBlocking}");
            
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
            
            LogMeleeSync(
                $"sending block request req={request.RequestId} corr={request.CorrelationId} " +
                $"action={request.Action} shieldHash={request.ShieldHash}");
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
            if (!m_IsLocalClient)
            {
                LogMeleeSync($"ignored block stop request: controller is not local client.");
                return;
            }

            if (!m_IsBlockingLocally)
            {
                LogMeleeSync(
                    $"ignored block stop request: not blocking locally. " +
                    $"gc2Blocking={m_Character?.Combat.Block.IsBlocking}");
                return;
            }

            LogMeleeSync(
                $"block stop requested shieldHash={m_CurrentShieldHash} " +
                $"gc2Blocking={m_Character?.Combat.Block.IsBlocking}");
            
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
            
            LogMeleeSync(
                $"sending block request req={request.RequestId} corr={request.CorrelationId} " +
                $"action={request.Action} shieldHash={request.ShieldHash}");
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
            LogMeleeSync(
                $"server received block request fromClient={clientNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"actor={request.ActorNetworkId} action={request.Action} shieldHash={request.ShieldHash} " +
                $"busy={m_Character?.Busy.IsBusy} gc2Blocking={m_Character?.Combat.Block.IsBlocking}");

            if (!m_IsServer)
            {
                return RejectBlockRequest(request, BlockRejectionReason.CheatSuspected, "ProcessBlockRequest called on non-server controller");
            }
            
            // Check if character is busy (attacking, reacting, etc.)
            if (m_Character.Busy.IsBusy && request.Action == NetworkBlockAction.Raise)
            {
                return RejectBlockRequest(
                    request,
                    BlockRejectionReason.CharacterBusy,
                    $"character busy phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");
            }
            
            // Check shield is equipped
            var weapon = GetCurrentMeleeWeapon();
            if (weapon?.Shield == null && request.Action == NetworkBlockAction.Raise)
            {
                return RejectBlockRequest(
                    request,
                    BlockRejectionReason.NoShieldEquipped,
                    $"no shield on equipped weapon={(weapon != null ? weapon.name : "null")}");
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
                LogMeleeSync(
                    $"server raising guard shield={shield.Name} defense={defense:F2} parryTime={parryTime:F2} " +
                    $"serverTime={serverTime:F3}");
                
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
                LogMeleeSync($"server RaiseGuard complete gc2Blocking={m_Character.Combat.Block.IsBlocking}");
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
                LogMeleeSync($"server LowerGuard complete gc2Blocking={m_Character.Combat.Block.IsBlocking}");
            }
            
            LogMeleeSync(
                $"accepted block request req={request.RequestId} action={request.Action} " +
                $"serverTime={serverTime:F3}");
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

        private NetworkBlockResponse RejectBlockRequest(
            NetworkBlockRequest request,
            BlockRejectionReason reason,
            string details)
        {
            LogMeleeSyncWarning(
                $"rejected block request req={request.RequestId} corr={request.CorrelationId} reason={reason}: {details}. " +
                $"action={request.Action} shieldHash={request.ShieldHash} " +
                $"busy={m_Character?.Busy.IsBusy} gc2Blocking={m_Character?.Combat.Block.IsBlocking}");

            return new NetworkBlockResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Validated = false,
                RejectionReason = reason
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to block request.
        /// </summary>
        public void ReceiveBlockResponse(NetworkBlockResponse response)
        {
            bool hasPending = TryTakePending(
                m_PendingBlockRequests,
                response.ActorNetworkId,
                response.CorrelationId,
                out PendingBlockRequest pending);

            if (!hasPending)
            {
                LogMeleeSyncWarning(
                    $"block response for stale/unknown request req={response.RequestId} corr={response.CorrelationId} " +
                    $"validated={response.Validated} reason={response.RejectionReason}");
            }

            LogMeleeSync(
                $"received block response req={response.RequestId} corr={response.CorrelationId} " +
                $"validated={response.Validated} reason={response.RejectionReason} serverStart={response.ServerBlockStartTime:F3}");
            
            if (!response.Validated)
            {
                if (hasPending)
                {
                    ApplyRejectedBlockRequest(pending.Request);
                }
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Block rejected: {response.RejectionReason}");
                }
            }
            else
            {
                if (hasPending)
                {
                    m_IsBlockingLocally = pending.Request.Action == NetworkBlockAction.Raise;
                }

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
            LogMeleeSync(
                $"received block broadcast target={broadcast.CharacterNetworkId} ours={ourNetworkId} " +
                $"action={broadcast.Action} shieldHash={broadcast.ShieldHash} remote={m_IsRemoteClient} " +
                $"gc2Blocking={m_Character?.Combat.Block.IsBlocking}");

            if (broadcast.CharacterNetworkId == ourNetworkId && m_IsRemoteClient)
            {
                // Apply block state from server
                if (broadcast.Action == NetworkBlockAction.Raise)
                {
                    m_Character.Combat.Block.RaiseGuard();
                    LogMeleeSync($"remote RaiseGuard complete gc2Blocking={m_Character.Combat.Block.IsBlocking}");
                }
                else
                {
                    m_Character.Combat.Block.LowerGuard();
                    LogMeleeSync($"remote LowerGuard complete gc2Blocking={m_Character.Combat.Block.IsBlocking}");
                }
            }
        }

        private void ObserveGc2BlockState()
        {
            if (m_Character?.Combat.Block == null) return;

            bool isBlocking = m_Character.Combat.Block.IsBlocking;
            if (!m_HasObservedGc2BlockState)
            {
                m_HasObservedGc2BlockState = true;
                m_LastObservedGc2BlockState = isBlocking;
                return;
            }

            if (isBlocking == m_LastObservedGc2BlockState) return;

            LogMeleeSync(
                $"observed GC2 block state changed {m_LastObservedGc2BlockState}->{isBlocking}. " +
                $"localFlag={m_IsBlockingLocally} pendingBlockRequests={m_PendingBlockRequests.Count} " +
                $"roleLocal={m_IsLocalClient} roleRemote={m_IsRemoteClient} roleServer={m_IsServer}");

            if (m_IsLocalClient)
            {
                if (isBlocking && !m_IsBlockingLocally)
                {
                    LogMeleeSync("detected local GC2 RaiseGuard without network request; sending block start.");
                    RequestBlockStart();
                }
                else if (!isBlocking && m_IsBlockingLocally)
                {
                    LogMeleeSync("detected local GC2 LowerGuard without network request; sending block stop.");
                    RequestBlockStop();
                }
            }

            m_LastObservedGc2BlockState = isBlocking;
        }

        private void ApplyRejectedBlockRequest(NetworkBlockRequest request)
        {
            if (request.Action == NetworkBlockAction.Raise)
            {
                m_IsBlockingLocally = false;
                if (m_Character?.Combat.Block.IsBlocking == true)
                {
                    m_Character.Combat.Block.LowerGuard();
                    LogMeleeSync("reverted rejected block start with local LowerGuard.");
                }
            }
            else
            {
                m_IsBlockingLocally = true;
                if (m_Character?.Combat.Block.IsBlocking == false)
                {
                    m_Character.Combat.Block.RaiseGuard();
                    LogMeleeSync("reverted rejected block stop with local RaiseGuard.");
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
            LogSkillDiagnostics(
                $"server received skill request fromClient={clientNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"actor={request.ActorNetworkId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy} " +
                $"lastSkill={m_LastAttackState.SkillHash} lastWeapon={m_LastAttackState.WeaponHash}");
            LogMeleeSync(
                $"server received skill request fromClient={clientNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"actor={request.ActorNetworkId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy}");

            if (!m_IsServer)
            {
                return RejectSkillRequest(request, SkillRejectionReason.CheatSuspected, "ProcessSkillRequest called on non-server controller");
            }
            
            // Check if character is in valid state for skill
            if (m_Character.Busy.IsBusy)
            {
                // Allow during certain phases (recovery allows combo transitions)
                MeleePhase phase = m_MeleeStance?.CurrentPhase ?? MeleePhase.None;
                bool isHostOwnerReplay =
                    m_IsLocalClient &&
                    request.SkillHash != 0 &&
                    request.SkillHash == m_LastAttackState.SkillHash;

                if (!isHostOwnerReplay && phase != MeleePhase.Recovery && phase != MeleePhase.None)
                {
                    return RejectSkillRequest(
                        request,
                        SkillRejectionReason.CharacterBusy,
                        $"character busy phase={phase} lastSkill={m_LastAttackState.SkillHash}");
                }
            }
            
            // Validate weapon is equipped
            var weapon = GetCurrentMeleeWeapon(request.WeaponHash);
            if (weapon == null || weapon.Id.Hash != request.WeaponHash)
            {
                return RejectSkillRequest(request, SkillRejectionReason.WeaponNotEquipped, "requested weapon hash is not equipped on server copy");
            }
            RegisterWeaponAndSkills(weapon);

            Skill skill = NetworkMeleeManager.GetSkillByHash(request.SkillHash);
            if (skill == null)
            {
                return RejectSkillRequest(request, SkillRejectionReason.SkillNotAvailable, "skill hash is not registered on server");
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
                        return RejectSkillRequest(
                            request,
                            SkillRejectionReason.ChargeNotValid,
                            $"invalid charge duration {request.ChargeDuration:F3}");
                    }
                }
            }
            
            // GC2 ComboTree node IDs are opaque asset IDs, not ordered combo
            // indices. They can legitimately be negative, so skill/weapon
            // registration is the authoritative validation here.

            float now = Time.time;
            if (now - m_LastValidatedSkillRequestTime < 0.05f)
            {
                return RejectSkillRequest(
                    request,
                    SkillRejectionReason.OnCooldown,
                    $"skill request rate limited dt={now - m_LastValidatedSkillRequestTime:F3}");
            }
            m_LastValidatedSkillRequestTime = now;

            LogMeleeSync(
                $"accepted skill request req={request.RequestId} skill={skill.name} weapon={weapon.name} " +
                $"serverWillPlay={!m_IsLocalClient} phaseBefore={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");
            LogSkillDiagnostics(
                $"accepted skill request req={request.RequestId} skill={skill.name} weapon={weapon.name} " +
                $"serverWillPlay={!m_IsLocalClient} phaseBefore={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} " +
                $"isLocalClient={m_IsLocalClient}");
            
            if (!m_IsLocalClient)
            {
                PlayNetworkSkill(weapon, skill, request.TargetNetworkId);
            }
            else
            {
                NetworkAttackState attackState = m_LastAttackState;
                TryGetCurrentSkillInfo(ref attackState);
                if (attackState.SkillHash == 0)
                {
                    attackState.SkillHash = request.SkillHash;
                    attackState.WeaponHash = request.WeaponHash;
                    attackState.ComboNodeId = request.ComboNodeId;
                    attackState.Phase = (byte)(m_MeleeStance?.CurrentPhase ?? MeleePhase.None);
                }

                m_LastAttackState = attackState;
            }
            
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

        private NetworkSkillResponse RejectSkillRequest(
            NetworkSkillRequest request,
            SkillRejectionReason reason,
            string details)
        {
            LogSkillDiagnosticsWarning(
                $"rejected skill request req={request.RequestId} corr={request.CorrelationId} reason={reason}: {details}. " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy}");
            LogMeleeSyncWarning(
                $"rejected skill request req={request.RequestId} corr={request.CorrelationId} reason={reason}: {details}. " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy}");

            return new NetworkSkillResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Validated = false,
                RejectionReason = reason
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to skill request.
        /// </summary>
        public void ReceiveSkillResponse(NetworkSkillResponse response)
        {
            bool hadPending = TryTakePending(m_PendingSkillRequests, response.ActorNetworkId, response.CorrelationId, out _);
            if (!hadPending)
            {
                LogSkillDiagnosticsWarning(
                    $"skill response has no pending request req={response.RequestId} corr={response.CorrelationId} " +
                    $"actor={response.ActorNetworkId} validated={response.Validated} reason={response.RejectionReason}");
                if (m_LogHits)
                {
                    Debug.LogWarning($"[NetworkMeleeController] Skill response dropped (stale/unknown): req={response.RequestId}, corr={response.CorrelationId}");
                }
            }

            LogSkillDiagnostics(
                $"received skill response req={response.RequestId} corr={response.CorrelationId} hadPending={hadPending} " +
                $"validated={response.Validated} reason={response.RejectionReason} combo={response.ComboNodeId} " +
                $"pendingRemaining={m_PendingSkillRequests.Count}");
            LogMeleeSync(
                $"received skill response req={response.RequestId} corr={response.CorrelationId} " +
                $"validated={response.Validated} reason={response.RejectionReason} combo={response.ComboNodeId}");
            
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
            LogMeleeSync(
                $"received skill broadcast actor={broadcast.CharacterNetworkId} ours={ourNetworkId} " +
                $"skillHash={broadcast.SkillHash} weaponHash={broadcast.WeaponHash} combo={broadcast.ComboNodeId} " +
                $"remote={m_IsRemoteClient} stance={(m_MeleeStance != null)}");
            LogSkillDiagnostics(
                $"received skill broadcast actor={broadcast.CharacterNetworkId} ours={ourNetworkId} " +
                $"skillHash={broadcast.SkillHash} weaponHash={broadcast.WeaponHash} combo={broadcast.ComboNodeId} " +
                $"target={broadcast.TargetNetworkId} remote={m_IsRemoteClient} local={m_IsLocalClient} server={m_IsServer} " +
                $"stance={(m_MeleeStance != null)} phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");

            if (broadcast.CharacterNetworkId == ourNetworkId && m_IsRemoteClient)
            {
                Skill skill = NetworkMeleeManager.GetSkillByHash(broadcast.SkillHash);
                MeleeWeapon weapon = NetworkMeleeManager.GetMeleeWeaponByHash(broadcast.WeaponHash);
                if (skill == null || weapon == null)
                {
                    RegisterCurrentMeleeAssets();
                    skill = NetworkMeleeManager.GetSkillByHash(broadcast.SkillHash);
                    weapon = NetworkMeleeManager.GetMeleeWeaponByHash(broadcast.WeaponHash);
                }
                
                if (skill == null || weapon == null || m_MeleeStance == null)
                {
                    LogSkillDiagnosticsWarning(
                        $"cannot play remote skill broadcast: skill={(skill != null ? skill.name : "null")} " +
                        $"weapon={(weapon != null ? weapon.name : "null")} stance={(m_MeleeStance != null)}");
                    LogMeleeSyncWarning(
                        $"cannot play remote skill broadcast: skill={(skill != null ? skill.name : "null")} " +
                        $"weapon={(weapon != null ? weapon.name : "null")} stance={(m_MeleeStance != null)}");
                    return;
                }

                if (skill != null && weapon != null && m_MeleeStance != null)
                {
                    PlayNetworkSkill(weapon, skill, broadcast.TargetNetworkId);
                    if (m_LogHits)
                    {
                        Debug.Log($"[NetworkMeleeController] Remote skill broadcast: {skill.name}" +
                                  (weapon != null ? $" with {weapon.name}" : ""));
                    }
                }
            }
            else if (broadcast.CharacterNetworkId == ourNetworkId)
            {
                LogMeleeSync($"skipped skill broadcast playback because this controller is not remote.");
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

        private void PlayNetworkSkill(MeleeWeapon weapon, Skill skill, uint targetNetworkId)
        {
            if (m_MeleeStance == null || weapon == null || skill == null)
            {
                LogMeleeSyncWarning(
                    $"PlayNetworkSkill skipped: stance={(m_MeleeStance != null)} " +
                    $"weapon={(weapon != null ? weapon.name : "null")} skill={(skill != null ? skill.name : "null")}");
                return;
            }

            GameObject targetObject = null;
            if (targetNetworkId != 0)
            {
                var targetNetChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(targetNetworkId);
                if (targetNetChar != null) targetObject = targetNetChar.gameObject;
            }

            LogMeleeSync(
                $"PlayNetworkSkill weapon={weapon.name} skill={skill.name} target={targetNetworkId} " +
                $"targetObject={(targetObject != null ? targetObject.name : "null")} phaseBefore={m_MeleeStance.CurrentPhase}");
            if (!TryInvokeMeleeStanceDirect(
                    s_PlaySkillDirectMethod,
                    "PlaySkillDirect",
                    weapon,
                    skill,
                    targetObject))
            {
                m_MeleeStance.PlaySkill(weapon, skill, targetObject);
            }
            LogMeleeSync($"PlayNetworkSkill complete phaseAfter={m_MeleeStance.CurrentPhase}");

            NetworkAttackState attackState = NetworkAttackState.FromPhase(m_MeleeStance.CurrentPhase);
            TryGetCurrentSkillInfo(ref attackState);
            if (attackState.SkillHash == 0)
            {
                attackState.SkillHash = StableHashUtility.GetStableHash(skill.name);
                attackState.WeaponHash = weapon.Id.Hash;
                attackState.ComboNodeId = -1;
            }

            m_LastPhase = m_MeleeStance.CurrentPhase;
            m_LastAttackState = attackState;
            OnAttackStateChanged?.Invoke(m_LastAttackState);
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
            var broadcast = new NetworkReactionBroadcast
            {
                CharacterNetworkId = targetNetworkId,
                FromNetworkId = attackerNetworkId,
                ReactionHash = reaction != null ? StableHashUtility.GetStableHash(reaction.GetType().FullName) : 0,
                Direction = NetworkReactionBroadcast.CompressDirection(direction),
                DirectionY = NetworkReactionBroadcast.CompressDirectionY(direction),
                Power = NetworkReactionBroadcast.CompressPower(power)
            };

            LogReactionDiagnostics(
                $"reaction broadcast encoded target={targetNetworkId} from={attackerNetworkId} " +
                $"reaction={ReactionLabel(reaction)} rawDir={FormatVector(direction)} " +
                $"bytes=({broadcast.Direction},{broadcast.DirectionY}) decoded={FormatVector(broadcast.GetDirection())} " +
                $"power={power:F3}->{broadcast.GetPower():F3}");

            return broadcast;
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a reaction.
        /// </summary>
        public void ReceiveReactionBroadcast(NetworkReactionBroadcast broadcast)
        {
            OnReactionReceived?.Invoke(broadcast);
            
            // Play reaction on this character if it's the target
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            Vector3 decodedDirection = broadcast.GetDirection();
            LogReactionDiagnostics(
                $"reaction broadcast received target={broadcast.CharacterNetworkId} ours={ourNetworkId} " +
                $"from={broadcast.FromNetworkId} reactionHash={broadcast.ReactionHash} " +
                $"bytes=({broadcast.Direction},{broadcast.DirectionY}) decoded={FormatVector(decodedDirection)} " +
                $"power={broadcast.GetPower():F3} phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");
            LogMeleeSync(
                $"received reaction broadcast target={broadcast.CharacterNetworkId} ours={ourNetworkId} " +
                $"from={broadcast.FromNetworkId} reactionHash={broadcast.ReactionHash}");
            if (broadcast.CharacterNetworkId == ourNetworkId)
            {
                if (ShouldSkipReactionBroadcastPlayback(broadcast)) return;
                RememberReactionBroadcast(broadcast);
                PlayReactionFromBroadcast(broadcast);
            }
        }

        private bool ShouldSkipReactionBroadcastPlayback(NetworkReactionBroadcast broadcast)
        {
            if (m_MeleeStance != null && m_MeleeStance.CurrentPhase == MeleePhase.Reaction)
            {
                RememberReactionBroadcast(broadcast);
                LogReactionDiagnostics(
                    $"reaction broadcast skipped: already reacting target={broadcast.CharacterNetworkId} " +
                    $"from={broadcast.FromNetworkId} bytes=({broadcast.Direction},{broadcast.DirectionY})");
                LogMeleeSync(
                    $"skipped reaction broadcast playback because {gameObject.name} is already reacting. " +
                    $"from={broadcast.FromNetworkId} reactionHash={broadcast.ReactionHash}");
                return true;
            }

            const float DuplicateReactionWindow = 0.2f;
            if (m_HasLastReactionBroadcast &&
                Time.time - m_LastReactionBroadcastTime <= DuplicateReactionWindow &&
                IsSameReactionBroadcast(m_LastReactionBroadcast, broadcast))
            {
                LogReactionDiagnostics(
                    $"reaction broadcast skipped: duplicate target={broadcast.CharacterNetworkId} " +
                    $"from={broadcast.FromNetworkId} bytes=({broadcast.Direction},{broadcast.DirectionY})");
                LogMeleeSync(
                    $"skipped duplicate reaction broadcast from={broadcast.FromNetworkId} " +
                    $"reactionHash={broadcast.ReactionHash}");
                return true;
            }

            return false;
        }

        private void RememberReactionBroadcast(NetworkReactionBroadcast broadcast)
        {
            m_HasLastReactionBroadcast = true;
            m_LastReactionBroadcast = broadcast;
            m_LastReactionBroadcastTime = Time.time;
        }

        private static bool IsSameReactionBroadcast(
            NetworkReactionBroadcast a,
            NetworkReactionBroadcast b)
        {
            return a.CharacterNetworkId == b.CharacterNetworkId &&
                   a.FromNetworkId == b.FromNetworkId &&
                   a.ReactionHash == b.ReactionHash &&
                   a.Direction == b.Direction &&
                   a.DirectionY == b.DirectionY &&
                   a.Power == b.Power;
        }
        
        private void PlayReactionFromBroadcast(NetworkReactionBroadcast broadcast)
        {
            if (m_MeleeStance == null)
            {
                LogMeleeSyncWarning("PlayReactionFromBroadcast skipped: no melee stance");
                return;
            }
            
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
            string candidate = BuildReactionCandidateDebug(fromObject, reactionInput, null);
            LogReactionDiagnostics(
                $"PlayReactionFromBroadcast start from={broadcast.FromNetworkId} " +
                $"fromObject={(fromObject != null ? fromObject.name : "null")} direction={FormatVector(direction)} " +
                $"power={power:F3} phaseBefore={m_MeleeStance.CurrentPhase} " +
                $"position={FormatVector(transform.position)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                $"candidate={candidate}");
            LogMeleeSync(
                $"PlayReactionFromBroadcast from={broadcast.FromNetworkId} fromObject={(fromObject != null ? fromObject.name : "null")} " +
                $"direction={direction} power={power:F3} phaseBefore={m_MeleeStance.CurrentPhase}");

            SuppressLocalOwnerReconciliation(OwnerReactionInitialReconciliationSuppression, true);
            
            // Play the reaction.
            // GC2 reactions are resolved by direction + power matching against the weapon's
            // reaction list, not by a specific hash. PlayReaction internally selects the
            // appropriate reaction animation based on the ReactionInput parameters.
            if (!TryInvokeMeleeStanceDirect(
                    s_PlayReactionDirectMethod,
                    "PlayReactionDirect",
                    fromObject,
                    reactionInput,
                    null,
                    true))
            {
                m_MeleeStance.PlayReaction(fromObject, reactionInput, null, true);
            }
            LogReactionDiagnostics(
                $"PlayReactionFromBroadcast complete phaseAfter={m_MeleeStance.CurrentPhase} " +
                $"position={FormatVector(transform.position)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3}");
            StartReactionMotionProbe("broadcast", direction);
            LogMeleeSync($"PlayReactionFromBroadcast complete phaseAfter={m_MeleeStance.CurrentPhase}");
        }

        private void StartReactionMotionProbe(string source, Vector3 direction)
        {
            if (!ShouldLogReactionDiagnostics) return;
            StartCoroutine(ReactionMotionProbe(source, direction, transform.position));
        }

        private IEnumerator ReactionMotionProbe(string source, Vector3 direction, Vector3 startPosition)
        {
            yield return null;
            LogReactionDiagnostics(
                $"reaction motion sample source={source} t=frame direction={FormatVector(direction)} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} pos={FormatVector(transform.position)} " +
                $"delta={FormatVector(transform.position - startPosition)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                $"gravityInfluence={(m_Character != null ? m_Character.Driver.GravityInfluence : 1f):F3}");

            yield return new WaitForSeconds(0.35f);
            LogReactionDiagnostics(
                $"reaction motion sample source={source} t=0.35 direction={FormatVector(direction)} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} pos={FormatVector(transform.position)} " +
                $"delta={FormatVector(transform.position - startPosition)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                $"gravityInfluence={(m_Character != null ? m_Character.Driver.GravityInfluence : 1f):F3}");

            yield return new WaitForSeconds(0.65f);
            LogReactionDiagnostics(
                $"reaction motion sample source={source} t=1.00 direction={FormatVector(direction)} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} pos={FormatVector(transform.position)} " +
                $"delta={FormatVector(transform.position - startPosition)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                $"gravityInfluence={(m_Character != null ? m_Character.Driver.GravityInfluence : 1f):F3}");
        }

        private bool TryInvokeMeleeStanceDirect(MethodInfo method, string methodName, params object[] args)
        {
            if (method == null || m_MeleeStance == null) return false;

            try
            {
                method.Invoke(m_MeleeStance, args);
                return true;
            }
            catch (Exception e)
            {
                LogMeleeSyncWarning($"{methodName} failed; falling back to normal GC2 melee path: {e.GetBaseException().Message}");
                return false;
            }
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
            if (!m_IsServer)
            {
                LogMeleeSync(
                    $"EvaluateBlock no-block target={targetNetworkId}: controller is not server. " +
                    $"attackPower={attackPower:F2} skillHash={skillHash}");
                return BlockEvaluationResult.NoBlock;
            }
            
            // Get target's block state
            if (!m_ServerBlockStates.TryGetValue(targetNetworkId, out var blockState))
            {
                LogMeleeSync(
                    $"EvaluateBlock no-block target={targetNetworkId}: no server block state. " +
                    $"attackPower={attackPower:F2} skillHash={skillHash}");
                return BlockEvaluationResult.NoBlock;
            }
            
            if (!blockState.IsBlocking)
            {
                LogMeleeSync(
                    $"EvaluateBlock no-block target={targetNetworkId}: state exists but IsBlocking=false. " +
                    $"attackPower={attackPower:F2} defense={blockState.CurrentDefense:F2}");
                return BlockEvaluationResult.NoBlock;
            }
            
            // Get target character for angle check
            var targetNetChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(targetNetworkId);
            if (targetNetChar == null)
            {
                LogMeleeSync($"EvaluateBlock no-block target={targetNetworkId}: target NetworkCharacter not found.");
                return BlockEvaluationResult.NoBlock;
            }
            
            var targetCharacter = targetNetChar.GetComponent<Character>();
            if (targetCharacter == null)
            {
                LogMeleeSync($"EvaluateBlock no-block target={targetNetworkId}: target has no GC2 Character.");
                return BlockEvaluationResult.NoBlock;
            }
            
            // GC2 ShieldInput.Direction is target-local. Match Shield.CanDefend so vertical
            // strikes such as uppercuts still resolve consistently with the local melee package.
            Vector3 localAttackDirection = attackDirection.sqrMagnitude > 0.0001f
                ? attackDirection.normalized
                : Vector3.back;
            float angle = Vector3.Angle(-localAttackDirection, Vector3.forward);
            
            const float DefaultBlockAngle = 90f; // Half of 180 degree coverage
            if (angle > DefaultBlockAngle)
            {
                // Attack came from outside block arc
                LogMeleeSync(
                    $"EvaluateBlock no-block target={targetNetworkId}: outside block arc angle={angle:F1} " +
                    $"limit={DefaultBlockAngle:F1} attackDir={attackDirection}");
                return BlockEvaluationResult.NoBlock;
            }
            
            float serverTime = Time.time;
            LogMeleeSync(
                $"EvaluateBlock target={targetNetworkId} in block arc angle={angle:F1} " +
                $"attackPower={attackPower:F2} defense={blockState.CurrentDefense:F2} " +
                $"parryUntil={blockState.ParryWindowEnd:F3} serverTime={serverTime:F3}");
            
            // Check for parry (within parry window)
            if (serverTime <= blockState.ParryWindowEnd)
            {
                LogMeleeSync($"EvaluateBlock result=Parried target={targetNetworkId}");
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
                
                LogMeleeSync($"EvaluateBlock result=BlockBroken target={targetNetworkId}");
                return BlockEvaluationResult.BlockBroken;
            }
            
            // Normal block - reduce defense
            var state = blockState;
            state.CurrentDefense = newDefense;
            m_ServerBlockStates[targetNetworkId] = state;
            
            LogMeleeSync($"EvaluateBlock result=Blocked target={targetNetworkId} defenseRemaining={newDefense:F2}");
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
