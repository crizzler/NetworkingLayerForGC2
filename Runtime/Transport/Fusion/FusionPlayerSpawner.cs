using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Authority-only player spawner. In Shared mode every player object is spawned as
    /// a Master Client Object; <see cref="FusionNetworkIdentity.LogicalOwner"/> retains
    /// the actual player ownership used by GC2 validation.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Player Spawner")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FusionAuthoritySpawnRegistry))]
    public sealed class FusionPlayerSpawner : MonoBehaviour, INetworkRunnerCallbacks
    {
        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private FusionAuthoritySpawnRegistry m_SpawnRegistry;
        [SerializeField] private NetworkObject m_PlayerPrefab;
        [Tooltip("Optional selectable character prefabs. The authority maps a validated selection index to this array.")]
        [SerializeField] private NetworkObject[] m_PlayerPrefabs = Array.Empty<NetworkObject>();
        [SerializeField] private FusionDemoCharacterSelection m_CharacterSelection;
        [SerializeField] private bool m_WaitForCharacterSelection;
        [Min(0f)]
        [SerializeField] private float m_SelectionWaitTimeout = 8f;
        [SerializeField] private Transform[] m_SpawnPoints = Array.Empty<Transform>();
        [SerializeField] private bool m_DespawnPlayerOnLeave = true;

        private readonly Dictionary<PlayerRef, NetworkObject> m_SpawnedPlayers =
            new Dictionary<PlayerRef, NetworkObject>();
        private readonly HashSet<NetworkId> m_AuthorityIssuedObjects = new HashSet<NetworkId>();
        private readonly Dictionary<uint, float> m_PendingSelectionSpawns =
            new Dictionary<uint, float>();
        private readonly List<uint> m_PendingClientScratch = new List<uint>();

        private NetworkRunner m_BoundRunner;
        private bool m_RuntimeWaitForSelection;

        public NetworkObject PlayerPrefab => m_PlayerPrefab;
        public IReadOnlyList<NetworkObject> PlayerPrefabs => m_PlayerPrefabs;
        public int SelectionSlotCount =>
            m_PlayerPrefabs != null && m_PlayerPrefabs.Length > 0
                ? m_PlayerPrefabs.Length
                : m_PlayerPrefab != null ? 1 : 0;

        public event Action<FusionPlayerObjectLifecycleInfo> PlayerObjectObservedSpawned;
        public event Action<FusionPlayerObjectLifecycleInfo> PlayerObjectObservedDespawned;

        private void Awake()
        {
            if (m_TransportBridge == null)
            {
                m_TransportBridge = GetComponentInParent<FusionTransportBridge>();
            }
            EnsureSpawnRegistry();
            AttachConfiguredCharacterSelection();
        }

        private void OnEnable()
        {
            Bind();
        }

        private void Update()
        {
            if (m_TransportBridge == null)
            {
                m_TransportBridge = NetworkTransportBridge.Active as FusionTransportBridge;
            }

            if (m_BoundRunner != m_TransportBridge?.Runner)
            {
                Bind();
            }

            ProcessPendingSelectionSpawns();
        }

        private void OnDisable()
        {
            Unbind();
        }

        public bool IsAuthorityIssued(NetworkId networkId)
        {
            return networkId.IsValid &&
                   m_SpawnRegistry != null &&
                   m_SpawnRegistry.IsAdmitted(networkId);
        }

        public bool TryGetSpawnedPlayer(PlayerRef player, out NetworkObject playerObject)
        {
            if (m_SpawnedPlayers.TryGetValue(player, out playerObject) && playerObject != null)
            {
                if (IsValidPlayerMapping(player, playerObject)) return true;
                if (playerObject.Id.IsValid) m_AuthorityIssuedObjects.Remove(playerObject.Id);
                m_SpawnedPlayers.Remove(player);
            }

            if (m_BoundRunner != null &&
                m_BoundRunner.TryGetPlayerObject(player, out playerObject) &&
                IsValidPlayerMapping(player, playerObject))
            {
                m_SpawnedPlayers[player] = playerObject;
                if (playerObject.Id.IsValid) m_AuthorityIssuedObjects.Add(playerObject.Id);
                return true;
            }

            playerObject = null;
            return false;
        }

        private bool IsValidPlayerMapping(PlayerRef player, NetworkObject playerObject)
        {
            if (!player.IsRealPlayer ||
                playerObject == null ||
                !playerObject.IsValid ||
                playerObject.Runner != m_BoundRunner)
            {
                return false;
            }

            EnsureSpawnRegistry();
            FusionNetworkIdentity identity =
                playerObject.GetComponent<FusionNetworkIdentity>();
            return identity != null &&
                   identity.LogicalOwner == player &&
                   identity.TransportAdmitted &&
                   m_SpawnRegistry != null &&
                   m_SpawnRegistry.IsAdmitted(identity) &&
                   IsConfiguredPlayerPrefabType(playerObject);
        }

        private bool IsConfiguredPlayerPrefabType(NetworkObject playerObject)
        {
            if (playerObject == null || !playerObject.NetworkTypeId.IsValid) return false;

            if (m_PlayerPrefabs != null && m_PlayerPrefabs.Length > 0)
            {
                for (int i = 0; i < m_PlayerPrefabs.Length; i++)
                {
                    NetworkObject prefab = m_PlayerPrefabs[i];
                    if (prefab != null &&
                        prefab.NetworkTypeId.IsValid &&
                        playerObject.NetworkTypeId.Equals(prefab.NetworkTypeId))
                    {
                        return true;
                    }
                }
                return false;
            }

            return m_PlayerPrefab != null &&
                   m_PlayerPrefab.NetworkTypeId.IsValid &&
                   playerObject.NetworkTypeId.Equals(m_PlayerPrefab.NetworkTypeId);
        }

        public NetworkObject SpawnPlayer(PlayerRef player)
        {
            if (!FusionTransportBridge.TryPlayerToClientId(player, out uint clientId) ||
                !TryResolvePlayerPrefab(clientId, true, out NetworkObject playerPrefab))
            {
                return null;
            }

            return SpawnPlayer(player, playerPrefab);
        }

        public bool TryGetSelectablePrefab(int index, out NetworkObject prefab)
        {
            prefab = null;
            if (m_PlayerPrefabs != null && m_PlayerPrefabs.Length > 0)
            {
                if (index < 0 || index >= m_PlayerPrefabs.Length) return false;
                prefab = m_PlayerPrefabs[index];
                return prefab != null;
            }

            if (index != 0 || m_PlayerPrefab == null) return false;
            prefab = m_PlayerPrefab;
            return true;
        }

        public bool IsValidSelectionIndex(int index)
        {
            return TryGetSelectablePrefab(index, out _);
        }

        public void AttachCharacterSelection(
            FusionDemoCharacterSelection selection,
            bool waitForSelection)
        {
            if (m_CharacterSelection != selection)
            {
                UnsubscribeCharacterSelection();
                m_CharacterSelection = selection;
                SubscribeCharacterSelection();
            }

            m_RuntimeWaitForSelection = selection != null && waitForSelection;
        }

        public void DetachCharacterSelection(FusionDemoCharacterSelection selection)
        {
            if (m_CharacterSelection != selection) return;
            UnsubscribeCharacterSelection();
            m_CharacterSelection = null;
            m_RuntimeWaitForSelection = false;
        }

        private NetworkObject SpawnPlayer(PlayerRef player, NetworkObject playerPrefab)
        {
            NetworkRunner runner = m_BoundRunner;
            if (runner == null || !runner.IsRunning ||
                m_TransportBridge == null || !m_TransportBridge.IsServer)
            {
                return null;
            }
            EnsureSpawnRegistry();
            if (m_SpawnRegistry == null) return null;

            if (!player.IsRealPlayer || !runner.IsPlayerValid(player) || playerPrefab == null)
            {
                return null;
            }

            if (TryGetSpawnedPlayer(player, out NetworkObject existing)) return existing;

            ResolveSpawnTransform(player, out Vector3 position, out Quaternion rotation);
            PlayerRef? inputAuthority =
                runner.GameMode == GameMode.Shared ? (PlayerRef?)null : player;
            NetworkObject spawned = m_SpawnRegistry.Spawn(
                playerPrefab,
                position,
                rotation,
                player,
                inputAuthority);

            if (spawned == null) return null;

            FusionNetworkIdentity spawnedIdentity = spawned.GetComponent<FusionNetworkIdentity>();
            if (spawnedIdentity == null)
            {
                Debug.LogError(
                    $"[FusionTransport] Spawned player '{spawned.name}' has no FusionNetworkIdentity.",
                    spawned);
                m_SpawnRegistry.Despawn(spawned.Id);
                return null;
            }

            m_SpawnedPlayers[player] = spawned;
            if (spawned.Id.IsValid) m_AuthorityIssuedObjects.Add(spawned.Id);
            runner.SetPlayerObject(player, spawned);
            PublishPlayerObjectObservation(
                FusionPlayerObjectLifecyclePhase.Spawned,
                player,
                spawned,
                spawned.Id.IsValid ? spawned.Id.Raw : 0);
            return spawned;
        }

        private void Bind()
        {
            Unbind();
            if (m_TransportBridge == null) return;

            m_TransportBridge.ClientSceneReady -= OnClientSceneReady;
            m_TransportBridge.ClientSceneReady += OnClientSceneReady;
            m_TransportBridge.AuthorityChanged -= OnAuthorityChanged;
            m_TransportBridge.AuthorityChanged += OnAuthorityChanged;
            SubscribeCharacterSelection();
            if (m_TransportBridge.Runner == null) return;

            m_BoundRunner = m_TransportBridge.Runner;
            EnsureSpawnRegistry();
            m_SpawnRegistry?.Configure(m_TransportBridge);
            m_BoundRunner.RemoveCallbacks(this);
            m_BoundRunner.AddCallbacks(this);
            RebuildRegistry();

            if (m_TransportBridge.IsServer)
            {
                foreach (uint clientId in m_TransportBridge.ConnectedClientIds)
                {
                    if (m_TransportBridge.IsClientSceneReady(clientId))
                    {
                        OnClientSceneReady(clientId);
                    }
                }
            }
        }

        private void EnsureSpawnRegistry()
        {
            if (m_SpawnRegistry != null) return;
            m_SpawnRegistry = GetComponentInParent<FusionAuthoritySpawnRegistry>();
            if (m_SpawnRegistry == null)
            {
                m_SpawnRegistry = gameObject.AddComponent<FusionAuthoritySpawnRegistry>();
            }
        }

        private void Unbind()
        {
            UnsubscribeCharacterSelection();
            if (m_TransportBridge != null)
            {
                m_TransportBridge.ClientSceneReady -= OnClientSceneReady;
                m_TransportBridge.AuthorityChanged -= OnAuthorityChanged;
            }

            if (m_BoundRunner != null)
            {
                m_BoundRunner.RemoveCallbacks(this);
            }

            m_BoundRunner = null;
            m_SpawnedPlayers.Clear();
            m_AuthorityIssuedObjects.Clear();
            m_PendingSelectionSpawns.Clear();
        }

        private void OnClientSceneReady(uint clientId)
        {
            if (m_TransportBridge == null || !m_TransportBridge.IsServer ||
                !m_TransportBridge.TryGetPlayerRef(clientId, out PlayerRef player))
            {
                return;
            }

            if (TryResolvePlayerPrefab(clientId, !ShouldWaitForSelection, out NetworkObject prefab))
            {
                SpawnPlayer(player, prefab);
                return;
            }

            if (ShouldWaitForSelection)
            {
                m_PendingSelectionSpawns[clientId] =
                    Time.unscaledTime + Mathf.Max(0f, m_SelectionWaitTimeout);
            }
        }

        private void OnAuthorityChanged(bool isAuthority, uint epoch)
        {
            RebuildRegistry();
            m_PendingSelectionSpawns.Clear();
            if (!isAuthority || m_TransportBridge == null) return;

            foreach (uint clientId in m_TransportBridge.ConnectedClientIds)
            {
                if (m_TransportBridge.IsClientSceneReady(clientId))
                {
                    OnClientSceneReady(clientId);
                }
            }
        }

        private void RebuildRegistry()
        {
            m_SpawnedPlayers.Clear();
            m_AuthorityIssuedObjects.Clear();
            if (m_BoundRunner == null || !m_BoundRunner.IsRunning) return;

            foreach (PlayerRef player in m_BoundRunner.ActivePlayers)
            {
                if (!m_BoundRunner.TryGetPlayerObject(player, out NetworkObject playerObject) ||
                    !IsValidPlayerMapping(player, playerObject))
                {
                    continue;
                }

                m_SpawnedPlayers[player] = playerObject;
                if (playerObject.Id.IsValid) m_AuthorityIssuedObjects.Add(playerObject.Id);
            }
        }

        private void ResolveSpawnTransform(
            PlayerRef player,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = transform.position;
            rotation = transform.rotation;
            if (m_SpawnPoints == null || m_SpawnPoints.Length == 0) return;

            int index = Mathf.Abs(player.AsIndex) % m_SpawnPoints.Length;
            Transform spawnPoint = m_SpawnPoints[index];
            if (spawnPoint == null) return;
            position = spawnPoint.position;
            rotation = spawnPoint.rotation;
        }

        private bool ShouldWaitForSelection =>
            m_WaitForCharacterSelection || m_RuntimeWaitForSelection;

        private bool TryResolvePlayerPrefab(
            uint clientId,
            bool allowFallback,
            out NetworkObject prefab)
        {
            prefab = null;
            if (m_CharacterSelection != null &&
                m_CharacterSelection.TryGetAuthoritySelection(clientId, out int selectedIndex) &&
                TryGetSelectablePrefab(selectedIndex, out prefab))
            {
                return true;
            }

            if (!allowFallback) return false;
            if (m_PlayerPrefab != null)
            {
                prefab = m_PlayerPrefab;
                return true;
            }

            if (m_PlayerPrefabs == null) return false;
            for (int i = 0; i < m_PlayerPrefabs.Length; i++)
            {
                if (m_PlayerPrefabs[i] == null) continue;
                prefab = m_PlayerPrefabs[i];
                return true;
            }

            return false;
        }

        private void AttachConfiguredCharacterSelection()
        {
            if (m_CharacterSelection == null) return;
            SubscribeCharacterSelection();
            m_CharacterSelection.AttachPlayerSpawner(this);
        }

        private void SubscribeCharacterSelection()
        {
            if (m_CharacterSelection == null) return;
            m_CharacterSelection.AuthoritySelectionAccepted -= OnAuthoritySelectionAccepted;
            m_CharacterSelection.AuthoritySelectionAccepted += OnAuthoritySelectionAccepted;
        }

        private void UnsubscribeCharacterSelection()
        {
            if (m_CharacterSelection == null) return;
            m_CharacterSelection.AuthoritySelectionAccepted -= OnAuthoritySelectionAccepted;
        }

        private void OnAuthoritySelectionAccepted(uint clientId, int selectionIndex)
        {
            if (m_TransportBridge == null || !m_TransportBridge.IsServer ||
                !m_PendingSelectionSpawns.Remove(clientId) ||
                !m_TransportBridge.IsClientSceneReady(clientId))
            {
                return;
            }

            if (m_TransportBridge.TryGetPlayerRef(clientId, out PlayerRef player) &&
                TryGetSelectablePrefab(selectionIndex, out NetworkObject prefab))
            {
                SpawnPlayer(player, prefab);
            }
        }

        private void ProcessPendingSelectionSpawns()
        {
            if (m_PendingSelectionSpawns.Count == 0 ||
                m_TransportBridge == null ||
                !m_TransportBridge.IsServer)
            {
                return;
            }

            float now = Time.unscaledTime;
            m_PendingClientScratch.Clear();
            foreach (var pair in m_PendingSelectionSpawns)
            {
                if (now >= pair.Value) m_PendingClientScratch.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingClientScratch.Count; i++)
            {
                uint clientId = m_PendingClientScratch[i];
                m_PendingSelectionSpawns.Remove(clientId);
                if (!m_TransportBridge.IsClientSceneReady(clientId) ||
                    !m_TransportBridge.TryGetPlayerRef(clientId, out PlayerRef player) ||
                    !TryResolvePlayerPrefab(clientId, true, out NetworkObject prefab))
                {
                    continue;
                }

                SpawnPlayer(player, prefab);
            }
            m_PendingClientScratch.Clear();
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (runner != m_BoundRunner) return;
            TryGetSpawnedPlayer(player, out NetworkObject playerObject);
            uint previousNetworkId =
                playerObject != null && playerObject.Id.IsValid
                    ? playerObject.Id.Raw
                    : 0;

            m_SpawnedPlayers.Remove(player);
            if (FusionTransportBridge.TryPlayerToClientId(player, out uint clientId))
            {
                m_PendingSelectionSpawns.Remove(clientId);
                m_CharacterSelection?.ForgetAuthoritySelection(clientId);
            }
            if (playerObject != null && playerObject.Id.IsValid)
            {
                m_AuthorityIssuedObjects.Remove(playerObject.Id);
            }

            if (m_DespawnPlayerOnLeave &&
                m_TransportBridge != null &&
                m_TransportBridge.IsServer &&
                playerObject != null &&
                playerObject.IsValid)
            {
                if (m_SpawnRegistry != null && m_SpawnRegistry.Despawn(playerObject.Id))
                {
                    PublishPlayerObjectObservation(
                        FusionPlayerObjectLifecyclePhase.Despawned,
                        player,
                        playerObject,
                        previousNetworkId);
                }
            }
        }

        private void PublishPlayerObjectObservation(
            FusionPlayerObjectLifecyclePhase phase,
            PlayerRef player,
            NetworkObject playerObject,
            uint networkId)
        {
            uint clientId = NetworkTransportBridge.InvalidClientId;
            FusionTransportBridge.TryPlayerToClientId(player, out clientId);
            var info = new FusionPlayerObjectLifecycleInfo(
                phase,
                m_BoundRunner,
                player,
                clientId,
                playerObject,
                networkId);
            Action<FusionPlayerObjectLifecycleInfo> handlers =
                phase == FusionPlayerObjectLifecyclePhase.Spawned
                    ? PlayerObjectObservedSpawned
                    : PlayerObjectObservedDespawned;
            FusionLifecycleEventUtility.InvokeBestEffort(
                handlers,
                info,
                this,
                phase == FusionPlayerObjectLifecyclePhase.Spawned
                    ? nameof(PlayerObjectObservedSpawned)
                    : nameof(PlayerObjectObservedDespawned));
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (runner == m_BoundRunner)
            {
                m_SpawnedPlayers.Clear();
                m_AuthorityIssuedObjects.Clear();
            }
        }

        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(
            NetworkRunner runner,
            NetworkRunnerCallbackArgs.ConnectRequest request,
            byte[] token) { }
        public void OnConnectFailed(
            NetworkRunner runner,
            NetAddress remoteAddress,
            NetConnectFailedReason reason) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(
            NetworkRunner runner,
            Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
    }
}
