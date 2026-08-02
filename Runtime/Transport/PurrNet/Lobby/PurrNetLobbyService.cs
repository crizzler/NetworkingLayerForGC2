using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Lobby;
using UnityEngine;
using PurrConnectionState = global::PurrNet.Transports.ConnectionState;
using PurrGenericTransport = global::PurrNet.Transports.GenericTransport;
using PurrNetworkManager = global::PurrNet.NetworkManager;
using PurrPlayerId = global::PurrNet.PlayerID;
using PurrPlayersManager = global::PurrNet.Modules.PlayersManager;
using PurrRoomTransport = global::PurrNet.Transports.PurrTransport;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby
{
    public enum PurrNetLobbyMode
    {
        Direct = 0,
        Lan = 1,
        RoomCode = 2
    }

    /// <summary>
    /// PurrNet implementation of the shared lobby API. Direct mode needs only a
    /// gameplay address, LAN mode adds bounded local discovery, and Room Code
    /// mode targets PurrTransport without taking a Steamworks dependency.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Lobby/PurrNet Lobby Service")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-450)]
    public sealed class PurrNetLobbyService : NetworkLobbyServiceBehaviour
    {
        private const string LanTransportMarker = "purrnet-lan";
        private const string DefaultRoomRelayHost = "purrtransport.purrservers.com";
        private const int MaximumDiscoveredHosts = 256;
        private const string DefaultRelayProductionWarning =
            "PurrNet's public PurrTransport relay is for development only. " +
            "Production games must use self-hosted relay servers.";

        [Header("References")]
        [Tooltip("Optional scene NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private PurrNetworkManager m_NetworkManager;

        [Header("Mode")]
        [Tooltip(
            "Direct joins a known address, LAN discovers hosts on the local network, " +
            "and Room Code uses PurrTransport. The default public PurrTransport relay " +
            "is strictly for development; production requires a self-hosted relay.")]
        [SerializeField] private PurrNetLobbyMode m_Mode = PurrNetLobbyMode.Lan;

        [Tooltip("Default client address for Direct mode and manual LAN fallback.")]
        [SerializeField] private string m_DefaultAddress = "127.0.0.1";

        [Tooltip("PurrNet gameplay port. LAN discovery uses a separate port below.")]
        [SerializeField] private ushort m_DefaultPort = 5000;

        [SerializeField] private NetworkLobbyCompatibilityProfile m_Compatibility =
            new NetworkLobbyCompatibilityProfile();

        [Header("LAN Discovery")]
        [Tooltip("UDP port used only for LAN advertisements and discovery queries.")]
        [SerializeField] private ushort m_LanDiscoveryPort = 47777;

        [Tooltip("Seconds between advertisements while this service owns a LAN host.")]
        [SerializeField, Min(0.2f)] private float m_LanAdvertiseInterval = 0.75f;

        [Tooltip("Seconds without an advertisement before a discovered host expires.")]
        [SerializeField, Min(1f)] private float m_LanSessionTtl = 3f;

        [Tooltip("How long Refresh waits for LAN replies before publishing the list.")]
        [SerializeField, Range(0.1f, 3f)] private float m_LanRefreshWindow = 0.65f;

        [Header("Connection")]
        [Tooltip("Seconds allowed for each PurrNet server or client half to connect.")]
        [SerializeField, Min(1f)] private float m_ConnectionTimeoutSeconds = 20f;

        [Tooltip(
            "Briefly keeps a join in the Joining state after PurrNet assigns the local player. " +
            "This lets authoritative closed/full admission rejections arrive before the lobby UI shows Connected.")]
        [SerializeField, Range(0.1f, 3f)] private float m_AdmissionStabilitySeconds = 0.5f;

        private sealed class DiscoveredHost
        {
            public PurrNetLanAdvertisement Advertisement;
            public string SourceAddress;
            public double LastSeen;
        }

        private readonly object m_OperationLock = new object();
        private readonly Dictionary<Guid, DiscoveredHost> m_DiscoveredHosts =
            new Dictionary<Guid, DiscoveredHost>();
        private readonly HashSet<PurrPlayerId> m_PendingCapacityKicks =
            new HashSet<PurrPlayerId>();

        private PurrNetLanDiscovery m_Discovery;
        private CancellationTokenSource m_LifetimeCancellation;
        private CancellationTokenSource m_ActiveOperation;
        private PurrNetworkManager m_HookedManager;
        private bool m_Initialized;
        private PurrNetLobbyMode m_InitializedMode;
        private bool m_OwnsServer;
        private bool m_OwnsClient;
        private bool m_IsHosting;
        private bool m_SuppressConnectionCallbacks;
        private Guid m_HostSessionId;
        private string m_HostSessionName = string.Empty;
        private int m_HostMaxPlayers = 4;
        private int m_CurrentMaxPlayers;
        private string m_LocalPlayerName = string.Empty;
        private bool m_HostIsVisible = true;
        private bool m_HostAcceptingJoins = true;
        private ushort m_HostGamePort;
        private double m_NextAdvertisementAt;
        private double m_NextDiscoveryWarningAt;
        private NetworkLobbyQuery m_LastQuery = new NetworkLobbyQuery(
            string.Empty,
            NetworkLobbyTopology.ClientServer);

        public override string ServiceName => m_Mode switch
        {
            PurrNetLobbyMode.Direct => "PurrNet Direct",
            PurrNetLobbyMode.Lan => "PurrNet LAN",
            PurrNetLobbyMode.RoomCode => "PurrNet Room Code",
            _ => "PurrNet"
        };

        public override NetworkLobbyCapabilities Capabilities
        {
            get
            {
                const NetworkLobbyCapabilities common =
                    NetworkLobbyCapabilities.Create |
                    NetworkLobbyCapabilities.PlayerCapacity;

                return m_Mode switch
                {
                    PurrNetLobbyMode.Direct =>
                        common | NetworkLobbyCapabilities.DirectAddress,
                    PurrNetLobbyMode.Lan =>
                        common |
                        NetworkLobbyCapabilities.QuickJoin |
                        NetworkLobbyCapabilities.Browse |
                        NetworkLobbyCapabilities.Refresh |
                        NetworkLobbyCapabilities.DirectAddress |
                        NetworkLobbyCapabilities.Visibility,
                    PurrNetLobbyMode.RoomCode =>
                        common | NetworkLobbyCapabilities.JoinByCode,
                    _ => NetworkLobbyCapabilities.None
                };
            }
        }

        public PurrNetLobbyMode Mode => m_Mode;
        public PurrNetworkManager ActiveNetworkManager => ActiveManager;
        public ushort LanDiscoveryPort => m_LanDiscoveryPort;

        /// <summary>
        /// Capacity supplied by the active create/join request. A staging-room
        /// controller can deliberately use this as its automatic start threshold.
        /// Zero means no active request supplied a known capacity.
        /// </summary>
        public int CurrentMaxPlayers => m_CurrentMaxPlayers;

        /// <summary>
        /// Local display name supplied by the shared lobby UI for the active
        /// request. PurrNet staging/chat components sanitize it again before use.
        /// </summary>
        public string LocalPlayerName => m_LocalPlayerName ?? string.Empty;

        /// <summary>
        /// Whether the owned host currently accepts new participants. LAN
        /// advertisements expose this as IsOpen; staging-room admission performs
        /// the authoritative post-authentication check for every transport mode.
        /// </summary>
        public bool HostAcceptingJoins => m_HostAcceptingJoins;

        public void SetHostAcceptingJoins(bool acceptingJoins)
        {
            if (m_HostAcceptingJoins == acceptingJoins) return;
            m_HostAcceptingJoins = acceptingJoins;

            if (m_Mode != PurrNetLobbyMode.Lan ||
                !m_IsHosting ||
                !m_HostIsVisible ||
                m_Discovery == null)
            {
                return;
            }

            // Publish the closed/open state immediately instead of waiting for
            // the next periodic advertisement.
            AdvertiseLanHost();
            m_NextAdvertisementAt = Time.realtimeSinceStartupAsDouble +
                                    Math.Max(0.2f, m_LanAdvertiseInterval);
        }

        private PurrNetworkManager ActiveManager =>
            m_NetworkManager != null ? m_NetworkManager : PurrNetworkManager.main;

        private NetworkLobbyCompatibilityProfile Compatibility =>
            m_Compatibility ??= new NetworkLobbyCompatibilityProfile();

        private void OnEnable()
        {
            EnsureLifetimeCancellation();
            HookNetworkManager();
        }

        private void Update()
        {
            if (m_HookedManager != ActiveManager) HookNetworkManager();
            if (m_Mode != PurrNetLobbyMode.Lan || m_Discovery == null) return;

            m_Discovery.Poll(HandleDiscoveryPacket);
            double now = Time.realtimeSinceStartupAsDouble;

            if (m_IsHosting &&
                m_HostIsVisible &&
                now >= m_NextAdvertisementAt)
            {
                AdvertiseLanHost();
                m_NextAdvertisementAt = now + Math.Max(0.2f, m_LanAdvertiseInterval);
            }

            if (PruneExpiredHosts(now)) PublishDiscoveredHosts(m_LastQuery);
        }

        private void OnDisable()
        {
            CancelLifetimeOperations();
            StopOwnedNetwork();
            CloseDiscovery();
            UnhookNetworkManager();
            m_Initialized = false;
            m_DiscoveredHosts.Clear();
            ClearSessions();
            SetDisconnected("PurrNet lobby service is disabled.");
        }

        private void OnDestroy()
        {
            CancelLifetimeOperations();
            StopOwnedNetwork();
            CloseDiscovery();
            UnhookNetworkManager();
        }

        private void OnValidate()
        {
            if (m_DefaultPort == 0) m_DefaultPort = 5000;
            if (m_LanDiscoveryPort == 0) m_LanDiscoveryPort = 47777;
            m_LanAdvertiseInterval = Mathf.Max(0.2f, m_LanAdvertiseInterval);
            m_LanSessionTtl = Mathf.Max(1f, m_LanSessionTtl);
            m_LanRefreshWindow = Mathf.Clamp(m_LanRefreshWindow, 0.1f, 3f);
            m_ConnectionTimeoutSeconds = Mathf.Max(1f, m_ConnectionTimeoutSeconds);
            m_AdmissionStabilitySeconds = Mathf.Clamp(m_AdmissionStabilitySeconds, 0.1f, 3f);
        }

        public override async Task<NetworkLobbyOperationResult> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(
                    cancellationToken,
                    out CancellationTokenSource operation,
                    out NetworkLobbyOperationResult busy))
            {
                return busy;
            }

            try
            {
                SetState(NetworkLobbyState.Initializing, $"Initializing {ServiceName}...");
                operation.Token.ThrowIfCancellationRequested();
                HookNetworkManager();

                if (!TryValidateConfiguration(out string error))
                    return Fail("configuration", error);

                if (m_Initialized && m_InitializedMode != m_Mode)
                {
                    CloseDiscovery();
                    m_DiscoveredHosts.Clear();
                    ClearSessions();
                }

                if (m_Mode == PurrNetLobbyMode.Lan &&
                    !TryOpenDiscovery(out error))
                {
                    return Fail("lan-unavailable", error);
                }

                m_Initialized = true;
                m_InitializedMode = m_Mode;
                SetDisconnected(GetReadyStatus());
                await Task.CompletedTask;
                return NetworkLobbyOperationResult.Success(GetReadyStatus());
            }
            catch (OperationCanceledException)
            {
                SetDisconnected("Lobby initialization was cancelled.");
                return NetworkLobbyOperationResult.Failure(
                    "cancelled",
                    "Lobby initialization was cancelled.");
            }
            finally
            {
                EndOperation(operation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> RefreshAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default)
        {
            if (m_Mode != PurrNetLobbyMode.Lan) return Unsupported("Refresh");
            if (!TryBeginOperation(
                    cancellationToken,
                    out CancellationTokenSource operation,
                    out NetworkLobbyOperationResult busy))
            {
                return busy;
            }

            try
            {
                if (!TryPrepareOperation(out string error))
                    return Fail("configuration", error);
                if (IsNetworkActive())
                    return Fail("already-connected", "Leave the current session before browsing.");

                return await RefreshLanCoreAsync(query, operation.Token);
            }
            catch (OperationCanceledException)
            {
                SetDisconnected("LAN refresh was cancelled.");
                return NetworkLobbyOperationResult.Failure(
                    "cancelled",
                    "LAN refresh was cancelled.");
            }
            finally
            {
                EndOperation(operation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> CreateAsync(
            NetworkLobbyCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(
                    cancellationToken,
                    out CancellationTokenSource operation,
                    out NetworkLobbyOperationResult busy))
            {
                return busy;
            }

            try
            {
                if (!TryPrepareOperation(out string error))
                    return Fail("configuration", error);
                if (request.Topology != NetworkLobbyTopology.ClientServer)
                    return Fail("topology", "PurrNet lobby sessions use Client/Server topology.");
                if (!EnsureManagerOffline(out error))
                    return Fail("already-connected", error);

                return await CreateCoreAsync(request, operation.Token);
            }
            catch (OperationCanceledException)
            {
                StopOwnedNetwork();
                SetDisconnected("Host startup was cancelled.");
                return NetworkLobbyOperationResult.Failure(
                    "cancelled",
                    "Host startup was cancelled.");
            }
            finally
            {
                EndOperation(operation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> QuickJoinAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default)
        {
            if (m_Mode != PurrNetLobbyMode.Lan) return Unsupported("Quick Join");
            if (!TryBeginOperation(
                    cancellationToken,
                    out CancellationTokenSource operation,
                    out NetworkLobbyOperationResult busy))
            {
                return busy;
            }

            try
            {
                if (!TryPrepareOperation(out string error))
                    return Fail("configuration", error);
                if (!EnsureManagerOffline(out error))
                    return Fail("already-connected", error);

                NetworkLobbyOperationResult refreshed =
                    await RefreshLanCoreAsync(query, operation.Token);
                if (!refreshed.Succeeded) return refreshed;

                NetworkLobbyEntry candidate = null;
                for (int i = 0; i < Sessions.Count; i++)
                {
                    if (!Sessions[i].CanJoin) continue;
                    candidate = Sessions[i];
                    break;
                }

                if (candidate == null)
                    return Fail("not-found", "No compatible open LAN session was found.");

                var joinRequest = new NetworkLobbyJoinRequest(
                    candidate,
                    string.Empty,
                    candidate.Address,
                    candidate.Port,
                    candidate.Region,
                    NetworkLobbyTopology.ClientServer,
                    query.PlayerName);
                return await JoinCoreAsync(joinRequest, operation.Token);
            }
            catch (OperationCanceledException)
            {
                StopOwnedNetwork();
                SetDisconnected("Quick Join was cancelled.");
                return NetworkLobbyOperationResult.Failure(
                    "cancelled",
                    "Quick Join was cancelled.");
            }
            finally
            {
                EndOperation(operation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> JoinAsync(
            NetworkLobbyJoinRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(
                    cancellationToken,
                    out CancellationTokenSource operation,
                    out NetworkLobbyOperationResult busy))
            {
                return busy;
            }

            try
            {
                if (!TryPrepareOperation(out string error))
                    return Fail("configuration", error);
                if (!EnsureManagerOffline(out error))
                    return Fail("already-connected", error);

                return await JoinCoreAsync(request, operation.Token);
            }
            catch (OperationCanceledException)
            {
                StopOwnedNetwork();
                SetDisconnected("Join was cancelled.");
                return NetworkLobbyOperationResult.Failure(
                    "cancelled",
                    "Join was cancelled.");
            }
            finally
            {
                EndOperation(operation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> LeaveAsync(
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(
                    cancellationToken,
                    out CancellationTokenSource operation,
                    out NetworkLobbyOperationResult busy))
            {
                return busy;
            }

            try
            {
                SetState(NetworkLobbyState.Leaving, "Leaving PurrNet session...");
                StopOwnedNetwork();

                PurrNetworkManager manager = ActiveManager;
                if (manager != null)
                {
                    await WaitForOfflineAsync(manager, operation.Token, 5f);
                }

                SetDisconnected(GetReadyStatus("Disconnected. "));
                return NetworkLobbyOperationResult.Success("Disconnected.");
            }
            catch (OperationCanceledException)
            {
                // StopOwnedNetwork has already issued all shutdown calls. A cancelled
                // wait must not resurrect or retain ownership of the session.
                SetDisconnected("Disconnect is completing in the background.");
                return NetworkLobbyOperationResult.Failure(
                    "cancelled",
                    "Waiting for PurrNet shutdown was cancelled.");
            }
            finally
            {
                EndOperation(operation);
            }
        }

        private async Task<NetworkLobbyOperationResult> CreateCoreAsync(
            NetworkLobbyCreateRequest request,
            CancellationToken cancellationToken)
        {
            PurrNetworkManager manager = ActiveManager;
            string sessionName = NormalizeSessionName(request.SessionName);
            int maxPlayers = Math.Max(1, request.MaxPlayers);
            ushort gamePort = request.Port != 0 ? request.Port : m_DefaultPort;
            string roomCode = string.Empty;

            m_CurrentMaxPlayers = maxPlayers;
            m_LocalPlayerName = (request.PlayerName ?? string.Empty).Trim();
            m_HostAcceptingJoins = true;

            if (m_Mode == PurrNetLobbyMode.RoomCode)
            {
                roomCode = NormalizeRoomCode(request.JoinCode);
                if (string.IsNullOrEmpty(roomCode)) roomCode = GenerateRoomCode();
                if (!TryValidateRoomCode(roomCode, out string roomError))
                    return Fail("invalid-code", roomError);

                ((PurrRoomTransport)manager.transport).roomName = roomCode;
            }
            else
            {
                if (gamePort == 0)
                    return Fail("invalid-port", "A gameplay port is required.");
                if (!TryConfigureEndpoint(
                        manager.transport,
                        "127.0.0.1",
                        gamePort,
                        out string endpointError))
                {
                    return Fail("transport", endpointError);
                }
            }

            TryConfigureMaximumPlayers(manager.transport, maxPlayers);

            m_HostSessionId = Guid.NewGuid();
            m_HostSessionName = sessionName;
            m_HostMaxPlayers = maxPlayers;
            m_HostIsVisible = request.IsVisible;
            m_HostGamePort = gamePort;
            m_NextAdvertisementAt = 0d;

            SetState(NetworkLobbyState.Creating, $"Starting {sessionName}...");
            try
            {
                m_OwnsServer = true;
                manager.StartServer();
                if (!await WaitForStateAsync(
                        manager,
                        true,
                        PurrConnectionState.Connected,
                        cancellationToken,
                        m_ConnectionTimeoutSeconds))
                {
                    throw new TimeoutException("PurrNet server startup timed out.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                m_OwnsClient = true;
                manager.StartClient();
                if (!await WaitForStateAsync(
                        manager,
                        false,
                        PurrConnectionState.Connected,
                        cancellationToken,
                        m_ConnectionTimeoutSeconds))
                {
                    throw new TimeoutException(
                        "The host's local PurrNet client startup timed out.");
                }

                m_IsHosting = true;
                string sessionId = m_Mode == PurrNetLobbyMode.RoomCode
                    ? roomCode
                    : m_Mode == PurrNetLobbyMode.Lan
                        ? $"lan:{m_HostSessionId:N}"
                        : $"direct:{gamePort}";
                string status = m_Mode == PurrNetLobbyMode.RoomCode
                    ? AppendRoomRelayWarning($"Hosting room {roomCode}.")
                    : m_Mode == PurrNetLobbyMode.Lan
                        ? $"Hosting {sessionName} on LAN port {gamePort}."
                        : $"Hosting {sessionName} on port {gamePort}.";
                SetConnected(sessionId, sessionName, status);

                if (m_Mode == PurrNetLobbyMode.Lan && m_HostIsVisible)
                    AdvertiseLanHost();

                return NetworkLobbyOperationResult.Success(status);
            }
            catch (OperationCanceledException)
            {
                StopOwnedNetwork();
                throw;
            }
            catch (Exception exception)
            {
                StopOwnedNetwork();
                return Fail("host-failed", $"Could not start the PurrNet host: {exception.Message}");
            }
        }

        private async Task<NetworkLobbyOperationResult> JoinCoreAsync(
            NetworkLobbyJoinRequest request,
            CancellationToken cancellationToken)
        {
            PurrNetworkManager manager = ActiveManager;
            m_CurrentMaxPlayers = Math.Max(0, request.Entry?.MaxPlayers ?? 0);
            m_LocalPlayerName = (request.PlayerName ?? string.Empty).Trim();
            string sessionId;
            string sessionName;
            string statusTarget;

            if (m_Mode == PurrNetLobbyMode.RoomCode)
            {
                string roomCode = NormalizeRoomCode(request.JoinCode);
                if (!TryValidateRoomCode(roomCode, out string roomError))
                    return Fail("invalid-code", roomError);

                ((PurrRoomTransport)manager.transport).roomName = roomCode;
                sessionId = roomCode;
                sessionName = roomCode;
                statusTarget = $"room {roomCode}";
            }
            else
            {
                NetworkLobbyEntry entry = request.Entry;
                if (entry != null)
                {
                    if (!entry.IsCompatible)
                        return Fail(
                            "incompatible",
                            string.IsNullOrEmpty(entry.CompatibilityMessage)
                                ? "The selected session is incompatible."
                                : entry.CompatibilityMessage);
                    if (!entry.IsOpen)
                        return Fail("closed", "The selected session is closed.");
                    if (entry.IsFull)
                        return Fail("full", "The selected session is full.");
                }

                string address = !string.IsNullOrWhiteSpace(request.Address)
                    ? request.Address.Trim()
                    : entry != null && !string.IsNullOrWhiteSpace(entry.Address)
                        ? entry.Address.Trim()
                        : (m_DefaultAddress ?? string.Empty).Trim();
                ushort port = request.Port != 0
                    ? request.Port
                    : entry != null && entry.Port != 0
                        ? entry.Port
                        : m_DefaultPort;
                if (!TryValidateAddress(address, out string addressError))
                    return Fail("invalid-address", addressError);
                if (port == 0)
                    return Fail("invalid-port", "A gameplay port is required.");

                if (!TryConfigureEndpoint(
                        manager.transport,
                        address,
                        port,
                        out string endpointError))
                {
                    return Fail("transport", endpointError);
                }

                sessionId = entry?.Id ?? $"direct:{address}:{port}";
                sessionName = entry?.Name ?? $"{address}:{port}";
                statusTarget = $"{address}:{port}";
            }

            SetState(NetworkLobbyState.Joining, $"Connecting to {statusTarget}...");
            try
            {
                m_OwnsClient = true;
                manager.StartClient();
                if (!await WaitForStateAsync(
                        manager,
                        false,
                        PurrConnectionState.Connected,
                        cancellationToken,
                        m_ConnectionTimeoutSeconds))
                {
                    throw new TimeoutException("PurrNet client connection timed out.");
                }

                if (!await WaitForAdmissionStabilityAsync(manager, cancellationToken))
                {
                    StopOwnedNetwork();
                    return Fail(
                        "admission-rejected",
                        "The host did not admit this player. The room may be closed or full, " +
                        "or player authentication did not complete.");
                }

                string connectedStatus = m_Mode == PurrNetLobbyMode.RoomCode
                    ? AppendRoomRelayWarning($"Connected to {statusTarget}.")
                    : $"Connected to {statusTarget}.";
                SetConnected(sessionId, sessionName, connectedStatus);
                return NetworkLobbyOperationResult.Success(connectedStatus);
            }
            catch (OperationCanceledException)
            {
                StopOwnedNetwork();
                throw;
            }
            catch (Exception exception)
            {
                StopOwnedNetwork();
                return Fail("join-failed", $"Could not join the PurrNet session: {exception.Message}");
            }
        }

        private async Task<NetworkLobbyOperationResult> RefreshLanCoreAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken)
        {
            if (query.Topology != NetworkLobbyTopology.ClientServer)
            {
                ClearSessions();
                SetDisconnected("PurrNet LAN uses Client/Server topology.");
                return NetworkLobbyOperationResult.Success(
                    "No PurrNet LAN sessions match Shared topology.");
            }

            if (!TryOpenDiscovery(out string error))
                return Fail("lan-unavailable", error);

            m_LastQuery = query;
            m_DiscoveredHosts.Clear();
            ClearSessions();
            SetState(NetworkLobbyState.Browsing, "Looking for PurrNet LAN sessions...");

            if (!m_Discovery.SendQuery(out error))
                return Fail("lan-send", error);

            int delayMilliseconds = Mathf.RoundToInt(
                Mathf.Clamp(m_LanRefreshWindow, 0.1f, 3f) * 1000f);
            await Task.Delay(delayMilliseconds, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            PruneExpiredHosts(Time.realtimeSinceStartupAsDouble);
            PublishDiscoveredHosts(query);
            string message = Sessions.Count == 1
                ? "Found 1 PurrNet LAN session."
                : $"Found {Sessions.Count} PurrNet LAN sessions.";
            SetDisconnected(message);
            return NetworkLobbyOperationResult.Success(message);
        }

        private void HandleDiscoveryPacket(PurrNetLanPacket packet)
        {
            if (packet.Kind == PurrNetLanPacketKind.Query)
            {
                if (!m_IsHosting || !m_HostIsVisible) return;
                m_Discovery.Reply(BuildHostAdvertisement(), packet.Source, out _);
                return;
            }

            if (packet.Kind != PurrNetLanPacketKind.Advertisement) return;
            PurrNetLanAdvertisement advertisement = packet.Advertisement;
            if (advertisement.SessionId == Guid.Empty ||
                advertisement.SessionId == m_HostSessionId ||
                packet.Source == null ||
                packet.Source.Address == null)
            {
                return;
            }

            if (!m_DiscoveredHosts.ContainsKey(advertisement.SessionId) &&
                m_DiscoveredHosts.Count >= MaximumDiscoveredHosts)
            {
                Guid oldestId = Guid.Empty;
                double oldestSeen = double.MaxValue;
                foreach (KeyValuePair<Guid, DiscoveredHost> pair in m_DiscoveredHosts)
                {
                    if (pair.Value.LastSeen >= oldestSeen) continue;
                    oldestSeen = pair.Value.LastSeen;
                    oldestId = pair.Key;
                }

                if (oldestId != Guid.Empty) m_DiscoveredHosts.Remove(oldestId);
            }

            m_DiscoveredHosts[advertisement.SessionId] = new DiscoveredHost
            {
                Advertisement = advertisement,
                // Deliberately use the datagram source. The wire format has no
                // address field that an untrusted sender could spoof.
                SourceAddress = packet.Source.Address.ToString(),
                LastSeen = Time.realtimeSinceStartupAsDouble
            };
        }

        private void AdvertiseLanHost()
        {
            if (!m_IsHosting || !m_HostIsVisible || m_Discovery == null) return;
            if (m_Discovery.Broadcast(BuildHostAdvertisement(), out string error)) return;

            double now = Time.realtimeSinceStartupAsDouble;
            if (now < m_NextDiscoveryWarningAt) return;
            m_NextDiscoveryWarningAt = now + 5d;
            Debug.LogWarning($"[PurrNetLobbyService] {error}", this);
        }

        private PurrNetLanAdvertisement BuildHostAdvertisement()
        {
            int playerCount = GetLocalServerPlayerCount();
            return new PurrNetLanAdvertisement(
                m_HostSessionId,
                m_HostSessionName,
                Compatibility.ProductId,
                Compatibility.BuildId,
                Compatibility.ProtocolVersion,
                m_HostGamePort,
                playerCount,
                m_HostMaxPlayers,
                ShouldAdvertiseOpen(
                    m_HostAcceptingJoins,
                    playerCount,
                    m_HostMaxPlayers),
                m_HostIsVisible);
        }

        internal static bool ShouldAdvertiseOpen(
            bool acceptingJoins,
            int playerCount,
            int maxPlayers)
        {
            return acceptingJoins && maxPlayers > 0 && playerCount < maxPlayers;
        }

        private int GetLocalServerPlayerCount()
        {
            PurrNetworkManager manager = ActiveManager;
            if (manager == null || manager.serverState != PurrConnectionState.Connected)
                return 0;

            return Math.Max(1, manager.playerCount);
        }

        private bool PruneExpiredHosts(double now)
        {
            double ttl = Math.Max(1f, m_LanSessionTtl);
            List<Guid> expired = null;
            foreach (KeyValuePair<Guid, DiscoveredHost> pair in m_DiscoveredHosts)
            {
                if (now - pair.Value.LastSeen <= ttl) continue;
                expired ??= new List<Guid>();
                expired.Add(pair.Key);
            }

            if (expired == null) return false;
            for (int i = 0; i < expired.Count; i++)
                m_DiscoveredHosts.Remove(expired[i]);
            return true;
        }

        private void PublishDiscoveredHosts(NetworkLobbyQuery query)
        {
            var entries = new List<NetworkLobbyEntry>(m_DiscoveredHosts.Count);
            foreach (KeyValuePair<Guid, DiscoveredHost> pair in m_DiscoveredHosts)
            {
                DiscoveredHost discovered = pair.Value;
                PurrNetLanAdvertisement advertisement = discovered.Advertisement;
                if (!advertisement.IsVisible) continue;

                bool compatible = Compatibility.IsCompatible(
                    advertisement.ProductId,
                    advertisement.BuildId,
                    advertisement.ProtocolVersion,
                    out string reason);
                if (!compatible && !query.IncludeIncompatible) continue;

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [NetworkLobbyCompatibilityProfile.ProductKey] = advertisement.ProductId,
                    [NetworkLobbyCompatibilityProfile.BuildKey] = advertisement.BuildId,
                    [NetworkLobbyCompatibilityProfile.ProtocolKey] =
                        advertisement.ProtocolVersion.ToString(),
                    [NetworkLobbyCompatibilityProfile.DisplayNameKey] =
                        advertisement.SessionName,
                    [NetworkLobbyCompatibilityProfile.TopologyKey] =
                        NetworkLobbyTopology.ClientServer.ToString(),
                    ["transport"] = LanTransportMarker
                };

                entries.Add(new NetworkLobbyEntry(
                    $"lan:{pair.Key:N}",
                    advertisement.SessionName,
                    string.Empty,
                    "LAN",
                    NetworkLobbyTopology.ClientServer,
                    NetworkLobbyConnectionKind.Lan,
                    advertisement.PlayerCount,
                    advertisement.MaxPlayers,
                    advertisement.IsOpen,
                    advertisement.IsVisible,
                    compatible,
                    reason,
                    discovered.SourceAddress,
                    advertisement.GamePort,
                    metadata));
            }

            entries.Sort((left, right) =>
            {
                int name = string.Compare(
                    left.Name,
                    right.Name,
                    StringComparison.OrdinalIgnoreCase);
                return name != 0
                    ? name
                    : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
            });
            ReplaceSessions(entries);
        }

        private bool TryPrepareOperation(out string error)
        {
            if (!m_Initialized || m_InitializedMode != m_Mode)
            {
                if (!TryValidateConfiguration(out error)) return false;
                if (m_Mode == PurrNetLobbyMode.Lan && !TryOpenDiscovery(out error))
                    return false;
                m_Initialized = true;
                m_InitializedMode = m_Mode;
            }

            error = string.Empty;
            HookNetworkManager();
            return TryValidateConfiguration(out error);
        }

        private bool TryValidateConfiguration(out string error)
        {
            error = string.Empty;
            PurrNetworkManager manager = ActiveManager;
            if (manager == null)
            {
                error = "No PurrNet NetworkManager is assigned or active.";
                return false;
            }

            PurrGenericTransport transport = manager.transport;
            if (transport == null)
            {
                error = "The PurrNet NetworkManager has no transport assigned.";
                return false;
            }

            if (!transport.isSupported)
            {
                error = $"{transport.GetType().Name} is not supported on this platform.";
                return false;
            }

            if (m_Mode == PurrNetLobbyMode.RoomCode)
            {
                if (!(transport is PurrRoomTransport))
                {
                    error = "Room Code mode requires PurrNet's PurrTransport.";
                    return false;
                }
            }
            else if (!HasWritableEndpoint(transport))
            {
                error =
                    $"{transport.GetType().Name} does not expose writable address and " +
                    "serverPort properties required by Direct/LAN mode.";
                return false;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            if (m_Mode == PurrNetLobbyMode.Lan)
            {
                error = "PurrNet LAN discovery is unavailable in WebGL builds.";
                return false;
            }
#endif

            if (m_DefaultPort == 0 && m_Mode != PurrNetLobbyMode.RoomCode)
            {
                error = "The default PurrNet gameplay port must be greater than zero.";
                return false;
            }

            return true;
        }

        private bool TryOpenDiscovery(out string error)
        {
            error = string.Empty;
#if UNITY_WEBGL && !UNITY_EDITOR
            error = "PurrNet LAN discovery is unavailable in WebGL builds.";
            return false;
#else
            m_Discovery ??= new PurrNetLanDiscovery();
            return m_Discovery.Open(m_LanDiscoveryPort, out error);
#endif
        }

        private void CloseDiscovery()
        {
            m_Discovery?.Dispose();
            m_Discovery = null;
        }

        private bool EnsureManagerOffline(out string error)
        {
            error = string.Empty;
            PurrNetworkManager manager = ActiveManager;
            if (manager == null)
            {
                error = "No PurrNet NetworkManager is assigned or active.";
                return false;
            }

            if (manager.serverState != PurrConnectionState.Disconnected ||
                manager.clientState != PurrConnectionState.Disconnected)
            {
                error = "The PurrNet NetworkManager is already starting, connected, or stopping.";
                return false;
            }

            return true;
        }

        private bool IsNetworkActive()
        {
            PurrNetworkManager manager = ActiveManager;
            return manager != null &&
                   (manager.serverState != PurrConnectionState.Disconnected ||
                    manager.clientState != PurrConnectionState.Disconnected);
        }

        private void StopOwnedNetwork()
        {
            PurrNetworkManager manager = ActiveManager;
            m_IsHosting = false; // Stops advertisements before shutdown begins.
            m_SuppressConnectionCallbacks = true;
            try
            {
                if (manager != null && m_OwnsClient)
                {
                    // This also cancels NetworkManager's one-frame delayed
                    // StartClient coroutine while clientState is still Disconnected.
                    manager.StopClient();
                }

                if (manager != null &&
                    m_OwnsServer &&
                    manager.serverState != PurrConnectionState.Disconnected)
                {
                    manager.StopServer();
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                m_OwnsClient = false;
                m_OwnsServer = false;
                m_SuppressConnectionCallbacks = false;
                m_HostSessionId = Guid.Empty;
                m_HostSessionName = string.Empty;
                m_CurrentMaxPlayers = 0;
                m_LocalPlayerName = string.Empty;
                m_HostAcceptingJoins = true;
                m_HostGamePort = 0;
                m_PendingCapacityKicks.Clear();
            }
        }

        private async Task<bool> WaitForStateAsync(
            PurrNetworkManager manager,
            bool asServer,
            PurrConnectionState desiredState,
            CancellationToken cancellationToken,
            float timeoutSeconds)
        {
            double deadline = Time.realtimeSinceStartupAsDouble +
                              Math.Max(1f, timeoutSeconds);
            while (manager != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PurrConnectionState state = asServer
                    ? manager.serverState
                    : manager.clientState;
                if (state == desiredState) return true;
                if (Time.realtimeSinceStartupAsDouble >= deadline) return false;
                await Task.Delay(25, cancellationToken);
            }

            return false;
        }

        private async Task<bool> WaitForAdmissionStabilityAsync(
            PurrNetworkManager manager,
            CancellationToken cancellationToken)
        {
            double playerDeadline = Time.realtimeSinceStartupAsDouble +
                                    Math.Max(1f, m_ConnectionTimeoutSeconds);
            while (manager != null &&
                   manager.clientState == PurrConnectionState.Connected &&
                   !manager.isLocalPlayerReady)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartupAsDouble >= playerDeadline) return false;
                await Task.Delay(25, cancellationToken);
            }

            if (manager == null ||
                manager.clientState != PurrConnectionState.Connected ||
                !manager.isLocalPlayerReady)
            {
                return false;
            }

            double stabilityDeadline = Time.realtimeSinceStartupAsDouble +
                                       Math.Max(0.1f, m_AdmissionStabilitySeconds);
            while (manager.clientState == PurrConnectionState.Connected &&
                   Time.realtimeSinceStartupAsDouble < stabilityDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);
            }

            return manager != null &&
                   manager.clientState == PurrConnectionState.Connected &&
                   manager.isLocalPlayerReady;
        }

        private static async Task WaitForOfflineAsync(
            PurrNetworkManager manager,
            CancellationToken cancellationToken,
            float timeoutSeconds)
        {
            double deadline = Time.realtimeSinceStartupAsDouble +
                              Math.Max(0.25f, timeoutSeconds);
            while (manager != null &&
                   (manager.serverState != PurrConnectionState.Disconnected ||
                    manager.clientState != PurrConnectionState.Disconnected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartupAsDouble >= deadline) return;
                await Task.Delay(25, cancellationToken);
            }
        }

        private void HookNetworkManager()
        {
            PurrNetworkManager manager = ActiveManager;
            if (m_HookedManager == manager) return;
            UnhookNetworkManager();
            m_HookedManager = manager;
            if (m_HookedManager == null) return;
            m_HookedManager.onServerConnectionState += HandleServerState;
            m_HookedManager.onClientConnectionState += HandleClientState;
            m_HookedManager.onPlayerJoined += HandlePlayerJoined;
        }

        private void UnhookNetworkManager()
        {
            if (m_HookedManager == null) return;
            m_HookedManager.onServerConnectionState -= HandleServerState;
            m_HookedManager.onClientConnectionState -= HandleClientState;
            m_HookedManager.onPlayerJoined -= HandlePlayerJoined;
            m_HookedManager = null;
        }

        private void HandlePlayerJoined(PurrPlayerId player, bool isReconnect, bool asServer)
        {
            PurrNetworkManager manager = ActiveManager;
            if (!asServer ||
                manager == null ||
                !manager.isServer ||
                !m_OwnsServer ||
                !WillRejectPlayerForCapacity(manager.players.Count, isReconnect))
            {
                return;
            }

            PurrPlayerId target = player;
            if (IsLocalHostPlayer(manager, target) &&
                !TryFindRemoteOverflowPlayer(manager, out target))
            {
                return;
            }

            if (m_PendingCapacityKicks.Add(target))
                StartCoroutine(KickCapacityOverflowRoutine(target));
        }

        private IEnumerator KickCapacityOverflowRoutine(PurrPlayerId player)
        {
            // PlayersManager raises onPlayerJoined while updating its own
            // collections. Defer the kick to avoid re-entrant mutation.
            yield return null;

            PurrNetworkManager manager = ActiveManager;
            if (!m_PendingCapacityKicks.Contains(player) ||
                manager == null ||
                !manager.isServer ||
                !m_OwnsServer ||
                !ShouldRejectCapacityOverflow(
                    manager.players.Count,
                    m_CurrentMaxPlayers,
                    false))
            {
                m_PendingCapacityKicks.Remove(player);
                yield break;
            }

            if (IsLocalHostPlayer(manager, player))
            {
                if (!TryFindRemoteOverflowPlayer(manager, out PurrPlayerId replacement))
                {
                    m_PendingCapacityKicks.Remove(player);
                    yield break;
                }

                m_PendingCapacityKicks.Remove(player);
                player = replacement;
                if (!m_PendingCapacityKicks.Add(player)) yield break;
            }

            if (manager.TryGetModule(out PurrPlayersManager players, true) &&
                players.IsValidPlayer(player))
            {
                players.KickPlayer(player);
            }

            m_PendingCapacityKicks.Remove(player);
        }

        private bool TryFindRemoteOverflowPlayer(
            PurrNetworkManager manager,
            out PurrPlayerId player)
        {
            IReadOnlyList<PurrPlayerId> connected = manager.players;
            for (int i = connected.Count - 1; i >= 0; i--)
            {
                PurrPlayerId candidate = connected[i];
                if (IsLocalHostPlayer(manager, candidate) ||
                    m_PendingCapacityKicks.Contains(candidate))
                {
                    continue;
                }

                player = candidate;
                return true;
            }

            player = default;
            return false;
        }

        private static bool IsLocalHostPlayer(PurrNetworkManager manager, PurrPlayerId player)
        {
            return manager != null &&
                   manager.isHost &&
                   manager.isLocalPlayerReady &&
                   manager.localPlayer == player;
        }

        internal static bool ShouldRejectCapacityOverflow(
            int connectedPlayers,
            int maximumPlayers,
            bool isReconnect)
        {
            // A reconnect bypasses a staging room's post-start admission lock,
            // but it cannot exceed the session's advertised hard capacity after
            // another player has filled the vacated slot.
            return maximumPlayers > 0 &&
                   connectedPlayers > maximumPlayers;
        }

        internal bool WillRejectPlayerForCapacity(int connectedPlayers, bool isReconnect)
        {
            return m_OwnsServer &&
                   ShouldRejectCapacityOverflow(
                       connectedPlayers,
                       m_CurrentMaxPlayers,
                       isReconnect);
        }

        private void HandleServerState(PurrConnectionState state)
        {
            if (m_SuppressConnectionCallbacks ||
                state != PurrConnectionState.Disconnected ||
                !m_IsHosting)
            {
                return;
            }

            StopOwnedNetwork();
            SetDisconnected("The PurrNet host stopped.");
        }

        private void HandleClientState(PurrConnectionState state)
        {
            if (m_SuppressConnectionCallbacks ||
                state != PurrConnectionState.Disconnected ||
                !m_OwnsClient ||
                State != NetworkLobbyState.Connected)
            {
                return;
            }

            bool wasHost = m_IsHosting;
            StopOwnedNetwork();
            SetDisconnected(wasHost
                ? "The host's local PurrNet client disconnected; hosting stopped."
                : "Disconnected from the PurrNet session.");
        }

        private bool TryBeginOperation(
            CancellationToken externalCancellation,
            out CancellationTokenSource operation,
            out NetworkLobbyOperationResult busy)
        {
            EnsureLifetimeCancellation();
            lock (m_OperationLock)
            {
                if (m_ActiveOperation != null)
                {
                    operation = null;
                    busy = NetworkLobbyOperationResult.Failure(
                        "busy",
                        "Another lobby operation is already in progress.");
                    return false;
                }

                operation = CancellationTokenSource.CreateLinkedTokenSource(
                    externalCancellation,
                    m_LifetimeCancellation.Token);
                m_ActiveOperation = operation;
            }

            busy = default;
            return true;
        }

        private void EndOperation(CancellationTokenSource operation)
        {
            if (operation == null) return;
            lock (m_OperationLock)
            {
                if (ReferenceEquals(m_ActiveOperation, operation))
                    m_ActiveOperation = null;
            }
            operation.Dispose();
        }

        private void EnsureLifetimeCancellation()
        {
            if (m_LifetimeCancellation != null &&
                !m_LifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            m_LifetimeCancellation?.Dispose();
            m_LifetimeCancellation = new CancellationTokenSource();
        }

        private void CancelLifetimeOperations()
        {
            CancellationTokenSource lifetime = m_LifetimeCancellation;
            m_LifetimeCancellation = null;
            if (lifetime == null) return;
            try
            {
                lifetime.Cancel();
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        private string GetReadyStatus(string prefix = "")
        {
            return m_Mode == PurrNetLobbyMode.RoomCode
                ? AppendRoomRelayWarning(prefix + "Room-code service ready.")
                : prefix + ServiceName + " is ready.";
        }

        private string AppendRoomRelayWarning(string status)
        {
            PurrNetworkManager manager = ActiveManager;
            if (!(manager?.transport is PurrRoomTransport roomTransport)) return status;
            string server = roomTransport.masterServer ?? string.Empty;
            return server.IndexOf(DefaultRoomRelayHost, StringComparison.OrdinalIgnoreCase) >= 0
                ? status + " " + DefaultRelayProductionWarning
                : status;
        }

        private static string NormalizeSessionName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value)
                ? Application.productName
                : value.Trim();
            if (string.IsNullOrWhiteSpace(name)) name = "PurrNet Game";
            return name.Length <= 64 ? name : name.Substring(0, 64);
        }

        private static string NormalizeRoomCode(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().ToUpperInvariant();
        }

        private static bool TryValidateRoomCode(string roomCode, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(roomCode))
            {
                error = "Enter a PurrTransport room code.";
                return false;
            }

            if (roomCode.Length > 64)
            {
                error = "Room codes are limited to 64 characters.";
                return false;
            }

            for (int i = 0; i < roomCode.Length; i++)
            {
                char character = roomCode[i];
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_')
                    continue;
                error = "Room codes may contain only letters, numbers, '-' and '_'.";
                return false;
            }

            return true;
        }

        private static string GenerateRoomCode()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
        }

        private static bool TryValidateAddress(string address, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(address))
            {
                error = "Enter a host address or IP.";
                return false;
            }

            if (address.Length > 255)
            {
                error = "The host address is too long.";
                return false;
            }

            for (int i = 0; i < address.Length; i++)
            {
                if (!char.IsControl(address[i])) continue;
                error = "The host address contains invalid control characters.";
                return false;
            }

            return true;
        }

        private static bool HasWritableEndpoint(PurrGenericTransport transport)
        {
            if (transport == null) return false;
            Type type = transport.GetType();
            PropertyInfo address = type.GetProperty(
                "address",
                BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo port = type.GetProperty(
                "serverPort",
                BindingFlags.Public | BindingFlags.Instance);
            return address != null && address.CanWrite &&
                   address.PropertyType == typeof(string) &&
                   port != null && port.CanWrite &&
                   (port.PropertyType == typeof(ushort) || port.PropertyType == typeof(int));
        }

        private static bool TryConfigureEndpoint(
            PurrGenericTransport transport,
            string address,
            ushort port,
            out string error)
        {
            error = string.Empty;
            if (!HasWritableEndpoint(transport))
            {
                error =
                    $"{transport?.GetType().Name ?? "The transport"} does not expose " +
                    "writable address and serverPort properties.";
                return false;
            }

            try
            {
                Type type = transport.GetType();
                type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance)
                    ?.SetValue(transport, address);
                PropertyInfo portProperty = type.GetProperty(
                    "serverPort",
                    BindingFlags.Public | BindingFlags.Instance);
                object portValue = portProperty?.PropertyType == typeof(int)
                    ? (object)(int)port
                    : port;
                portProperty?.SetValue(transport, portValue);
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not configure the PurrNet transport: {exception.Message}";
                return false;
            }
        }

        private static void TryConfigureMaximumPlayers(
            PurrGenericTransport transport,
            int maximumPlayers)
        {
            if (transport == null) return;
            PropertyInfo property = transport.GetType().GetProperty(
                "maxConnections",
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite) return;

            try
            {
                int value = Math.Max(1, maximumPlayers);
                if (property.PropertyType == typeof(int)) property.SetValue(transport, value);
                else if (property.PropertyType == typeof(ushort))
                    property.SetValue(transport, (ushort)Math.Min(ushort.MaxValue, value));
            }
            catch (Exception)
            {
                // The service-side authoritative overflow guard still enforces the
                // requested capacity when a transport property cannot be assigned.
            }
        }
    }
}
