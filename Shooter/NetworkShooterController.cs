#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Common.Audio;
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

        [Tooltip("How quickly remote shooter aim targets interpolate toward network updates.")]
        [SerializeField] private float m_RemoteAimInterpolationSpeed = 18f;
        
        [Header("Lag Compensation")]
        [Tooltip("Configuration for lag compensation validation.")]
        [SerializeField] private ShooterValidationConfig m_ValidationConfig = new();
        
        [Header("Debug")]
        [Tooltip("Logs Shooter controller wiring, equipped weapon changes, and missing network listeners.")]
        [SerializeField] private bool m_LogDiagnostics = true;
        [SerializeField] private bool m_LogShots = false;
        [SerializeField] private bool m_LogHits = false;
#if UNITY_EDITOR
        [SerializeField] private bool m_DrawDebugRays = false;
#endif
        
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
        public event Action<NetworkShooterController, NetworkWeaponState> OnWeaponStateChanged;

        /// <summary>Called when aim point/direction changes.</summary>
        public event Action<NetworkShooterController, NetworkAimState> OnAimStateChanged;
        
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
        private const float LEAN_SYNC_EPSILON = 0.01f;
        private static readonly int SHOOT_TRIGGER = Animator.StringToHash("Shoot");
        private static readonly MethodInfo SHOOTING_ON_SHOOT_METHOD = typeof(Shooting).GetMethod(
            "OnShoot",
            BindingFlags.Instance | BindingFlags.NonPublic);
        
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
        private static readonly FieldInfo WEAPON_DATA_LAST_SHOT_FRAME_FIELD = typeof(WeaponData).GetField(
            "<LastShotFrame>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo WEAPON_DATA_LAST_SHOT_TIME_FIELD = typeof(WeaponData).GetField(
            "<LastShotTime>k__BackingField",
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
        
        // State tracking
        private NetworkWeaponState m_LastWeaponState;
        private NetworkAimState m_LastAimState;
        private NetworkAimState m_RemoteAimState;
        private float m_LastWeaponStateSync;
        private float m_LastAimStateSync;
        private float m_LastRemoteAimStateTime;
        private bool m_HasRemoteAimState;
        private Vector3 m_RemoteAimTargetPoint;
        private Vector3 m_RemoteAimSmoothedPoint;
        private bool m_HasRemoteAimSmoothedPoint;
        private int m_LastRemoteAimSmoothFrame = -1;
        private bool m_RecoveringMissingWeaponProp;
        private float m_NextWeaponSyncSkipDiagnosticTime;
        private float m_NextAimSyncSkipDiagnosticTime;
        private float m_NextRemoteAimResolverDiagnosticTime;
        
        // Current weapon tracking
        private ShooterWeapon m_CurrentWeapon;
        private WeaponData m_CurrentWeaponData;
        private float m_LastServerValidatedShotTime;
        
        // Lag compensation validator (server-only)
        private ShooterLagCompensationValidator m_Validator;

        private string DebugRole =>
            m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : (m_IsRemoteClient ? "RemoteClient" : "Uninitialized"));

        private void LogDiagnostics(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.Log($"[NetworkShooterController] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogDiagnosticsWarning(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.LogWarning($"[NetworkShooterController] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private bool ShouldLogDiagnostic(ref float nextTime, float interval = 1f)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return false;

            float now = Time.unscaledTime;
            if (now < nextTime) return false;

            nextTime = now + Mathf.Max(0.1f, interval);
            return true;
        }

        private string BuildAmmoDebug(ShooterWeapon weapon = null)
        {
            ShooterWeapon selectedWeapon = weapon != null ? weapon : m_CurrentWeapon;
            if (selectedWeapon == null)
            {
                return "weapon=null";
            }

            bool hasMagazine = false;
            bool hasMunition = false;
            int inMagazine = -1;
            int magazineSize = -1;
            int totalAmmo = -1;
            string error = null;

            try
            {
                Args args = m_CurrentWeaponData != null && m_CurrentWeapon == selectedWeapon
                    ? m_CurrentWeaponData.WeaponArgs
                    : GetShooterWeaponArgs(selectedWeapon);

                hasMagazine = selectedWeapon.Magazine.GetHasMagazine(args);
                if (hasMagazine)
                {
                    magazineSize = selectedWeapon.Magazine.GetMagazineSize(args);
                    totalAmmo = selectedWeapon.Magazine.GetTotalAmmo(args);

                    if (m_Character != null &&
                        m_Character.Combat.RequestMunition(selectedWeapon) is ShooterMunition munition)
                    {
                        hasMunition = true;
                        inMagazine = munition.InMagazine;
                    }
                }
            }
            catch (Exception exception)
            {
                error = $"{exception.GetType().Name}: {exception.Message}";
            }

            bool isReloading = m_ShooterStance != null && m_ShooterStance.Reloading.IsReloading;
            string reloadWeapon = m_ShooterStance != null &&
                                  m_ShooterStance.Reloading.WeaponReloading != null
                ? m_ShooterStance.Reloading.WeaponReloading.name
                : "none";

            return $"weapon={selectedWeapon.name} hash={selectedWeapon.Id.Hash} hasMagazine={hasMagazine} " +
                   $"munition={hasMunition} inMagazine={inMagazine} magazineSize={magazineSize} " +
                   $"totalAmmo={totalAmmo} weaponData={(m_CurrentWeaponData != null)} " +
                   $"reloading={isReloading} reloadWeapon={reloadWeapon}" +
                   (!string.IsNullOrEmpty(error) ? $" error={error}" : string.Empty);
        }
        
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

        private static bool HasWeaponStateChanged(NetworkWeaponState current, NetworkWeaponState previous)
        {
            return current.WeaponHash != previous.WeaponHash ||
                   current.SightHash != previous.SightHash ||
                   current.StateFlags != previous.StateFlags ||
                   current.AmmoInMagazine != previous.AmmoInMagazine ||
                   HasLeanStateChanged(current, previous);
        }

        private static bool HasLeanStateChanged(NetworkWeaponState current, NetworkWeaponState previous)
        {
            return Mathf.Abs(current.LeanAmount - previous.LeanAmount) > LEAN_SYNC_EPSILON ||
                   Mathf.Abs(current.LeanDecay - previous.LeanDecay) > LEAN_SYNC_EPSILON;
        }

        public bool TryGetNetworkAimPoint(out Vector3 aimPoint)
        {
            aimPoint = default;
            if (m_IsLocalClient) return false;

            if (!m_HasRemoteAimState)
            {
                if (ShouldLogDiagnostic(ref m_NextRemoteAimResolverDiagnosticTime))
                {
                    LogDiagnostics("remote aim resolver has no received aim state yet");
                }

                return false;
            }

            if (!m_RemoteAimState.IsAiming)
            {
                if (ShouldLogDiagnostic(ref m_NextRemoteAimResolverDiagnosticTime))
                {
                    LogDiagnostics(
                        $"remote aim resolver has state but shooter is not aiming point={m_RemoteAimState.AimPoint}");
                }

                return false;
            }

            float age = Time.unscaledTime - m_LastRemoteAimStateTime;
            if (age > 1f)
            {
                if (ShouldLogDiagnostic(ref m_NextRemoteAimResolverDiagnosticTime))
                {
                    LogDiagnostics(
                        $"remote aim resolver state is stale age={age:F2}s point={m_RemoteAimState.AimPoint}");
                }

                return false;
            }

            if (!m_HasRemoteAimSmoothedPoint)
            {
                m_RemoteAimSmoothedPoint = m_RemoteAimTargetPoint;
                m_HasRemoteAimSmoothedPoint = true;
            }

            if (m_LastRemoteAimSmoothFrame != Time.frameCount)
            {
                float speed = Mathf.Max(0.01f, m_RemoteAimInterpolationSpeed);
                float t = 1f - Mathf.Exp(-speed * Time.deltaTime);
                m_RemoteAimSmoothedPoint = Vector3.Lerp(m_RemoteAimSmoothedPoint, m_RemoteAimTargetPoint, t);
                m_LastRemoteAimSmoothFrame = Time.frameCount;
            }

            aimPoint = m_RemoteAimSmoothedPoint;
            return aimPoint.sqrMagnitude > 0.0001f;
        }

        public void ApplyRemoteAimState(NetworkAimState state)
        {
            if (m_IsLocalClient)
            {
                LogDiagnostics(
                    $"ignored remote aim state on local owner aiming={state.IsAiming} " +
                    $"point={state.AimPoint} compressed={state.CompressedDirection}");
                return;
            }

            m_RemoteAimState = state;
            m_HasRemoteAimState = true;
            m_LastRemoteAimStateTime = Time.unscaledTime;
            m_RemoteAimTargetPoint = state.AimPoint;
            if (!state.IsAiming || !m_HasRemoteAimSmoothedPoint)
            {
                m_RemoteAimSmoothedPoint = state.AimPoint;
                m_HasRemoteAimSmoothedPoint = state.IsAiming;
            }

            LogDiagnostics(
                $"applied remote aim state aiming={state.IsAiming} point={state.AimPoint} " +
                $"compressed={state.CompressedDirection} accuracy={state.Accuracy}");
        }

        private void GetShooterLeanState(out float leanAmount, out float leanDecay)
        {
            RigShooterHuman rig = m_Character != null ? m_Character.IK.GetRig<RigShooterHuman>() : null;
            leanAmount = rig != null ? rig.LeanAmount : 0f;
            leanDecay = rig != null ? rig.LeanDecay : 0f;
        }

        private void ApplyRemoteLeanState(NetworkWeaponState state)
        {
            if (m_IsLocalClient || m_Character == null) return;

            RigShooterHuman rig = m_Character.IK.GetRig<RigShooterHuman>();
            bool hasLeanTarget =
                Mathf.Abs(state.LeanAmount) > LEAN_SYNC_EPSILON ||
                Mathf.Abs(state.LeanDecay) > LEAN_SYNC_EPSILON;

            if (rig == null)
            {
                if (!hasLeanTarget) return;
                rig = m_Character.IK.RequireRig<RigShooterHuman>();
            }

            rig.LeanAmount = state.LeanAmount;
            rig.LeanDecay = state.LeanDecay;

            LogDiagnostics(
                $"applied remote lean amount={state.LeanAmount:F1} decay={state.LeanDecay:F2}");
        }

        private static float GetNetworkTime()
        {
            return NetworkShooterManager.Instance != null
                ? NetworkShooterManager.Instance.NetworkTime
                : Time.time;
        }

        private bool ApplyRemoteSightHash(ShooterWeapon weapon, int sightHash)
        {
            if (weapon == null || sightHash == 0) return false;

            TryGetShooterStance();
            if (m_ShooterStance == null) return false;

            m_CurrentWeaponData ??= m_ShooterStance.Get(weapon);
            if (m_CurrentWeaponData == null) return false;
            if (m_CurrentWeaponData.SightId.Hash == sightHash) return true;

            if (!TryResolveSightIdByHash(weapon.Sights, sightHash, out IdString sightId))
            {
                LogDiagnosticsWarning(
                    $"remote sight state skipped: sight hash {sightHash} was not found on {weapon.name}");
                return false;
            }

            m_ShooterStance.EnterSight(weapon, sightId);
            m_CurrentWeaponData = m_ShooterStance.Get(weapon);
            LogDiagnostics($"applied remote sight state weapon={weapon.name} sightHash={sightHash}");
            return true;
        }

        private void NotifyRemoteShotState(ShooterWeapon weapon, Args args, float animationDuration)
        {
            TryGetShooterStance();
            if (m_ShooterStance == null || m_CurrentWeaponData == null)
            {
                LogDiagnostics(
                    $"remote shooter shot state skipped stance={(m_ShooterStance != null)} " +
                    $"weaponData={(m_CurrentWeaponData != null)} weapon={(weapon != null ? weapon.name : "null")}");
                return;
            }

            float duration = animationDuration > 0f
                ? animationDuration
                : GetFallbackRemoteShotDuration(weapon, args);

            try
            {
                WEAPON_DATA_LAST_SHOT_FRAME_FIELD?.SetValue(
                    m_CurrentWeaponData,
                    m_Character != null ? m_Character.Time.Frame : Time.frameCount);

                WEAPON_DATA_LAST_SHOT_TIME_FIELD?.SetValue(
                    m_CurrentWeaponData,
                    m_Character != null ? m_Character.Time.Time : Time.time);

                SHOOTING_ON_SHOOT_METHOD?.Invoke(m_ShooterStance.Shooting, new object[] { duration });
                LogDiagnostics($"marked remote shooter shot state duration={duration:F2}");
            }
            catch (Exception exception)
            {
                LogDiagnosticsWarning($"remote shooter shot state marker failed: {exception.GetType().Name}");
            }
        }

        private static float GetFallbackRemoteShotDuration(ShooterWeapon weapon, Args args)
        {
            if (weapon == null) return 0.1f;

            float fireRate = weapon.Fire.FireRate(args);
            if (float.IsNaN(fireRate) || float.IsInfinity(fireRate) || fireRate <= float.Epsilon)
            {
                return 0.1f;
            }

            return Mathf.Clamp(0.75f / fireRate, 0.08f, 0.25f);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            LogDiagnostics(
                $"awake character={(m_Character != null)} networkCharacter={(m_NetworkCharacter != null)}");
        }
        
        private void Start()
        {
            // Subscribe to weapon equip events
            m_Character.Combat.EventEquip += OnWeaponEquipped;
            m_Character.Combat.EventUnequip += OnWeaponUnequipped;
            
            // Try to get initial shooter stance
            TryGetShooterStance();
            TryAdoptEquippedShooterWeapon();
            LogDiagnostics(
                $"start stance={(m_ShooterStance != null)} manager={(NetworkShooterManager.Instance != null)} " +
                $"currentWeapon={(m_CurrentWeapon != null ? m_CurrentWeapon.name : "none")}");
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
            RecoverMissingEquippedPropIfNeeded();

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

            LogDiagnostics($"initialized server={isServer} localClient={isLocalClient}");
            TryAdoptEquippedShooterWeapon();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STANCE & WEAPON TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void TryGetShooterStance()
        {
            m_ShooterStance = m_Character.Combat.RequestStance<ShooterStance>();
            if (m_ShooterStance == null)
            {
                LogDiagnosticsWarning("could not resolve ShooterStance from GC2 Combat");
            }
        }
        
        private void OnWeaponEquipped(IWeapon weapon, GameObject instance)
        {
            if (weapon is not ShooterWeapon shooterWeapon)
            {
                PublishShooterUnequipStateForNonShooterEquip(weapon);
                return;
            }

            if (shooterWeapon != null)
            {
                m_CurrentWeapon = shooterWeapon;
                NetworkShooterManager.RegisterShooterWeapon(shooterWeapon);
                TryGetShooterStance();
                
                if (m_ShooterStance != null)
                {
                    m_CurrentWeaponData = m_ShooterStance.Get(shooterWeapon);
                }

                EnsureMagazineInitialized(shooterWeapon);
                m_LastWeaponState = BuildWeaponState();
                if (m_IsLocalClient)
                {
                    OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
                }

                LogDiagnostics(
                    $"equipped shooter weapon={shooterWeapon.name} hash={shooterWeapon.Id.Hash} " +
                    $"model={(instance != null ? instance.name : "null")} " +
                    $"stance={(m_ShooterStance != null)} weaponData={(m_CurrentWeaponData != null)} " +
                    $"registered={NetworkShooterManager.IsShooterWeaponRegistered(shooterWeapon)}");
            }
        }

        private bool TryAdoptEquippedShooterWeapon()
        {
            if (m_Character?.Combat?.Weapons == null) return false;
            if (m_CurrentWeapon != null && m_CurrentWeaponData != null) return true;

            Weapon[] equippedWeapons = m_Character.Combat.Weapons;
            for (int i = 0; i < equippedWeapons.Length; i++)
            {
                if (equippedWeapons[i]?.Asset is not ShooterWeapon shooterWeapon) continue;
                if (!TryAdoptShooterWeapon(shooterWeapon, requireEquipped: true)) continue;
                return true;
            }

            return false;
        }

        private bool TryAdoptShooterWeapon(ShooterWeapon shooterWeapon, bool requireEquipped)
        {
            if (shooterWeapon == null || m_Character == null) return false;

            bool isEquipped = m_Character.Combat.IsEquipped(shooterWeapon);
            GameObject prop = m_Character.Combat.GetProp(shooterWeapon);
            if (requireEquipped && !isEquipped && prop == null) return false;

            if (m_CurrentWeapon != shooterWeapon)
            {
                LogDiagnostics(
                    $"adopting shooter weapon from GC2 state weapon={shooterWeapon.name} hash={shooterWeapon.Id.Hash} " +
                    $"equipped={isEquipped} prop={(prop != null)}");
            }

            m_CurrentWeapon = shooterWeapon;
            NetworkShooterManager.RegisterShooterWeapon(shooterWeapon);
            TryGetShooterStance();
            m_CurrentWeaponData = m_ShooterStance != null ? m_ShooterStance.Get(shooterWeapon) : null;
            EnsureMagazineInitialized(shooterWeapon);

            return m_CurrentWeaponData != null;
        }

        private void PublishShooterUnequipStateForNonShooterEquip(IWeapon weapon)
        {
            if (m_CurrentWeapon == null && m_LastWeaponState.WeaponHash == 0 && !HasEquippedShooterWeapon())
            {
                return;
            }

            LogDiagnostics(
                $"non-shooter weapon equipped ({weapon?.GetType().Name ?? "null"}). Clearing shooter weapon state.");

            int previousWeaponHash = m_CurrentWeapon != null
                ? m_CurrentWeapon.Id.Hash
                : m_LastWeaponState.WeaponHash;
            GameObject previousProp = m_CurrentWeapon != null
                ? m_Character.Combat.GetProp(m_CurrentWeapon)
                : null;

            if (m_CurrentWeapon != null)
            {
                StopRemoteReload(m_CurrentWeapon, CancelReason.ForceStop);
            }

            RemoveShooterProp(previousWeaponHash, previousProp);

            m_CurrentWeapon = null;
            m_CurrentWeaponData = null;
            m_LastWeaponState = NetworkWeaponState.None;

            if (m_IsLocalClient)
            {
                OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
            }
        }
        
        private void OnWeaponUnequipped(IWeapon weapon, GameObject instance)
        {
            if (weapon is ShooterWeapon)
            {
                LogDiagnostics(
                    $"unequipped shooter weapon={weapon} model={(instance != null ? instance.name : "null")}");
                m_CurrentWeapon = null;
                m_CurrentWeaponData = null;
                m_LastWeaponState = NetworkWeaponState.None;
                if (m_IsLocalClient)
                {
                    OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATE SYNC
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public void ForceNetworkStateSync()
        {
            if (!m_IsLocalClient && !m_IsServer)
            {
                LogDiagnostics("forced state sync skipped: controller is not local/server yet");
                return;
            }

            TryAdoptEquippedShooterWeapon();
            UpdateWeaponState(force: true);
            UpdateAimState(force: true);
        }

        private void UpdateWeaponState(bool force = false)
        {
            bool intervalElapsed = force || Time.time - m_LastWeaponStateSync >= m_WeaponStateSyncInterval;
            
            if (m_CurrentWeapon == null || m_CurrentWeaponData == null)
            {
                if (!intervalElapsed) return;
                m_LastWeaponStateSync = Time.time;

                TryAdoptEquippedShooterWeapon();
                TryRefreshCurrentWeaponData();

                if (m_CurrentWeapon != null && m_CurrentWeaponData != null)
                {
                    NetworkWeaponState refreshedState = BuildWeaponState();
                    if (force || HasWeaponStateChanged(refreshedState, m_LastWeaponState))
                    {
                        m_LastWeaponState = refreshedState;
                        LogDiagnostics(
                            $"weapon state refreshed weaponHash={refreshedState.WeaponHash} " +
                            $"ammo={refreshedState.AmmoInMagazine} flags=0x{refreshedState.StateFlags:X2} " +
                            $"listener={(OnWeaponStateChanged != null)} force={force}");
                        OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
                    }

                    return;
                }

                if (ShouldLogDiagnostic(ref m_NextWeaponSyncSkipDiagnosticTime))
                {
                    LogDiagnostics(
                        $"weapon state sync idle currentWeapon={(m_CurrentWeapon != null ? m_CurrentWeapon.name : "null")} " +
                        $"weaponData={(m_CurrentWeaponData != null)} stance={(m_ShooterStance != null)} " +
                        $"listener={(OnWeaponStateChanged != null)}");
                }

                if (m_LastWeaponState.WeaponHash != 0)
                {
                    m_LastWeaponState = NetworkWeaponState.None;
                    if (OnWeaponStateChanged == null)
                    {
                        LogDiagnostics("weapon state changed to none, but no listeners are registered");
                    }
                    else
                    {
                        LogDiagnostics("weapon state changed to none");
                    }

                    OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
                }
                return;
            }

            NetworkWeaponState newState = BuildWeaponState();
            bool changed = HasWeaponStateChanged(newState, m_LastWeaponState);
            bool leanChanged = HasLeanStateChanged(newState, m_LastWeaponState);
            if (!changed && !force) return;
            if (!intervalElapsed && !leanChanged && !force) return;

            m_LastWeaponStateSync = Time.time;
            
            if (changed || force)
            {
                m_LastWeaponState = newState;
                if (OnWeaponStateChanged == null)
                {
                    LogDiagnostics(
                        $"weapon state changed weaponHash={newState.WeaponHash} ammo={newState.AmmoInMagazine} " +
                        $"sightHash={newState.SightHash} flags=0x{newState.StateFlags:X2} " +
                        $"lean={newState.LeanAmount:F1}/{newState.LeanDecay:F2}, but no listeners are registered");
                }
                else
                {
                    LogDiagnostics(
                        $"weapon state changed weaponHash={newState.WeaponHash} ammo={newState.AmmoInMagazine} " +
                        $"sightHash={newState.SightHash} flags=0x{newState.StateFlags:X2} " +
                        $"lean={newState.LeanAmount:F1}/{newState.LeanDecay:F2}");
                }

                OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
            }
        }

        private NetworkWeaponState BuildWeaponState()
        {
            if (m_CurrentWeapon == null) return NetworkWeaponState.None;

            TryRefreshCurrentWeaponData();

            var munition = m_Character.Combat.RequestMunition(m_CurrentWeapon) as ShooterMunition;
            ushort ammo = munition != null ? (ushort)Mathf.Clamp(munition.InMagazine, 0, ushort.MaxValue) : (ushort)0;

            byte flags = 0;
            if (m_ShooterStance != null && m_ShooterStance.Reloading.IsReloading) flags |= NetworkWeaponState.FLAG_IS_RELOADING;
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsJammed) flags |= NetworkWeaponState.FLAG_IS_JAMMED;
            if (m_CurrentWeaponData != null && IsAimingState(m_CurrentWeapon, m_CurrentWeaponData)) flags |= NetworkWeaponState.FLAG_IS_AIMING;
            if (m_ShooterStance != null && m_ShooterStance.Shooting.IsShootingAnimation) flags |= NetworkWeaponState.FLAG_IS_SHOOTING;
            if (m_CurrentWeaponData != null && m_CurrentWeaponData.IsPullingTrigger) flags |= NetworkWeaponState.FLAG_IS_CHARGING;

            GetShooterLeanState(out float leanAmount, out float leanDecay);

            return new NetworkWeaponState
            {
                WeaponHash = m_CurrentWeapon.Id.Hash,
                SightHash = m_CurrentWeaponData?.SightId.Hash ?? 0,
                AmmoInMagazine = ammo,
                StateFlags = flags,
                LeanAmount = leanAmount,
                LeanDecay = leanDecay
            };
        }

        private void TryRefreshCurrentWeaponData()
        {
            if (m_CurrentWeapon == null || m_CurrentWeaponData != null) return;

            TryGetShooterStance();
            if (m_ShooterStance == null) return;

            m_CurrentWeaponData = m_ShooterStance.Get(m_CurrentWeapon);
        }

        public async void ApplyRemoteWeaponState(
            NetworkWeaponState state,
            ShooterWeapon weapon,
            GameObject modelPrefab,
            Handle handle)
        {
            if (m_IsLocalClient)
            {
                LogDiagnostics(
                    $"ignored remote weapon state on local owner weaponHash={state.WeaponHash} flags=0x{state.StateFlags:X2}");
                return;
            }

            if (m_Character == null)
            {
                LogDiagnosticsWarning($"remote weapon state skipped because Character is missing weaponHash={state.WeaponHash}");
                return;
            }

            LogDiagnostics(
                $"apply remote weapon state weaponHash={state.WeaponHash} ammo={state.AmmoInMagazine} " +
                $"flags=0x{state.StateFlags:X2} weapon={(weapon != null ? weapon.name : "null")} " +
                $"prefab={(modelPrefab != null ? modelPrefab.name : "null")} handle={(handle != null ? handle.name : "null")}");

            if (state.WeaponHash == 0)
            {
                ShooterWeapon weaponToUnequip = m_CurrentWeapon;
                if (weaponToUnequip == null && m_LastWeaponState.WeaponHash != 0)
                {
                    weaponToUnequip = NetworkShooterManager.GetShooterWeaponByHash(m_LastWeaponState.WeaponHash);
                }

                int previousWeaponHash = weaponToUnequip != null
                    ? weaponToUnequip.Id.Hash
                    : m_LastWeaponState.WeaponHash;
                GameObject previousProp = weaponToUnequip != null
                    ? m_Character.Combat.GetProp(weaponToUnequip)
                    : null;

                if (weaponToUnequip != null)
                {
                    StopRemoteReload(weaponToUnequip, CancelReason.ForceStop);

                    if (m_Character.Combat.IsEquipped(weaponToUnequip))
                    {
                        await m_Character.Combat.Unequip(weaponToUnequip, new Args(m_Character.gameObject));
                    }
                }

                RemoveShooterProp(previousWeaponHash, previousProp);
                await UnequipEquippedShooterWeapons();

                m_CurrentWeapon = null;
                m_CurrentWeaponData = null;
                m_LastWeaponState = NetworkWeaponState.None;
                ApplyRemoteLeanState(state);
                LogDiagnostics("applied remote shooter unequip state");
                return;
            }

            if (weapon == null)
            {
                LogDiagnosticsWarning($"remote weapon state skipped because weapon hash {state.WeaponHash} is not registered");
                return;
            }

            NetworkShooterManager.RegisterShooterWeapon(weapon);
            bool wasReloading = m_LastWeaponState.IsReloading;
            bool wasEquipped = m_CurrentWeapon == weapon || m_Character.Combat.IsEquipped(weapon);
            m_CurrentWeapon = weapon;
            TryGetShooterStance();

            await UnequipNonShooterWeapons();

            GameObject model = m_Character.Combat.GetProp(weapon);
            if (wasEquipped && model == null)
            {
                await m_Character.Combat.Unequip(weapon, new Args(m_Character.gameObject));
                wasEquipped = false;
            }

            if (model == null && modelPrefab != null)
            {
                model = AttachWeaponModel(modelPrefab, handle);
            }

            if (!m_Character.Combat.IsEquipped(weapon) && model != null)
            {
                await m_Character.Combat.Equip(weapon, model, new Args(m_Character.gameObject, model));
            }

            if (m_ShooterStance != null)
            {
                m_CurrentWeaponData = m_ShooterStance.Get(weapon);
            }

            NetworkWeaponState appliedState = state;
            bool preserveServerAmmo = m_IsServer && !m_IsLocalClient && wasEquipped;
            if (preserveServerAmmo && TryGetMagazineAmmo(weapon.Id.Hash, out ushort serverAmmo))
            {
                appliedState.AmmoInMagazine = serverAmmo;
                if (serverAmmo != state.AmmoInMagazine)
                {
                    LogDiagnostics(
                        $"[ShooterAmmoDebug] preserved server authoritative magazine from remote state " +
                        $"weapon={weapon.name} remoteAmmo={state.AmmoInMagazine} serverAmmo={serverAmmo}");
                }
            }

            ApplyRemoteSightHash(weapon, appliedState.SightHash);
            ApplyMagazineState(appliedState, weapon, wasEquipped);
            ApplyRemoteLeanState(appliedState);

            if (appliedState.IsReloading && !wasReloading)
            {
                PlayRemoteReload(weapon);
            }

            if (appliedState.IsShooting)
            {
                NotifyRemoteShotState(weapon, GetShooterWeaponArgs(weapon), 0f);
            }

            m_LastWeaponState = appliedState;

            LogDiagnostics(
                $"applied remote weapon state weapon={weapon.name} equipped={m_Character.Combat.IsEquipped(weapon)} " +
                $"weaponData={(m_CurrentWeaponData != null)} prop={(m_Character.Combat.GetProp(weapon) != null)} " +
                $"flags=0x{appliedState.StateFlags:X2}");
        }

        private async System.Threading.Tasks.Task UnequipEquippedShooterWeapons()
        {
            if (m_Character?.Combat?.Weapons == null) return;

            Weapon[] equippedWeapons = m_Character.Combat.Weapons;
            for (int i = 0; i < equippedWeapons.Length; i++)
            {
                if (equippedWeapons[i]?.Asset is not ShooterWeapon shooterWeapon) continue;
                if (!m_Character.Combat.IsEquipped(shooterWeapon)) continue;

                GameObject prop = m_Character.Combat.GetProp(shooterWeapon);
                StopRemoteReload(shooterWeapon, CancelReason.ForceStop);
                await m_Character.Combat.Unequip(shooterWeapon, new Args(m_Character.gameObject));
                RemoveShooterProp(shooterWeapon.Id.Hash, prop);
            }
        }

        private void RemoveShooterProp(int weaponHash, GameObject propInstance)
        {
            if (m_Character == null) return;

            GameObject modelPrefab = null;
            if (weaponHash != 0 &&
                NetworkShooterManager.TryGetShooterWeaponRegistryEntry(weaponHash, out var entry))
            {
                modelPrefab = entry.ModelPrefab;
            }

            if (modelPrefab != null)
            {
                if (propInstance != null)
                {
                    m_Character.Props.RemovePrefab(modelPrefab, propInstance.GetInstanceID());
                }

                m_Character.Props.RemovePrefab(modelPrefab);
                return;
            }

            if (propInstance != null)
            {
                m_Character.Props.RemoveInstance(propInstance);
            }
        }

        private bool HasEquippedShooterWeapon()
        {
            if (m_Character?.Combat?.Weapons == null) return false;

            Weapon[] equippedWeapons = m_Character.Combat.Weapons;
            for (int i = 0; i < equippedWeapons.Length; i++)
            {
                if (equippedWeapons[i]?.Asset is ShooterWeapon shooterWeapon &&
                    m_Character.Combat.IsEquipped(shooterWeapon))
                {
                    return true;
                }
            }

            return false;
        }

        public bool IsShooterWeaponEquipped(int weaponHash)
        {
            if (weaponHash == 0 || m_Character?.Combat == null) return false;
            if (m_CurrentWeapon == null || m_CurrentWeapon.Id.Hash != weaponHash) return false;

            return m_Character.Combat.IsEquipped(m_CurrentWeapon);
        }

        private async System.Threading.Tasks.Task UnequipNonShooterWeapons()
        {
            if (m_Character?.Combat?.Weapons == null) return;

            Weapon[] equippedWeapons = m_Character.Combat.Weapons;
            for (int i = 0; i < equippedWeapons.Length; i++)
            {
                IWeapon asset = equippedWeapons[i]?.Asset;
                if (asset == null || asset is ShooterWeapon) continue;
                if (!m_Character.Combat.IsEquipped(asset)) continue;

                await m_Character.Combat.Unequip(asset, new Args(m_Character.gameObject));
            }
        }

        private async void RecoverMissingEquippedPropIfNeeded()
        {
            if (m_RecoveringMissingWeaponProp) return;
            if (m_Character == null || m_CurrentWeapon == null) return;
            if (!m_Character.Combat.IsEquipped(m_CurrentWeapon)) return;
            if (m_Character.Combat.GetProp(m_CurrentWeapon) != null) return;

            ShooterWeapon weapon = m_CurrentWeapon;
            m_RecoveringMissingWeaponProp = true;

            try
            {
                LogDiagnosticsWarning(
                    $"recovering shooter weapon with missing prop weapon={weapon.name} hash={weapon.Id.Hash}");

                await m_Character.Combat.Unequip(weapon, new Args(m_Character.gameObject));
                if (m_CurrentWeapon != null && m_CurrentWeapon != weapon) return;

                GameObject model = AttachRegisteredWeaponModel(weapon);
                if (model != null)
                {
                    await m_Character.Combat.Equip(weapon, model, new Args(m_Character.gameObject, model));

                    m_CurrentWeapon = weapon;
                    TryGetShooterStance();
                    m_CurrentWeaponData = m_ShooterStance != null ? m_ShooterStance.Get(weapon) : null;

                    NetworkWeaponState recoveredState = BuildWeaponState();
                    m_LastWeaponState = recoveredState;

                    if (m_IsLocalClient || m_IsServer)
                    {
                        OnWeaponStateChanged?.Invoke(this, recoveredState);
                    }

                    LogDiagnostics(
                        $"recovered shooter weapon prop weapon={weapon.name} model={model.name} " +
                        $"weaponData={(m_CurrentWeaponData != null)}");
                    return;
                }

                if (m_CurrentWeapon == weapon)
                {
                    m_CurrentWeapon = null;
                    m_CurrentWeaponData = null;
                    m_LastWeaponState = NetworkWeaponState.None;

                    if (m_IsLocalClient || m_IsServer)
                    {
                        OnWeaponStateChanged?.Invoke(this, m_LastWeaponState);
                    }
                }

                LogDiagnosticsWarning(
                    $"could not recover shooter weapon prop because no registered model prefab exists for hash={weapon.Id.Hash}");
            }
            finally
            {
                m_RecoveringMissingWeaponProp = false;
            }
        }

        private GameObject AttachRegisteredWeaponModel(ShooterWeapon weapon)
        {
            if (weapon == null || m_Character == null) return null;
            if (!NetworkShooterManager.TryGetShooterWeaponRegistryEntry(weapon.Id.Hash, out var entry)) return null;
            return AttachWeaponModel(entry.ModelPrefab, entry.Handle);
        }

        private GameObject AttachWeaponModel(GameObject modelPrefab, Handle handle)
        {
            if (modelPrefab == null || m_Character == null) return null;

            var handleField = handle != null
                ? new HandleField(handle)
                : new HandleField(HumanBodyBones.RightHand);

            HandleResult result = handleField.Get(new Args(m_Character.gameObject));
            return m_Character.Props.AttachPrefab(
                result.Bone,
                modelPrefab,
                result.LocalPosition,
                result.LocalRotation
            );
        }

        private void EnsureMagazineInitialized(ShooterWeapon weapon)
        {
            if (weapon == null || m_Character == null) return;

            Args args = GetShooterWeaponArgs(weapon);
            if (!weapon.Magazine.GetHasMagazine(args)) return;

            ShooterMunition munition = m_Character.Combat.RequestMunition(weapon) as ShooterMunition;
            if (munition == null || munition.InMagazine > 0) return;

            int magazineSize = Mathf.Max(0, weapon.Magazine.GetMagazineSize(args));
            int totalAmmo = Mathf.Max(0, weapon.Magazine.GetTotalAmmo(args));
            int fillAmount = Mathf.Min(magazineSize, totalAmmo);
            if (fillAmount > 0) munition.InMagazine = fillAmount;
        }

        private void ApplyMagazineState(
            NetworkWeaponState state,
            ShooterWeapon weapon,
            bool wasEquipped)
        {
            if (weapon == null || m_Character == null) return;

            Args args = GetShooterWeaponArgs(weapon);
            if (!weapon.Magazine.GetHasMagazine(args)) return;

            ShooterMunition munition = m_Character.Combat.RequestMunition(weapon) as ShooterMunition;
            if (munition == null) return;

            if (state.AmmoInMagazine > 0 || wasEquipped)
            {
                munition.InMagazine = state.AmmoInMagazine;
                return;
            }

            EnsureMagazineInitialized(weapon);
        }

        private Args GetShooterWeaponArgs(ShooterWeapon weapon)
        {
            if (m_CurrentWeaponData != null) return m_CurrentWeaponData.WeaponArgs;

            GameObject prop = m_Character != null && weapon != null
                ? m_Character.Combat.GetProp(weapon)
                : null;

            return new Args(
                m_Character != null ? m_Character.gameObject : gameObject,
                prop
            );
        }
        
        private void UpdateAimState(bool force = false)
        {
            if (!force && Time.time - m_LastAimStateSync < m_AimStateSyncInterval) return;
            m_LastAimStateSync = Time.time;
            
            if (m_CurrentWeapon == null || m_CurrentWeaponData == null)
            {
                TryAdoptEquippedShooterWeapon();
                TryRefreshCurrentWeaponData();
                if (m_CurrentWeapon == null || m_CurrentWeaponData == null)
                {
                    if (ShouldLogDiagnostic(ref m_NextAimSyncSkipDiagnosticTime))
                    {
                        LogDiagnostics(
                            $"aim state sync skipped currentWeapon={(m_CurrentWeapon != null ? m_CurrentWeapon.name : "null")} " +
                            $"weaponData={(m_CurrentWeaponData != null)} stance={(m_ShooterStance != null)} " +
                            $"listener={(OnAimStateChanged != null)}");
                    }

                    return;
                }
            }
            
            // Get aim point from sight
            var sight = m_CurrentWeapon.Sights.Get(m_CurrentWeaponData.SightId);
            if (sight?.Sight == null)
            {
                if (ShouldLogDiagnostic(ref m_NextAimSyncSkipDiagnosticTime))
                {
                    LogDiagnostics(
                        $"aim state sync skipped missing sight weapon={m_CurrentWeapon.name} " +
                        $"sightHash={m_CurrentWeaponData.SightId.Hash}");
                }

                return;
            }
            
            var muzzle = sight.Sight.GetMuzzle(m_CurrentWeaponData.WeaponArgs, m_CurrentWeapon);
            Vector3 aimPoint = sight.Sight.Aim.GetPoint(m_CurrentWeaponData.WeaponArgs);
            Vector3 aimDirection = aimPoint - muzzle.Position;
            if (aimDirection.sqrMagnitude <= 0.0001f)
            {
                aimDirection = muzzle.Direction;
                aimPoint = muzzle.Position + aimDirection.normalized * 100f;
            }

            NetworkAimState previousState = m_LastAimState;
            NetworkAimState newState = new NetworkAimState
            {
                AimPoint = aimPoint,
                Accuracy = (byte)(m_ShooterStance.CurrentAccuracy * 255f),
                IsAiming = IsAimingState(m_CurrentWeapon, m_CurrentWeaponData),
                CompressedDirection = NetworkAimState.CompressDirection(aimDirection.normalized)
            };

            m_LastAimState = newState;

            if (force ||
                previousState.IsAiming != newState.IsAiming ||
                previousState.CompressedDirection != newState.CompressedDirection ||
                (previousState.AimPoint - newState.AimPoint).sqrMagnitude > 0.04f)
            {
                LogDiagnostics(
                    $"aim state changed aiming={newState.IsAiming} accuracy={newState.Accuracy} " +
                    $"direction={aimDirection.normalized} aimPoint={newState.AimPoint} " +
                    $"compressed={newState.CompressedDirection} listener={(OnAimStateChanged != null)}");

                OnAimStateChanged?.Invoke(this, newState);
            }
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
