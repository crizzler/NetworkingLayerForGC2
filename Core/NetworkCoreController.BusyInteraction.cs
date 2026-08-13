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

            if ((request.Limbs & ~BusyLimbs.Every) != 0 || request.Limbs == BusyLimbs.None ||
                !IsFinite(request.Timeout) || request.Timeout < 0f)
            {
                SendBusyResponse(senderNetworkId, request.RequestId, false,
                    BusyRejectReason.InvalidValue, request.ActorNetworkId, request.CorrelationId);
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
                    float timeout = Mathf.Min(request.Timeout, Mathf.Max(0.01f, m_MaxBusyTimeout));
                    ApplyTimedBusyAndBroadcast(
                        request.CharacterNetworkId,
                        character,
                        gc2Limbs,
                        timeout);
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

            BroadcastCurrentBusyState(request.CharacterNetworkId, character);
        }

        private async void ApplyTimedBusyAndBroadcast(
            uint characterNetworkId,
            Character character,
            GameCreator.Runtime.Characters.Busy.Limb limbs,
            float timeout)
        {
            try
            {
                await character.Busy.Timeout(limbs, timeout);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                return;
            }

            if (!m_IsServer || character == null) return;
            Character current = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (current != character) return;
            BroadcastCurrentBusyState(characterNetworkId, character);
        }

        private void BroadcastCurrentBusyState(uint characterNetworkId, Character character)
        {
            if (character == null) return;
            BusyLimbs currentBusy = BusyLimbs.None;
            if (character.Busy.IsArmLeftBusy) currentBusy |= BusyLimbs.ArmLeft;
            if (character.Busy.IsArmRightBusy) currentBusy |= BusyLimbs.ArmRight;
            if (character.Busy.IsLegLeftBusy) currentBusy |= BusyLimbs.LegLeft;
            if (character.Busy.IsLegRightBusy) currentBusy |= BusyLimbs.LegRight;

            BroadcastBusyToClients?.Invoke(new NetworkBusyBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                CurrentBusyLimbs = currentBusy,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            });
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
            if (character == null)
            {
                CachePendingBusyBroadcast(broadcast);
                return;
            }

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
            TryRequestInteraction(
                characterNetworkId,
                targetNetworkId,
                targetHash,
                interactionPosition,
                InteractionType.Generic,
                out _,
                callback);
        }

        /// <summary>
        /// [Client] Request a typed interaction while preserving the generated request context.
        /// Returns false when this controller cannot issue the request locally.
        /// </summary>
        public bool TryRequestInteraction(
            uint characterNetworkId,
            uint targetNetworkId,
            int targetHash,
            Vector3 interactionPosition,
            InteractionType interactionType,
            out NetworkInteractionRequest request,
            Action<NetworkInteractionResponse> callback = null)
        {
            request = default;
            if (!m_IsClient || characterNetworkId == 0) return false;
            if (!IsFinite(interactionPosition) || !IsSupportedInteractionType(interactionType))
            {
                return false;
            }

            request = new NetworkInteractionRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                TargetNetworkId = targetNetworkId,
                TargetHash = targetHash,
                InteractionType = interactionType,
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
            return true;
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
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.CharacterNotFound, 0);
                m_Stats.InteractionRejected++;
                return;
            }

            if (!IsFinite(request.InteractionPosition) || !IsFinite(request.ClientTime))
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.SecurityViolation, 0);
                m_Stats.InteractionRejected++;
                return;
            }

            if (!IsSupportedInteractionType(request.InteractionType))
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.InvalidValue, 0);
                m_Stats.InteractionRejected++;
                return;
            }

            float currentTime = GetServerTime?.Invoke() ?? Time.time;

            if (!TryResolveServerInteractionTarget(
                    character,
                    request,
                    out IInteractive target,
                    out uint resolvedTargetNetworkId,
                    out int resolvedTargetHash))
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.TargetNotFound, 0);
                m_Stats.InteractionRejected++;
                return;
            }

            // Check interaction cooldown
            uint targetCooldownId = resolvedTargetNetworkId != 0
                ? resolvedTargetNetworkId
                : unchecked((uint)resolvedTargetHash);
            if (targetCooldownId == 0) targetCooldownId = 1;
            var cooldownKey = (request.CharacterNetworkId, targetCooldownId);
            if (m_InteractionCooldowns.TryGetValue(cooldownKey, out float cooldownEnd) && currentTime < cooldownEnd)
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.OnCooldown, 0,
                    resolvedTargetNetworkId, resolvedTargetHash);
                m_Stats.InteractionRejected++;
                return;
            }

            // Validate the server-resolved target position. The client position is only a hint
            // used to disambiguate unkeyed scene interactions and is never authoritative.
            float distance = Vector3.Distance(character.transform.position, target.Position);
            if (!IsFinite(distance) || distance > Mathf.Max(0f, m_MaxInteractionRange))
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.OutOfRange, 0,
                    resolvedTargetNetworkId, resolvedTargetHash);
                m_Stats.InteractionRejected++;
                return;
            }


            if (target.IsInteracting)
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.TargetBusy, 0,
                    resolvedTargetNetworkId, resolvedTargetHash);
                m_Stats.InteractionRejected++;
                return;
            }

            // Check if character can interact
            if (character.Interaction.Target == target && !character.Interaction.CanInteract)
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.CharacterBusy, 0,
                    resolvedTargetNetworkId, resolvedTargetHash);
                m_Stats.InteractionRejected++;
                return;
            }

            // Execute the server-resolved target exactly once. Prefer GC2's Character API when
            // its current focus matches so Character.EventInteract subscribers are preserved.
            bool interacted;
            if (character.Interaction.Target == target)
            {
                interacted = character.Interaction.Interact();
            }
            else
            {
                target.Interact(character);
                interacted = true;
            }

            if (!interacted)
            {
                SendInteractionResponse(senderNetworkId, request, false,
                    InteractionRejectReason.ConditionsFailed, 0,
                    resolvedTargetNetworkId, resolvedTargetHash);
                m_Stats.InteractionRejected++;
                return;
            }

            // Update cooldown
            m_InteractionCooldowns[cooldownKey] = currentTime + m_InteractionCooldown;

            const int resultData = 0;

            // Send response
            SendInteractionResponse(senderNetworkId, request, true,
                InteractionRejectReason.None, resultData,
                resolvedTargetNetworkId, resolvedTargetHash);
            m_Stats.InteractionApproved++;

            // Broadcast
            var broadcast = new NetworkInteractionBroadcast
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                CharacterNetworkId = request.CharacterNetworkId,
                TargetNetworkId = resolvedTargetNetworkId,
                TargetHash = resolvedTargetHash,
                InteractionType = request.InteractionType,
                ResultData = resultData,
                ServerTime = currentTime
            };

            BroadcastInteractionToClients?.Invoke(broadcast);
        }

        private bool TryResolveServerInteractionTarget(
            Character character,
            NetworkInteractionRequest request,
            out IInteractive target,
            out uint targetNetworkId,
            out int targetHash)
        {
            target = null;
            targetNetworkId = 0;
            targetHash = 0;
            if (character == null) return false;

            // GC2's authoritative focus is the first choice. Dedicated servers can have visual
            // presentation disabled while still retaining this gameplay query.
            IInteractive focused = character.Interaction.Target;
            if (MatchesInteractionTarget(focused, request, out targetNetworkId, out targetHash))
            {
                target = focused;
                return true;
            }

            var candidates = new List<ISpatialHash>(16);
            SpatialHashInteractions.Find(
                character.transform.position,
                Mathf.Max(0f, m_MaxInteractionRange),
                candidates);

            float bestDistance = float.MaxValue;
            uint bestNetworkId = 0;
            int bestHash = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i] is not IInteractive candidate) continue;
                if (!MatchesInteractionTarget(
                        candidate,
                        request,
                        out uint candidateNetworkId,
                        out int candidateHash))
                {
                    continue;
                }

                float claimedDistance =
                    (candidate.Position - request.InteractionPosition).sqrMagnitude;
                bool hasStableKey = request.TargetNetworkId != 0 || request.TargetHash != 0;
                float tolerance = Mathf.Max(0.01f, m_InteractionPositionTolerance);
                if (!hasStableKey && claimedDistance > tolerance * tolerance) continue;
                if (claimedDistance >= bestDistance) continue;

                target = candidate;
                bestDistance = claimedDistance;
                bestNetworkId = candidateNetworkId;
                bestHash = candidateHash;
            }

            targetNetworkId = bestNetworkId;
            targetHash = bestHash;
            return target != null;
        }

        private bool MatchesInteractionTarget(
            IInteractive target,
            NetworkInteractionRequest request,
            out uint targetNetworkId,
            out int targetHash)
        {
            targetNetworkId = 0;
            targetHash = 0;
            if (target?.Instance == null) return false;

            targetNetworkId = GetNetworkIdForGameObject?.Invoke(target.Instance) ?? 0;
            targetHash = targetNetworkId == 0
                ? GetStableInteractionTargetHash(target.Instance)
                : 0;

            if (request.TargetNetworkId != 0 &&
                request.TargetNetworkId != targetNetworkId)
            {
                return false;
            }

            return request.TargetHash == 0 || request.TargetHash == targetHash;
        }

        public static int GetStableInteractionTargetHash(GameObject target)
        {
            if (target == null) return 0;

            string path = $"{target.transform.GetSiblingIndex()}:{target.name}";
            Transform parent = target.transform.parent;
            while (parent != null)
            {
                path = $"{parent.GetSiblingIndex()}:{parent.name}/{path}";
                parent = parent.parent;
            }

            string scene = !string.IsNullOrEmpty(target.scene.path)
                ? target.scene.path
                : target.scene.name;
            return StableHashUtility.GetStableHash($"{scene}|{path}|CoreInteraction");
        }

        private void SendInteractionResponse(
            uint clientId,
            NetworkInteractionRequest request,
            bool approved,
            InteractionRejectReason reason,
            int resultData)
        {
            SendInteractionResponse(
                clientId,
                request,
                approved,
                reason,
                resultData,
                request.TargetNetworkId,
                request.TargetHash);
        }

        private void SendInteractionResponse(
            uint clientId,
            NetworkInteractionRequest request,
            bool approved,
            InteractionRejectReason reason,
            int resultData,
            uint targetNetworkId,
            int targetHash)
        {
            var response = new NetworkInteractionResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                CharacterNetworkId = request.CharacterNetworkId,
                TargetNetworkId = targetNetworkId,
                TargetHash = targetHash,
                InteractionType = request.InteractionType,
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
                try
                {
                    pending.Callback?.Invoke(response);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }

            NetworkInteractionEvents.RaiseResponse(response);
            OnInteractionResponseReceived?.Invoke(response);
        }

        /// <summary>
        /// [Client] Handle interaction broadcast from server.
        /// </summary>
        public void ReceiveInteractionBroadcast(NetworkInteractionBroadcast broadcast)
        {
            if (m_IsServer) return;

            // Interaction effects/animations can be triggered here
            NetworkInteractionEvents.RaiseBroadcast(broadcast);
            OnInteractionBroadcastReceived?.Invoke(broadcast);
        }

        private static bool IsSupportedInteractionType(InteractionType interactionType)
        {
            return interactionType == InteractionType.Generic ||
                   interactionType == InteractionType.Pickup ||
                   interactionType == InteractionType.Use ||
                   interactionType == InteractionType.Open ||
                   interactionType == InteractionType.Close ||
                   interactionType == InteractionType.Talk ||
                   interactionType == InteractionType.Read ||
                   interactionType == InteractionType.Custom;
        }

        /// <summary>[Client] Handle an interaction focus/blur presentation broadcast.</summary>
        public void ReceiveInteractionFocusBroadcast(NetworkInteractionFocusBroadcast broadcast)
        {
            if (m_IsServer) return;
            OnInteractionFocusBroadcastReceived?.Invoke(broadcast);
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

            ApplyNetworkInvincibility(character, duration > 0f, duration);

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
