using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby
{
    public enum PurrNetStagingStartPolicy
    {
        HostManual = 0,
        AutomaticPlayerThreshold = 1,
        AutomaticAllReady = 2
    }

    /// <summary>
    /// Immutable, UI-facing representation of one player in a PurrNet staging room.
    /// </summary>
    public readonly struct PurrNetStagingPlayer
    {
        public PurrNetStagingPlayer(PlayerID playerId, string displayName, bool ready, bool host)
        {
            PlayerId = playerId;
            DisplayName = displayName ?? string.Empty;
            Ready = ready;
            Host = host;
        }

        public PlayerID PlayerId { get; }
        public string DisplayName { get; }
        public bool Ready { get; }
        public bool Host { get; }
    }

    // These packets deliberately contain only bounded primitive/PurrNet-auto-packed
    // fields. The server never trusts a client-supplied PlayerID or host flag.
    public struct PurrNetStagingNameRequestPacket : IPackedAuto
    {
        public string displayName;
    }

    public struct PurrNetStagingReadyRequestPacket : IPackedAuto
    {
        public bool ready;
    }

    public struct PurrNetStagingStartRequestPacket : IPackedAuto
    {
        public uint requestSequence;
    }

    public struct PurrNetStagingPlayerStatePacket : IPackedAuto
    {
        public PlayerID playerId;
        public string displayName;
        public bool ready;
        public bool host;
    }

    public struct PurrNetStagingSnapshotPacket : IPackedAuto
    {
        public uint revision;
        public PurrNetStagingPlayerStatePacket[] players;
        public int requiredPlayerCount;
        public int startPolicy;
        public bool matchStarted;
        public string statusMessage;
    }

    /// <summary>
    /// Server-authoritative pre-game room for listen-host PurrNet sessions.
    /// Transport connection and gameplay start are separate phases: the assigned
    /// demo player spawner remains disabled until the authoritative start packet.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Lobby/PurrNet Staging Room")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-520)]
    public sealed class PurrNetStagingRoomController : MonoBehaviour
    {
        private sealed class ServerPlayer
        {
            public string DisplayName;
            public bool Ready;
        }

        [Header("References")]
        [Tooltip("Optional scene NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;
        [SerializeField] private PurrNetLobbyService m_LobbyService;
        [SerializeField] private PurrNetChatBoxUI m_ChatBox;

        [Tooltip("Kept disabled while players are in the staging room, then enabled for everyone when the server starts the match.")]
        [SerializeField] private PurrNetDemoPlayerSpawner m_PlayerSpawner;

        [Header("Start Rules")]
        [SerializeField] private PurrNetStagingStartPolicy m_StartPolicy =
            PurrNetStagingStartPolicy.HostManual;
        [SerializeField, Min(1)] private int m_MinPlayersToStart = 1;
        [SerializeField, Min(1)] private int m_RequiredPlayerCount = 8;

        [Tooltip("For Automatic Player Threshold, use the capacity entered in the lobby Create panel as the required player count.")]
        [SerializeField] private bool m_UseLobbyCapacityAsPlayerThreshold = true;

        [Tooltip("Prevents the host's Start Match request until every connected player is ready.")]
        [SerializeField] private bool m_RequireAllReadyForHostStart = true;

        [Tooltip("Automatic Player Threshold waits for both the player threshold and every connected player to be ready.")]
        [SerializeField] private bool m_RequireAllReadyForAutomaticStart = true;

        [Tooltip("Allow fresh players to join after gameplay starts. Reconnecting participants bypass the closed-room rule while capacity remains.")]
        [SerializeField] private bool m_AllowJoinInProgress = false;

        [Header("Player Profile")]
        [SerializeField] private string m_DefaultDisplayName = "Player";
        [SerializeField, Range(1, 48)] private int m_MaxDisplayNameLength = 24;
        [SerializeField, Min(0.25f)] private float m_ProfilePublishTimeout = 8f;

        [Header("Dedicated Server")]
        [Tooltip("A listen host is always host. On a dedicated server, optionally grant host controls to the first connected player.")]
        [SerializeField] private bool m_FirstPlayerHostsDedicatedServer = true;

        private readonly Dictionary<PlayerID, ServerPlayer> m_ServerPlayers = new();
        private readonly List<PurrNetStagingPlayer> m_Players = new();
        private readonly HashSet<PlayerID> m_PendingLateJoinKicks = new();

        private ReadOnlyCollection<PurrNetStagingPlayer> m_ReadOnlyPlayers;
        private NetworkManager m_HookedManager;
        private Coroutine m_ProfilePublishRoutine;
        private PlayerID? m_ServerHostPlayer;
        private uint m_ServerRevision;
        private uint m_ClientRevision;
        private uint m_LocalStartSequence;
        private bool m_ServerSubscribed;
        private bool m_ClientSubscribed;
        private bool m_ProfileSubmitted;
        private bool m_LocalNameExplicit;
        private bool m_MatchStarted;
        private bool m_MatchStartedEventRaised;
        private bool m_PlayerSpawnerInitiallyEnabled;
        private bool m_GateCaptured;
        private bool m_HasSnapshotStartPolicy;
        private int m_SnapshotRequiredPlayers;
        private PurrNetStagingStartPolicy m_SnapshotStartPolicy;
        private string m_LocalDisplayName;
        private string m_StatusMessage = "Connect to a session to enter the staging room.";

        private NetworkManager ActiveManager =>
            m_NetworkManager != null ? m_NetworkManager : NetworkManager.main;

        public IReadOnlyList<PurrNetStagingPlayer> Players =>
            m_ReadOnlyPlayers ??= m_Players.AsReadOnly();

        public int PlayerCount => m_Players.Count;

        public int ReadyPlayerCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < m_Players.Count; i++)
                {
                    if (m_Players[i].Ready) count++;
                }

                return count;
            }
        }

        public int RequiredPlayerCount => m_SnapshotRequiredPlayers > 0
            ? m_SnapshotRequiredPlayers
            : ResolveServerRequiredPlayerCount();

        public string LocalDisplayName => m_LocalDisplayName ?? string.Empty;

        public bool LocalReady
        {
            get
            {
                if (!TryGetLocalPlayer(out PlayerID local)) return false;
                for (int i = 0; i < m_Players.Count; i++)
                {
                    if (m_Players[i].PlayerId == local) return m_Players[i].Ready;
                }

                return false;
            }
        }

        public bool IsLocalHost
        {
            get
            {
                if (!TryGetLocalPlayer(out PlayerID local)) return false;
                for (int i = 0; i < m_Players.Count; i++)
                {
                    if (m_Players[i].PlayerId == local) return m_Players[i].Host;
                }

                return false;
            }
        }

        public bool MatchStarted => m_MatchStarted;
        public bool AllowJoinInProgress => m_AllowJoinInProgress;
        public PurrNetStagingStartPolicy StartPolicy => m_HasSnapshotStartPolicy
            ? m_SnapshotStartPolicy
            : m_StartPolicy;
        public string StatusMessage => m_StatusMessage ?? string.Empty;

        public bool CanLocalStartMatch =>
            IsLocalHost &&
            !m_MatchStarted &&
            PurrNetStagingRules.AllowsManualStart(StartPolicy) &&
            PurrNetStagingRules.CanHostStart(
                PlayerCount,
                ReadyPlayerCount,
                m_MinPlayersToStart,
                m_RequireAllReadyForHostStart);

        public event Action PlayersChanged;
        public event Action StateChanged;
        public event Action MatchStartedEvent;

        /// <summary>
        /// Runtime/editor generator-friendly alternative to private serialized-field
        /// wiring. Call before entering Play Mode whenever possible.
        /// </summary>
        public void Configure(
            NetworkManager networkManager,
            PurrNetLobbyService lobbyService,
            PurrNetChatBoxUI chatBox,
            PurrNetDemoPlayerSpawner playerSpawner,
            PurrNetStagingStartPolicy startPolicy = PurrNetStagingStartPolicy.HostManual,
            int requiredPlayerCount = 8,
            bool useLobbyCapacityAsPlayerThreshold = true,
            bool allowJoinInProgress = false)
        {
            if (Application.isPlaying && m_GateCaptured && m_PlayerSpawner != null)
                m_PlayerSpawner.enabled = m_PlayerSpawnerInitiallyEnabled;

            m_NetworkManager = networkManager;
            m_LobbyService = lobbyService;
            m_ChatBox = chatBox;
            m_PlayerSpawner = playerSpawner;
            m_StartPolicy = startPolicy;
            m_RequiredPlayerCount = Mathf.Max(1, requiredPlayerCount);
            m_UseLobbyCapacityAsPlayerThreshold = useLobbyCapacityAsPlayerThreshold;
            m_AllowJoinInProgress = allowJoinInProgress;
            m_GateCaptured = false;

            ApplyDisplayNameToChat();
            if (Application.isPlaying)
            {
                CaptureAndCloseGameplayGate();
                if (isActiveAndEnabled) HookNetworkManager();
            }
        }

        private void Awake()
        {
            m_ReadOnlyPlayers = m_Players.AsReadOnly();
            m_LocalDisplayName = PurrNetStagingRules.SanitizeDisplayName(
                m_DefaultDisplayName,
                m_MaxDisplayNameLength,
                "Player");
            CaptureAndCloseGameplayGate();
            ApplyDisplayNameToChat();
        }

        private void OnEnable()
        {
            CaptureAndCloseGameplayGate();
            HookNetworkManager();
            ScheduleProfilePublish();
        }

        private void Start()
        {
            HookNetworkManager();
            ScheduleProfilePublish();
        }

        private void Update()
        {
            if (m_HookedManager != ActiveManager) HookNetworkManager();

            NetworkManager manager = ActiveManager;
            if (manager == null) return;
            if (manager.isServer) SubscribeServer(manager);
            if (manager.isClient) SubscribeClient(manager);

            if (manager.isServer && !m_MatchStarted)
            {
                PlayerID? previousHost = m_ServerHostPlayer;
                EnsureServerHost(manager);
                if (previousHost != m_ServerHostPlayer)
                    BroadcastSnapshot(manager, BuildWaitingStatus());
                EvaluateAutomaticStart(manager);
            }

            if (!m_ProfileSubmitted && manager.isLocalPlayerReady)
                ScheduleProfilePublish();
        }

        private void OnDisable()
        {
            StopProfilePublish();
            UnhookNetworkManager();
            RestoreGameplayGate();
        }

        private void OnDestroy()
        {
            RestoreGameplayGate();
        }

        private void OnValidate()
        {
            m_MinPlayersToStart = Mathf.Max(1, m_MinPlayersToStart);
            m_RequiredPlayerCount = Mathf.Max(1, m_RequiredPlayerCount);
            m_MaxDisplayNameLength = Mathf.Clamp(m_MaxDisplayNameLength, 1, 48);
            m_ProfilePublishTimeout = Mathf.Max(0.25f, m_ProfilePublishTimeout);
        }

        public void SetDisplayName(string displayName)
        {
            m_LocalNameExplicit = true;
            m_LocalDisplayName = PurrNetStagingRules.SanitizeDisplayName(
                displayName,
                m_MaxDisplayNameLength,
                m_DefaultDisplayName);
            ApplyDisplayNameToChat();
            m_ProfileSubmitted = false;
            ScheduleProfilePublish();
            StateChanged?.Invoke();
        }

        public bool SetReady(bool ready)
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isLocalPlayerReady || m_MatchStarted)
                return false;

            var request = new PurrNetStagingReadyRequestPacket { ready = ready };
            if (manager.isServer)
            {
                HandleReadyRequestServer(manager.localPlayer, request, true);
                return true;
            }

            if (!manager.isClient) return false;
            try
            {
                manager.SendToServer(request, Channel.ReliableOrdered);
                return true;
            }
            catch (Exception exception)
            {
                SetStatus($"Could not update ready state: {exception.Message}");
                Debug.LogException(exception, this);
                return false;
            }
        }

        public bool ToggleReady()
        {
            return SetReady(!LocalReady);
        }

        public bool StartMatch()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null ||
                !manager.isLocalPlayerReady ||
                !IsLocalHost ||
                m_MatchStarted ||
                !PurrNetStagingRules.AllowsManualStart(StartPolicy))
            {
                return false;
            }

            var request = new PurrNetStagingStartRequestPacket
            {
                requestSequence = ++m_LocalStartSequence
            };

            if (manager.isServer)
            {
                HandleStartRequestServer(manager.localPlayer, request, true);
                return m_MatchStarted;
            }

            if (!manager.isClient) return false;
            try
            {
                manager.SendToServer(request, Channel.ReliableOrdered);
                return true;
            }
            catch (Exception exception)
            {
                SetStatus($"Could not request match start: {exception.Message}");
                Debug.LogException(exception, this);
                return false;
            }
        }

        private void HookNetworkManager()
        {
            NetworkManager manager = ActiveManager;
            if (m_HookedManager == manager) return;

            UnhookNetworkManager();
            m_HookedManager = manager;
            if (m_HookedManager == null) return;

            m_HookedManager.onNetworkStarted += OnNetworkStarted;
            m_HookedManager.onNetworkShutdown += OnNetworkShutdown;
            m_HookedManager.onPlayerJoined += OnPlayerJoined;
            m_HookedManager.onPlayerLeft += OnPlayerLeft;
            m_HookedManager.onLocalPlayerReceivedID += OnLocalPlayerReceivedId;
            m_HookedManager.onServerConnectionState += OnServerConnectionState;
            m_HookedManager.onClientConnectionState += OnClientConnectionState;

            if (m_HookedManager.isServer) SubscribeServer(m_HookedManager);
            if (m_HookedManager.isClient) SubscribeClient(m_HookedManager);
        }

        private void UnhookNetworkManager()
        {
            if (m_HookedManager == null) return;

            UnsubscribeServer();
            UnsubscribeClient();
            m_HookedManager.onNetworkStarted -= OnNetworkStarted;
            m_HookedManager.onNetworkShutdown -= OnNetworkShutdown;
            m_HookedManager.onPlayerJoined -= OnPlayerJoined;
            m_HookedManager.onPlayerLeft -= OnPlayerLeft;
            m_HookedManager.onLocalPlayerReceivedID -= OnLocalPlayerReceivedId;
            m_HookedManager.onServerConnectionState -= OnServerConnectionState;
            m_HookedManager.onClientConnectionState -= OnClientConnectionState;
            m_HookedManager = null;
        }

        private void OnNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer) SubscribeServer(manager);
            else SubscribeClient(manager);

            if (!asServer)
            {
                m_ProfileSubmitted = false;
                ScheduleProfilePublish();
            }

            SetStatus("Connected. Waiting for the staging-room roster...");
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer) UnsubscribeServer();
            else UnsubscribeClient();

            if (manager.isServer || manager.isClient) return;
            ResetRoomState();
        }

        private void OnServerConnectionState(ConnectionState state)
        {
            if (state == ConnectionState.Disconnected) TryResetDisconnectedRoom();
        }

        private void OnClientConnectionState(ConnectionState state)
        {
            if (state == ConnectionState.Disconnected) TryResetDisconnectedRoom();
        }

        private void TryResetDisconnectedRoom()
        {
            NetworkManager manager = ActiveManager;
            if (manager != null && (manager.isServer || manager.isClient)) return;
            ResetRoomState();
        }

        private void OnPlayerJoined(PlayerID player, bool isReconnect, bool asServer)
        {
            if (!asServer) return;
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer) return;

            // The lobby service owns hard-cap rejection. Do not briefly add an
            // overflow player to the synchronized roster before that kick runs.
            if (m_LobbyService != null &&
                m_LobbyService.WillRejectPlayerForCapacity(
                    manager.players.Count,
                    isReconnect))
            {
                return;
            }

            bool listenHost = IsListenHostPlayer(manager, player);
            if (PurrNetStagingRules.ShouldRejectLateJoin(
                    m_MatchStarted,
                    m_AllowJoinInProgress,
                    isReconnect,
                    listenHost || player.isBot))
            {
                if (m_PendingLateJoinKicks.Add(player))
                    StartCoroutine(KickFreshLateJoinRoutine(player));
                return;
            }

            AdmitPlayerServer(manager, player, isReconnect);
        }

        private void AdmitPlayerServer(NetworkManager manager, PlayerID player, bool isReconnect)
        {
            if (manager == null || !manager.isServer) return;

            if (!m_ServerPlayers.TryGetValue(player, out ServerPlayer state))
            {
                state = new ServerPlayer
                {
                    DisplayName = DefaultNameFor(player),
                    Ready = PurrNetStagingRules.InitialReady(player.isBot)
                };
                m_ServerPlayers.Add(player, state);
            }
            else if (isReconnect)
            {
                state.Ready = PurrNetStagingRules.InitialReady(player.isBot);
            }

            EnsureServerHost(manager, player);
            BroadcastSnapshot(manager, $"{state.DisplayName} joined the room.");
            EvaluateAutomaticStart(manager);
        }

        private void OnPlayerLeft(PlayerID player, bool asServer)
        {
            if (!asServer) return;
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer) return;

            // A deliberately rejected late join was never admitted to the staging
            // roster, so do not emit a misleading joined/left room update.
            if (m_PendingLateJoinKicks.Remove(player))
            {
                m_ServerPlayers.Remove(player);
                return;
            }

            string name = m_ServerPlayers.TryGetValue(player, out ServerPlayer state)
                ? state.DisplayName
                : DefaultNameFor(player);
            m_ServerPlayers.Remove(player);

            if (m_ServerHostPlayer.HasValue && m_ServerHostPlayer.Value == player)
            {
                m_ServerHostPlayer = null;
                EnsureServerHost(manager);
            }

            BroadcastSnapshot(manager, $"{name} left the room.");
            EvaluateAutomaticStart(manager);
        }

        private void OnLocalPlayerReceivedId(PlayerID player)
        {
            m_ProfileSubmitted = false;
            ScheduleProfilePublish();
        }

        private void SubscribeServer(NetworkManager manager)
        {
            if (m_ServerSubscribed || manager == null || !manager.isServer) return;

            manager.Subscribe<PurrNetStagingNameRequestPacket>(HandleNameRequestServer, true);
            manager.Subscribe<PurrNetStagingReadyRequestPacket>(HandleReadyRequestServer, true);
            manager.Subscribe<PurrNetStagingStartRequestPacket>(HandleStartRequestServer, true);
            m_ServerSubscribed = true;

            IReadOnlyList<PlayerID> players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerID player = players[i];
                if (m_ServerPlayers.ContainsKey(player)) continue;
                m_ServerPlayers[player] = new ServerPlayer
                {
                    DisplayName = DefaultNameFor(player),
                    Ready = PurrNetStagingRules.InitialReady(player.isBot)
                };
            }

            EnsureServerHost(manager);
            if (m_ServerPlayers.Count > 0) BroadcastSnapshot(manager, BuildWaitingStatus());
        }

        private void SubscribeClient(NetworkManager manager)
        {
            if (m_ClientSubscribed || manager == null || !manager.isClient) return;
            manager.Subscribe<PurrNetStagingSnapshotPacket>(HandleSnapshotClient, false);
            m_ClientSubscribed = true;
        }

        private void UnsubscribeServer()
        {
            if (!m_ServerSubscribed || m_HookedManager == null) return;
            try
            {
                m_HookedManager.Unsubscribe<PurrNetStagingNameRequestPacket>(HandleNameRequestServer, true);
                m_HookedManager.Unsubscribe<PurrNetStagingReadyRequestPacket>(HandleReadyRequestServer, true);
                m_HookedManager.Unsubscribe<PurrNetStagingStartRequestPacket>(HandleStartRequestServer, true);
            }
            catch (Exception)
            {
                // PurrNet may already have torn down the server broadcaster.
            }

            m_ServerSubscribed = false;
        }

        private void UnsubscribeClient()
        {
            if (!m_ClientSubscribed || m_HookedManager == null) return;
            try
            {
                m_HookedManager.Unsubscribe<PurrNetStagingSnapshotPacket>(HandleSnapshotClient, false);
            }
            catch (Exception)
            {
                // PurrNet may already have torn down the client broadcaster.
            }

            m_ClientSubscribed = false;
        }

        private void HandleNameRequestServer(
            PlayerID sender,
            PurrNetStagingNameRequestPacket request,
            bool asServer)
        {
            if (!asServer) return;
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer || !IsConnectedPlayer(manager, sender)) return;

            // A late joiner's first roster broadcast can race its client-side
            // subscription. Its profile request is the reliable catch-up path.
            // Names remain immutable after the match starts, but the authoritative
            // started snapshot must still be returned so its local gate opens.
            if (m_MatchStarted)
            {
                BroadcastSnapshot(manager, "Match already started; joining in progress.");
                return;
            }

            if (!m_ServerPlayers.TryGetValue(sender, out ServerPlayer player))
            {
                player = new ServerPlayer
                {
                    Ready = PurrNetStagingRules.InitialReady(sender.isBot)
                };
                m_ServerPlayers[sender] = player;
            }

            player.DisplayName = PurrNetStagingRules.SanitizeDisplayName(
                request.displayName,
                m_MaxDisplayNameLength,
                DefaultNameFor(sender));
            BroadcastSnapshot(manager, BuildWaitingStatus());
        }

        private void HandleReadyRequestServer(
            PlayerID sender,
            PurrNetStagingReadyRequestPacket request,
            bool asServer)
        {
            if (!asServer || m_MatchStarted) return;
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer || !IsConnectedPlayer(manager, sender)) return;
            if (sender.isBot) return;

            if (!m_ServerPlayers.TryGetValue(sender, out ServerPlayer player))
            {
                player = new ServerPlayer
                {
                    DisplayName = DefaultNameFor(sender),
                    Ready = PurrNetStagingRules.InitialReady(sender.isBot)
                };
                m_ServerPlayers[sender] = player;
            }

            if (player.Ready == request.ready) return;
            player.Ready = request.ready;
            BroadcastSnapshot(manager, BuildWaitingStatus());
            EvaluateAutomaticStart(manager);
        }

        private void HandleStartRequestServer(
            PlayerID sender,
            PurrNetStagingStartRequestPacket request,
            bool asServer)
        {
            if (!asServer || m_MatchStarted) return;
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer) return;
            if (!m_ServerHostPlayer.HasValue || m_ServerHostPlayer.Value != sender) return;
            if (!PurrNetStagingRules.AllowsManualStart(m_StartPolicy)) return;

            int ready = CountServerReadyPlayers();
            if (!PurrNetStagingRules.CanHostStart(
                    m_ServerPlayers.Count,
                    ready,
                    m_MinPlayersToStart,
                    m_RequireAllReadyForHostStart))
            {
                BroadcastSnapshot(manager, BuildHostBlockedStatus());
                return;
            }

            StartMatchServer(manager, "The host started the match.");
        }

        private void HandleSnapshotClient(
            PlayerID sender,
            PurrNetStagingSnapshotPacket snapshot,
            bool asServer)
        {
            if (asServer) return;
            ApplySnapshot(snapshot);
        }

        private void ScheduleProfilePublish()
        {
            if (!isActiveAndEnabled || m_ProfileSubmitted || m_ProfilePublishRoutine != null)
                return;
            m_ProfilePublishRoutine = StartCoroutine(PublishProfileRoutine());
        }

        private void StopProfilePublish()
        {
            if (m_ProfilePublishRoutine == null) return;
            StopCoroutine(m_ProfilePublishRoutine);
            m_ProfilePublishRoutine = null;
        }

        private IEnumerator PublishProfileRoutine()
        {
            float deadline = Time.unscaledTime + Mathf.Max(0.25f, m_ProfilePublishTimeout);
            while (Time.unscaledTime <= deadline)
            {
                if (TryPublishLocalProfile())
                {
                    m_ProfilePublishRoutine = null;
                    yield break;
                }

                yield return null;
            }

            m_ProfilePublishRoutine = null;
        }

        private bool TryPublishLocalProfile()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isLocalPlayerReady) return false;

            ResolveInitialDisplayName();
            var request = new PurrNetStagingNameRequestPacket
            {
                displayName = m_LocalDisplayName
            };

            if (manager.isServer)
            {
                HandleNameRequestServer(manager.localPlayer, request, true);
                m_ProfileSubmitted = true;
                return true;
            }

            if (!manager.isClient) return false;
            try
            {
                manager.SendToServer(request, Channel.ReliableOrdered);
                m_ProfileSubmitted = true;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ResolveInitialDisplayName()
        {
            if (m_LocalNameExplicit) return;

            string requested = m_LobbyService != null
                ? m_LobbyService.LocalPlayerName
                : string.Empty;
            if (string.IsNullOrWhiteSpace(requested) && m_ChatBox != null)
                requested = m_ChatBox.DisplayName;
            if (string.IsNullOrWhiteSpace(requested)) requested = m_DefaultDisplayName;

            m_LocalDisplayName = PurrNetStagingRules.SanitizeDisplayName(
                requested,
                m_MaxDisplayNameLength,
                "Player");
            ApplyDisplayNameToChat();
        }

        private void ApplyDisplayNameToChat()
        {
            if (m_ChatBox != null) m_ChatBox.SetDisplayName(m_LocalDisplayName);
        }

        private void EnsureServerHost(NetworkManager manager, PlayerID? newlyJoined = null)
        {
            // A listen host always owns host controls. Check this before retaining
            // a provisional dedicated-server host: a remote peer can authenticate
            // during the short gap between StartServer and the loopback client.
            if (manager != null && manager.isHost && manager.isLocalPlayerReady)
            {
                PlayerID local = manager.localPlayer;
                if (m_ServerPlayers.ContainsKey(local))
                {
                    m_ServerHostPlayer = local;
                    return;
                }
            }

            if (m_ServerHostPlayer.HasValue &&
                m_ServerPlayers.ContainsKey(m_ServerHostPlayer.Value))
            {
                return;
            }

            m_ServerHostPlayer = null;

            if (!m_FirstPlayerHostsDedicatedServer) return;

            // PurrNetLobbyService.CreateAsync always starts a listen host. It sets
            // capacity before StartServer, so do not grant a remote peer temporary
            // host authority while the local loopback client is still connecting.
            if (IsWaitingForLobbyListenHost(manager))
            {
                return;
            }

            if (newlyJoined.HasValue && m_ServerPlayers.ContainsKey(newlyJoined.Value))
            {
                if (!newlyJoined.Value.isBot)
                {
                    m_ServerHostPlayer = newlyJoined.Value;
                    return;
                }
            }

            foreach (PlayerID player in m_ServerPlayers.Keys)
            {
                if (player.isBot) continue;
                if (!m_ServerHostPlayer.HasValue || player.id.value < m_ServerHostPlayer.Value.id.value)
                    m_ServerHostPlayer = player;
            }
        }

        private bool IsWaitingForLobbyListenHost(NetworkManager manager)
        {
            return manager != null &&
                   m_LobbyService != null &&
                   m_LobbyService.CurrentMaxPlayers > 0 &&
                   !manager.isHost;
        }

        private void EvaluateAutomaticStart(NetworkManager manager)
        {
            if (manager == null || !manager.isServer || m_MatchStarted) return;
            if (IsWaitingForLobbyListenHost(manager)) return;

            int players = m_ServerPlayers.Count;
            int ready = CountServerReadyPlayers();
            bool shouldStart = PurrNetStagingRules.ShouldStartAutomatically(
                m_StartPolicy,
                players,
                ready,
                m_MinPlayersToStart,
                ResolveServerRequiredPlayerCount(),
                m_RequireAllReadyForAutomaticStart);
            if (shouldStart) StartMatchServer(manager, "The staging-room start requirements were met.");
        }

        private void StartMatchServer(NetworkManager manager, string reason)
        {
            if (m_MatchStarted) return;
            m_MatchStarted = true;
            if (m_LobbyService != null)
                m_LobbyService.SetHostAcceptingJoins(m_AllowJoinInProgress);
            BroadcastSnapshot(manager, reason);
        }

        private IEnumerator KickFreshLateJoinRoutine(PlayerID player)
        {
            // Avoid modifying PlayersManager's collection reentrantly from its
            // onPlayerJoined invocation. In the normal case one frame is still
            // earlier than the scene-loaded/player-spawn handshake.
            yield return null;

            // If a listen host is still acquiring its local PlayerID, wait until
            // it can be distinguished from remote connections. This guarantees
            // that strict admission never closes the host's loopback connection.
            while (m_PendingLateJoinKicks.Contains(player) &&
                   isActiveAndEnabled &&
                   IsWaitingForLocalHostIdentity(ActiveManager))
            {
                yield return null;
            }

            if (!m_PendingLateJoinKicks.Contains(player)) yield break;
            NetworkManager manager = ActiveManager;
            if (!isActiveAndEnabled || manager == null || !manager.isServer)
            {
                m_PendingLateJoinKicks.Remove(player);
                yield break;
            }

            if (!m_MatchStarted ||
                m_AllowJoinInProgress ||
                player.isBot ||
                IsListenHostPlayer(manager, player))
            {
                m_PendingLateJoinKicks.Remove(player);
                AdmitPlayerServer(manager, player, false);
                yield break;
            }

            if (manager.TryGetModule(out PlayersManager players, true) &&
                players.IsValidPlayer(player))
            {
                players.KickPlayer(player);
            }

            m_PendingLateJoinKicks.Remove(player);
        }

        private void BroadcastSnapshot(NetworkManager manager, string status)
        {
            if (manager == null || !manager.isServer) return;

            PurrNetStagingSnapshotPacket snapshot = BuildSnapshot(status);
            ApplySnapshot(snapshot);
            try
            {
                manager.SendToAll(snapshot, Channel.ReliableOrdered);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private PurrNetStagingSnapshotPacket BuildSnapshot(string status)
        {
            var ids = new List<PlayerID>(m_ServerPlayers.Keys);
            ids.Sort((left, right) => left.id.value.CompareTo(right.id.value));

            var players = new PurrNetStagingPlayerStatePacket[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                PlayerID id = ids[i];
                ServerPlayer state = m_ServerPlayers[id];
                players[i] = new PurrNetStagingPlayerStatePacket
                {
                    playerId = id,
                    displayName = state.DisplayName,
                    ready = state.Ready,
                    host = m_ServerHostPlayer.HasValue && m_ServerHostPlayer.Value == id
                };
            }

            return new PurrNetStagingSnapshotPacket
            {
                revision = ++m_ServerRevision,
                players = players,
                requiredPlayerCount = ResolveServerRequiredPlayerCount(),
                startPolicy = (int)m_StartPolicy,
                matchStarted = m_MatchStarted,
                statusMessage = string.IsNullOrWhiteSpace(status)
                    ? BuildWaitingStatus()
                    : status
            };
        }

        private void ApplySnapshot(PurrNetStagingSnapshotPacket snapshot)
        {
            if (snapshot.revision <= m_ClientRevision) return;
            m_ClientRevision = snapshot.revision;

            bool startedNow = snapshot.matchStarted && !m_MatchStartedEventRaised;
            m_MatchStarted = snapshot.matchStarted;
            m_SnapshotRequiredPlayers = Mathf.Max(1, snapshot.requiredPlayerCount);
            m_SnapshotStartPolicy = PurrNetStagingRules.NormalizeStartPolicy(snapshot.startPolicy);
            m_HasSnapshotStartPolicy = true;
            m_StatusMessage = snapshot.statusMessage ?? string.Empty;

            m_Players.Clear();
            PurrNetStagingPlayerStatePacket[] states = snapshot.players ??
                                                        Array.Empty<PurrNetStagingPlayerStatePacket>();
            for (int i = 0; i < states.Length; i++)
            {
                PurrNetStagingPlayerStatePacket state = states[i];
                m_Players.Add(new PurrNetStagingPlayer(
                    state.playerId,
                    PurrNetStagingRules.SanitizeDisplayName(
                        state.displayName,
                        m_MaxDisplayNameLength,
                        DefaultNameFor(state.playerId)),
                    state.ready,
                    state.host));
            }

            if (m_MatchStarted) ReleaseGameplayGate();
            PlayersChanged?.Invoke();
            StateChanged?.Invoke();
            if (startedNow)
            {
                m_MatchStartedEventRaised = true;
                MatchStartedEvent?.Invoke();
            }
        }

        private int ResolveServerRequiredPlayerCount()
        {
            if (m_UseLobbyCapacityAsPlayerThreshold &&
                m_LobbyService != null &&
                m_LobbyService.CurrentMaxPlayers > 0)
            {
                return Mathf.Max(1, m_LobbyService.CurrentMaxPlayers);
            }

            return Mathf.Max(1, m_RequiredPlayerCount);
        }

        private int CountServerReadyPlayers()
        {
            int count = 0;
            foreach (ServerPlayer player in m_ServerPlayers.Values)
            {
                if (player.Ready) count++;
            }

            return count;
        }

        private string BuildWaitingStatus()
        {
            int players = m_ServerPlayers.Count;
            int ready = CountServerReadyPlayers();
            if (m_MatchStarted) return "Match started.";

            return m_StartPolicy switch
            {
                PurrNetStagingStartPolicy.AutomaticPlayerThreshold =>
                    $"Waiting for players: {players}/{ResolveServerRequiredPlayerCount()} ({ready} ready).",
                PurrNetStagingStartPolicy.AutomaticAllReady =>
                    $"Waiting for everyone to be ready: {ready}/{players}.",
                _ => $"Staging room: {players} player(s), {ready} ready. The host starts the match."
            };
        }

        private string BuildHostBlockedStatus()
        {
            if (m_ServerPlayers.Count < Mathf.Max(1, m_MinPlayersToStart))
                return $"At least {Mathf.Max(1, m_MinPlayersToStart)} player(s) are required.";
            if (m_RequireAllReadyForHostStart && CountServerReadyPlayers() < m_ServerPlayers.Count)
                return "Every connected player must be ready before the host can start.";
            return BuildWaitingStatus();
        }

        private bool TryGetLocalPlayer(out PlayerID player)
        {
            NetworkManager manager = ActiveManager;
            if (manager != null && manager.isLocalPlayerReady)
            {
                player = manager.localPlayer;
                return true;
            }

            player = default;
            return false;
        }

        private static bool IsListenHostPlayer(NetworkManager manager, PlayerID player)
        {
            return manager != null &&
                   manager.isHost &&
                   manager.isLocalPlayerReady &&
                   manager.localPlayer == player;
        }

        private bool IsWaitingForLocalHostIdentity(NetworkManager manager)
        {
            return manager != null &&
                   manager.isServer &&
                   !manager.isLocalPlayerReady &&
                   (manager.pendingHost || IsWaitingForLobbyListenHost(manager));
        }

        private static bool IsConnectedPlayer(NetworkManager manager, PlayerID player)
        {
            IReadOnlyList<PlayerID> players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                if (players[i] == player) return true;
            }

            return false;
        }

        private string DefaultNameFor(PlayerID player)
        {
            if (player.isBot) return $"Bot {player.id.value}";
            return player.isServer ? "Host" : $"Player {player.id.value}";
        }

        private void SetStatus(string status)
        {
            m_StatusMessage = status ?? string.Empty;
            StateChanged?.Invoke();
        }

        private void ResetRoomState()
        {
            StopProfilePublish();
            m_ServerPlayers.Clear();
            m_PendingLateJoinKicks.Clear();
            m_Players.Clear();
            m_ServerHostPlayer = null;
            m_ServerRevision = 0;
            m_ClientRevision = 0;
            m_SnapshotRequiredPlayers = 0;
            m_SnapshotStartPolicy = default;
            m_HasSnapshotStartPolicy = false;
            m_ProfileSubmitted = false;
            m_MatchStarted = false;
            m_MatchStartedEventRaised = false;
            m_StatusMessage = "Connect to a session to enter the staging room.";
            CaptureAndCloseGameplayGate();
            PlayersChanged?.Invoke();
            StateChanged?.Invoke();
        }

        private void CaptureAndCloseGameplayGate()
        {
            if (!Application.isPlaying) return;
            if (m_PlayerSpawner == null) return;
            if (!m_GateCaptured)
            {
                m_PlayerSpawnerInitiallyEnabled = m_PlayerSpawner.enabled;
                m_GateCaptured = true;
            }

            if (!m_MatchStarted) m_PlayerSpawner.enabled = false;
        }

        private void ReleaseGameplayGate()
        {
            if (m_PlayerSpawner == null || !m_GateCaptured) return;
            m_PlayerSpawner.enabled = m_PlayerSpawnerInitiallyEnabled;
        }

        private void RestoreGameplayGate()
        {
            if (m_PlayerSpawner == null || !m_GateCaptured) return;
            m_PlayerSpawner.enabled = m_PlayerSpawnerInitiallyEnabled;
        }
    }

    internal static class PurrNetStagingRules
    {
        internal static bool InitialReady(bool isBot)
        {
            // Bots have no client-side Ready request path. Treat a configured bot
            // slot as server-ready so it cannot deadlock an all-ready room.
            return isBot;
        }

        internal static bool AllowsManualStart(PurrNetStagingStartPolicy policy)
        {
            return policy == PurrNetStagingStartPolicy.HostManual;
        }

        internal static PurrNetStagingStartPolicy NormalizeStartPolicy(int value)
        {
            return value switch
            {
                (int)PurrNetStagingStartPolicy.AutomaticPlayerThreshold =>
                    PurrNetStagingStartPolicy.AutomaticPlayerThreshold,
                (int)PurrNetStagingStartPolicy.AutomaticAllReady =>
                    PurrNetStagingStartPolicy.AutomaticAllReady,
                _ => PurrNetStagingStartPolicy.HostManual
            };
        }

        internal static bool CanHostStart(
            int playerCount,
            int readyPlayerCount,
            int minimumPlayers,
            bool requireAllReady)
        {
            int players = Math.Max(0, playerCount);
            if (players < Math.Max(1, minimumPlayers)) return false;
            return !requireAllReady || readyPlayerCount >= players;
        }

        internal static bool ShouldStartAutomatically(
            PurrNetStagingStartPolicy policy,
            int playerCount,
            int readyPlayerCount,
            int minimumPlayers,
            int requiredPlayers,
            bool requireAllReadyAtThreshold)
        {
            int players = Math.Max(0, playerCount);
            int ready = Math.Max(0, readyPlayerCount);
            int minimum = Math.Max(1, minimumPlayers);

            switch (policy)
            {
                case PurrNetStagingStartPolicy.AutomaticPlayerThreshold:
                    if (players < Math.Max(minimum, Math.Max(1, requiredPlayers))) return false;
                    return !requireAllReadyAtThreshold || ready >= players;

                case PurrNetStagingStartPolicy.AutomaticAllReady:
                    return players >= minimum && ready >= players;

                default:
                    return false;
            }
        }

        internal static bool ShouldRejectLateJoin(
            bool matchStarted,
            bool allowJoinInProgress,
            bool isReconnect,
            bool isListenHost)
        {
            return matchStarted &&
                   !allowJoinInProgress &&
                   !isReconnect &&
                   !isListenHost;
        }

        internal static string SanitizeDisplayName(string value, int maximumLength, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value;
            if (string.IsNullOrWhiteSpace(candidate)) candidate = "Player";

            var chars = candidate.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (char.IsControl(chars[i])) chars[i] = ' ';
            }

            string cleaned = new string(chars).Trim();
            if (string.IsNullOrEmpty(cleaned)) cleaned = "Player";
            int max = Math.Max(1, maximumLength);
            return cleaned.Length <= max ? cleaned : cleaned.Substring(0, max);
        }
    }
}
