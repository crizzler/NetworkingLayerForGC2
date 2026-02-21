using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using Arawn.NetworkingCore;
using Arawn.NetworkingCore.LagCompensation;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative combat controller for Game Creator 2.
    /// Intercepts local hits and routes them through server validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component works alongside GC2's combat system without modifying it.
    /// On clients, it intercepts hit detection and sends requests to the server.
    /// On the server, it validates hits using lag compensation and applies damage.
    /// </para>
    /// <para>
    /// <b>Network Flow:</b>
    /// 1. Client detects hit locally (melee striker, projectile raycast)
    /// 2. Instead of applying damage, client sends NetworkHitRequest to server
    /// 3. Server rewinds target to client's timestamp, validates hit geometry
    /// 4. If valid, server applies damage and broadcasts NetworkHitBroadcast
    /// 5. All clients play hit effects (particles, sounds, reactions)
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Network Combat Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT)]
    public class NetworkCombatController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON (Optional - for easy access)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static NetworkCombatController s_Instance;
        
        /// <summary>
        /// Global combat controller instance (if using singleton pattern).
        /// </summary>
        public static NetworkCombatController Instance => s_Instance;
        
        /// <summary>
        /// Whether a global instance exists.
        /// </summary>
        public static bool HasInstance => s_Instance != null;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Network Settings")]
        [Tooltip("Maximum time in the past that hits can be validated (seconds).")]
        [SerializeField] private float m_MaxRewindTime = 0.5f;
        
        [Tooltip("Additional tolerance for hit detection (meters).")]
        [SerializeField] private float m_HitTolerance = 0.2f;
        
        [Tooltip("Send hit requests unreliably for lower latency (may drop).")]
        [SerializeField] private bool m_UseUnreliableHitRequests = false;
        
        [Header("Client Settings")]
        [Tooltip("Show optimistic hit effects before server confirmation.")]
        [SerializeField] private bool m_OptimisticHitEffects = true;
        
        [Tooltip("Timeout for hit response before giving up (seconds).")]
        [SerializeField] private float m_HitResponseTimeout = 1f;
        
        [Header("Server Settings")]
        [Tooltip("Log rejected hits for anti-cheat analysis.")]
        [SerializeField] private bool m_LogRejectedHits = true;
        
        [Tooltip("Maximum hit requests to process per frame.")]
        [SerializeField] private int m_MaxHitsPerFrame = 10;
        
        [Header("Debug")]
        [SerializeField] private bool m_DebugDrawHits = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when a hit request is sent to the server.
        /// </summary>
        public event Action<NetworkHitRequest> OnHitRequestSent;
        
        /// <summary>
        /// [Client] Called when server responds to our hit request.
        /// </summary>
        public event Action<NetworkHitResponse> OnHitResponseReceived;
        
        /// <summary>
        /// [Server] Called when a hit request is received from a client.
        /// </summary>
        public event Action<uint, NetworkHitRequest> OnHitRequestReceived;
        
        /// <summary>
        /// [Server] Called when a hit is validated and damage applied.
        /// </summary>
        public event Action<ValidatedDamage> OnHitValidated;
        
        /// <summary>
        /// [Server] Called when a hit is rejected.
        /// </summary>
        public event Action<NetworkHitRequest, HitResult> OnHitRejected;
        
        /// <summary>
        /// [All] Called when a hit broadcast is received (for effects).
        /// </summary>
        public event Action<NetworkHitBroadcast> OnHitBroadcastReceived;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DELEGATES (Network Integration Points)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Assign this to send hit requests to the server.
        /// </summary>
        public Action<NetworkHitRequest> SendHitRequestToServer;
        
        /// <summary>
        /// Assign this to send hit responses to specific clients.
        /// </summary>
        public Action<uint, NetworkHitResponse> SendHitResponseToClient;
        
        /// <summary>
        /// Assign this to broadcast hits to all clients.
        /// </summary>
        public Action<NetworkHitBroadcast> BroadcastHitToClients;
        
        /// <summary>
        /// Assign this to get the current server time.
        /// </summary>
        public Func<float> GetServerTime;
        
        /// <summary>
        /// Assign this to get a character by network ID.
        /// </summary>
        public Func<uint, Character> GetCharacterByNetworkId;
        
        /// <summary>
        /// Assign this to get the local player's network ID.
        /// </summary>
        public Func<uint> GetLocalPlayerNetworkId;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        
        // Client-side pending requests
        private ushort m_NextRequestId = 1;
        private readonly Dictionary<ushort, PendingHitRequest> m_PendingRequests = new(32);
        
        // Server-side request queue
        private readonly Queue<QueuedHitRequest> m_ServerHitQueue = new(64);
        
        // Statistics
        private NetworkCombatStats m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingHitRequest
        {
            public NetworkHitRequest request;
            public float sentTime;
            public bool optimisticEffectsPlayed;
        }
        
        private struct QueuedHitRequest
        {
            public uint clientNetworkId;
            public NetworkHitRequest request;
            public float receivedTime;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServer => m_IsServer;
        
        /// <summary>Whether this is running on a client.</summary>
        public bool IsClient => m_IsClient;
        
        /// <summary>Combat network statistics.</summary>
        public NetworkCombatStats Stats => m_Stats;
        
        /// <summary>Whether hit requests should be sent unreliably (lower latency, may drop).</summary>
        public bool UseUnreliableHitRequests => m_UseUnreliableHitRequests;
        
        /// <summary>Maximum rewind time for hit validation.</summary>
        public float MaxRewindTime => m_MaxRewindTime;
        
        /// <summary>Hit tolerance for validation.</summary>
        public float HitTolerance => m_HitTolerance;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            if (s_Instance == null)
            {
                s_Instance = this;
            }
            else if (s_Instance != this)
            {
                Debug.LogWarning("[NetworkCombat] Multiple NetworkCombatController instances. Using first.");
            }
        }
        
        private void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }
        
        private void Update()
        {
            if (m_IsClient)
            {
                UpdatePendingRequests();
            }
            
            if (m_IsServer)
            {
                ProcessServerHitQueue();
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the combat controller with network role.
        /// </summary>
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            
            m_PendingRequests.Clear();
            m_ServerHitQueue.Clear();
            m_Stats = default;
            
            Debug.Log($"[NetworkCombat] Initialized - Server: {isServer}, Client: {isClient}");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request server validation for a melee hit.
        /// Call this instead of applying damage directly.
        /// </summary>
        public void RequestMeleeHit(Character target, Vector3 hitPoint, Vector3 hitDirection, 
            int weaponHash, float clientTime = -1f)
        {
            if (!m_IsClient)
            {
                Debug.LogWarning("[NetworkCombat] RequestMeleeHit called on non-client");
                return;
            }
            
            var request = CreateHitRequest(
                target, hitPoint, hitDirection, weaponHash, 
                HitType.MeleeStrike, clientTime
            );
            
            SendHitRequest(request);
        }
        
        /// <summary>
        /// [Client] Request server validation for a projectile hit.
        /// </summary>
        public void RequestProjectileHit(Character target, Vector3 hitPoint, Vector3 hitDirection,
            int weaponHash, float clientTime = -1f)
        {
            if (!m_IsClient)
            {
                Debug.LogWarning("[NetworkCombat] RequestProjectileHit called on non-client");
                return;
            }
            
            var request = CreateHitRequest(
                target, hitPoint, hitDirection, weaponHash,
                HitType.Projectile, clientTime
            );
            
            SendHitRequest(request);
        }
        
        /// <summary>
        /// [Client] Request server validation for AOE damage.
        /// </summary>
        public void RequestAOEHit(Character target, Vector3 hitPoint, Vector3 sourceDirection,
            int sourceHash, float clientTime = -1f)
        {
            if (!m_IsClient)
            {
                Debug.LogWarning("[NetworkCombat] RequestAOEHit called on non-client");
                return;
            }
            
            var request = CreateHitRequest(
                target, hitPoint, sourceDirection, sourceHash,
                HitType.AreaOfEffect, clientTime
            );
            
            SendHitRequest(request);
        }
        
        private NetworkHitRequest CreateHitRequest(Character target, Vector3 hitPoint, 
            Vector3 hitDirection, int weaponHash, HitType hitType, float clientTime)
        {
            // Get target's network ID
            uint targetNetworkId = 0;
            var networkChar = target.GetComponent<NetworkCharacter>();
            if (networkChar != null)
            {
#if UNITY_NETCODE
                var networkObject = target.GetComponent<NetworkObject>();
                if (networkObject != null)
                {
                    targetNetworkId = (uint)networkObject.NetworkObjectId;
                }
#endif
            }
            
            var request = new NetworkHitRequest
            {
                requestId = m_NextRequestId++,
                targetNetworkId = targetNetworkId,
                clientTime = clientTime >= 0 ? clientTime : (GetServerTime?.Invoke() ?? Time.time),
                hitPoint = hitPoint,
                weaponHash = weaponHash,
                hitType = hitType
            };
            request.SetDirection(hitDirection);
            
            return request;
        }
        
        private void SendHitRequest(NetworkHitRequest request)
        {
            // Store pending request
            m_PendingRequests[request.requestId] = new PendingHitRequest
            {
                request = request,
                sentTime = Time.time,
                optimisticEffectsPlayed = false
            };
            
            // Play optimistic effects if enabled
            if (m_OptimisticHitEffects)
            {
                PlayOptimisticHitEffects(request);
                var pending = m_PendingRequests[request.requestId];
                pending.optimisticEffectsPlayed = true;
                m_PendingRequests[request.requestId] = pending;
            }
            
            // Send to server
            SendHitRequestToServer?.Invoke(request);
            
            m_Stats.hitRequestsSent++;
            OnHitRequestSent?.Invoke(request);
            
            if (m_DebugDrawHits)
            {
                Debug.DrawRay(request.hitPoint, request.GetDirection() * 0.5f, Color.yellow, 1f);
            }
        }
        
        private void PlayOptimisticHitEffects(NetworkHitRequest request)
        {
            // Play local hit effects (particles, sounds) before server confirmation
            // These are cosmetic only - no damage applied yet
            
            var target = GetCharacterByNetworkId?.Invoke(request.targetNetworkId);
            if (target == null) return;
            
            // TODO: Play local particle effects, hit sounds
            // This is game-specific - hook into your VFX system
        }
        
        /// <summary>
        /// [Client] Called when server responds to our hit request.
        /// </summary>
        public void OnReceiveHitResponse(NetworkHitResponse response)
        {
            if (!m_PendingRequests.TryGetValue(response.requestId, out var pending))
            {
                Debug.LogWarning($"[NetworkCombat] Received response for unknown request: {response.requestId}");
                return;
            }
            
            m_PendingRequests.Remove(response.requestId);
            
            if (response.result == HitResult.Valid)
            {
                // Hit confirmed - effects already played if optimistic
                m_Stats.hitsValidated++;
            }
            else
            {
                // Hit rejected - may need to revert optimistic effects
                m_Stats.hitsRejected++;
                
                if (pending.optimisticEffectsPlayed)
                {
                    RevertOptimisticEffects(pending.request, response);
                }
            }
            
            OnHitResponseReceived?.Invoke(response);
        }
        
        private void RevertOptimisticEffects(NetworkHitRequest request, NetworkHitResponse response)
        {
            // Optionally revert visual effects if hit was rejected
            // This is usually not noticeable in fast-paced combat
        }
        
        private void UpdatePendingRequests()
        {
            // Clean up timed-out requests
            var toRemove = new List<ushort>();
            float currentTime = Time.time;
            
            foreach (var kvp in m_PendingRequests)
            {
                if (currentTime - kvp.Value.sentTime > m_HitResponseTimeout)
                {
                    toRemove.Add(kvp.Key);
                    Debug.LogWarning($"[NetworkCombat] Hit request {kvp.Key} timed out");
                }
            }
            
            foreach (var id in toRemove)
            {
                m_PendingRequests.Remove(id);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a client sends a hit request.
        /// </summary>
        public void OnReceiveHitRequest(uint clientNetworkId, NetworkHitRequest request)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCombat] OnReceiveHitRequest called on non-server");
                return;
            }
            
            m_Stats.hitRequestsReceived++;
            OnHitRequestReceived?.Invoke(clientNetworkId, request);
            
            // Queue for processing
            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                clientNetworkId = clientNetworkId,
                request = request,
                receivedTime = GetServerTime?.Invoke() ?? Time.time
            });
        }
        
        private void ProcessServerHitQueue()
        {
            int processedCount = 0;
            
            while (m_ServerHitQueue.Count > 0 && processedCount < m_MaxHitsPerFrame)
            {
                var queued = m_ServerHitQueue.Dequeue();
                ProcessHitRequest(queued.clientNetworkId, queued.request);
                processedCount++;
            }
        }
        
        private void ProcessHitRequest(uint clientNetworkId, NetworkHitRequest request)
        {
            float startTime = Time.realtimeSinceStartup;
            
            // Validate the hit
            var result = ValidateHit(clientNetworkId, request, out var validatedDamage);
            
            // Create response
            var response = new NetworkHitResponse
            {
                requestId = request.requestId,
                result = result,
                finalDamage = result == HitResult.Valid ? validatedDamage.finalDamage : 0f,
                hitZone = validatedDamage.hitZone,
                effects = validatedDamage.effects
            };
            
            // Send response to requesting client
            SendHitResponseToClient?.Invoke(clientNetworkId, response);
            
            if (result == HitResult.Valid)
            {
                // Apply damage on server
                ApplyValidatedDamage(validatedDamage);
                
                // Broadcast to all clients for effects
                BroadcastHit(validatedDamage);
                
                m_Stats.hitsValidated++;
                OnHitValidated?.Invoke(validatedDamage);
            }
            else
            {
                m_Stats.hitsRejected++;
                OnHitRejected?.Invoke(request, result);
                
                if (m_LogRejectedHits)
                {
                    Debug.Log($"[NetworkCombat] Rejected hit from client {clientNetworkId}: {result}");
                }
            }
            
            // Update stats
            float validationTime = Time.realtimeSinceStartup - startTime;
            m_Stats.averageValidationTime = Mathf.Lerp(m_Stats.averageValidationTime, validationTime, 0.1f);
        }
        
        private HitResult ValidateHit(uint clientNetworkId, NetworkHitRequest request, 
            out ValidatedDamage validatedDamage)
        {
            validatedDamage = default;
            
            // Get target character
            var target = GetCharacterByNetworkId?.Invoke(request.targetNetworkId);
            if (target == null)
            {
                return HitResult.InvalidTarget;
            }
            
            // Check if target is alive
            if (target.IsDead)
            {
                return HitResult.InvalidTarget;
            }
            
            // Get lag compensation data
            var lagComp = target.GetComponent<CharacterLagCompensation>();
            if (lagComp == null)
            {
                // No lag compensation - validate against current position
                return ValidateHitAgainstCurrentPosition(target, request, out validatedDamage);
            }
            
            // Calculate rewind time
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            float rewindAmount = serverTime - request.clientTime;
            
            // Check if request is too old
            if (rewindAmount > m_MaxRewindTime)
            {
                return HitResult.TooOld;
            }
            
            // Validate using lag compensation
            var timestamp = new NetworkTimestamp { serverTime = request.clientTime };
            var lagResult = lagComp.ValidateHit(request.hitPoint, timestamp);
            
            if (!lagResult.isValid)
            {
                return HitResult.OutOfRange;
            }
            
            // Check invincibility at historical time
            if (target.Combat.Invincibility.IsInvincible)
            {
                return HitResult.Invincible;
            }
            
            // Check dodge
            if (target.Dash.IsDodge)
            {
                return HitResult.Invincible;
            }
            
            // Check block/parry
            var shieldResult = ValidateBlockParry(target, request);
            if (shieldResult != HitResult.Valid)
            {
                return shieldResult;
            }
            
            // Hit is valid - calculate damage
            validatedDamage = CalculateDamage(clientNetworkId, request, target, lagResult);
            
            return HitResult.Valid;
        }
        
        private HitResult ValidateHitAgainstCurrentPosition(Character target, NetworkHitRequest request,
            out ValidatedDamage validatedDamage)
        {
            validatedDamage = default;
            
            // Simple distance check
            float distance = Vector3.Distance(request.hitPoint, target.transform.position);
            float maxDistance = target.Motion.Radius + m_HitTolerance;
            
            if (distance > maxDistance + target.Motion.Height)
            {
                return HitResult.OutOfRange;
            }
            
            // Check invincibility
            if (target.Combat.Invincibility.IsInvincible)
            {
                return HitResult.Invincible;
            }
            
            // Calculate damage
            uint attackerId = GetLocalPlayerNetworkId?.Invoke() ?? 0;
            validatedDamage = new ValidatedDamage
            {
                attackerNetworkId = attackerId,
                targetNetworkId = request.targetNetworkId,
                baseDamage = 10f, // Default - should come from weapon
                finalDamage = 10f,
                damageMultiplier = 1f,
                hitPoint = request.hitPoint,
                hitDirection = request.GetDirection(),
                hitZone = HitZone.Body,
                effects = HitEffectFlags.None,
                weaponHash = request.weaponHash,
                hitType = request.hitType
            };
            
            return HitResult.Valid;
        }
        
        private HitResult ValidateBlockParry(Character target, NetworkHitRequest request)
        {
            // Check if target is blocking
            if (!target.Combat.Block.IsBlocking)
            {
                return HitResult.Valid;
            }
            
            // Determine if attack can be blocked/parried
            Vector3 attackDirection = request.GetDirection();
            Vector3 toAttacker = -attackDirection;
            Vector3 targetForward = target.transform.forward;
            
            float dotProduct = Vector3.Dot(toAttacker, targetForward);
            
            // Check if attack is from front (within ~120 degree arc)
            if (dotProduct > 0.5f)
            {
                // Check parry timing using Combat's LastBlockTime
                float timeSinceBlock = Time.time - target.Combat.LastBlockTime;
                if (timeSinceBlock < 0.2f) // Parry window
                {
                    return HitResult.Parried;
                }
                
                return HitResult.Blocked;
            }
            
            return HitResult.Valid;
        }
        
        private ValidatedDamage CalculateDamage(uint attackerNetworkId, NetworkHitRequest request,
            Character target, HitValidationResult lagResult)
        {
            // Get weapon damage (simplified - you'd look this up from your weapon system)
            float baseDamage = 10f;
            
            // Apply zone multiplier
            float zoneMultiplier = lagResult.damageMultiplier;
            
            // Determine hit zone from lag compensation result
            HitZone hitZone = HitZone.Body;
            if (!string.IsNullOrEmpty(lagResult.hitZoneName))
            {
                hitZone = lagResult.hitZoneName switch
                {
                    "Head" => HitZone.Head,
                    "Torso" => HitZone.Torso,
                    "Legs" => HitZone.LeftLeg,
                    _ => HitZone.Body
                };
            }
            
            // Check for backstab
            HitEffectFlags effects = HitEffectFlags.None;
            Vector3 attackDirection = request.GetDirection();
            float behindDot = Vector3.Dot(attackDirection, target.transform.forward);
            if (behindDot > 0.5f)
            {
                effects |= HitEffectFlags.Backstab;
                zoneMultiplier *= 1.5f;
            }
            
            // Check for critical
            if (hitZone == HitZone.Head)
            {
                effects |= HitEffectFlags.Critical;
            }
            
            // Calculate final damage
            float finalDamage = baseDamage * zoneMultiplier;
            
            // Apply target's defense
            float defense = target.Combat.CurrentDefense;
            finalDamage = Mathf.Max(1f, finalDamage - defense);
            
            // Check if lethal
            // (Would need to integrate with Stats module for actual health)
            
            return new ValidatedDamage
            {
                attackerNetworkId = attackerNetworkId,
                targetNetworkId = request.targetNetworkId,
                baseDamage = baseDamage,
                finalDamage = finalDamage,
                damageMultiplier = zoneMultiplier,
                hitPoint = request.hitPoint,
                hitDirection = attackDirection,
                hitZone = hitZone,
                effects = effects,
                weaponHash = request.weaponHash,
                hitType = request.hitType
            };
        }
        
        private void ApplyValidatedDamage(ValidatedDamage damage)
        {
            var target = GetCharacterByNetworkId?.Invoke(damage.targetNetworkId);
            if (target == null) return;
            
            // Get attacker for Args
            var attacker = GetCharacterByNetworkId?.Invoke(damage.attackerNetworkId);
            GameObject attackerObj = attacker != null ? attacker.gameObject : null;
            
            Args args = new Args(attackerObj, target.gameObject);
            
            // Create reaction input
            ReactionInput reactionInput = damage.ToReactionInput();
            
            // Apply through GC2's combat system
            _ = target.Combat.GetHitReaction(reactionInput, args, null);
            
            if (m_DebugDrawHits)
            {
                Debug.DrawRay(damage.hitPoint, damage.hitDirection * 0.5f, Color.red, 2f);
                Debug.DrawRay(damage.hitPoint, Vector3.up * 0.5f, Color.green, 2f);
            }
        }
        
        private void BroadcastHit(ValidatedDamage damage)
        {
            var target = GetCharacterByNetworkId?.Invoke(damage.targetNetworkId);
            if (target == null) return;
            
            var broadcast = new NetworkHitBroadcast
            {
                attackerNetworkId = damage.attackerNetworkId,
                targetNetworkId = damage.targetNetworkId,
                hitZone = damage.hitZone,
                effects = damage.effects
            };
            broadcast.SetHitOffset(target.transform.position, damage.hitPoint);
            
            BroadcastHitToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [All Clients] Called when receiving a hit broadcast from server.
        /// </summary>
        public void OnReceiveHitBroadcast(NetworkHitBroadcast broadcast)
        {
            // Play hit effects (particles, sounds, reactions)
            var target = GetCharacterByNetworkId?.Invoke(broadcast.targetNetworkId);
            if (target == null) return;
            
            Vector3 hitPoint = broadcast.GetHitPoint(target.transform.position);
            
            // Play hit VFX
            PlayHitEffects(target, hitPoint, broadcast.hitZone, broadcast.effects);
            
            OnHitBroadcastReceived?.Invoke(broadcast);
        }
        
        private void PlayHitEffects(Character target, Vector3 hitPoint, HitZone zone, HitEffectFlags effects)
        {
            // Play appropriate effects based on zone and flags
            // This is game-specific - integrate with your VFX/audio system
            
            if ((effects & HitEffectFlags.Critical) != 0)
            {
                // Play critical hit effect
            }
            
            if ((effects & HitEffectFlags.Backstab) != 0)
            {
                // Play backstab effect
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DEBUG
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!m_DebugDrawHits) return;
            
            // Draw pending requests
            Gizmos.color = Color.yellow;
            foreach (var pending in m_PendingRequests.Values)
            {
                Gizmos.DrawWireSphere(pending.request.hitPoint, 0.1f);
            }
        }
#endif
    }
}
