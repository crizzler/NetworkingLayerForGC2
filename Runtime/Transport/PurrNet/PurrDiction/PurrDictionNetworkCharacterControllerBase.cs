using GameCreator.Runtime.Characters;
using PurrNet;
using PurrNet.Prediction;
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction
{
    public abstract class PurrDictionNetworkCharacterControllerBase<TInput, TState> :
        PredictedIdentity<TInput, TState>,
        INetworkCharacterPredictionBackend
        where TInput : struct, IPredictedData
        where TState : struct, IPredictedData<TState>
    {
        private const uint PURRNET_PREDICTED_ID_NAMESPACE = 0x80000000u;
        private const float DEFAULT_MAX_SPEED_MULTIPLIER = 1.2f;
        private const string SECURITY_MODULE_CORE = "Core";

        [Header("Server Authority")]
        [SerializeField] private bool m_EnableServerSecurityValidation = true;

        private PredictionManager m_RegisteredPredictionManager;
        private bool m_PredictionRegistered;
        private bool m_QueuedSpawnRegistration;
        private bool m_MissingPredictionManagerWarned;
        private bool m_RuntimeIsServer;
        private bool m_MissingSecurityContextViolationRecorded;
        private float m_MaxSpeedMultiplier = DEFAULT_MAX_SPEED_MULTIPLIER;
        private uint m_SecurityActorNetworkId;
        private uint m_SecurityOwnerClientId = NetworkTransportBridge.InvalidClientId;

        protected NetworkCharacter NetworkCharacterComponent { get; private set; }
        protected Character GameCreatorCharacter { get; private set; }
        protected NetworkIdentity PurrNetIdentity { get; private set; }

        protected bool ServerSecurityValidationEnabled => m_EnableServerSecurityValidation;
        protected bool IsAuthoritativeServer => m_RuntimeIsServer || isServer;
        protected bool ShouldValidateServerSecurity =>
            m_EnableServerSecurityValidation && IsAuthoritativeServer;
        protected float MaxSpeedMultiplier => Mathf.Max(1f, m_MaxSpeedMultiplier);
        protected uint SecurityActorNetworkId => m_SecurityActorNetworkId;
        protected uint SecurityOwnerClientId => m_SecurityOwnerClientId;

        public NetworkPredictionBackend Backend => NetworkPredictionBackend.PurrDiction;

        public abstract IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role);

        public void Initialize(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        {
            EnsureBaseReferences(networkCharacter);
            m_RuntimeIsServer = isServer;
            if (ShouldValidateServerSecurity)
            {
                SecurityIntegration.EnsureSecurityManagerInitialized(true);
            }

            RefreshSecurityContext();
            OnBackendInitialized(networkCharacter, role, isServer, isOwner, isHost);
            TryRegisterWithPredictionManager();
            RefreshSecurityContext();
        }

        public virtual void ApplySessionProfile(NetworkSessionProfile profile)
        {
            if (profile != null)
            {
                m_MaxSpeedMultiplier = Mathf.Max(1f, profile.maxSpeedMultiplier);
            }

            // PurrDiction follows PurrNet's tick module. GC2 session profiles still drive
            // the non-movement bridge layer through NetworkCharacter and transport bridges.
        }

        public void ResetBackend(NetworkCharacter networkCharacter)
        {
            UnregisterFromPredictionManager();
            OnBackendReset(networkCharacter);

            NetworkCharacterComponent = null;
            GameCreatorCharacter = null;
            PurrNetIdentity = null;
            m_QueuedSpawnRegistration = false;
            m_MissingPredictionManagerWarned = false;
            m_RuntimeIsServer = false;
            m_MissingSecurityContextViolationRecorded = false;
            m_SecurityActorNetworkId = 0;
            m_SecurityOwnerClientId = NetworkTransportBridge.InvalidClientId;
        }

        protected virtual void Awake()
        {
            EnsureBaseReferences(GetComponent<NetworkCharacter>());
        }

        protected override void OnDestroy()
        {
            m_PredictionRegistered = false;
            m_RegisteredPredictionManager = null;
            base.OnDestroy();
        }

        public override void OnViewOwnerChanged(PlayerID? oldOwner, PlayerID? newOwner)
        {
            base.OnViewOwnerChanged(oldOwner, newOwner);
            RefreshSecurityContext();
        }

        protected virtual void OnBackendInitialized(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        { }

        protected virtual void OnBackendReset(NetworkCharacter networkCharacter)
        { }

        protected void EnsureBaseReferences(NetworkCharacter networkCharacter = null)
        {
            if (networkCharacter != null) NetworkCharacterComponent = networkCharacter;
            if (NetworkCharacterComponent == null) NetworkCharacterComponent = GetComponent<NetworkCharacter>();
            if (GameCreatorCharacter == null) GameCreatorCharacter = GetComponent<Character>();
            if (PurrNetIdentity == null) PurrNetIdentity = GetComponentInParent<NetworkIdentity>();
        }

        protected void RefreshSecurityContext()
        {
            EnsureBaseReferences();

            m_SecurityActorNetworkId = NetworkCharacterComponent != null
                ? NetworkCharacterComponent.NetworkId
                : 0;
            m_SecurityOwnerClientId = ResolveOwnerClientId();

            if (m_SecurityActorNetworkId == 0 ||
                !NetworkTransportBridge.IsValidClientId(m_SecurityOwnerClientId))
            {
                return;
            }

            m_MissingSecurityContextViolationRecorded = false;
            SecurityIntegration.RegisterActorOwnership(m_SecurityActorNetworkId, m_SecurityOwnerClientId);
        }

        protected bool TryResolveSecurityContext(out uint ownerClientId, out uint actorNetworkId)
        {
            RefreshSecurityContext();

            ownerClientId = m_SecurityOwnerClientId;
            actorNetworkId = m_SecurityActorNetworkId;
            return actorNetworkId != 0 && NetworkTransportBridge.IsValidClientId(ownerClientId);
        }

        protected bool ValidateServerCoreRequest(ushort sequence, string requestType)
        {
            if (!ShouldValidateServerSecurity) return true;

            if (sequence == 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"{requestType}: missing command sequence");
                return false;
            }

            if (!TryResolveSecurityContext(out uint ownerClientId, out uint actorNetworkId))
            {
                RecordMissingSecurityContextOnce(requestType);
                return false;
            }

            uint correlationId = NetworkCorrelation.Compose(actorNetworkId, (uint)sequence);
            return SecurityIntegration.ValidateCoreRequest(
                ownerClientId,
                actorNetworkId,
                correlationId,
                requestType);
        }

        protected bool ValidateServerCorePosition(
            Vector3 position,
            Vector3 velocity,
            float maxSpeed,
            string requestType)
        {
            if (!ShouldValidateServerSecurity) return true;

            if (!IsFinite(position) || !IsFinite(velocity) || !IsFinite(maxSpeed) || maxSpeed <= 0f)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"{requestType}: invalid movement state position={position}, velocity={velocity}, maxSpeed={maxSpeed}");
                return false;
            }

            if (!TryResolveSecurityContext(out uint ownerClientId, out uint actorNetworkId))
            {
                RecordMissingSecurityContextOnce(requestType);
                return false;
            }

            return SecurityIntegration.ValidateCorePositionUpdate(
                ownerClientId,
                actorNetworkId,
                position,
                velocity,
                maxSpeed);
        }

        protected void RecordCoreSecurityViolation(SecurityViolationType type, string details)
        {
            if (!ShouldValidateServerSecurity) return;

            RefreshSecurityContext();
            uint ownerClientId = NetworkTransportBridge.IsValidClientId(m_SecurityOwnerClientId)
                ? m_SecurityOwnerClientId
                : NetworkTransportBridge.InvalidClientId;

            SecurityIntegration.RecordViolation(
                ownerClientId,
                m_SecurityActorNetworkId,
                type,
                SECURITY_MODULE_CORE,
                details);
        }

        protected static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        protected static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) &&
                   IsFinite(value.y) &&
                   IsFinite(value.z) &&
                   IsFinite(value.w);
        }

        protected static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        protected float ResolveMaxAllowedHorizontalSpeed()
        {
            float speed = GameCreatorCharacter?.Motion != null
                ? GameCreatorCharacter.Motion.LinearSpeed
                : 0f;

            return Mathf.Max(1f, speed * MaxSpeedMultiplier);
        }

        private uint ResolveOwnerClientId()
        {
            if (owner.HasValue)
            {
                return PlayerIdToClientId(owner.Value);
            }

            if (PurrNetIdentity != null && PurrNetIdentity.owner.HasValue)
            {
                return PlayerIdToClientId(PurrNetIdentity.owner.Value);
            }

            if (m_SecurityActorNetworkId != 0 &&
                NetworkTransportBridge.Active != null &&
                NetworkTransportBridge.Active.TryGetCharacterOwner(
                    m_SecurityActorNetworkId,
                    out uint ownerClientId))
            {
                return ownerClientId;
            }

            return NetworkTransportBridge.InvalidClientId;
        }

        private static uint PlayerIdToClientId(PlayerID playerId)
        {
            ulong raw = playerId.id;
            if (raw > uint.MaxValue) return NetworkTransportBridge.InvalidClientId;
            return (uint)raw;
        }

        private void RecordMissingSecurityContextOnce(string requestType)
        {
            if (m_MissingSecurityContextViolationRecorded) return;
            m_MissingSecurityContextViolationRecorded = true;

            uint ownerClientId = NetworkTransportBridge.IsValidClientId(m_SecurityOwnerClientId)
                ? m_SecurityOwnerClientId
                : NetworkTransportBridge.InvalidClientId;

            SecurityIntegration.RecordViolation(
                ownerClientId,
                m_SecurityActorNetworkId,
                SecurityViolationType.InvalidTarget,
                SECURITY_MODULE_CORE,
                $"{requestType}: unresolved PurrDiction actor ownership context " +
                $"actor={m_SecurityActorNetworkId}, owner={m_SecurityOwnerClientId}");
        }

        private void TryRegisterWithPredictionManager()
        {
            EnsureBaseReferences();

            if (predictionManager != null)
            {
                m_RegisteredPredictionManager = predictionManager;
                m_PredictionRegistered = true;
                return;
            }

            if (m_PredictionRegistered) return;

            if (PurrNetIdentity != null && !PurrNetIdentity.isSpawned)
            {
                if (!m_QueuedSpawnRegistration)
                {
                    m_QueuedSpawnRegistration = true;
                    PurrNetIdentity.QueueOnSpawned(TryRegisterWithPredictionManager);
                }

                return;
            }

            if (!PredictionManager.TryGetInstance(gameObject.scene.handle, out PredictionManager manager) ||
                manager == null)
            {
                WarnMissingPredictionManagerOnce();
                return;
            }

            if (!TryResolvePredictedObjectId(out PredictedObjectID objectId))
            {
                Debug.LogWarning(
                    $"[GC2 PurrDiction] Could not resolve a PurrNet NetworkIdentity object id for '{name}'. " +
                    "PurrDiction movement prediction will not run for this character.",
                    this);
                return;
            }

            PlayerID? owner = PurrNetIdentity != null ? PurrNetIdentity.owner : null;
            manager.RegisterInstance(gameObject, objectId, owner, reset: false, triggedOnRemovedFromPool: false);

            m_RegisteredPredictionManager = manager;
            m_PredictionRegistered = true;
            RefreshSecurityContext();
        }

        private bool TryResolvePredictedObjectId(out PredictedObjectID objectId)
        {
            objectId = default;
            if (PurrNetIdentity == null) return false;

            ulong purrNetObjectId = PurrNetIdentity.objectId;
            if (purrNetObjectId == 0 || purrNetObjectId > int.MaxValue) return false;

            objectId = new PredictedObjectID(PURRNET_PREDICTED_ID_NAMESPACE | (uint)purrNetObjectId);
            return true;
        }

        private void WarnMissingPredictionManagerOnce()
        {
            if (m_MissingPredictionManagerWarned) return;
            m_MissingPredictionManagerWarned = true;

            Debug.LogWarning(
                $"[GC2 PurrDiction] No PurrDiction PredictionManager was found in scene '{gameObject.scene.name}'. " +
                "Run the PurrNet Scene Setup Wizard with Prediction Backend set to PurrDiction.",
                this);
        }

        private void UnregisterFromPredictionManager()
        {
            if (!m_PredictionRegistered && predictionManager == null) return;

            PredictionManager manager = predictionManager != null
                ? predictionManager
                : m_RegisteredPredictionManager;

            if (manager != null)
            {
                manager.UnregisterInstance(this);
            }

            predictionManager = null;
            m_RegisteredPredictionManager = null;
            m_PredictionRegistered = false;
        }
    }
}
