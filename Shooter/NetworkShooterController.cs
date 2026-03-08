#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Shooter;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

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
    public partial class NetworkShooterController : MonoBehaviour
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
        private ushort m_LastIssuedRequestId = 1;
        private ushort m_LastSentShotRequestId;
        private static readonly List<ulong> s_SharedPendingRemovalBuffer = new(32);
        private static readonly FieldInfo SIGHT_ITEMS_FIELD = typeof(SightList).GetField(
            "m_Sights",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private readonly Dictionary<ulong, PendingShotRequest> m_PendingShots = new(16);
        private readonly Dictionary<ulong, PendingHitRequest> m_PendingHits = new(32);
        private readonly HashSet<int> m_ProcessedHits = new(64);
        
        // Validated shots (for hit validation)
        private readonly Dictionary<ushort, ValidatedShot> m_ValidatedShots = new(16);
        
        // Pending reload/jam/charge requests
        private readonly Dictionary<ulong, PendingReloadRequest> m_PendingReloads = new(4);
        private readonly Dictionary<ulong, PendingFixJamRequest> m_PendingFixJams = new(4);
        private readonly Dictionary<ulong, PendingChargeRequest> m_PendingCharges = new(4);
        private readonly Dictionary<ulong, PendingSightSwitchRequest> m_PendingSightSwitches = new(4);
        
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
        
        private struct PendingShotRequest : ITimedPendingRequest
        {
            public NetworkShotRequest Request;
            public float SentTime;
            public bool OptimisticPlayed;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingHitRequest : ITimedPendingRequest
        {
            public NetworkShooterHitRequest Request;
            public float SentTime;
            public bool OptimisticPlayed;
            public float PendingSentTime => SentTime;
        }
        
        private struct ValidatedShot
        {
            public NetworkShotRequest Request;
            public float ValidatedTime;
            public int HitsProcessed;
        }
        
        private struct PendingReloadRequest : ITimedPendingRequest
        {
            public NetworkReloadRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingFixJamRequest : ITimedPendingRequest
        {
            public NetworkFixJamRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingChargeRequest : ITimedPendingRequest
        {
            public NetworkChargeStartRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingSightSwitchRequest : ITimedPendingRequest
        {
            public NetworkSightSwitchRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
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

        private ushort GetNextRequestId()
        {
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            ushort requestId = m_NextRequestId;
            m_NextRequestId++;
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            m_LastIssuedRequestId = requestId;
            return requestId;
        }

        private ulong GetPendingKey(uint actorNetworkId, uint correlationId)
        {
            uint resolvedActorNetworkId = actorNetworkId != 0 ? actorNetworkId : NetworkId;
            if (resolvedActorNetworkId == 0 || correlationId == 0) return 0;
            return ((ulong)resolvedActorNetworkId << 32) | correlationId;
        }

        private bool TryTakePending<T>(
            Dictionary<ulong, T> pendingRequests,
            uint actorNetworkId,
            uint correlationId,
            out T pending)
        {
            ulong key = GetPendingKey(actorNetworkId, correlationId);
            if (key != 0 && pendingRequests.TryGetValue(key, out pending))
            {
                pendingRequests.Remove(key);
                return true;
            }

            pending = default;
            return false;
        }

        private static bool TryResolveSightIdByHash(SightList sightList, int sightHash, out IdString sightId)
        {
            sightId = IdString.EMPTY;
            if (sightList == null || sightHash == 0) return false;

            if (SIGHT_ITEMS_FIELD?.GetValue(sightList) is not SightItem[] sightItems) return false;

            for (int i = 0; i < sightItems.Length; i++)
            {
                SightItem item = sightItems[i];
                if (item == null) continue;
                if (item.Id.Hash != sightHash) continue;

                sightId = item.Id;
                return true;
            }

            return false;
        }

        private static bool IsAimingState(ShooterWeapon weapon, WeaponData weaponData)
        {
            if (weapon == null || weaponData == null) return false;
            return weaponData.SightId.Hash != weapon.Sights.DefaultId.Hash;
        }

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
            if (IsAimingState(m_CurrentWeapon, m_CurrentWeaponData)) flags |= NetworkWeaponState.FLAG_IS_AIMING;
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
                IsAiming = IsAimingState(m_CurrentWeapon, m_CurrentWeaponData),
                CompressedDirection = NetworkAimState.CompressDirection(muzzle.Direction)
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void CleanupPendingRequests()
        {
            float timeout = 2f;
            float currentTime = Time.time;

            PendingRequestCleanup.RemoveTimedOut(m_PendingShots, s_SharedPendingRemovalBuffer, currentTime, timeout);
            PendingRequestCleanup.RemoveTimedOut(m_PendingHits, s_SharedPendingRemovalBuffer, currentTime, timeout);
            PendingRequestCleanup.RemoveTimedOut(m_PendingReloads, s_SharedPendingRemovalBuffer, currentTime, timeout);
            PendingRequestCleanup.RemoveTimedOut(m_PendingFixJams, s_SharedPendingRemovalBuffer, currentTime, timeout);
            PendingRequestCleanup.RemoveTimedOut(m_PendingCharges, s_SharedPendingRemovalBuffer, currentTime, timeout);
            PendingRequestCleanup.RemoveTimedOut(m_PendingSightSwitches, s_SharedPendingRemovalBuffer, currentTime, timeout);
            
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
            foreach (var pending in m_PendingShots.Values)
            {
                Gizmos.DrawLine(
                    pending.Request.MuzzlePosition,
                    pending.Request.MuzzlePosition + pending.Request.ShotDirection * 10f
                );
            }
            
            // Draw pending hits
            Gizmos.color = Color.red;
            foreach (var pending in m_PendingHits.Values)
            {
                Gizmos.DrawWireSphere(pending.Request.HitPoint, 0.1f);
            }
        }
#endif
    }
}
#endif
