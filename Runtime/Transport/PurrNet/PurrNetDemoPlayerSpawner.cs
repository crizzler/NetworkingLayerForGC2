using System.Collections;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Modules;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Demo-only player spawner for the GC2/PurrNet sample scene.
    /// It mirrors PurrNet's PlayerSpawner for remote players and also ensures
    /// a StartHost peer gets a locally owned GC2 character.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Demo Player Spawner")]
    public sealed class PurrNetDemoPlayerSpawner : MonoBehaviour
    {
        [SerializeField] private GameObject m_PlayerPrefab = null;
        [SerializeField] private List<GameObject> m_PlayerPrefabs = new();
        [SerializeField] private PurrNetDemoCharacterSelection m_CharacterSelection;
        [SerializeField] private bool m_WaitForCharacterSelection = false;
        [SerializeField, Min(0f)] private float m_SelectionWaitTimeout = 5f;
        [SerializeField] private bool m_IgnoreNetworkRules = false;
        [SerializeField] private List<Transform> m_SpawnPoints = new();

        private NetworkManager m_Manager;
        private ScenePlayersModule m_ScenePlayers;
        private Coroutine m_HostSpawnRoutine;
        private readonly HashSet<PlayerID> m_PendingSelectionSpawns = new();
        private int m_CurrentSpawnPoint;
        private bool m_SubscribedServer;

        private void OnEnable()
        {
            TryHookNetworkManager();
            ScheduleHostSpawnCheck();
        }

        private void Start()
        {
            TryHookNetworkManager();
            ScheduleHostSpawnCheck();
        }

        private void OnDisable()
        {
            StopHostSpawnCheck();
            UnsubscribeServer();
            m_PendingSelectionSpawns.Clear();

            if (m_Manager == null) return;
            m_Manager.onNetworkStarted -= OnNetworkStarted;
            m_Manager.onNetworkShutdown -= OnNetworkShutdown;
            m_Manager = null;
        }

        private void TryHookNetworkManager()
        {
            var manager = NetworkManager.main;
            if (manager == null || m_Manager == manager) return;

            if (m_Manager != null)
            {
                m_Manager.onNetworkStarted -= OnNetworkStarted;
                m_Manager.onNetworkShutdown -= OnNetworkShutdown;
                UnsubscribeServer();
            }

            m_Manager = manager;
            m_Manager.onNetworkStarted += OnNetworkStarted;
            m_Manager.onNetworkShutdown += OnNetworkShutdown;

            if (m_Manager.isServer)
            {
                TrySubscribeServer(m_Manager);
            }
        }

        private void OnNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer)
            {
                TrySubscribeServer(manager);
            }

            if (manager.isHost || !asServer)
            {
                ScheduleHostSpawnCheck();
            }
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            StopHostSpawnCheck();
            UnsubscribeServer();
        }

        private bool TrySubscribeServer(NetworkManager manager)
        {
            if (m_SubscribedServer) return true;
            if (manager == null || !manager.isServer) return false;
            if (!manager.TryGetModule(out m_ScenePlayers, true)) return false;

            m_ScenePlayers.onPlayerLoadedScene += OnPlayerLoadedScene;
            m_SubscribedServer = true;
            SpawnExistingLoadedPlayers(manager);
            return true;
        }

        private void UnsubscribeServer()
        {
            if (!m_SubscribedServer || m_ScenePlayers == null) return;
            m_ScenePlayers.onPlayerLoadedScene -= OnPlayerLoadedScene;
            m_ScenePlayers = null;
            m_SubscribedServer = false;
        }

        private void SpawnExistingLoadedPlayers(NetworkManager manager)
        {
            if (!TryGetSpawnerSceneId(manager, out SceneID sceneId)) return;
            if (m_ScenePlayers == null || !m_ScenePlayers.TryGetPlayersInScene(sceneId, out var players)) return;

            for (int i = 0; i < players.Count; i++)
            {
                SpawnForPlayer(manager, players[i], sceneId, "loaded-player");
            }
        }

        private void OnPlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            var manager = m_Manager != null ? m_Manager : NetworkManager.main;
            if (!TryGetSpawnerSceneId(manager, out SceneID spawnerScene) || spawnerScene != scene) return;

            SpawnForPlayer(manager, player, scene, "scene-loaded");
        }

        private void ScheduleHostSpawnCheck()
        {
            if (!isActiveAndEnabled || m_HostSpawnRoutine != null) return;
            m_HostSpawnRoutine = StartCoroutine(HostSpawnRoutine());
        }

        private void StopHostSpawnCheck()
        {
            if (m_HostSpawnRoutine == null) return;
            StopCoroutine(m_HostSpawnRoutine);
            m_HostSpawnRoutine = null;
        }

        private IEnumerator HostSpawnRoutine()
        {
            yield return null;

            float deadline = Time.unscaledTime + 8f;
            while (Time.unscaledTime < deadline)
            {
                TryHookNetworkManager();
                var manager = m_Manager != null ? m_Manager : NetworkManager.main;
                if (manager != null &&
                    manager.isHost &&
                    manager.isLocalPlayerReady &&
                    TrySubscribeServer(manager) &&
                    TryGetSpawnerSceneId(manager, out SceneID sceneId))
                {
                    SpawnForPlayer(manager, manager.localPlayer, sceneId, "host-local");
                    break;
                }

                yield return null;
            }

            m_HostSpawnRoutine = null;
        }

        private bool TryGetSpawnerSceneId(NetworkManager manager, out SceneID sceneId)
        {
            sceneId = default;
            return manager != null &&
                   manager.TryGetModule(out ScenesModule scenes, true) &&
                   scenes.TryGetSceneID(gameObject.scene, out sceneId);
        }

        private void SpawnForPlayer(NetworkManager manager, PlayerID player, SceneID scene, string reason)
        {
            if (manager == null || !manager.isServer) return;

            if (!TryResolvePlayerPrefab(player, !m_WaitForCharacterSelection, out GameObject playerPrefab))
            {
                ScheduleSpawnAfterSelection(manager, player, scene, reason);
                return;
            }

            SpawnPrefabForPlayer(manager, player, scene, playerPrefab, reason);
        }

        private void SpawnPrefabForPlayer(
            NetworkManager manager,
            PlayerID player,
            SceneID scene,
            GameObject playerPrefab,
            string reason)
        {
            if (manager == null || !manager.isServer || playerPrefab == null) return;
            if (!m_IgnoreNetworkRules && PlayerAlreadyHasNetworkCharacter(manager, player, scene)) return;

            Transform point = NextSpawnPoint();
            Vector3 position = point != null ? point.position : playerPrefab.transform.position;
            Quaternion rotation = point != null ? point.rotation : playerPrefab.transform.rotation;

            GameObject instance = UnityProxy.Instantiate(playerPrefab, position, rotation, gameObject.scene);
            if (instance == null) return;

            if (instance.TryGetComponent(out PurrNetNetworkCharacterAuto autoInit))
            {
                autoInit.SetSpawnedOwnerHint(player);
            }

            if (instance.TryGetComponent(out NetworkIdentity identity))
            {
                identity.GiveOwnership(player);
            }
            else
            {
                Debug.LogError($"[PurrNetDemoPlayerSpawner] Spawned prefab '{instance.name}' has no NetworkIdentity.", instance);
                return;
            }

        }

        private void ScheduleSpawnAfterSelection(
            NetworkManager manager,
            PlayerID player,
            SceneID scene,
            string reason)
        {
            if (!m_WaitForCharacterSelection) return;
            if (m_PendingSelectionSpawns.Contains(player)) return;

            m_PendingSelectionSpawns.Add(player);
            StartCoroutine(SpawnAfterSelectionRoutine(manager, player, scene, reason));
        }

        private IEnumerator SpawnAfterSelectionRoutine(
            NetworkManager manager,
            PlayerID player,
            SceneID scene,
            string reason)
        {
            float deadline = Time.unscaledTime + Mathf.Max(0f, m_SelectionWaitTimeout);

            while (Time.unscaledTime <= deadline)
            {
                if (!isActiveAndEnabled) break;
                if (manager == null || !manager.isServer) break;
                if (!m_IgnoreNetworkRules && PlayerAlreadyHasNetworkCharacter(manager, player, scene)) break;

                if (TryResolvePlayerPrefab(player, false, out GameObject selectedPrefab))
                {
                    m_PendingSelectionSpawns.Remove(player);
                    SpawnPrefabForPlayer(manager, player, scene, selectedPrefab, $"{reason}-selection");
                    yield break;
                }

                yield return null;
            }

            m_PendingSelectionSpawns.Remove(player);

            if (!isActiveAndEnabled) yield break;

            if (manager != null &&
                manager.isServer &&
                TryResolvePlayerPrefab(player, true, out GameObject fallbackPrefab))
            {
                SpawnPrefabForPlayer(manager, player, scene, fallbackPrefab, $"{reason}-fallback");
            }
        }

        private bool TryResolvePlayerPrefab(PlayerID player, bool allowFallback, out GameObject prefab)
        {
            prefab = null;

            if (m_CharacterSelection != null &&
                m_CharacterSelection.TryGetPlayerPrefab(player, out prefab) &&
                prefab != null)
            {
                return true;
            }

            if (!allowFallback) return false;

            if (m_CharacterSelection != null)
            {
                prefab = m_CharacterSelection.GetDefaultPrefab();
                if (prefab != null) return true;
            }

            if (m_PlayerPrefabs != null)
            {
                for (int i = 0; i < m_PlayerPrefabs.Count; i++)
                {
                    if (m_PlayerPrefabs[i] == null) continue;
                    prefab = m_PlayerPrefabs[i];
                    return true;
                }
            }

            prefab = m_PlayerPrefab;
            return prefab != null;
        }

        private Transform NextSpawnPoint()
        {
            for (int i = m_SpawnPoints.Count - 1; i >= 0; i--)
            {
                if (m_SpawnPoints[i] == null) m_SpawnPoints.RemoveAt(i);
            }

            if (m_SpawnPoints.Count == 0) return null;

            int index = m_CurrentSpawnPoint % m_SpawnPoints.Count;
            m_CurrentSpawnPoint = (m_CurrentSpawnPoint + 1) % m_SpawnPoints.Count;
            return m_SpawnPoints[index];
        }

        private bool PlayerAlreadyHasNetworkCharacter(NetworkManager manager, PlayerID player, SceneID scene)
        {
            if (!manager.TryGetModule(out GlobalOwnershipModule ownership, true)) return false;

            List<NetworkIdentity> owned = ownership.GetAllPlayerOwnedIds(player);
            for (int i = 0; i < owned.Count; i++)
            {
                var identity = owned[i];
                if (identity == null || identity.sceneId != scene) continue;
                if (identity.GetComponentInChildren<NetworkCharacter>(true) != null) return true;
            }

            return false;
        }
    }
}
