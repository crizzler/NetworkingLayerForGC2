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
        // RAGDOLL - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start ragdoll on a character.
        /// </summary>
        public void RequestStartRagdoll(uint characterNetworkId, Vector3 force = default, 
            Vector3 forcePoint = default, Action<NetworkRagdollResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkRagdollRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ClientTime = GetServerTime?.Invoke() ?? Time.time,
                ActionType = force != default ? RagdollActionType.StartRagdollWithForce : RagdollActionType.StartRagdoll,
                Force = force,
                ForcePoint = forcePoint
            };
            
            m_PendingRagdollRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingRagdollRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.RagdollRequestsSent++;
            SendRagdollRequestToServer?.Invoke(request);
            OnRagdollRequestSent?.Invoke(request);
        }
        
        /// <summary>
        /// [Client] Request to start recovery from ragdoll.
        /// </summary>
        public void RequestStartRecover(uint characterNetworkId, bool instant = false,
            Action<NetworkRagdollResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkRagdollRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ClientTime = GetServerTime?.Invoke() ?? Time.time,
                ActionType = instant ? RagdollActionType.InstantRecover : RagdollActionType.StartRecover
            };
            
            m_PendingRagdollRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingRagdollRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.RagdollRequestsSent++;
            SendRagdollRequestToServer?.Invoke(request);
            OnRagdollRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RAGDOLL - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process ragdoll request from client.
        /// </summary>
        public void ProcessRagdollRequest(uint senderNetworkId, NetworkRagdollRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.RagdollRequestsReceived++;
            OnRagdollRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false, RagdollRejectReason.CharacterNotFound, request.ActorNetworkId, request.CorrelationId);
                return;
            }

            if (!Enum.IsDefined(typeof(RagdollActionType), request.ActionType) ||
                !IsFinite(request.ClientTime) ||
                !IsFinite(request.Force) ||
                !IsFinite(request.ForcePoint) ||
                request.Force.magnitude > Mathf.Max(0f, m_MaxRagdollForce) ||
                (request.ActionType == RagdollActionType.StartRagdollWithForce &&
                 Vector3.Distance(character.transform.position, request.ForcePoint) >
                 Mathf.Max(0f, m_MaxRagdollForcePointDistance)))
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false,
                    RagdollRejectReason.SecurityViolation,
                    request.ActorNetworkId, request.CorrelationId);
                m_Stats.RagdollRejected++;
                return;
            }
            
            // Check cooldown
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            if (m_RagdollCooldowns.TryGetValue(request.CharacterNetworkId, out float cooldownEnd) && currentTime < cooldownEnd)
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false, RagdollRejectReason.Cooldown, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            // Validate based on action type
            bool canPerform = false;
            RagdollRejectReason rejectReason = RagdollRejectReason.None;
            
            switch (request.ActionType)
            {
                case RagdollActionType.StartRagdoll:
                case RagdollActionType.StartRagdollWithForce:
                    canPerform = !character.Ragdoll.IsRagdoll;
                    rejectReason = canPerform ? RagdollRejectReason.None : RagdollRejectReason.AlreadyRagdoll;
                    break;
                    
                case RagdollActionType.StartRecover:
                case RagdollActionType.InstantRecover:
                    canPerform = character.Ragdoll.IsRagdoll;
                    rejectReason = canPerform ? RagdollRejectReason.None : RagdollRejectReason.NotRagdoll;
                    break;
            }
            
            if (!canPerform)
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false, rejectReason, request.ActorNetworkId, request.CorrelationId);
                m_Stats.RagdollRejected++;
                return;
            }
            
            // Apply ragdoll action
            ApplyRagdollAction(character, request);
            
            // Update cooldown
            m_RagdollCooldowns[request.CharacterNetworkId] = currentTime + m_RagdollCooldown;
            
            // Send response
            SendRagdollResponse(senderNetworkId, request.RequestId, true, RagdollRejectReason.None, request.ActorNetworkId, request.CorrelationId);
            m_Stats.RagdollApproved++;
            
            // Broadcast to all clients
            var broadcast = new NetworkRagdollBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                ActionType = request.ActionType,
                ServerTime = currentTime,
                Force = request.Force,
                ForcePoint = request.ForcePoint
            };
            
            BroadcastRagdollToClients?.Invoke(broadcast);
        }
        
        private void ApplyRagdollAction(Character character, NetworkRagdollRequest request)
        {
            switch (request.ActionType)
            {
                case RagdollActionType.StartRagdoll:
                    _ = character.Ragdoll.StartRagdoll(); // Fire-and-forget async
                    break;
                    
                case RagdollActionType.StartRagdollWithForce:
                    _ = ApplyRagdollWithForceAsync(character, request.Force, request.ForcePoint);
                    break;
                    
                case RagdollActionType.StartRecover:
                    _ = character.Ragdoll.StartRecover(); // Fire-and-forget async
                    break;
                    
                case RagdollActionType.InstantRecover:
                    // Force immediate recovery
                    _ = character.Ragdoll.StartRecover(); // Fire-and-forget async
                    break;
            }
        }
        
        private async System.Threading.Tasks.Task ApplyRagdollWithForceAsync(Character character, Vector3 force, Vector3 forcePoint)
        {
            await character.Ragdoll.StartRagdoll();
            
            // Apply force after ragdoll starts
            if (force != Vector3.zero)
            {
                // Small delay to let ragdoll physics activate
                await System.Threading.Tasks.Task.Yield();
                
                var rigidbodies = character.GetComponentsInChildren<Rigidbody>();
                foreach (var rb in rigidbodies)
                {
                    rb.AddForceAtPosition(force, forcePoint, ForceMode.Impulse);
                }
            }
        }
        
        private void SendRagdollResponse(uint clientId, ushort requestId, bool approved, RagdollRejectReason reason,
            uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkRagdollResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason
            };
            
            SendRagdollResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle ragdoll response from server.
        /// </summary>
        public void ReceiveRagdollResponse(NetworkRagdollResponse response)
        {
            if (!m_IsClient) return;
            
            ulong pendingKey = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingRagdollRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingRagdollRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnRagdollResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle ragdoll broadcast from server.
        /// </summary>
        public void ReceiveRagdollBroadcast(NetworkRagdollBroadcast broadcast)
        {
            // A host already applied the authoritative action on its server path.
            if (m_IsServer) return;

            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null)
            {
                CachePendingRagdollBroadcast(broadcast);
                return;
            }
            
            var request = new NetworkRagdollRequest
            {
                ActionType = broadcast.ActionType,
                Force = broadcast.Force,
                ForcePoint = broadcast.ForcePoint
            };
            
            ApplyRagdollAction(character, request);
            OnRagdollBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPS - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to attach a prop prefab.
        /// </summary>
        public void RequestAttachProp(uint characterNetworkId, int propHash, int boneHash,
            Vector3 localPosition = default, Quaternion localRotation = default,
            Action<NetworkPropResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPropRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.AttachPrefab,
                PropHash = propHash,
                BoneHash = boneHash,
                LocalPosition = localPosition
            };
            request.SetLocalRotation(localRotation == default ? Quaternion.identity : localRotation);
            
            m_PendingPropRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingPropRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PropRequestsSent++;
            SendPropRequestToServer?.Invoke(request);
            OnPropRequestSent?.Invoke(request);
        }
        
        /// <summary>
        /// [Client] Request to detach a prop.
        /// </summary>
        public void RequestDetachProp(uint characterNetworkId, int propHash,
            Action<NetworkPropResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPropRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.DetachPrefab,
                PropHash = propHash,
                PropInstanceId = 0
            };
            
            m_PendingPropRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingPropRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PropRequestsSent++;
            SendPropRequestToServer?.Invoke(request);
            OnPropRequestSent?.Invoke(request);
        }

        /// <summary>
        /// [Client] Request removal of one exact server-assigned prop instance.
        /// </summary>
        public void RequestDetachPropInstance(uint characterNetworkId, int propInstanceId,
            Action<NetworkPropResponse> callback = null)
        {
            SendPropActionRequest(characterNetworkId, PropActionType.DetachInstance, 0,
                propInstanceId, callback);
        }

        /// <summary>[Client] Request removal of every network-managed prop on a character.</summary>
        public void RequestDetachAllProps(uint characterNetworkId,
            Action<NetworkPropResponse> callback = null)
        {
            SendPropActionRequest(characterNetworkId, PropActionType.DetachAll, 0, 0, callback);
        }

        private void SendPropActionRequest(uint characterNetworkId, PropActionType actionType,
            int propHash, int propInstanceId, Action<NetworkPropResponse> callback)
        {
            if (!m_IsClient) return;

            var request = new NetworkPropRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, m_LastIssuedRequestId),
                CharacterNetworkId = characterNetworkId,
                ActionType = actionType,
                PropHash = propHash,
                PropInstanceId = propInstanceId
            };

            m_PendingPropRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] =
                new PendingPropRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback
                };

            m_Stats.PropRequestsSent++;
            SendPropRequestToServer?.Invoke(request);
            OnPropRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPS - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process prop request from client.
        /// </summary>
        public void ProcessPropRequest(uint senderNetworkId, NetworkPropRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.PropRequestsReceived++;
            OnPropRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendPropResponse(senderNetworkId, request.RequestId, false, PropRejectReason.CharacterNotFound, 0, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            PropRejectReason rejectReason;
            int propInstanceId;
            NetworkPropBroadcast broadcast;
            bool approved = TryApplyServerPropAction(character, request, out rejectReason,
                out propInstanceId, out broadcast);

            if (!approved)
            {
                SendPropResponse(senderNetworkId, request.RequestId, false, rejectReason, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.PropRejected++;
                return;
            }
            
            // Send response
            SendPropResponse(senderNetworkId, request.RequestId, true, PropRejectReason.None, propInstanceId, request.ActorNetworkId, request.CorrelationId);
            m_Stats.PropApproved++;
            
            BroadcastPropToClients?.Invoke(broadcast);
        }

        private bool TryApplyServerPropAction(Character character, NetworkPropRequest request,
            out PropRejectReason rejectReason, out int propInstanceId,
            out NetworkPropBroadcast broadcast)
        {
            rejectReason = PropRejectReason.None;
            propInstanceId = 0;
            broadcast = default;

            List<NetworkPropAttachmentState> props = GetOrCreatePropStates(request.CharacterNetworkId);

            switch (request.ActionType)
            {
                case PropActionType.AttachInstance:
                    rejectReason = PropRejectReason.UnsupportedAction;
                    return false;

                case PropActionType.AttachPrefab:
                {
                    float maxOffset = Mathf.Max(0f, m_MaxPropLocalOffset);
                    if (!IsFinite(request.LocalPosition) ||
                        request.LocalPosition.sqrMagnitude > maxOffset * maxOffset)
                    {
                        rejectReason = PropRejectReason.SecurityViolation;
                        return false;
                    }

                    GameObject prefab = GetPropPrefabByHash?.Invoke(request.PropHash);
                    if (prefab == null)
                    {
                        rejectReason = PropRejectReason.PropNotFound;
                        return false;
                    }

                    if (props.Count >= Mathf.Max(0, m_MaxPropsPerCharacter))
                    {
                        rejectReason = PropRejectReason.MaxPropsReached;
                        return false;
                    }

                    Transform bone = ResolveBone(character, request.BoneHash);
                    if (bone == null)
                    {
                        rejectReason = PropRejectReason.BoneNotFound;
                        return false;
                    }

                    propInstanceId = AllocatePropInstanceId();
                    var state = CreateAttachmentState(request, propInstanceId);
                    props.Add(state);

                    // Dedicated servers own the descriptor but do not need a cosmetic instance.
                    if (m_IsClient && !ApplyPropAttach(character, state))
                    {
                        props.RemoveAt(props.Count - 1);
                        rejectReason = PropRejectReason.BoneNotFound;
                        propInstanceId = 0;
                        return false;
                    }

                    broadcast = state.ToBroadcast(PropActionType.AttachPrefab);
                    return true;
                }

                case PropActionType.DetachPrefab:
                {
                    int index = FindPropIndexByHash(props, request.PropHash);
                    if (index < 0)
                    {
                        rejectReason = PropRejectReason.NotAttached;
                        return false;
                    }

                    NetworkPropAttachmentState state = props[index];
                    propInstanceId = state.PropInstanceId;
                    if (m_IsClient) RemoveLocalProp(character, state);
                    props.RemoveAt(index);
                    broadcast = state.ToBroadcast(PropActionType.DetachPrefab);
                    return true;
                }

                case PropActionType.DetachInstance:
                {
                    int index = FindPropIndexByInstance(props, request.PropInstanceId);
                    if (index < 0)
                    {
                        rejectReason = PropRejectReason.NotAttached;
                        return false;
                    }

                    NetworkPropAttachmentState state = props[index];
                    propInstanceId = state.PropInstanceId;
                    if (m_IsClient) RemoveLocalProp(character, state);
                    props.RemoveAt(index);
                    broadcast = state.ToBroadcast(PropActionType.DetachInstance);
                    return true;
                }

                case PropActionType.DetachAll:
                    if (m_IsClient) RemoveAllLocalProps(character, props);
                    props.Clear();
                    broadcast = new NetworkPropBroadcast
                    {
                        CharacterNetworkId = request.CharacterNetworkId,
                        ActionType = PropActionType.DetachAll
                    };
                    return true;

                default:
                    rejectReason = PropRejectReason.UnsupportedAction;
                    return false;
            }
        }

        private int AllocatePropInstanceId()
        {
            if (m_NextPropInstanceId <= 0) m_NextPropInstanceId = 1;
            int result = m_NextPropInstanceId++;
            if (m_NextPropInstanceId <= 0) m_NextPropInstanceId = 1;
            return result;
        }

        private static NetworkPropAttachmentState CreateAttachmentState(NetworkPropRequest request, int instanceId)
        {
            return new NetworkPropAttachmentState
            {
                CharacterNetworkId = request.CharacterNetworkId,
                PropInstanceId = instanceId,
                PropHash = request.PropHash,
                BoneHash = request.BoneHash,
                LocalPosition = request.LocalPosition,
                RotationX = request.RotationX,
                RotationY = request.RotationY,
                RotationZ = request.RotationZ
            };
        }

        private List<NetworkPropAttachmentState> GetOrCreatePropStates(uint characterNetworkId)
        {
            if (!m_CharacterProps.TryGetValue(characterNetworkId, out List<NetworkPropAttachmentState> props))
            {
                props = new List<NetworkPropAttachmentState>();
                m_CharacterProps.Add(characterNetworkId, props);
            }

            return props;
        }

        private static int FindPropIndexByHash(List<NetworkPropAttachmentState> props, int propHash)
        {
            for (int i = 0; i < props.Count; i++)
            {
                if (props[i].PropHash == propHash) return i;
            }

            return -1;
        }

        private static int FindPropIndexByInstance(List<NetworkPropAttachmentState> props, int instanceId)
        {
            if (instanceId <= 0) return -1;
            for (int i = 0; i < props.Count; i++)
            {
                if (props[i].PropInstanceId == instanceId) return i;
            }

            return -1;
        }

        private Transform ResolveBone(Character character, int boneHash)
        {
            if (character == null) return null;
            if (boneHash == 0) return character.transform;

            Transform resolved = GetBoneByHashForCharacter?.Invoke(character, boneHash);
#pragma warning disable CS0618
            if (resolved == null) resolved = GetBoneByHash?.Invoke(boneHash);
#pragma warning restore CS0618
            return resolved;
        }

        private bool ApplyPropAttach(Character character, NetworkPropAttachmentState state)
        {
            if (character == null) return false;

            NetworkPropTracker[] trackers = character.GetComponentsInChildren<NetworkPropTracker>(true);
            for (int i = 0; i < trackers.Length; i++)
            {
                if (trackers[i].InstanceId == state.PropInstanceId) return true;
            }

            GameObject prefab = GetPropPrefabByHash?.Invoke(state.PropHash);
            if (prefab == null) return false;

            Transform bone = ResolveBone(character, state.BoneHash);
            if (bone == null) return false;

            GameObject instance = character.Props.AttachPrefab(
                new ResolvedNetworkBone(bone),
                prefab,
                state.LocalPosition,
                state.GetLocalRotation());
            if (instance == null) return false;

            NetworkPropTracker tracker = instance.GetComponent<NetworkPropTracker>();
            if (tracker == null) tracker = instance.AddComponent<NetworkPropTracker>();
            tracker.InstanceId = state.PropInstanceId;
            tracker.PropHash = state.PropHash;
            tracker.CharacterNetworkId = state.CharacterNetworkId;
            return true;
        }

        private void RemoveLocalProp(Character character, NetworkPropAttachmentState state)
        {
            if (character == null) return;
            NetworkPropTracker[] trackers = character.GetComponentsInChildren<NetworkPropTracker>(true);
            for (int i = 0; i < trackers.Length; i++)
            {
                NetworkPropTracker tracker = trackers[i];
                if (tracker.InstanceId != state.PropInstanceId) continue;

                GameObject prefab = GetPropPrefabByHash?.Invoke(state.PropHash);
                if (prefab != null)
                {
                    character.Props.RemovePrefab(prefab, tracker.gameObject.GetInstanceID());
                }
                else
                {
                    Destroy(tracker.gameObject);
                }
                return;
            }
        }

        private void RemoveAllLocalProps(Character character, List<NetworkPropAttachmentState> props)
        {
            for (int i = props.Count - 1; i >= 0; i--)
            {
                RemoveLocalProp(character, props[i]);
            }
        }

        private void ClearAllTrackedPropState()
        {
            foreach (KeyValuePair<uint, List<NetworkPropAttachmentState>> pair in m_CharacterProps)
            {
                Character character = GetCharacterByNetworkId?.Invoke(pair.Key);
                if (character != null) RemoveAllLocalProps(character, pair.Value);
            }

            m_CharacterProps.Clear();
            m_NextPropInstanceId = 1;
        }

        /// <summary>
        /// Remove all persistent Core state associated with a despawned character. The server
        /// emits detach-all first so peers that retain the character for another frame converge
        /// before a transport reuses its ID.
        /// </summary>
        public void ForgetCharacterState(uint characterNetworkId, bool broadcastDetachAll)
        {
            if (characterNetworkId == 0) return;

            if (m_CharacterProps.TryGetValue(
                    characterNetworkId,
                    out List<NetworkPropAttachmentState> props))
            {
                Character character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
                if (character != null) RemoveAllLocalProps(character, props);
                m_CharacterProps.Remove(characterNetworkId);

                if (broadcastDetachAll && m_IsServer && props.Count > 0)
                {
                    BroadcastPropToClients?.Invoke(new NetworkPropBroadcast
                    {
                        CharacterNetworkId = characterNetworkId,
                        ActionType = PropActionType.DetachAll
                    });
                }
            }

            m_PendingCoreSnapshots.Remove(characterNetworkId);
            DiscardPendingPersistentBroadcasts(characterNetworkId);
            m_InvincibilityCooldowns.Remove(characterNetworkId);
            m_RagdollCooldowns.Remove(characterNetworkId);

            var cooldownKeys = new List<(uint, uint)>();
            foreach ((uint CharacterId, uint TargetId) key in m_InteractionCooldowns.Keys)
            {
                if (key.CharacterId == characterNetworkId || key.TargetId == characterNetworkId)
                {
                    cooldownKeys.Add(key);
                }
            }

            for (int i = 0; i < cooldownKeys.Count; i++)
            {
                m_InteractionCooldowns.Remove(cooldownKeys[i]);
            }
        }
        
        private void SendPropResponse(uint clientId, ushort requestId, bool approved, PropRejectReason reason, int instanceId,
            uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkPropResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                PropInstanceId = instanceId
            };
            
            SendPropResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle prop response from server.
        /// </summary>
        public void ReceivePropResponse(NetworkPropResponse response)
        {
            if (!m_IsClient) return;
            
            ulong pendingKey = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingPropRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingPropRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnPropResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle prop broadcast from server.
        /// </summary>
        public void ReceivePropBroadcast(NetworkPropBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null)
            {
                CachePendingPropBroadcast(broadcast);
                return;
            }

            List<NetworkPropAttachmentState> props = GetOrCreatePropStates(broadcast.CharacterNetworkId);
            var state = new NetworkPropAttachmentState
            {
                CharacterNetworkId = broadcast.CharacterNetworkId,
                PropInstanceId = broadcast.PropInstanceId,
                PropHash = broadcast.PropHash,
                BoneHash = broadcast.BoneHash,
                LocalPosition = broadcast.LocalPosition,
                RotationX = broadcast.RotationX,
                RotationY = broadcast.RotationY,
                RotationZ = broadcast.RotationZ
            };

            switch (broadcast.ActionType)
            {
                case PropActionType.AttachPrefab:
                    if (FindPropIndexByInstance(props, state.PropInstanceId) < 0 && ApplyPropAttach(character, state))
                    {
                        props.Add(state);
                    }
                    break;
                    
                case PropActionType.DetachPrefab:
                case PropActionType.DetachInstance:
                {
                    int index = FindPropIndexByInstance(props, state.PropInstanceId);
                    if (index < 0 && broadcast.ActionType == PropActionType.DetachPrefab)
                    {
                        index = FindPropIndexByHash(props, state.PropHash);
                    }
                    if (index >= 0)
                    {
                        NetworkPropAttachmentState existing = props[index];
                        RemoveLocalProp(character, existing);
                        props.RemoveAt(index);
                    }
                    break;
                }

                case PropActionType.DetachAll:
                    RemoveAllLocalProps(character, props);
                    props.Clear();
                    break;
            }
            
            OnPropBroadcastReceived?.Invoke(broadcast);
        }

        /// <summary>
        /// [Server] Attach a registered prefab without a client request and broadcast the result.
        /// </summary>
        public bool TryServerAttachProp(uint characterNetworkId, int propHash, int boneHash,
            Vector3 localPosition, Quaternion localRotation, out int propInstanceId,
            out PropRejectReason rejectReason)
        {
            propInstanceId = 0;
            rejectReason = PropRejectReason.NotAuthorized;
            if (!m_IsServer) return false;

            Character character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null)
            {
                rejectReason = PropRejectReason.CharacterNotFound;
                return false;
            }

            var request = new NetworkPropRequest
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.AttachPrefab,
                PropHash = propHash,
                BoneHash = boneHash,
                LocalPosition = localPosition
            };
            request.SetLocalRotation(localRotation == default ? Quaternion.identity : localRotation);

            bool approved = TryApplyServerPropAction(character, request, out rejectReason,
                out propInstanceId, out NetworkPropBroadcast broadcast);
            if (approved) BroadcastPropToClients?.Invoke(broadcast);
            return approved;
        }

        /// <summary>[Server] Detach one exact network prop and broadcast the result.</summary>
        public bool TryServerDetachProp(uint characterNetworkId, int propInstanceId,
            out PropRejectReason rejectReason)
        {
            rejectReason = PropRejectReason.NotAuthorized;
            if (!m_IsServer) return false;

            Character character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null)
            {
                rejectReason = PropRejectReason.CharacterNotFound;
                return false;
            }

            var request = new NetworkPropRequest
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.DetachInstance,
                PropInstanceId = propInstanceId
            };

            bool approved = TryApplyServerPropAction(character, request, out rejectReason,
                out _, out NetworkPropBroadcast broadcast);
            if (approved) BroadcastPropToClients?.Invoke(broadcast);
            return approved;
        }

        /// <summary>[Server] Detach every network prop from a character.</summary>
        public bool ServerDetachAllProps(uint characterNetworkId, out PropRejectReason rejectReason)
        {
            rejectReason = PropRejectReason.NotAuthorized;
            if (!m_IsServer) return false;

            Character character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null)
            {
                rejectReason = PropRejectReason.CharacterNotFound;
                return false;
            }

            var request = new NetworkPropRequest
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.DetachAll
            };
            bool approved = TryApplyServerPropAction(character, request, out rejectReason,
                out _, out NetworkPropBroadcast broadcast);
            if (approved) BroadcastPropToClients?.Invoke(broadcast);
            return approved;
        }
        
    }

    internal sealed class ResolvedNetworkBone : IBone
    {
        private readonly Transform m_Transform;

        public ResolvedNetworkBone(Transform transform)
        {
            m_Transform = transform;
        }

        public Transform GetTransform(Animator animator)
        {
            return m_Transform;
        }
    }
}
