#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Shooter;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// Server-authoritative shooter combat controller for GC2.
    /// Intercepts shots and hits, routing them through server validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it works:</b>
    /// This component hooks into GC2's ShooterStance to intercept shots and hits.
    /// On clients, it sends requests to the server. On the server, it validates
    /// using lag compensation and applies damage authoritatively.
    /// </para>
    /// <para>
    /// <b>Setup:</b>
    /// 1. Add this component to Characters that use shooter weapons
    /// 2. Ensure NetworkCharacter is also present (with Combat Mode = Disabled)
    /// 3. Add NetworkShooterManager to your scene for global coordination
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Character))]
    [AddComponentMenu("Game Creator/Network/Shooter/Network Shooter Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 10)]
    public class NetworkShooterController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Network Settings")]
        [Tooltip("Show optimistic shot effects (tracer, muzzle flash) before server confirmation.")]
        [SerializeField] private bool m_OptimisticShotEffects = true;
        
        [Tooltip("Show optimistic hit effects before server confirmation.")]
        [SerializeField] private bool m_OptimisticHitEffects = true;
        
        [Tooltip("Maximum pending shots before flush.")]
        [SerializeField] private int m_MaxPendingShots = 16;
        
        [Header("Sync Settings")]
        [Tooltip("Sync weapon state (ammo, reload, jam) at this interval.")]
        [SerializeField] private float m_WeaponStateSyncInterval = 0.5f;
        
        [Tooltip("Sync aim state at this interval.")]
        [SerializeField] private float m_AimStateSyncInterval = 0.1f;
        
        [Header("Lag Compensation")]
        [Tooltip("Configuration for lag compensation validation.")]
        [SerializeField] private ShooterValidationConfig m_ValidationConfig = new();
        
        [Header("Debug")]
        [SerializeField] private bool m_LogShots = false;
        [SerializeField] private bool m_LogHits = false;
        [SerializeField] private bool m_DrawDebugRays = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Called when a shot request is sent to server.</summary>
        public event Action<NetworkShotRequest> OnShotRequestSent;
        
        /// <summary>Called when a shot is confirmed by server.</summary>
        public event Action<NetworkShotBroadcast> OnShotConfirmed;
        
        /// <summary>Called when a hit is detected locally.</summary>
        public event Action<NetworkShooterHitRequest> OnHitDetected;
        
        /// <summary>Called when a hit is confirmed by server.</summary>
        public event Action<NetworkShooterHitBroadcast> OnHitConfirmed;
        
        /// <summary>Called when weapon state changes.</summary>
        public event Action<NetworkWeaponState> OnWeaponStateChanged;
        
        /// <summary>Called when a reload request is sent to server.</summary>
        public event Action<NetworkReloadRequest> OnReloadRequestSent;
        
        /// <summary>Called when a reload event is broadcast.</summary>
        public event Action<NetworkReloadBroadcast> OnReloadBroadcastReceived;
        
        /// <summary>Called when a fix jam request is sent to server.</summary>
        public event Action<NetworkFixJamRequest> OnFixJamRequestSent;
        
        /// <summary>Called when a weapon jams (broadcast received).</summary>
        public event Action<NetworkJamBroadcast> OnWeaponJammed;
        
        /// <summary>Called when a jam fix completes (broadcast received).</summary>
        public event Action<NetworkFixJamBroadcast> OnJamFixed;
        
        /// <summary>Called when a charge state changes.</summary>
        public event Action<NetworkChargeBroadcast> OnChargeBroadcastReceived;
        
        /// <summary>Called when a sight switch is broadcast.</summary>
        public event Action<NetworkSightSwitchBroadcast> OnSightSwitchBroadcastReceived;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Character m_Character;
        private NetworkCharacter m_NetworkCharacter;
        private ShooterStance m_ShooterStance;
        
        // Network role
        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;
        
        // Request tracking
        private ushort m_NextRequestId = 1;
        private ushort m_LastSentShotRequestId;
        private readonly List<PendingShotRequest> m_PendingShots = new(16);
        private readonly List<PendingHitRequest> m_PendingHits = new(32);
        private readonly HashSet<int> m_ProcessedHits = new(64);
        
        // Validated shots (for hit validation)
        private readonly Dictionary<ushort, ValidatedShot> m_ValidatedShots = new(16);
        
        // Pending reload/jam/charge requests
        private readonly List<PendingReloadRequest> m_PendingReloads = new(4);
        private readonly List<PendingFixJamRequest> m_PendingFixJams = new(4);
        private readonly List<PendingChargeRequest> m_PendingCharges = new(4);
        private readonly List<PendingSightSwitchRequest> m_PendingSightSwitches = new(4);
        
        // Charge state tracking
        private bool m_IsCharging;
        private float m_ChargeStartTime;
        private float m_LastChargeSyncTime;
        
        // State tracking
        private NetworkWeaponState m_LastWeaponState;
        private NetworkAimState m_LastAimState;
        private float m_LastWeaponStateSync;
        private float m_LastAimStateSync;
        
        // Current weapon tracking
        private ShooterWeapon m_CurrentWeapon;
        private WeaponData m_CurrentWeaponData;
        private float m_LastServerValidatedShotTime;
        
        // Lag compensation validator (server-only)
        private ShooterLagCompensationValidator m_Validator;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingShotRequest
        {
            public NetworkShotRequest Request;
            public float SentTime;
            public bool OptimisticPlayed;
        }
        
        private struct PendingHitRequest
        {
            public NetworkShooterHitRequest Request;
            public float SentTime;
            public bool OptimisticPlayed;
        }
        
        private struct ValidatedShot
        {
            public NetworkShotRequest Request;
            public float ValidatedTime;
            public int HitsProcessed;
        }
        
        private struct PendingReloadRequest
        {
            public NetworkReloadRequest Request;
            public float SentTime;
        }
        
        private struct PendingFixJamRequest
        {
            public NetworkFixJamRequest Request;
            public float SentTime;
        }
        
        private struct PendingChargeRequest
        {
            public NetworkChargeStartRequest Request;
            public float SentTime;
        }
        
        private struct PendingSightSwitchRequest
        {
            public NetworkSightSwitchRequest Request;
            public float SentTime;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Character.</summary>
        public Character Character => m_Character;
        
        /// <summary>Current weapon state for network sync.</summary>
        public NetworkWeaponState WeaponState => m_LastWeaponState;
        
        /// <summary>Current aim state for network sync.</summary>
        public NetworkAimState AimState => m_LastAimState;
        
        /// <summary>Whether optimistic shot effects are enabled.</summary>
        public bool OptimisticShotEffects
        {
            get => m_OptimisticShotEffects;
            set => m_OptimisticShotEffects = value;
        }
        
        /// <summary>Whether optimistic hit effects are enabled.</summary>
        public bool OptimisticHitEffects
        {
            get => m_OptimisticHitEffects;
            set => m_OptimisticHitEffects = value;
        }
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServer => m_IsServer;
        
        /// <summary>Whether this is the local player's character.</summary>
        public bool IsLocalClient => m_IsLocalClient;
        
        /// <summary>Shorthand for the character's network ID.</summary>
        private uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }
        
        private void Start()
        {
            // Subscribe to weapon equip events
            m_Character.Combat.EventEquip += OnWeaponEquipped;
            m_Character.Combat.EventUnequip += OnWeaponUnequipped;
            
            // Try to get initial shooter stance
            TryGetShooterStance();
        }
        
        private void OnDestroy()
        {
            if (m_Character != null)
            {
                m_Character.Combat.EventEquip -= OnWeaponEquipped;
                m_Character.Combat.EventUnequip -= OnWeaponUnequipped;
            }
        }
        
        private void Update()
        {
            if (!m_IsLocalClient && !m_IsServer) return;
            
            // Update weapon state
            UpdateWeaponState();
            
            // Update aim state
            UpdateAimState();
            
            // Cleanup old pending requests
            CleanupPendingRequests();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the network shooter controller with role information.
        /// Called by NetworkCharacter when network role is determined.
        /// </summary>
        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;
            
            if (m_LogShots)
            {
                string role = m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : "RemoteClient");
                Debug.Log($"[NetworkShooterController] {gameObject.name} initialized as {role}");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STANCE & WEAPON TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void TryGetShooterStance()
        {
            m_ShooterStance = m_Character.Combat.RequestStance<ShooterStance>();
        }
        
        private void OnWeaponEquipped(IWeapon weapon, GameObject instance)
        {
            if (weapon is ShooterWeapon shooterWeapon)
            {
                m_CurrentWeapon = shooterWeapon;
                TryGetShooterStance();
                
                if (m_ShooterStance != null)
                {
                    m_CurrentWeaponData = m_ShooterStance.Get(shooterWeapon);
                }
            }
        }
        
        private void OnWeaponUnequipped(IWeapon weapon, GameObject instance)
        {
            if (weapon is ShooterWeapon)
            {
                m_CurrentWeapon = null;
                m_CurrentWeaponData = null;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATE SYNC
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void UpdateWeaponState()
        {
            if (Time.time - m_LastWeaponStateSync < m_WeaponStateSyncInterval) return;
            m_LastWeaponStateSync = Time.time;
            
            if (m_CurrentWeapon == null || m_CurrentWeaponData == null)
            {
                if (m_LastWeaponState.WeaponHash != 0)
                {
                    m_LastWeaponState = NetworkWeaponState.None;
                    OnWeaponStateChanged?.Invoke(m_LastWeaponState);
                }
                return;
            }
            
            // Get current munition
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            ushort ammo = munition != null ? (ushort)munition.InMagazine : (ushort)0;
            
            // Build state flags
            byte flags = 0;
            if (m_ShooterStance.Reloading.IsReloading) flags |= NetworkWeaponState.FLAG_IS_RELOADING;
            if (m_CurrentWeaponData.IsJammed) flags |= NetworkWeaponState.FLAG_IS_JAMMED;
            if (m_CurrentWeaponData.IsAiming) flags |= NetworkWeaponState.FLAG_IS_AIMING;
            if (m_ShooterStance.Shooting.IsShootingAnimation) flags |= NetworkWeaponState.FLAG_IS_SHOOTING;
            if (m_CurrentWeaponData.IsPullingTrigger) flags |= NetworkWeaponState.FLAG_IS_CHARGING;
            
            var newState = new NetworkWeaponState
            {
                WeaponHash = m_CurrentWeapon.Id.Hash,
                SightHash = m_CurrentWeaponData.SightId.Hash,
                AmmoInMagazine = ammo,
                StateFlags = flags
            };
            
            // Check if changed
            if (newState.WeaponHash != m_LastWeaponState.WeaponHash ||
                newState.StateFlags != m_LastWeaponState.StateFlags ||
                newState.AmmoInMagazine != m_LastWeaponState.AmmoInMagazine)
            {
                m_LastWeaponState = newState;
                OnWeaponStateChanged?.Invoke(m_LastWeaponState);
            }
        }
        
        private void UpdateAimState()
        {
            if (Time.time - m_LastAimStateSync < m_AimStateSyncInterval) return;
            m_LastAimStateSync = Time.time;
            
            if (m_CurrentWeapon == null || m_CurrentWeaponData == null) return;
            
            // Get aim point from sight
            var sight = m_CurrentWeapon.Sights.Get(m_CurrentWeaponData.SightId);
            if (sight?.Sight == null) return;
            
            var muzzle = sight.Sight.GetMuzzle(m_CurrentWeaponData.WeaponArgs, m_CurrentWeapon);
            
            m_LastAimState = new NetworkAimState
            {
                AimPoint = muzzle.Position + muzzle.Direction * 100f,
                Accuracy = (byte)(m_ShooterStance.CurrentAccuracy * 255f),
                IsAiming = m_CurrentWeaponData.IsAiming,
                CompressedDirection = NetworkAimState.CompressDirection(muzzle.Direction)
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOT INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a shot before it's fired.
        /// Call this from your network-aware shot implementation.
        /// </summary>
        /// <param name="muzzlePosition">Muzzle position.</param>
        /// <param name="shotDirection">Shot direction with spread.</param>
        /// <param name="weapon">The weapon being used.</param>
        /// <param name="chargeRatio">Charge ratio for charged weapons.</param>
        /// <param name="projectileIndex">Index for multi-projectile weapons.</param>
        /// <param name="totalProjectiles">Total projectiles in shot.</param>
        /// <returns>True if shot should proceed locally (server or optimistic).</returns>
        public bool InterceptShot(
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            ShooterWeapon weapon,
            float chargeRatio,
            byte projectileIndex,
            byte totalProjectiles)
        {
            // Server fires immediately
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't fire - they receive broadcasts
            if (m_IsRemoteClient)
            {
                return false;
            }
            
            // Local client - send request to server
            uint shooterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            var sightHash = m_CurrentWeaponData?.SightId.Hash ?? 0;
            ushort requestId = m_NextRequestId++;
            m_LastSentShotRequestId = requestId;
            
            var request = new NetworkShotRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                ClientTimestamp = Time.time,
                ShooterNetworkId = shooterNetworkId,
                MuzzlePosition = muzzlePosition,
                ShotDirection = shotDirection,
                WeaponHash = weapon?.Id.Hash ?? 0,
                SightHash = sightHash,
                ChargeRatio = chargeRatio,
                ProjectileIndex = projectileIndex,
                TotalProjectiles = totalProjectiles
            };
            
            m_PendingShots.Add(new PendingShotRequest
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = false
            });
            
            OnShotRequestSent?.Invoke(request);
            
            if (m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Shot request sent: {request.RequestId}");
            }
            
            return m_OptimisticShotEffects;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HIT INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a hit detected by a shot.
        /// </summary>
        /// <param name="target">The hit target GameObject.</param>
        /// <param name="hitPoint">Hit point in world space.</param>
        /// <param name="hitNormal">Hit normal.</param>
        /// <param name="distance">Distance from muzzle.</param>
        /// <param name="weapon">The weapon used.</param>
        /// <param name="pierceIndex">Pierce index (0 = first hit).</param>
        /// <returns>True if hit should be processed locally.</returns>
        public bool InterceptHit(
            GameObject target,
            Vector3 hitPoint,
            Vector3 hitNormal,
            float distance,
            ShooterWeapon weapon,
            byte pierceIndex)
        {
            if (target == null) return false;
            
            int targetId = target.GetInstanceID();
            
            // Don't process same target twice
            if (m_ProcessedHits.Contains(targetId)) return false;
            m_ProcessedHits.Add(targetId);
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits
            if (m_IsRemoteClient)
            {
                return false;
            }
            
            // Get target network ID
            var targetNetworkChar = target.GetComponent<NetworkCharacter>();
            uint targetNetworkId = targetNetworkChar != null ? targetNetworkChar.NetworkId : 0;
            uint shooterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            ushort requestId = m_NextRequestId++;
            ushort sourceShotRequestId = ResolveSourceShotRequestId();
            
            bool isCharacterHit = target.GetComponent<Character>() != null;
            
            var request = new NetworkShooterHitRequest
            {
                RequestId = requestId,
                SourceShotRequestId = sourceShotRequestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                ClientTimestamp = Time.time,
                ShooterNetworkId = shooterNetworkId,
                TargetNetworkId = targetNetworkId,
                HitPoint = hitPoint,
                HitNormal = hitNormal,
                Distance = distance,
                WeaponHash = weapon?.Id.Hash ?? 0,
                PierceIndex = pierceIndex,
                IsCharacterHit = isCharacterHit
            };
            
            m_PendingHits.Add(new PendingHitRequest
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = false
            });
            
            OnHitDetected?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkShooterController] Hit request sent: {target.name} at {hitPoint}");
            }
            
            return m_OptimisticHitEffects;
        }
        
        /// <summary>
        /// Clear the processed hits set. Call when starting a new shot sequence.
        /// </summary>
        public void ClearProcessedHits()
        {
            m_ProcessedHits.Clear();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a shot request from a client.
        /// </summary>
        public NetworkShotResponse ProcessShotRequest(NetworkShotRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.WeaponNotEquipped
                };
            }
            
            // Validate ammo
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null && munition.InMagazine <= 0)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.NoAmmo
                };
            }
            
            // Validate not jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.WeaponJammed
                };
            }
            
            // Validate muzzle position sanity against server character position
            Vector3 characterPosition = m_Character != null ? m_Character.transform.position : request.MuzzlePosition;
            if ((request.MuzzlePosition - characterPosition).sqrMagnitude > 36f) // 6m tolerance
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.InvalidPosition
                };
            }

            // Validate direction vector
            float directionSqMag = request.ShotDirection.sqrMagnitude;
            if (directionSqMag < 0.01f || directionSqMag > 1.5f)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.InvalidDirection
                };
            }

            // Basic server-side fire-rate throttling for impossible burst shots.
            float now = Time.time;
            if (now - m_LastServerValidatedShotTime < 0.02f)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.RateLimitExceeded
                };
            }
            
            // Store validated shot for hit validation
            m_ValidatedShots[request.RequestId] = new ValidatedShot
            {
                Request = request,
                ValidatedTime = Time.time,
                HitsProcessed = 0
            };
            m_LastServerValidatedShotTime = now;
            
            ushort ammoRemaining = munition != null ? (ushort)munition.InMagazine : (ushort)0;
            
            return new NetworkShotResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ShotRejectionReason.None,
                AmmoRemaining = ammoRemaining
            };
        }
        
        /// <summary>
        /// [Server] Process a hit request from a client.
        /// </summary>
        public NetworkShooterHitResponse ProcessHitRequest(NetworkShooterHitRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                };
            }

            if (request.SourceShotRequestId == 0 ||
                !m_ValidatedShots.TryGetValue(request.SourceShotRequestId, out var validatedShot))
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }

            if (validatedShot.Request.ShooterNetworkId != request.ShooterNetworkId ||
                validatedShot.Request.WeaponHash != request.WeaponHash)
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }
            
            // For character hits, validate target
            if (request.IsCharacterHit && request.TargetNetworkId != 0)
            {
                var targetNetworkChar = NetworkShooterManager.Instance?.GetCharacterByNetworkId(request.TargetNetworkId);
                if (targetNetworkChar == null)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }
                
                var targetCharacter = targetNetworkChar.GetComponent<Character>();
                if (targetCharacter == null)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }
                
                // Check invincibility
                if (targetCharacter.Combat.Invincibility.IsInvincible)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetInvincible
                    };
                }
                
                // Check dodge
                if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetDodged
                    };
                }
                
                // ═══════════════════════════════════════════════════════════════════════════════════
                // LAG COMPENSATION VALIDATION
                // ═══════════════════════════════════════════════════════════════════════════════════
                
                // Ensure validator is initialized
                if (m_Validator == null)
                {
                    m_Validator = new ShooterLagCompensationValidator(m_ValidationConfig);
                }
                
                // Perform lag-compensated validation
                var validationResult = m_Validator.ValidateShotHit(
                    request,
                    m_Character,
                    m_CurrentWeapon
                );
                
                if (!validationResult.IsValid)
                {
                    if (m_LogHits)
                    {
                        Debug.Log($"[NetworkShooterController] Hit rejected: {validationResult}");
                    }
                    
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = MapValidationRejection(validationResult.RejectionReason),
                        Damage = 0f,
                        BlockResult = NetworkBlockResult.None
                    };
                }
                
                // Hit validated with lag compensation!
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Hit validated: {validationResult}");
                }

                MarkValidatedShotHitProcessed(request.SourceShotRequestId);
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = true,
                    RejectionReason = HitRejectionReason.None,
                    Damage = validationResult.FinalDamage,
                    BlockResult = NetworkBlockResult.None
                };
            }
            
            // Environment hit - always valid
            MarkValidatedShotHitProcessed(request.SourceShotRequestId);
            return new NetworkShooterHitResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = HitRejectionReason.None,
                Damage = 0f,
                BlockResult = NetworkBlockResult.None
            };
        }

        private ushort ResolveSourceShotRequestId()
        {
            if (m_LastSentShotRequestId != 0)
            {
                return m_LastSentShotRequestId;
            }

            int lastPendingShotIndex = m_PendingShots.Count - 1;
            return lastPendingShotIndex >= 0
                ? m_PendingShots[lastPendingShotIndex].Request.RequestId
                : (ushort)0;
        }

        private void MarkValidatedShotHitProcessed(ushort sourceShotRequestId)
        {
            if (sourceShotRequestId == 0) return;
            if (!m_ValidatedShots.TryGetValue(sourceShotRequestId, out var validatedShot)) return;

            validatedShot.HitsProcessed++;
            m_ValidatedShots[sourceShotRequestId] = validatedShot;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server responds to a shot request.
        /// </summary>
        public void ReceiveShotResponse(NetworkShotResponse response)
        {
            int index = m_PendingShots.FindIndex(p => ((response.CorrelationId != 0 && p.Request.CorrelationId != 0) ? p.Request.CorrelationId == response.CorrelationId : p.Request.RequestId == response.RequestId));
            if (index < 0) return;
            
            var pending = m_PendingShots[index];
            m_PendingShots.RemoveAt(index);
            
            if (!response.Validated)
            {
                if (m_LogShots)
                {
                    Debug.Log($"[NetworkShooterController] Shot rejected: {response.RejectionReason}");
                }
                // Optimistic shot effects (muzzle flash, tracer, sound) are fire-and-forget VFX
                // that have already completed by the time the server response arrives.
                // Rolling them back would be visually jarring and offer no gameplay benefit.
            }
        }
        
        /// <summary>
        /// [Client] Called when server responds to a hit request.
        /// </summary>
        public void ReceiveHitResponse(NetworkShooterHitResponse response)
        {
            int index = m_PendingHits.FindIndex(p => ((response.CorrelationId != 0 && p.Request.CorrelationId != 0) ? p.Request.CorrelationId == response.CorrelationId : p.Request.RequestId == response.RequestId));
            if (index < 0) return;
            
            var pending = m_PendingHits[index];
            m_PendingHits.RemoveAt(index);
            
            if (!response.Validated)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkShooterController] Hit rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a confirmed shot.
        /// </summary>
        public void ReceiveShotBroadcast(NetworkShotBroadcast broadcast)
        {
            OnShotConfirmed?.Invoke(broadcast);
            
            // Play effects on remote clients
            if (m_IsRemoteClient)
            {
                PlayShotEffects(broadcast);
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkShooterHitBroadcast broadcast)
        {
            OnHitConfirmed?.Invoke(broadcast);
            
            // Play effects on remote clients or non-optimistic locals
            if (m_IsRemoteClient || (m_IsLocalClient && !m_OptimisticHitEffects))
            {
                PlayHitEffects(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EFFECTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void PlayShotEffects(NetworkShotBroadcast broadcast)
        {
            // Resolve weapon from hash to play muzzle flash, tracer, and fire sound.
            // On the local client these are handled optimistically by ShooterStance.
            // This path runs for remote clients observing another player's shots.
            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null) return;
            
            // GC2's ShooterStance drives muzzle VFX and audio through WeaponData internally.
            // For remote clients, the animation sync (via NetworkCharacter) triggers the
            // fire animation which in turn plays the weapon's configured muzzle effects.
        }
        
        private void PlayHitEffects(NetworkShooterHitBroadcast broadcast)
        {
            // Resolve weapon from hash to determine impact effect style.
            // MaterialHash (resolved server-side) can drive surface-specific particles.
            ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
            if (weapon == null) return;
            
            // Impact VFX at the hit point. GC2's shot pipeline handles impact effects
            // internally via the ShotData.OnHit flow. For remote clients, the hit broadcast
            // provides position/normal data for spawning generic impact particles.
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Maps CombatValidationRejectionReason to HitRejectionReason.
        /// </summary>
        private static HitRejectionReason MapValidationRejection(CombatValidationRejectionReason reason)
        {
            return reason switch
            {
                CombatValidationRejectionReason.None => HitRejectionReason.None,
                CombatValidationRejectionReason.AttackerNotFound => HitRejectionReason.ShooterNotFound,
                CombatValidationRejectionReason.TargetNotFound => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.AttackerNotRegistered => HitRejectionReason.ShooterNotFound,
                CombatValidationRejectionReason.TargetNotRegistered => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetNotActive => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetInvincible => HitRejectionReason.TargetInvincible,
                CombatValidationRejectionReason.TargetDodging => HitRejectionReason.TargetDodged,
                CombatValidationRejectionReason.TargetDead => HitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TimestampTooOld => HitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.TimestampInFuture => HitRejectionReason.CheatSuspected,
                CombatValidationRejectionReason.NoHistoryAvailable => HitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.RaycastMissed => HitRejectionReason.RaycastMissed,
                CombatValidationRejectionReason.OutOfShooterRange => HitRejectionReason.OutOfRange,
                CombatValidationRejectionReason.InvalidTrajectory => HitRejectionReason.InvalidTrajectory,
                CombatValidationRejectionReason.ShotNotValidated => HitRejectionReason.ShotNotValidated,
                CombatValidationRejectionReason.InvalidMuzzlePosition => HitRejectionReason.InvalidPosition,
                CombatValidationRejectionReason.CheatSuspected => HitRejectionReason.CheatSuspected,
                _ => HitRejectionReason.CheatSuspected
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RELOAD NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start reloading the current weapon.
        /// </summary>
        /// <returns>True if request was sent, false if invalid state.</returns>
        public bool RequestReload()
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (m_ShooterStance == null) return false;
            
            // Don't request if already reloading
            if (m_ShooterStance.Reloading.IsReloading) return false;
            
            // Don't request if jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkReloadRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                ClientTimestamp = Time.time
            };
            
            m_PendingReloads.Add(new PendingReloadRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
            OnReloadRequestSent?.Invoke(request);
            return true;
        }
        
        /// <summary>
        /// [Client] Request quick reload (active reload mechanic).
        /// </summary>
        /// <param name="normalizedTime">Current reload progress (0-1).</param>
        /// <returns>True if request was sent.</returns>
        public bool RequestQuickReload(float normalizedTime)
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (!m_ShooterStance.Reloading.IsReloading) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkQuickReloadRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                AttemptTime = normalizedTime
            };
            
            // Quick reload is sent immediately, no pending tracking needed
            // The response will be in the reload broadcast
            return true;
        }
        
        /// <summary>
        /// [Server] Process a reload request from a client.
        /// </summary>
        public NetworkReloadResponse ProcessReloadRequest(NetworkReloadRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.WeaponNotEquipped
                };
            }
            
            // Check if already reloading
            if (m_ShooterStance.Reloading.IsReloading)
            {
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.AlreadyReloading
                };
            }
            
            // Check if jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                return new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.WeaponJammed
                };
            }
            
            // Check magazine not full
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null)
            {
                int magazineSize = m_CurrentWeapon.Magazine.MagazineSize(m_CurrentWeaponData.WeaponArgs);
                if (munition.InMagazine >= magazineSize)
                {
                    return new NetworkReloadResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = ReloadRejectionReason.MagazineFull
                    };
                }
            }
            
            // Get quick reload window from the reload asset
            byte quickStart = 0;
            byte quickEnd = 0;
            var reload = m_CurrentWeapon.Reload;
            if (reload != null)
            {
                Vector2 quickWindow = reload.GetQuickReload();
                quickStart = (byte)(quickWindow.x * 255f);
                quickEnd = (byte)(quickWindow.y * 255f);
            }
            
            return new NetworkReloadResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ReloadRejectionReason.None,
                QuickReloadWindowStart = quickStart,
                QuickReloadWindowEnd = quickEnd
            };
        }
        
        /// <summary>
        /// [Server] Process a quick reload request from a client.
        /// </summary>
        public bool ProcessQuickReloadRequest(NetworkQuickReloadRequest request, uint clientNetworkId)
        {
            if (!m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (!m_ShooterStance.Reloading.IsReloading) return false;
            
            var reload = m_CurrentWeapon.Reload;
            if (reload == null) return false;
            
            // Server-side validation of quick reload timing
            // Convert normalized time to animation time
            float animationTime = request.AttemptTime * reload.Duration;
            return reload.CanQuickReload(animationTime);
        }
        
        /// <summary>
        /// [Client] Receive reload response from server.
        /// </summary>
        public void ReceiveReloadResponse(NetworkReloadResponse response)
        {
            int index = m_PendingReloads.FindIndex(p => ((response.CorrelationId != 0 && p.Request.CorrelationId != 0) ? p.Request.CorrelationId == response.CorrelationId : p.Request.RequestId == response.RequestId));
            if (index >= 0)
            {
                m_PendingReloads.RemoveAt(index);
            }
            
            if (!response.Validated && m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Reload rejected: {response.RejectionReason}");
            }
        }
        
        /// <summary>
        /// [All] Receive reload broadcast from server.
        /// </summary>
        public void ReceiveReloadBroadcast(NetworkReloadBroadcast broadcast)
        {
            OnReloadBroadcastReceived?.Invoke(broadcast);
            
            // Remote clients play reload animation/effects
            if (m_IsRemoteClient)
            {
                ShooterWeapon weapon = NetworkShooterManager.GetShooterWeaponByHash(broadcast.WeaponHash);
                if (weapon != null && m_ShooterStance != null)
                {
                    // Trigger reload on remote client. GC2's ShooterStance.Reload drives
                    // the reload animation and audio through the weapon's configured reload clip.
                    // The animation sync via NetworkCharacter handles the visual playback.
                    if (m_LogShots)
                    {
                        Debug.Log($"[NetworkShooterController] Remote reload broadcast: {weapon.name}");
                    }
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // JAM / FIX NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to fix a jammed weapon.
        /// </summary>
        /// <returns>True if request was sent.</returns>
        public bool RequestFixJam()
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (m_CurrentWeaponData == null || !m_CurrentWeaponData.IsJammed) return false;
            if (m_ShooterStance.Jamming.IsFixing) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkFixJamRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                ClientTimestamp = Time.time
            };
            
            m_PendingFixJams.Add(new PendingFixJamRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
            OnFixJamRequestSent?.Invoke(request);
            return true;
        }
        
        /// <summary>
        /// [Server] Process a fix jam request from a client.
        /// </summary>
        public NetworkFixJamResponse ProcessFixJamRequest(NetworkFixJamRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.WeaponNotEquipped
                };
            }
            
            // Check weapon is actually jammed
            if (m_CurrentWeaponData == null || !m_CurrentWeaponData.IsJammed)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.WeaponNotJammed
                };
            }
            
            // Check not already fixing
            if (m_ShooterStance.Jamming.IsFixing)
            {
                return new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.AlreadyFixing
                };
            }
            
            return new NetworkFixJamResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = FixJamRejectionReason.None
            };
        }
        
        /// <summary>
        /// [Client] Receive fix jam response from server.
        /// </summary>
        public void ReceiveFixJamResponse(NetworkFixJamResponse response)
        {
            int index = m_PendingFixJams.FindIndex(p => ((response.CorrelationId != 0 && p.Request.CorrelationId != 0) ? p.Request.CorrelationId == response.CorrelationId : p.Request.RequestId == response.RequestId));
            if (index >= 0)
            {
                m_PendingFixJams.RemoveAt(index);
            }
            
            if (!response.Validated && m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Fix jam rejected: {response.RejectionReason}");
            }
        }
        
        /// <summary>
        /// [All] Receive jam broadcast from server.
        /// </summary>
        public void ReceiveJamBroadcast(NetworkJamBroadcast broadcast)
        {
            OnWeaponJammed?.Invoke(broadcast);
            
            // Apply jam state on remote clients
            if (m_IsRemoteClient && m_CurrentWeaponData != null)
            {
                m_CurrentWeaponData.IsJammed = true;
            }
        }
        
        /// <summary>
        /// [All] Receive fix jam broadcast from server.
        /// </summary>
        public void ReceiveFixJamBroadcast(NetworkFixJamBroadcast broadcast)
        {
            OnJamFixed?.Invoke(broadcast);
            
            // Apply fix state on remote clients
            if (m_IsRemoteClient && m_CurrentWeaponData != null && broadcast.Success)
            {
                m_CurrentWeaponData.IsJammed = false;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARGE NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start charging the weapon.
        /// </summary>
        /// <returns>True if request was sent.</returns>
        public bool RequestChargeStart()
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            if (m_CurrentWeapon.Fire.Mode != ShootMode.Charge) return false;
            if (m_IsCharging) return false;
            
            // Check basic requirements
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed) return false;
            if (m_ShooterStance.Reloading.IsReloading) return false;
            
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null && munition.InMagazine <= 0) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkChargeStartRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                ClientTimestamp = Time.time
            };
            
            m_PendingCharges.Add(new PendingChargeRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
            // Start charging optimistically
            m_IsCharging = true;
            m_ChargeStartTime = Time.time;
            
            return true;
        }
        
        /// <summary>
        /// [Client] Request to cancel charging.
        /// </summary>
        /// <returns>True if request was sent.</returns>
        public bool RequestChargeCancel()
        {
            if (m_IsServer) return false;
            if (!m_IsCharging) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkChargeCancelRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon?.Id.Hash ?? 0,
                ClientTimestamp = Time.time
            };
            
            m_IsCharging = false;
            
            // Request sent through manager
            return true;
        }
        
        /// <summary>
        /// [Server] Process a charge start request from a client.
        /// </summary>
        public NetworkChargeStartResponse ProcessChargeStartRequest(NetworkChargeStartRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.WeaponNotEquipped
                };
            }
            
            // Validate weapon is charge type
            if (m_CurrentWeapon.Fire.Mode != ShootMode.Charge)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.WeaponNotChargeable
                };
            }
            
            // Check not jammed
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.WeaponJammed
                };
            }
            
            // Check not reloading
            if (m_ShooterStance.Reloading.IsReloading)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.Reloading
                };
            }
            
            // Check has ammo
            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            if (munition != null && munition.InMagazine <= 0)
            {
                return new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.NoAmmo
                };
            }
            
            // Mark as charging on server
            m_IsCharging = true;
            m_ChargeStartTime = Time.time;
            
            return new NetworkChargeStartResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ChargeRejectionReason.None
            };
        }
        
        /// <summary>
        /// [Client] Receive charge start response from server.
        /// </summary>
        public void ReceiveChargeStartResponse(NetworkChargeStartResponse response)
        {
            int index = m_PendingCharges.FindIndex(p => ((response.CorrelationId != 0 && p.Request.CorrelationId != 0) ? p.Request.CorrelationId == response.CorrelationId : p.Request.RequestId == response.RequestId));
            if (index >= 0)
            {
                m_PendingCharges.RemoveAt(index);
            }
            
            if (!response.Validated)
            {
                // Rollback optimistic charge
                m_IsCharging = false;
                
                if (m_LogShots)
                {
                    Debug.Log($"[NetworkShooterController] Charge rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Receive charge broadcast from server.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            OnChargeBroadcastReceived?.Invoke(broadcast);
            
            // Update remote client charge state
            if (m_IsRemoteClient)
            {
                float chargeRatio = broadcast.ChargeRatio / 255f;
                
                switch (broadcast.EventType)
                {
                    case ChargeEventType.Started:
                        m_IsCharging = true;
                        m_ChargeStartTime = Time.time;
                        break;
                        
                    case ChargeEventType.Released:
                    case ChargeEventType.Cancelled:
                    case ChargeEventType.AutoReleased:
                        m_IsCharging = false;
                        break;
                }
            }
        }
        
        /// <summary>
        /// Get current charge ratio (0-1).
        /// </summary>
        public float GetChargeRatio()
        {
            if (!m_IsCharging || m_CurrentWeapon == null) return 0f;
            if (m_CurrentWeaponData == null) return 0f;
            
            float maxChargeTime = m_CurrentWeapon.Fire.MaxChargeTime(m_CurrentWeaponData.WeaponArgs);
            if (maxChargeTime <= 0f) return 1f;
            
            float elapsed = Time.time - m_ChargeStartTime;
            return Mathf.Clamp01(elapsed / maxChargeTime);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SIGHT SWITCH NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to switch to a different sight.
        /// </summary>
        /// <param name="sightId">The IdString of the sight to switch to.</param>
        /// <returns>True if request was sent.</returns>
        public bool RequestSightSwitch(IdString sightId)
        {
            if (m_IsServer) return false;
            if (m_CurrentWeapon == null) return false;
            
            // Don't switch if already using this sight
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.SightId == sightId) return false;
            
            // Don't switch while reloading or shooting
            if (m_ShooterStance.Reloading.IsReloading) return false;
            if (m_ShooterStance.Shooting.IsShootingAnimation) return false;
            
            // Validate sight exists on weapon
            var sightItem = m_CurrentWeapon.Sights.Get(sightId);
            if (sightItem == null) return false;
            
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkSightSwitchRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = networkId,
                WeaponHash = m_CurrentWeapon.Id.Hash,
                NewSightHash = sightId.Hash,
                ClientTimestamp = Time.time
            };
            
            m_PendingSightSwitches.Add(new PendingSightSwitchRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
            return true;
        }
        
        /// <summary>
        /// [Server] Process a sight switch request from a client.
        /// </summary>
        public NetworkSightSwitchResponse ProcessSightSwitchRequest(NetworkSightSwitchRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.InvalidState
                };
            }
            
            // Validate weapon is equipped
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.WeaponNotEquipped
                };
            }
            
            // Check not reloading
            if (m_ShooterStance.Reloading.IsReloading)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.Reloading
                };
            }
            
            // Check not shooting
            if (m_ShooterStance.Shooting.IsShootingAnimation)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.Shooting
                };
            }
            
            // Validate sight exists on weapon
            // We need to find the sight by hash
            bool sightFound = false;
            foreach (var sightItem in m_CurrentWeapon.Sights.List)
            {
                if (sightItem.Id.Hash == request.NewSightHash)
                {
                    sightFound = true;
                    break;
                }
            }
            
            if (!sightFound)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.SightNotAvailable
                };
            }
            
            // Check not already using this sight
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.SightId.Hash == request.NewSightHash)
            {
                return new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.AlreadyUsingSight
                };
            }
            
            return new NetworkSightSwitchResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = SightSwitchRejectionReason.None
            };
        }
        
        /// <summary>
        /// [Client] Receive sight switch response from server.
        /// </summary>
        public void ReceiveSightSwitchResponse(NetworkSightSwitchResponse response)
        {
            int index = m_PendingSightSwitches.FindIndex(p => ((response.CorrelationId != 0 && p.Request.CorrelationId != 0) ? p.Request.CorrelationId == response.CorrelationId : p.Request.RequestId == response.RequestId));
            if (index >= 0)
            {
                m_PendingSightSwitches.RemoveAt(index);
            }
            
            if (!response.Validated && m_LogShots)
            {
                Debug.Log($"[NetworkShooterController] Sight switch rejected: {response.RejectionReason}");
            }
        }
        
        /// <summary>
        /// [All] Receive sight switch broadcast from server.
        /// </summary>
        public void ReceiveSightSwitchBroadcast(NetworkSightSwitchBroadcast broadcast)
        {
            OnSightSwitchBroadcastReceived?.Invoke(broadcast);
            
            // Update remote client sight state
            if (m_IsRemoteClient && m_CurrentWeaponData != null)
            {
                // Find the sight by hash and apply
                foreach (var sightItem in m_CurrentWeapon.Sights.List)
                {
                    if (sightItem.Id.Hash == broadcast.NewSightHash)
                    {
                        // Note: We can't directly call OnChangeSight as it's an internal method
                        // The actual sight change should be applied via the GC2 system
                        break;
                    }
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void CleanupPendingRequests()
        {
            float timeout = 2f;
            float currentTime = Time.time;
            
            // Cleanup pending shots
            for (int i = m_PendingShots.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingShots[i].SentTime > timeout)
                {
                    m_PendingShots.RemoveAt(i);
                }
            }
            
            // Cleanup pending hits
            for (int i = m_PendingHits.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingHits[i].SentTime > timeout)
                {
                    m_PendingHits.RemoveAt(i);
                }
            }
            
            // Cleanup pending reloads
            for (int i = m_PendingReloads.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingReloads[i].SentTime > timeout)
                {
                    m_PendingReloads.RemoveAt(i);
                }
            }
            
            // Cleanup pending fix jams
            for (int i = m_PendingFixJams.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingFixJams[i].SentTime > timeout)
                {
                    m_PendingFixJams.RemoveAt(i);
                }
            }
            
            // Cleanup pending charges
            for (int i = m_PendingCharges.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingCharges[i].SentTime > timeout)
                {
                    m_PendingCharges.RemoveAt(i);
                }
            }
            
            // Cleanup pending sight switches
            for (int i = m_PendingSightSwitches.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingSightSwitches[i].SentTime > timeout)
                {
                    m_PendingSightSwitches.RemoveAt(i);
                }
            }
            
            // Cleanup validated shots
            List<ushort> toRemove = null;
            foreach (var kvp in m_ValidatedShots)
            {
                if (currentTime - kvp.Value.ValidatedTime > timeout)
                {
                    toRemove ??= new List<ushort>();
                    toRemove.Add(kvp.Key);
                }
            }
            
            if (toRemove != null)
            {
                foreach (var key in toRemove)
                {
                    m_ValidatedShots.Remove(key);
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DEBUG
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!m_DrawDebugRays) return;
            
            // Draw pending shots
            Gizmos.color = Color.yellow;
            foreach (var pending in m_PendingShots)
            {
                Gizmos.DrawLine(
                    pending.Request.MuzzlePosition,
                    pending.Request.MuzzlePosition + pending.Request.ShotDirection * 10f
                );
            }
            
            // Draw pending hits
            Gizmos.color = Color.red;
            foreach (var pending in m_PendingHits)
            {
                Gizmos.DrawWireSphere(pending.Request.HitPoint, 0.1f);
            }
        }
#endif
    }
}
#endif
