using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Lobby;
using Fusion;
using Fusion.Photon.Realtime;
using Fusion.Sockets;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Photon-backed discovery and matchmaking for Fusion. A temporary, explicitly marked
    /// runner listens for session lists; it is always shut down before the gameplay bootstrap
    /// creates its single-use runner.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Lobby/Fusion Lobby Service")]
    [DisallowMultipleComponent]
    public sealed class FusionLobbyService : NetworkLobbyServiceBehaviour, INetworkRunnerCallbacks
    {
        [SerializeField] private FusionSessionBootstrap m_SessionBootstrap;
        [Tooltip("Optional bare runner prefab. It must already include FusionLobbyDiscoveryRunnerMarker so its Awake callbacks cannot be mistaken for gameplay.")]
        [SerializeField] private NetworkRunner m_DiscoveryRunnerPrefab;
        [SerializeField] private NetworkLobbyCompatibilityProfile m_Compatibility =
            new NetworkLobbyCompatibilityProfile();
        [Tooltip("Optional Photon custom lobby shared by discovery and gameplay sessions.")]
        [SerializeField] private string m_CustomLobbyName = "gc2-networking";
        [Tooltip("How long Refresh waits for Photon's first session-list snapshot after connecting.")]
        [Min(1f)]
        [SerializeField] private float m_SessionListTimeoutSeconds = 8f;
        [SerializeField] private bool m_UseCachedBestRegion = true;
        [SerializeField] private bool m_DontDestroyDiscoveryRunnerOnLoad = true;

        private NetworkRunner m_DiscoveryRunner;
        private Task m_DiscoveryShutdownTask;
        private int m_DiscoveryGeneration;
        private NetworkLobbyQuery m_ActiveQuery;
        private TaskCompletionSource<bool> m_SessionListCompletion;
        private CancellationTokenSource m_LifetimeCancellation;
        private CancellationTokenSource m_OperationCancellation;
        private int m_OperationGeneration;
        private bool m_OperationInProgress;
        private bool m_Destroying;

        public override string ServiceName => "Fusion / Photon";

        public override NetworkLobbyCapabilities Capabilities =>
            NetworkLobbyCapabilities.Create |
            NetworkLobbyCapabilities.QuickJoin |
            NetworkLobbyCapabilities.JoinByCode |
            NetworkLobbyCapabilities.Browse |
            NetworkLobbyCapabilities.Refresh |
            NetworkLobbyCapabilities.RegionSelection |
            NetworkLobbyCapabilities.TopologySelection |
            NetworkLobbyCapabilities.PlayerCapacity |
            NetworkLobbyCapabilities.Visibility;

        public NetworkRunner DiscoveryRunner => m_DiscoveryRunner;
        public FusionSessionBootstrap SessionBootstrap => m_SessionBootstrap;
        public string CustomLobbyName => NormalizeLobbyName(m_CustomLobbyName);

        private void Awake()
        {
            m_LifetimeCancellation = new CancellationTokenSource();
            ResolveBootstrap();
        }

        private void OnEnable()
        {
            SubscribeBootstrap();
        }

        private void OnDisable()
        {
            UnsubscribeBootstrap();
        }

        private async void OnDestroy()
        {
            m_Destroying = true;
            UnsubscribeBootstrap();
            m_OperationGeneration++;
            m_OperationCancellation?.Cancel();
            m_LifetimeCancellation?.Cancel();
            try
            {
                await ShutdownDiscoveryRunnerAsync();
            }
            catch (Exception)
            {
                // Unity is destroying this owner; cleanup is best-effort.
            }
            finally
            {
                m_OperationCancellation?.Dispose();
                m_LifetimeCancellation?.Dispose();
            }
        }

        public override Task<NetworkLobbyOperationResult> InitializeAsync(
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(CancelledResult());
            }

            if (m_Destroying)
            {
                return Task.FromResult(NetworkLobbyOperationResult.Failure(
                    "destroyed",
                    "The Fusion lobby service is being destroyed."));
            }

            ResolveBootstrap();
            if (m_SessionBootstrap == null)
            {
                return Task.FromResult(Fail(
                    "missing_bootstrap",
                    "Assign a Fusion Session Bootstrap before using the lobby service."));
            }

            if (m_OperationInProgress)
            {
                return Task.FromResult(BusyResult());
            }

            if (HasGameplayRunner())
            {
                return Task.FromResult(NetworkLobbyOperationResult.Success(
                    "A Fusion gameplay session is already active."));
            }

            SubscribeBootstrap();
            SetDisconnected("Ready");
            return Task.FromResult(NetworkLobbyOperationResult.Success("Fusion lobby is ready."));
        }

        public override async Task<NetworkLobbyOperationResult> RefreshAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(cancellationToken, false, out int generation, out CancellationToken token))
            {
                return BusyResult();
            }

            try
            {
                return await RefreshCoreAsync(query, generation, token);
            }
            catch (OperationCanceledException)
            {
                return CompleteCancellation(generation);
            }
            catch (Exception exception)
            {
                return CompleteException(generation, "refresh_failed", exception);
            }
            finally
            {
                EndOperation(generation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> CreateAsync(
            NetworkLobbyCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(cancellationToken, false, out int generation, out CancellationToken token))
            {
                return BusyResult();
            }

            try
            {
                ResolveBootstrap();
                if (m_SessionBootstrap == null)
                {
                    return FailIfCurrent(
                        generation,
                        "missing_bootstrap",
                        "Assign a Fusion Session Bootstrap before creating a session.");
                }
                if (HasGameplayRunner())
                {
                    return NetworkLobbyOperationResult.Failure(
                        "already_connected",
                        "Leave the current Fusion session before creating another one.");
                }

                string sessionName = ResolveCreateSessionName(request);
                string displayName = string.IsNullOrWhiteSpace(request.SessionName)
                    ? sessionName
                    : request.SessionName.Trim();
                string region = NormalizeRegion(request.Region);

                SetStateIfCurrent(generation, NetworkLobbyState.Creating, "Creating Fusion session...");
                await ShutdownDiscoveryRunnerAsync();
                token.ThrowIfCancellationRequested();

                Dictionary<string, SessionProperty> properties =
                    BuildSessionProperties(displayName, request.Topology);
                var options = new FusionSessionStartOptions(
                    sessionName,
                    region,
                    forcePhotonRelay: m_SessionBootstrap.ForcePhotonRelay,
                    isOpen: true,
                    isVisible: request.IsVisible,
                    customLobbyName: CustomLobbyName,
                    sessionProperties: properties,
                    maxPlayers: request.MaxPlayers);

                Task<StartGameResult> startTask = request.Topology == NetworkLobbyTopology.Shared
                    ? m_SessionBootstrap.CreateSharedAsync(options)
                    : m_SessionBootstrap.StartHostAsync(options);
                StartGameResult result = await AwaitStartWithCancellationAsync(startTask, token);
                if (!result.Ok)
                {
                    return FailIfCurrent(
                        generation,
                        "create_failed",
                        FormatStartFailure("Could not create the Fusion session", result));
                }

                if (IsCurrent(generation))
                {
                    SetConnected(sessionName, displayName, $"Hosting {displayName}");
                }
                return NetworkLobbyOperationResult.Success("Fusion session created.");
            }
            catch (OperationCanceledException)
            {
                return CompleteCancellation(generation);
            }
            catch (Exception exception)
            {
                return CompleteException(generation, "create_failed", exception);
            }
            finally
            {
                EndOperation(generation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> QuickJoinAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(cancellationToken, false, out int generation, out CancellationToken token))
            {
                return BusyResult();
            }

            try
            {
                NetworkLobbyOperationResult refresh = await RefreshCoreAsync(query, generation, token);
                if (!refresh.Succeeded) return refresh;

                NetworkLobbyEntry candidate = FindQuickJoinCandidate();
                if (candidate == null)
                {
                    SetStateIfCurrent(generation, NetworkLobbyState.Offline, "No joinable sessions found");
                    return NetworkLobbyOperationResult.Failure(
                        "not_found",
                        "No compatible, open Fusion session is currently available.");
                }

                return await JoinCoreAsync(
                    new NetworkLobbyJoinRequest(
                        candidate,
                        candidate.JoinCode,
                        string.Empty,
                        0,
                        candidate.Region,
                        candidate.Topology,
                        query.PlayerName),
                    generation,
                    token);
            }
            catch (OperationCanceledException)
            {
                return CompleteCancellation(generation);
            }
            catch (Exception exception)
            {
                return CompleteException(generation, "quick_join_failed", exception);
            }
            finally
            {
                EndOperation(generation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> JoinAsync(
            NetworkLobbyJoinRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!TryBeginOperation(cancellationToken, false, out int generation, out CancellationToken token))
            {
                return BusyResult();
            }

            try
            {
                return await JoinCoreAsync(request, generation, token);
            }
            catch (OperationCanceledException)
            {
                return CompleteCancellation(generation);
            }
            catch (Exception exception)
            {
                return CompleteException(generation, "join_failed", exception);
            }
            finally
            {
                EndOperation(generation);
            }
        }

        public override async Task<NetworkLobbyOperationResult> LeaveAsync(
            CancellationToken cancellationToken = default)
        {
            // Leave is the one operation allowed to supersede an in-flight refresh/start.
            if (!TryBeginOperation(cancellationToken, true, out int generation, out CancellationToken token))
            {
                return BusyResult();
            }

            try
            {
                SetStateIfCurrent(generation, NetworkLobbyState.Leaving, "Leaving Fusion session...");
                await ShutdownDiscoveryRunnerAsync();
                token.ThrowIfCancellationRequested();

                ResolveBootstrap();
                if (m_SessionBootstrap != null &&
                    (m_SessionBootstrap.Runner != null || m_SessionBootstrap.IsStarting))
                {
                    await m_SessionBootstrap.ShutdownAsync();
                }

                if (IsCurrent(generation))
                {
                    ClearSessions();
                    SetDisconnected("Ready");
                }
                return NetworkLobbyOperationResult.Success("Fusion session left.");
            }
            catch (OperationCanceledException)
            {
                return CompleteCancellation(generation);
            }
            catch (Exception exception)
            {
                return CompleteException(generation, "leave_failed", exception);
            }
            finally
            {
                EndOperation(generation);
            }
        }

        private async Task<NetworkLobbyOperationResult> RefreshCoreAsync(
            NetworkLobbyQuery query,
            int generation,
            CancellationToken token)
        {
            ResolveBootstrap();
            if (HasGameplayRunner())
            {
                return NetworkLobbyOperationResult.Failure(
                    "already_connected",
                    "Leave the current Fusion session before browsing matchmaking.");
            }

            SetStateIfCurrent(generation, NetworkLobbyState.Browsing, "Refreshing Fusion sessions...");
            await ShutdownDiscoveryRunnerAsync();
            token.ThrowIfCancellationRequested();

            m_ActiveQuery = new NetworkLobbyQuery(
                NormalizeRegion(query.Region),
                query.Topology,
                query.IncludeIncompatible,
                query.PlayerName);
            m_DiscoveryGeneration++;
            int discoveryGeneration = m_DiscoveryGeneration;
            m_SessionListCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            NetworkRunner runner = CreateDiscoveryRunner(discoveryGeneration);
            m_DiscoveryRunner = runner;
            bool retainDiscoveryRunner = false;
            try
            {
                FusionAppSettings appSettings = CreatePhotonAppSettings(m_ActiveQuery.Region);
                SessionLobby lobby = m_ActiveQuery.Topology == NetworkLobbyTopology.Shared
                    ? SessionLobby.Shared
                    : SessionLobby.ClientServer;
                StartGameResult result = await runner.JoinSessionLobby(
                    lobby,
                    CustomLobbyName,
                    null,
                    appSettings,
                    token,
                    m_UseCachedBestRegion && string.IsNullOrEmpty(m_ActiveQuery.Region));

                if (!IsCurrent(generation) ||
                    runner != m_DiscoveryRunner ||
                    discoveryGeneration != m_DiscoveryGeneration)
                {
                    throw new OperationCanceledException(token);
                }

                if (!result.Ok)
                {
                    return FailIfCurrent(
                        generation,
                        "lobby_connection_failed",
                        FormatStartFailure("Could not connect to the Photon session lobby", result));
                }

                await WaitForFirstSessionListAsync(generation, discoveryGeneration, token);
                token.ThrowIfCancellationRequested();
                int count = Sessions.Count;
                SetStateIfCurrent(
                    generation,
                    NetworkLobbyState.Offline,
                    count == 0 ? "No matching sessions" : $"Found {count} session{(count == 1 ? string.Empty : "s")}");
                retainDiscoveryRunner = true;
                return NetworkLobbyOperationResult.Success("Fusion session list refreshed.");
            }
            finally
            {
                if (!retainDiscoveryRunner && runner == m_DiscoveryRunner)
                {
                    await ShutdownDiscoveryRunnerAsync();
                }
            }
        }

        private async Task<NetworkLobbyOperationResult> JoinCoreAsync(
            NetworkLobbyJoinRequest request,
            int generation,
            CancellationToken token)
        {
            ResolveBootstrap();
            if (m_SessionBootstrap == null)
            {
                return FailIfCurrent(
                    generation,
                    "missing_bootstrap",
                    "Assign a Fusion Session Bootstrap before joining a session.");
            }
            if (HasGameplayRunner())
            {
                return NetworkLobbyOperationResult.Failure(
                    "already_connected",
                    "Leave the current Fusion session before joining another one.");
            }

            NetworkLobbyEntry entry = request.Entry;
            if (entry != null && !entry.CanJoin)
            {
                string reason = entry.IsCompatible
                    ? entry.IsFull ? "The selected session is full." : "The selected session is closed."
                    : string.IsNullOrEmpty(entry.CompatibilityMessage)
                        ? "The selected session is incompatible."
                        : entry.CompatibilityMessage;
                return FailIfCurrent(generation, "session_unavailable", reason);
            }

            string sessionName = FirstNonEmpty(
                request.JoinCode,
                entry?.JoinCode,
                entry?.Id);
            if (string.IsNullOrWhiteSpace(sessionName))
            {
                return FailIfCurrent(
                    generation,
                    "missing_join_code",
                    "Enter a Fusion session code or select a session first.");
            }
            sessionName = sessionName.Trim();

            NetworkLobbyTopology topology = entry?.Topology ?? request.Topology;
            string region = NormalizeRegion(FirstNonEmpty(request.Region, entry?.Region));
            string displayName = string.IsNullOrWhiteSpace(entry?.Name)
                ? sessionName
                : entry.Name;

            SetStateIfCurrent(generation, NetworkLobbyState.Joining, $"Joining {displayName}...");
            await ShutdownDiscoveryRunnerAsync();
            token.ThrowIfCancellationRequested();

            var options = new FusionSessionStartOptions(
                sessionName,
                region,
                forcePhotonRelay: m_SessionBootstrap.ForcePhotonRelay,
                customLobbyName: CustomLobbyName,
                maxPlayers: entry != null && entry.MaxPlayers > 0
                    ? entry.MaxPlayers
                    : (int?)null);
            Task<StartGameResult> startTask = topology == NetworkLobbyTopology.Shared
                ? m_SessionBootstrap.JoinSharedAsync(options)
                : m_SessionBootstrap.JoinHostAsync(options);
            StartGameResult result = await AwaitStartWithCancellationAsync(startTask, token);
            if (!result.Ok)
            {
                return FailIfCurrent(
                    generation,
                    "join_failed",
                    FormatStartFailure("Could not join the Fusion session", result));
            }

            if (IsCurrent(generation))
            {
                SetConnected(sessionName, displayName, $"Connected to {displayName}");
            }
            return NetworkLobbyOperationResult.Success("Fusion session joined.");
        }

        private NetworkRunner CreateDiscoveryRunner(int generation)
        {
            NetworkRunner runner;
            if (m_DiscoveryRunnerPrefab != null &&
                FusionLobbyDiscoveryRunnerMarker.IsDiscoveryRunner(m_DiscoveryRunnerPrefab))
            {
                runner = Instantiate(m_DiscoveryRunnerPrefab);
                runner.name = "Arawn Fusion Lobby Discovery Runner";
            }
            else
            {
                if (m_DiscoveryRunnerPrefab != null)
                {
                    Debug.LogWarning(
                        "[FusionLobby] The discovery runner prefab has no " +
                        $"{nameof(FusionLobbyDiscoveryRunnerMarker)}. A safe bare runner was created instead.",
                        this);
                }
                var runnerObject = new GameObject("Arawn Fusion Lobby Discovery Runner");
                runner = runnerObject.AddComponent<NetworkRunner>();
            }

            FusionLobbyDiscoveryRunnerMarker marker =
                runner.GetComponent<FusionLobbyDiscoveryRunnerMarker>() ??
                runner.gameObject.AddComponent<FusionLobbyDiscoveryRunnerMarker>();
            marker.Initialize(generation);
            runner.ProvideInput = false;
            runner.AddCallbacks(this);
            if (m_DontDestroyDiscoveryRunnerOnLoad)
            {
                DontDestroyOnLoad(runner.gameObject);
            }
            return runner;
        }

        private Task ShutdownDiscoveryRunnerAsync()
        {
            if (m_DiscoveryShutdownTask != null && !m_DiscoveryShutdownTask.IsCompleted)
            {
                return m_DiscoveryShutdownTask;
            }

            NetworkRunner runner = m_DiscoveryRunner;
            m_DiscoveryRunner = null;
            m_DiscoveryGeneration++;
            m_SessionListCompletion?.TrySetCanceled();
            m_SessionListCompletion = null;
            if (runner == null) return Task.CompletedTask;

            runner.RemoveCallbacks(this);
            m_DiscoveryShutdownTask = ShutdownDiscoveryRunnerCoreAsync(runner);
            return m_DiscoveryShutdownTask;
        }

        private async Task ShutdownDiscoveryRunnerCoreAsync(NetworkRunner runner)
        {
            try
            {
                if (!runner.IsShutdown)
                {
                    await runner.Shutdown();
                }
            }
            finally
            {
                if (runner != null) Destroy(runner.gameObject);
            }
        }

        private async Task WaitForFirstSessionListAsync(
            int operationGeneration,
            int discoveryGeneration,
            CancellationToken token)
        {
            Task listTask = m_SessionListCompletion?.Task ?? Task.CompletedTask;
            int milliseconds = Mathf.Max(1, Mathf.CeilToInt(m_SessionListTimeoutSeconds * 1000f));
            Task timeoutTask = Task.Delay(milliseconds, token);
            Task completed = await Task.WhenAny(listTask, timeoutTask);
            token.ThrowIfCancellationRequested();

            if (!IsCurrent(operationGeneration) || discoveryGeneration != m_DiscoveryGeneration)
            {
                throw new OperationCanceledException(token);
            }

            if (completed == listTask)
            {
                await listTask;
            }
            // A successful lobby connection remains useful when Photon has not emitted a
            // snapshot yet. The runner stays subscribed and will update the immutable list later.
        }

        private async Task<StartGameResult> AwaitStartWithCancellationAsync(
            Task<StartGameResult> startTask,
            CancellationToken token)
        {
            if (!token.CanBeCanceled) return await startTask;

            var cancellationCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancellationCompletion.TrySetResult(true)))
            {
                Task completed = await Task.WhenAny(startTask, cancellationCompletion.Task);
                if (completed == startTask) return await startTask;
            }

            // Fusion runners are single-use. Ask the bootstrap to finish/cancel its pending
            // start and dispose the runner before propagating cancellation.
            if (m_SessionBootstrap != null)
            {
                await m_SessionBootstrap.ShutdownAsync();
            }
            token.ThrowIfCancellationRequested();
            throw new OperationCanceledException(token);
        }

        private Dictionary<string, SessionProperty> BuildSessionProperties(
            string displayName,
            NetworkLobbyTopology topology)
        {
            NetworkLobbyCompatibilityProfile compatibility = Compatibility;
            return new Dictionary<string, SessionProperty>
            {
                [NetworkLobbyCompatibilityProfile.ProductKey] =
                    SessionProperty.Convert(compatibility.ProductId),
                [NetworkLobbyCompatibilityProfile.BuildKey] =
                    SessionProperty.Convert(compatibility.BuildId),
                [NetworkLobbyCompatibilityProfile.ProtocolKey] =
                    SessionProperty.Convert(compatibility.ProtocolVersion),
                [NetworkLobbyCompatibilityProfile.DisplayNameKey] =
                    SessionProperty.Convert(displayName ?? string.Empty),
                [NetworkLobbyCompatibilityProfile.TopologyKey] =
                    SessionProperty.Convert(TopologyValue(topology))
            };
        }

        private List<NetworkLobbyEntry> MapSessions(List<SessionInfo> sessions)
        {
            var mapped = new List<NetworkLobbyEntry>(sessions?.Count ?? 0);
            if (sessions == null) return mapped;

            for (int i = 0; i < sessions.Count; i++)
            {
                SessionInfo session = sessions[i];
                if (session == null || string.IsNullOrWhiteSpace(session.Name)) continue;

                Dictionary<string, string> metadata = CopyMetadata(session.Properties);
                string product = GetMetadata(metadata, NetworkLobbyCompatibilityProfile.ProductKey);
                string build = GetMetadata(metadata, NetworkLobbyCompatibilityProfile.BuildKey);
                int protocol = ParseProtocol(
                    GetMetadata(metadata, NetworkLobbyCompatibilityProfile.ProtocolKey));
                bool compatible = Compatibility.IsCompatible(product, build, protocol, out string reason);

                string topologyMetadata =
                    GetMetadata(metadata, NetworkLobbyCompatibilityProfile.TopologyKey);
                if (!TryParseTopology(topologyMetadata, out NetworkLobbyTopology topology))
                {
                    topology = m_ActiveQuery.Topology;
                    compatible = false;
                    reason = "Missing or invalid network topology";
                }
                else if (topology != m_ActiveQuery.Topology)
                {
                    compatible = false;
                    reason = "Different network topology";
                }

                if (!string.IsNullOrEmpty(m_ActiveQuery.Region) &&
                    !string.Equals(
                        NormalizeRegion(session.Region),
                        m_ActiveQuery.Region,
                        StringComparison.Ordinal))
                {
                    compatible = false;
                    reason = "Different Photon region";
                }

                if (!compatible && !m_ActiveQuery.IncludeIncompatible) continue;

                string displayName = GetMetadata(
                    metadata,
                    NetworkLobbyCompatibilityProfile.DisplayNameKey);
                if (string.IsNullOrWhiteSpace(displayName)) displayName = session.Name;

                mapped.Add(new NetworkLobbyEntry(
                    session.Name,
                    displayName,
                    session.Name,
                    session.Region,
                    topology,
                    NetworkLobbyConnectionKind.Cloud,
                    session.PlayerCount,
                    session.MaxPlayers,
                    session.IsOpen,
                    session.IsVisible,
                    compatible,
                    reason,
                    metadata: metadata));
            }

            mapped.Sort(CompareEntries);
            return mapped;
        }

        private static Dictionary<string, string> CopyMetadata(
            IReadOnlyDictionary<string, SessionProperty> properties)
        {
            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (properties == null) return metadata;
            foreach (KeyValuePair<string, SessionProperty> pair in properties)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null) continue;
                object value = pair.Value.PropertyValue;
                metadata[pair.Key] = value == null
                    ? string.Empty
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            return metadata;
        }

        private NetworkLobbyEntry FindQuickJoinCandidate()
        {
            NetworkLobbyEntry best = null;
            for (int i = 0; i < Sessions.Count; i++)
            {
                NetworkLobbyEntry candidate = Sessions[i];
                if (candidate == null || !candidate.CanJoin) continue;
                if (best == null || candidate.PlayerCount > best.PlayerCount)
                {
                    best = candidate;
                }
            }
            return best;
        }

        private bool TryBeginOperation(
            CancellationToken externalToken,
            bool supersede,
            out int generation,
            out CancellationToken token)
        {
            generation = 0;
            token = externalToken;
            if (m_Destroying) return false;
            if (m_OperationInProgress && !supersede) return false;

            if (supersede) m_OperationCancellation?.Cancel();
            m_OperationCancellation?.Dispose();
            CancellationToken lifetimeToken = m_LifetimeCancellation?.Token ?? default;
            m_OperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                externalToken,
                lifetimeToken);
            generation = ++m_OperationGeneration;
            token = m_OperationCancellation.Token;
            m_OperationInProgress = true;
            return true;
        }

        private void EndOperation(int generation)
        {
            if (!IsCurrent(generation)) return;
            m_OperationInProgress = false;
            m_OperationCancellation?.Dispose();
            m_OperationCancellation = null;
        }

        private bool IsCurrent(int generation)
        {
            return !m_Destroying && generation == m_OperationGeneration;
        }

        private void SetStateIfCurrent(
            int generation,
            NetworkLobbyState state,
            string message)
        {
            if (IsCurrent(generation)) SetState(state, message);
        }

        private NetworkLobbyOperationResult FailIfCurrent(
            int generation,
            string code,
            string message)
        {
            return IsCurrent(generation)
                ? Fail(code, message)
                : NetworkLobbyOperationResult.Failure("superseded", "The operation was superseded.");
        }

        private NetworkLobbyOperationResult CompleteCancellation(int generation)
        {
            if (IsCurrent(generation)) SetDisconnected("Ready");
            return CancelledResult();
        }

        private NetworkLobbyOperationResult CompleteException(
            int generation,
            string code,
            Exception exception)
        {
            if (!IsCurrent(generation))
            {
                return NetworkLobbyOperationResult.Failure(
                    "superseded",
                    "The operation was superseded.");
            }
            Debug.LogException(exception, this);
            return Fail(code, exception.Message);
        }

        private static NetworkLobbyOperationResult BusyResult()
        {
            return NetworkLobbyOperationResult.Failure(
                "busy",
                "Another Fusion lobby operation is already in progress.");
        }

        private static NetworkLobbyOperationResult CancelledResult()
        {
            return NetworkLobbyOperationResult.Failure(
                "cancelled",
                "The Fusion lobby operation was cancelled.");
        }

        private void ResolveBootstrap()
        {
            if (m_SessionBootstrap != null) return;
            m_SessionBootstrap = GetComponent<FusionSessionBootstrap>() ??
                                 GetComponentInParent<FusionSessionBootstrap>();
        }

        private bool HasGameplayRunner()
        {
            return m_SessionBootstrap != null &&
                   (m_SessionBootstrap.Runner != null || m_SessionBootstrap.IsStarting);
        }

        private void SubscribeBootstrap()
        {
            ResolveBootstrap();
            if (m_SessionBootstrap == null) return;
            m_SessionBootstrap.SessionStopped -= OnGameplaySessionStopped;
            m_SessionBootstrap.SessionStopped += OnGameplaySessionStopped;
        }

        private void UnsubscribeBootstrap()
        {
            if (m_SessionBootstrap == null) return;
            m_SessionBootstrap.SessionStopped -= OnGameplaySessionStopped;
        }

        private void OnGameplaySessionStopped()
        {
            if (m_Destroying || m_OperationInProgress) return;
            SetDisconnected("Ready");
        }

        private NetworkLobbyCompatibilityProfile Compatibility =>
            m_Compatibility ??= new NetworkLobbyCompatibilityProfile();

        private static FusionAppSettings CreatePhotonAppSettings(string region)
        {
            if (string.IsNullOrWhiteSpace(region)) return null;
            FusionAppSettings settings = PhotonAppSettings.Global.AppSettings.GetCopy();
            settings.UseNameServer = true;
            settings.FixedRegion = region;
            return settings;
        }

        private static string ResolveCreateSessionName(NetworkLobbyCreateRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.JoinCode)) return request.JoinCode.Trim();
            if (!string.IsNullOrWhiteSpace(request.SessionName)) return request.SessionName.Trim();
            return $"GC2-{Guid.NewGuid():N}".Substring(0, 12);
        }

        private static string FormatStartFailure(string prefix, StartGameResult result)
        {
            string detail = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? result.ShutdownReason.ToString()
                : result.ErrorMessage;
            return $"{prefix}: {detail}";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i])) return values[i];
            }
            return string.Empty;
        }

        private static string GetMetadata(
            IReadOnlyDictionary<string, string> metadata,
            string key)
        {
            return metadata != null && metadata.TryGetValue(key, out string value)
                ? value ?? string.Empty
                : string.Empty;
        }

        private static int ParseProtocol(string value)
        {
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsed)
                ? parsed
                : 0;
        }

        private static string TopologyValue(NetworkLobbyTopology topology)
        {
            return topology == NetworkLobbyTopology.Shared ? "shared" : "client-server";
        }

        private static bool TryParseTopology(
            string value,
            out NetworkLobbyTopology topology)
        {
            if (string.Equals(value, "shared", StringComparison.OrdinalIgnoreCase))
            {
                topology = NetworkLobbyTopology.Shared;
                return true;
            }
            if (string.Equals(value, "client-server", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "clientserver", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "host", StringComparison.OrdinalIgnoreCase))
            {
                topology = NetworkLobbyTopology.ClientServer;
                return true;
            }
            topology = NetworkLobbyTopology.ClientServer;
            return false;
        }

        private static int CompareEntries(NetworkLobbyEntry left, NetworkLobbyEntry right)
        {
            int compatibility = right.IsCompatible.CompareTo(left.IsCompatible);
            if (compatibility != 0) return compatibility;
            int availability = right.CanJoin.CompareTo(left.CanJoin);
            if (availability != 0) return availability;
            int population = right.PlayerCount.CompareTo(left.PlayerCount);
            if (population != 0) return population;
            return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRegion(string region)
        {
            return string.IsNullOrWhiteSpace(region)
                ? string.Empty
                : region.Trim().ToLowerInvariant();
        }

        private static string NormalizeLobbyName(string lobbyName)
        {
            return string.IsNullOrWhiteSpace(lobbyName)
                ? null
                : lobbyName.Trim();
        }

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList)
        {
            if (runner == null || runner != m_DiscoveryRunner) return;
            FusionLobbyDiscoveryRunnerMarker marker =
                runner.GetComponent<FusionLobbyDiscoveryRunnerMarker>();
            if (marker == null || marker.Generation != m_DiscoveryGeneration) return;

            ReplaceSessions(MapSessions(sessionList));
            m_SessionListCompletion?.TrySetResult(true);
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectFailed(
            NetworkRunner runner,
            NetAddress remoteAddress,
            NetConnectFailedReason reason) { }
        public void OnCustomAuthenticationResponse(
            NetworkRunner runner,
            Dictionary<string, object> data) { }
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectRequest(
            NetworkRunner runner,
            NetworkRunnerCallbackArgs.ConnectRequest request,
            byte[] token) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
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
    }
}
