#if GC2_SHOOTER
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterManager
    {
        public void ReceiveReloadRequest(uint clientNetworkId, NetworkReloadRequest request)
        {
            if (!m_IsServer) return;
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkReloadRequest)))
            {
                SendReloadResponseToClient?.Invoke(clientNetworkId, new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.CharacterNetworkId, nameof(NetworkReloadRequest), nameof(request.CharacterNetworkId)))
            {
                SendReloadResponseToClient?.Invoke(clientNetworkId, new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.ReloadRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerReloadQueue,
                    m_MaxReloadQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkReloadRequest)))
            {
                SendReloadResponseToClient?.Invoke(clientNetworkId, new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.RateLimitExceeded
                });
                return;
            }
            
            m_ServerReloadQueue.Enqueue(new QueuedReloadRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerReloadQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerReloadQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale reload requests");
            }

            while (m_ServerReloadQueue.Count > 0)
            {
                var queued = m_ServerReloadQueue.Dequeue();
                ProcessReloadRequest(queued);
            }
        }
        
        private void ProcessReloadRequest(QueuedReloadRequest queued)
        {
            var request = queued.Request;
            NetworkReloadResponse response;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.CharacterNetworkId)
            {
                response = new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.CheatSuspected
                };
                SendReloadResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }
            
            if (m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                response = controller.ProcessReloadRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.CharacterNotFound
                };
            }

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            SendReloadResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.ReloadsValidated++;
                
                // Broadcast reload started
                var broadcast = new NetworkReloadBroadcast
                {
                    CharacterNetworkId = request.ActorNetworkId,
                    WeaponHash = request.WeaponHash,
                    NewAmmoCount = 0,
                    EventType = ReloadEventType.Started
                };
                
                BroadcastReloadToAllClients?.Invoke(broadcast);
                OnReloadValidated?.Invoke(broadcast);
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Reload broadcast: {request.ActorNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a reload response.
        /// </summary>
        public void ReceiveReloadResponse(NetworkReloadResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveReloadResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a reload event.
        /// </summary>
        public void ReceiveReloadBroadcast(NetworkReloadBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received reload broadcast: {broadcast.CharacterNetworkId}, Event: {broadcast.EventType}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveReloadBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // JAM / FIX NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a fix jam request is received from a client.
        /// </summary>
        public void ReceiveFixJamRequest(uint clientNetworkId, NetworkFixJamRequest request)
        {
            if (!m_IsServer) return;
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkFixJamRequest)))
            {
                SendFixJamResponseToClient?.Invoke(clientNetworkId, new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.CharacterNetworkId, nameof(NetworkFixJamRequest), nameof(request.CharacterNetworkId)))
            {
                SendFixJamResponseToClient?.Invoke(clientNetworkId, new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.FixJamRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerFixJamQueue,
                    m_MaxFixJamQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkFixJamRequest)))
            {
                SendFixJamResponseToClient?.Invoke(clientNetworkId, new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.RateLimitExceeded
                });
                return;
            }
            
            m_ServerFixJamQueue.Enqueue(new QueuedFixJamRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerFixJamQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerFixJamQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale fix-jam requests");
            }

            while (m_ServerFixJamQueue.Count > 0)
            {
                var queued = m_ServerFixJamQueue.Dequeue();
                ProcessFixJamRequest(queued);
            }
        }
        
        private void ProcessFixJamRequest(QueuedFixJamRequest queued)
        {
            var request = queued.Request;
            NetworkFixJamResponse response;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.CharacterNetworkId)
            {
                response = new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.CheatSuspected
                };
                SendFixJamResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }
            
            if (m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                response = controller.ProcessFixJamRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.CharacterNotFound
                };
            }

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            SendFixJamResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.FixJamsValidated++;
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Fix jam validated: {request.ActorNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Server] Broadcast that a weapon has jammed.
        /// Call this when server determines a jam occurs (e.g., during shot processing).
        /// </summary>
        public void BroadcastJam(uint characterNetworkId, int weaponHash)
        {
            if (!m_IsServer) return;
            
            var broadcast = new NetworkJamBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                WeaponHash = weaponHash
            };
            
            BroadcastJamToAllClients?.Invoke(broadcast);
            OnWeaponJammed?.Invoke(broadcast);
            
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Jam broadcast: {characterNetworkId}");
            }
        }
        
        /// <summary>
        /// [Server] Broadcast that a jam fix has completed.
        /// </summary>
        public void BroadcastFixJamComplete(uint characterNetworkId, int weaponHash, bool success)
        {
            if (!m_IsServer) return;
            
            var broadcast = new NetworkFixJamBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                WeaponHash = weaponHash,
                Success = success
            };
            
            BroadcastFixJamToAllClients?.Invoke(broadcast);
            OnJamFixed?.Invoke(broadcast);
            
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Fix jam complete broadcast: {characterNetworkId}, Success: {success}");
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a fix jam response.
        /// </summary>
        public void ReceiveFixJamResponse(NetworkFixJamResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveFixJamResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a weapon jam.
        /// </summary>
        public void ReceiveJamBroadcast(NetworkJamBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received jam broadcast: {broadcast.CharacterNetworkId}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveJamBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a jam fix complete.
        /// </summary>
        public void ReceiveFixJamBroadcast(NetworkFixJamBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received fix jam broadcast: {broadcast.CharacterNetworkId}, Success: {broadcast.Success}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveFixJamBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARGE NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a charge start request is received from a client.
        /// </summary>
        public void ReceiveChargeStartRequest(uint clientNetworkId, NetworkChargeStartRequest request)
        {
            if (!m_IsServer) return;
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkChargeStartRequest)))
            {
                SendChargeStartResponseToClient?.Invoke(clientNetworkId, new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.CharacterNetworkId, nameof(NetworkChargeStartRequest), nameof(request.CharacterNetworkId)))
            {
                SendChargeStartResponseToClient?.Invoke(clientNetworkId, new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.ChargeRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerChargeQueue,
                    m_MaxChargeQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkChargeStartRequest)))
            {
                SendChargeStartResponseToClient?.Invoke(clientNetworkId, new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.InvalidState
                });
                return;
            }
            
            m_ServerChargeQueue.Enqueue(new QueuedChargeRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerChargeQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerChargeQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale charge requests");
            }

            while (m_ServerChargeQueue.Count > 0)
            {
                var queued = m_ServerChargeQueue.Dequeue();
                ProcessChargeRequest(queued);
            }
        }
        
        private void ProcessChargeRequest(QueuedChargeRequest queued)
        {
            var request = queued.Request;
            NetworkChargeStartResponse response;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.CharacterNetworkId)
            {
                response = new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.CheatSuspected
                };
                SendChargeStartResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }
            
            if (m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                response = controller.ProcessChargeStartRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.CharacterNotFound
                };
            }

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            SendChargeStartResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.ChargesValidated++;
                
                // Broadcast charge started
                var broadcast = new NetworkChargeBroadcast
                {
                    CharacterNetworkId = request.ActorNetworkId,
                    WeaponHash = request.WeaponHash,
                    ChargeRatio = 0,
                    EventType = ChargeEventType.Started
                };
                
                BroadcastChargeToAllClients?.Invoke(broadcast);
                OnChargeStateChanged?.Invoke(broadcast);
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Charge start broadcast: {request.ActorNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Server] Broadcast charge state update.
        /// Call this periodically while a character is charging.
        /// </summary>
        public void BroadcastChargeState(uint characterNetworkId, int weaponHash, float chargeRatio, ChargeEventType eventType)
        {
            if (!m_IsServer) return;
            
            var broadcast = new NetworkChargeBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                WeaponHash = weaponHash,
                ChargeRatio = (byte)(chargeRatio * 255f),
                EventType = eventType
            };
            
            BroadcastChargeToAllClients?.Invoke(broadcast);
            OnChargeStateChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Client] Called when server sends a charge start response.
        /// </summary>
        public void ReceiveChargeStartResponse(NetworkChargeStartResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveChargeStartResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a charge state.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveChargeBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SIGHT SWITCH NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a sight switch request is received from a client.
        /// </summary>
        public void ReceiveSightSwitchRequest(uint clientNetworkId, NetworkSightSwitchRequest request)
        {
            if (!m_IsServer) return;
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkSightSwitchRequest)))
            {
                SendSightSwitchResponseToClient?.Invoke(clientNetworkId, new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.CharacterNetworkId, nameof(NetworkSightSwitchRequest), nameof(request.CharacterNetworkId)))
            {
                SendSightSwitchResponseToClient?.Invoke(clientNetworkId, new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.SightSwitchRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerSightSwitchQueue,
                    m_MaxSightSwitchQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkSightSwitchRequest)))
            {
                SendSightSwitchResponseToClient?.Invoke(clientNetworkId, new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.RateLimitExceeded
                });
                return;
            }
            
            m_ServerSightSwitchQueue.Enqueue(new QueuedSightSwitchRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerSightSwitchQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerSightSwitchQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale sight-switch requests");
            }

            while (m_ServerSightSwitchQueue.Count > 0)
            {
                var queued = m_ServerSightSwitchQueue.Dequeue();
                ProcessSightSwitchRequest(queued);
            }
        }
        
        private void ProcessSightSwitchRequest(QueuedSightSwitchRequest queued)
        {
            var request = queued.Request;
            NetworkSightSwitchResponse response;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.CharacterNetworkId)
            {
                response = new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.CheatSuspected
                };
                SendSightSwitchResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }
            
            if (m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                response = controller.ProcessSightSwitchRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.CharacterNotFound
                };
            }

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            SendSightSwitchResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.SightSwitchesValidated++;
                
                // Broadcast sight switch
                var broadcast = new NetworkSightSwitchBroadcast
                {
                    CharacterNetworkId = request.ActorNetworkId,
                    WeaponHash = request.WeaponHash,
                    NewSightHash = request.NewSightHash
                };
                
                BroadcastSightSwitchToAllClients?.Invoke(broadcast);
                OnSightSwitched?.Invoke(broadcast);
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Sight switch broadcast: {request.ActorNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a sight switch response.
        /// </summary>
        public void ReceiveSightSwitchResponse(NetworkSightSwitchResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveSightSwitchResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a sight switch.
        /// </summary>
        public void ReceiveSightSwitchBroadcast(NetworkSightSwitchBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received sight switch broadcast: {broadcast.CharacterNetworkId}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveSightSwitchBroadcast(broadcast);
            }
        }
    }
}
#endif
