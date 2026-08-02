using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Admission boundary for every transport-managed <see cref="FusionNetworkIdentity"/>.
    /// Dynamic objects must be spawned through this registry. Scene objects and objects
    /// replicated from the current Shared master are admitted when their master ownership
    /// is verified. Client-owned Shared objects are quarantined from GC2 authorization.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Authority Spawn Registry")]
    [DisallowMultipleComponent]
    public sealed class FusionAuthoritySpawnRegistry : MonoBehaviour
    {
        private static readonly Dictionary<NetworkRunner, FusionAuthoritySpawnRegistry> s_Registries =
            new Dictionary<NetworkRunner, FusionAuthoritySpawnRegistry>();

        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private bool m_DespawnUnadmittedWhenPossible = true;
        [Min(0.1f)]
        [SerializeField] private float m_ValidationInterval = 1f;

        private readonly Dictionary<NetworkId, FusionNetworkIdentity> m_Admitted =
            new Dictionary<NetworkId, FusionNetworkIdentity>();
        private readonly HashSet<int> m_PendingSpawnInstanceIds = new HashSet<int>();
        private readonly List<NetworkId> m_StaleIds = new List<NetworkId>();

        private NetworkRunner m_BoundRunner;
        private float m_NextValidationAt;
        private bool m_RebuildingAuthority;

        public int AdmittedCount => m_Admitted.Count;

        private void Awake()
        {
            if (m_TransportBridge == null)
            {
                m_TransportBridge = GetComponentInParent<FusionTransportBridge>();
            }
        }

        private void OnEnable()
        {
            TryBind();
        }

        private void Update()
        {
            if (m_TransportBridge == null)
            {
                m_TransportBridge = NetworkTransportBridge.Active as FusionTransportBridge;
            }

            if (m_BoundRunner != m_TransportBridge?.Runner)
            {
                TryBind();
            }

            if (m_BoundRunner == null || !m_BoundRunner.IsRunning ||
                Time.unscaledTime < m_NextValidationAt)
            {
                return;
            }

            m_NextValidationAt = Time.unscaledTime + Mathf.Max(0.1f, m_ValidationInterval);
            ValidateAllIdentities();
        }

        private void OnDisable()
        {
            Unbind();
        }

        public static bool TryGet(
            NetworkRunner runner,
            out FusionAuthoritySpawnRegistry registry)
        {
            registry = null;
            return runner != null &&
                   s_Registries.TryGetValue(runner, out registry) &&
                   registry != null;
        }

        public void Configure(FusionTransportBridge transportBridge)
        {
            if (m_TransportBridge == transportBridge &&
                m_BoundRunner == transportBridge?.Runner)
            {
                return;
            }

            Unbind();
            m_TransportBridge = transportBridge;
            TryBind();
        }

        public bool IsAdmitted(NetworkId networkId)
        {
            return networkId.IsValid &&
                   m_Admitted.TryGetValue(networkId, out FusionNetworkIdentity identity) &&
                   identity != null;
        }

        public bool IsAdmitted(FusionNetworkIdentity identity)
        {
            return identity != null &&
                   identity.NetworkObject != null &&
                   identity.NetworkObject.Id.IsValid &&
                   IsAdmitted(identity.NetworkObject.Id);
        }

        public NetworkObject Spawn(
            NetworkObject prefab,
            Vector3 position,
            Quaternion rotation,
            PlayerRef logicalOwner = default,
            PlayerRef? inputAuthority = null,
            NetworkSpawnFlags additionalFlags = default)
        {
            const NetworkSpawnFlags allowedCallerFlags =
                NetworkSpawnFlags.DontDestroyOnLoad;
            NetworkSpawnFlags rejectedFlags = additionalFlags & ~allowedCallerFlags;
            if (rejectedFlags != default)
            {
                Debug.LogError(
                    $"[FusionTransport] Rejected spawn flags '{rejectedFlags}'. " +
                    "Callers may only request DontDestroyOnLoad; the authority registry " +
                    "selects all State Authority flags.",
                    this);
                return null;
            }

            NetworkRunner runner = m_BoundRunner;
            if (runner == null || !runner.IsRunning || m_TransportBridge == null ||
                !m_TransportBridge.IsServer || prefab == null)
            {
                return null;
            }

            if (prefab.GetComponent<FusionNetworkIdentity>() == null)
            {
                Debug.LogError(
                    $"[FusionTransport] Transport-managed prefab '{prefab.name}' requires " +
                    "FusionNetworkIdentity.",
                    prefab);
                return null;
            }

            NetworkSpawnFlags flags = additionalFlags;
            if (runner.GameMode == GameMode.Shared)
            {
                flags |= NetworkSpawnFlags.SharedModeStateAuthMasterClient;
                inputAuthority = null;
            }

            NetworkObject spawned = runner.Spawn(
                prefab,
                position,
                rotation,
                inputAuthority,
                (spawnRunner, networkObject) =>
                {
                    m_PendingSpawnInstanceIds.Add(networkObject.GetInstanceID());
                    FusionNetworkIdentity identity =
                        networkObject.GetComponent<FusionNetworkIdentity>();
                    if (identity != null)
                    {
                        identity.TryIssueAuthorityAdmission(true);
                        if (logicalOwner.IsRealPlayer)
                        {
                            identity.AssignLogicalOwnerBeforeSpawn(logicalOwner);
                        }
                    }
                },
                flags);

            if (spawned == null) return null;

            m_PendingSpawnInstanceIds.Remove(spawned.GetInstanceID());
            FusionNetworkIdentity spawnedIdentity =
                spawned.GetComponent<FusionNetworkIdentity>();
            if (spawnedIdentity == null || !Admit(spawnedIdentity))
            {
                if (spawned.IsValid && spawned.HasStateAuthority)
                {
                    runner.Despawn(spawned);
                }
                return null;
            }

            return spawned;
        }

        public bool Admit(FusionNetworkIdentity identity)
        {
            if (identity == null || identity.NetworkObject == null ||
                !identity.NetworkObject.IsValid ||
                identity.Runner != m_BoundRunner ||
                !identity.HasAuthorityAdmission ||
                !HasSafeAuthorityFlags(identity.NetworkObject) ||
                !IsMasterAuthoritative(identity.NetworkObject))
            {
                identity?.SetTransportAdmission(false);
                return false;
            }

            NetworkId id = identity.NetworkObject.Id;
            m_Admitted[id] = identity;
            identity.SetTransportAdmission(true);
            return true;
        }

        public bool Despawn(NetworkId networkId)
        {
            if (m_BoundRunner == null ||
                m_TransportBridge == null ||
                !m_TransportBridge.IsServer ||
                !m_Admitted.TryGetValue(networkId, out FusionNetworkIdentity identity))
            {
                return false;
            }

            m_Admitted.Remove(networkId);
            if (identity == null || identity.NetworkObject == null)
            {
                return true;
            }

            identity.SetTransportAdmission(false);
            NetworkObject networkObject = identity.NetworkObject;
            if (!networkObject.IsValid || !networkObject.HasStateAuthority) return false;
            identity.TryIssueAuthorityAdmission(false);
            m_BoundRunner.Despawn(networkObject);
            return true;
        }

        internal void ObserveSpawned(FusionNetworkIdentity identity)
        {
            if (identity == null || identity.NetworkObject == null ||
                identity.Runner != m_BoundRunner)
            {
                return;
            }

            NetworkObject networkObject = identity.NetworkObject;
            bool pending = m_PendingSpawnInstanceIds.Contains(networkObject.GetInstanceID());
            bool sceneObject = networkObject.NetworkTypeId.IsSceneObject;
            if (m_TransportBridge.IsServer &&
                (pending || sceneObject) &&
                IsMasterAuthoritative(networkObject))
            {
                identity.TryIssueAuthorityAdmission(true);
            }

            bool admittedSource =
                !m_TransportBridge.IsServer ||
                pending ||
                sceneObject ||
                m_RebuildingAuthority;
            if (admittedSource &&
                identity.HasAuthorityAdmission &&
                IsMasterAuthoritative(networkObject))
            {
                Admit(identity);
                return;
            }

            Reject(identity, "object was not spawned by the logical authority");
        }

        internal void ObserveDespawned(FusionNetworkIdentity identity, NetworkId previousId)
        {
            if (previousId.IsValid) m_Admitted.Remove(previousId);
            identity?.SetTransportAdmission(false);
        }

        private void TryBind()
        {
            Unbind();
            if (m_TransportBridge == null || m_TransportBridge.Runner == null) return;

            m_BoundRunner = m_TransportBridge.Runner;
            if (s_Registries.TryGetValue(m_BoundRunner, out var existing) &&
                existing != null &&
                existing != this)
            {
                Debug.LogError(
                    $"[FusionTransport] Runner '{m_BoundRunner.name}' has multiple authority spawn registries.",
                    this);
                m_BoundRunner = null;
                return;
            }

            s_Registries[m_BoundRunner] = this;
            m_TransportBridge.AuthorityChanged -= OnAuthorityChanged;
            m_TransportBridge.AuthorityChanged += OnAuthorityChanged;
            RebuildAfterAuthorityChange();
        }

        private void Unbind()
        {
            if (m_TransportBridge != null)
            {
                m_TransportBridge.AuthorityChanged -= OnAuthorityChanged;
            }

            if (m_BoundRunner != null &&
                s_Registries.TryGetValue(m_BoundRunner, out var registry) &&
                registry == this)
            {
                s_Registries.Remove(m_BoundRunner);
            }

            foreach (FusionNetworkIdentity identity in m_Admitted.Values)
            {
                identity?.SetTransportAdmission(false);
            }

            m_BoundRunner = null;
            m_Admitted.Clear();
            m_PendingSpawnInstanceIds.Clear();
        }

        private void OnAuthorityChanged(bool isAuthority, uint epoch)
        {
            RebuildAfterAuthorityChange();
        }

        private void RebuildAfterAuthorityChange()
        {
            foreach (FusionNetworkIdentity identity in m_Admitted.Values)
            {
                identity?.SetTransportAdmission(false);
            }
            m_Admitted.Clear();
            m_RebuildingAuthority = true;
            try
            {
                ValidateAllIdentities();
            }
            finally
            {
                m_RebuildingAuthority = false;
            }
        }

        private void ValidateAllIdentities()
        {
            if (m_BoundRunner == null || !m_BoundRunner.IsRunning) return;

            m_StaleIds.Clear();
            foreach (var pair in m_Admitted)
            {
                if (pair.Value == null ||
                    pair.Value.NetworkObject == null ||
                    !pair.Value.NetworkObject.IsValid)
                {
                    m_StaleIds.Add(pair.Key);
                }
            }
            for (int i = 0; i < m_StaleIds.Count; i++)
            {
                m_Admitted.Remove(m_StaleIds[i]);
            }

            FusionNetworkIdentity[] identities = FindObjectsByType<FusionNetworkIdentity>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < identities.Length; i++)
            {
                FusionNetworkIdentity identity = identities[i];
                if (identity == null || identity.Runner != m_BoundRunner ||
                    identity.NetworkObject == null || !identity.NetworkObject.IsValid)
                {
                    continue;
                }

                bool previouslyAdmitted = IsAdmitted(identity);
                bool sceneObject = identity.NetworkObject.NetworkTypeId.IsSceneObject;
                if (m_TransportBridge.IsServer &&
                    sceneObject &&
                    IsMasterAuthoritative(identity.NetworkObject))
                {
                    identity.TryIssueAuthorityAdmission(true);
                }

                bool admittedSource =
                    !m_TransportBridge.IsServer ||
                    previouslyAdmitted ||
                    sceneObject ||
                    (m_RebuildingAuthority && identity.HasAuthorityAdmission);
                if (admittedSource &&
                    identity.HasAuthorityAdmission &&
                    IsMasterAuthoritative(identity.NetworkObject))
                {
                    Admit(identity);
                }
                else
                {
                    Reject(identity, "Shared State Authority is not assigned to the Master Client");
                }
            }
        }

        private bool IsMasterAuthoritative(NetworkObject networkObject)
        {
            if (networkObject == null || !networkObject.IsValid || m_BoundRunner == null)
            {
                return false;
            }

            if (m_BoundRunner.GameMode != GameMode.Shared)
            {
                return networkObject.HasStateAuthority || !m_TransportBridge.IsServer;
            }

            if (!HasSafeAuthorityFlags(networkObject)) return false;

            PlayerRef currentMaster = m_BoundRunner.GetMasterClient();
            return networkObject.StateAuthority.IsInternalMasterClientIdentifier ||
                   (currentMaster.IsRealPlayer &&
                    networkObject.StateAuthority == currentMaster) ||
                   (m_BoundRunner.IsSharedModeMasterClient &&
                    networkObject.HasStateAuthority);
        }

        private bool HasSafeAuthorityFlags(NetworkObject networkObject)
        {
            if (networkObject == null || m_BoundRunner == null ||
                m_BoundRunner.GameMode != GameMode.Shared)
            {
                return true;
            }

            NetworkObjectFlags flags = networkObject.Flags;
            return (flags & NetworkObjectFlags.MasterClientObject) != 0 &&
                   (flags & NetworkObjectFlags.AllowStateAuthorityOverride) == 0 &&
                   (flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) == 0;
        }

        private void Reject(FusionNetworkIdentity identity, string reason)
        {
            if (identity == null) return;
            NetworkObject networkObject = identity.NetworkObject;
            if (networkObject != null && networkObject.Id.IsValid)
            {
                m_Admitted.Remove(networkObject.Id);
            }
            identity.SetTransportAdmission(false);

            if (m_TransportBridge == null || !m_TransportBridge.IsServer) return;
            identity.TryIssueAuthorityAdmission(false);
            Debug.LogWarning(
                $"[FusionTransport] Rejected unadmitted object '{identity.name}': {reason}.",
                identity);

            if (m_DespawnUnadmittedWhenPossible &&
                networkObject != null &&
                networkObject.IsValid &&
                networkObject.HasStateAuthority)
            {
                m_BoundRunner.Despawn(networkObject);
            }
        }
    }
}
