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
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            // Skip if we're the server (already applied)
            if (m_IsServer) return;
            
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
                PropHash = propHash
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
            
            // Check max props
            if (!m_CharacterProps.TryGetValue(request.CharacterNetworkId, out var propList))
            {
                propList = new List<int>();
                m_CharacterProps[request.CharacterNetworkId] = propList;
            }
            
            PropRejectReason rejectReason = PropRejectReason.None;
            int propInstanceId = 0;
            
            switch (request.ActionType)
            {
                case PropActionType.AttachPrefab:
                case PropActionType.AttachInstance:
                    if (propList.Count >= m_MaxPropsPerCharacter)
                    {
                        rejectReason = PropRejectReason.MaxPropsReached;
                    }
                    else
                    {
                        propInstanceId = m_NextPropInstanceId++;
                        propList.Add(propInstanceId);
                        
                        // Apply prop attachment
                        ApplyPropAttach(character, request, propInstanceId);
                    }
                    break;
                    
                case PropActionType.DetachPrefab:
                case PropActionType.DetachInstance:
                    // Find and remove prop
                    // For simplicity, using propHash as lookup
                    ApplyPropDetach(character, request);
                    break;
                    
                case PropActionType.DetachAll:
                    propList.Clear();
                    // Detach all props from character
                    break;
            }
            
            if (rejectReason != PropRejectReason.None)
            {
                SendPropResponse(senderNetworkId, request.RequestId, false, rejectReason, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.PropRejected++;
                return;
            }
            
            // Send response
            SendPropResponse(senderNetworkId, request.RequestId, true, PropRejectReason.None, propInstanceId, request.ActorNetworkId, request.CorrelationId);
            m_Stats.PropApproved++;
            
            // Broadcast
            var broadcast = new NetworkPropBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                ActionType = request.ActionType,
                PropHash = request.PropHash,
                BoneHash = request.BoneHash,
                PropInstanceId = propInstanceId,
                LocalPosition = request.LocalPosition,
                RotationX = request.RotationX,
                RotationY = request.RotationY,
                RotationZ = request.RotationZ
            };
            
            BroadcastPropToClients?.Invoke(broadcast);
        }
        
        private void ApplyPropAttach(Character character, NetworkPropRequest request, int instanceId)
        {
            var prefab = GetPropPrefabByHash?.Invoke(request.PropHash);
            if (prefab == null) return;
            
            // GC2 Props system uses bone names, we need to resolve bone
            // This is a simplified implementation - full impl would use GC2's Props.AttachPrefab
            var bone = GetBoneByHash?.Invoke(request.BoneHash);
            if (bone == null) bone = character.transform;
            
            var instance = Instantiate(prefab, bone);
            instance.transform.localPosition = request.LocalPosition;
            instance.transform.localRotation = request.GetLocalRotation();
            
            // Tag with instance ID for later removal
            var tracker = instance.AddComponent<NetworkPropTracker>();
            tracker.InstanceId = instanceId;
            tracker.PropHash = request.PropHash;
        }
        
        private void ApplyPropDetach(Character character, NetworkPropRequest request)
        {
            var trackers = character.GetComponentsInChildren<NetworkPropTracker>();
            foreach (var tracker in trackers)
            {
                if (tracker.PropHash == request.PropHash)
                {
                    Destroy(tracker.gameObject);
                    break;
                }
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
            if (character == null) return;
            
            var request = new NetworkPropRequest
            {
                ActionType = broadcast.ActionType,
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
                case PropActionType.AttachInstance:
                    ApplyPropAttach(character, request, broadcast.PropInstanceId);
                    break;
                    
                case PropActionType.DetachPrefab:
                case PropActionType.DetachInstance:
                    ApplyPropDetach(character, request);
                    break;
            }
            
            OnPropBroadcastReceived?.Invoke(broadcast);
        }
        
    }
}
