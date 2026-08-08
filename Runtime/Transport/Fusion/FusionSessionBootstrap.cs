using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    public enum FusionDefaultLaunchMode
    {
        Host = 0,
        Shared = 1,
        JoinHost = 2,
        JoinShared = 3
    }

    /// <summary>
    /// Small, production-safe runner owner for projects that do not already have matchmaking.
    /// The transport can instead bind to any externally owned runner. A fresh runner is created
    /// for every start because Fusion NetworkRunners are single-use.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Session Bootstrap")]
    [DisallowMultipleComponent]
    public sealed class FusionSessionBootstrap : MonoBehaviour
    {
        private const int MaxSharedSessionNamePrefixLength = 64;
        private const string ExactSharedCreationClaimPropertyKey = "gc2x";

        private readonly struct StartDiagnosticContext
        {
            public StartDiagnosticContext(
                int attempt,
                GameMode gameMode,
                string sessionName,
                bool allowSessionCreation,
                string requestedRegion,
                string configuredRegion,
                string appVersion,
                string customLobbyName,
                string appId,
                string processContext)
            {
                Attempt = attempt;
                GameMode = gameMode;
                SessionName = sessionName ?? string.Empty;
                AllowSessionCreation = allowSessionCreation;
                RequestedRegion = requestedRegion ?? string.Empty;
                ConfiguredRegion = configuredRegion ?? string.Empty;
                AppVersion = appVersion ?? string.Empty;
                CustomLobbyName = customLobbyName ?? string.Empty;
                AppId = appId ?? string.Empty;
                AppIdDiagnostic = FormatAppIdForDiagnostic(appId);
                ProcessContext = processContext ?? string.Empty;
            }

            public int Attempt { get; }
            public GameMode GameMode { get; }
            public string SessionName { get; }
            public bool AllowSessionCreation { get; }
            public string RequestedRegion { get; }
            public string ConfiguredRegion { get; }
            public string AppVersion { get; }
            public string CustomLobbyName { get; }
            public string AppId { get; }
            public string AppIdDiagnostic { get; }
            public string ProcessContext { get; }
        }

        private static int s_StartDiagnosticAttempt;

        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private NetworkRunner m_RunnerPrefab;
        [SerializeField] private FusionDefaultLaunchMode m_DefaultLaunchMode =
            FusionDefaultLaunchMode.Shared;
        [Tooltip("Exact session name for Host/Join operations. Create Shared uses it as a readable prefix and generates a unique join code.")]
        [SerializeField] private string m_DefaultSessionName = "GC2-Fusion";
        [Tooltip("Optional Photon region code (for example: us, eu, asia, jp). Empty selects the best region.")]
        [SerializeField] private string m_Region = string.Empty;
        [Tooltip("Host/Client only. Disables NAT punch-through so gameplay uses Photon Cloud Relay. Shared Mode already uses Photon Relay.")]
        [SerializeField] private bool m_ForcePhotonRelay;
        [Tooltip("Optional project component implementing IFusionAuthenticationProvider. Keep Steamworks and other provider SDK references outside the core Fusion transport assembly.")]
        [SerializeField] private MonoBehaviour m_AuthenticationProviderBehaviour;
        [Min(1)]
        [SerializeField] private int m_MaxPlayers = 16;
        [SerializeField] private bool m_CreateDefaultSceneManager = true;
        [SerializeField] private bool m_IncludeActiveScene = true;
        [SerializeField] private bool m_DontDestroyRunnerOnLoad = true;

        private NetworkRunner m_Runner;
        private bool m_StartInProgress;
        private bool m_OwnsRunner;
        private bool m_ShutdownRequested;
        private bool m_Destroying;
        private bool m_StartAwaitCompleted;
        private CancellationTokenSource m_StartCancellation;
        private Task<StartGameResult> m_ActiveStartTask;
        private Task m_ActiveShutdownTask;
        private FusionSessionLifecycleState m_SessionLifecycleState =
            FusionSessionLifecycleState.Offline;
        private FusionDefaultLaunchMode m_ActiveLaunchMode;
        private bool m_HasActiveLaunchMode;
        private string m_PendingSessionName = string.Empty;
        private string m_PendingRegion = string.Empty;
        private IFusionAuthenticationProvider m_RuntimeAuthenticationProvider;
        private FusionSessionSnapshot m_ActiveSession;
        private bool m_HasActiveSession;
        private FusionSessionFailureInfo m_LastStartFailure;
        private bool m_HasLastStartFailure;
        private FusionSessionStopInfo m_LastStop;
        private bool m_HasLastStop;
        private bool m_StartFailureObservedForAttempt;
        private NetworkRunner m_ObservedStartedRunner;
        private NetworkRunner m_ObservedStoppedRunner;

        public event Action<NetworkRunner> SessionStarted;
        public event Action<StartGameResult> SessionStartFailed;
        public event Action SessionStopped;

        public event Action<FusionSessionSnapshot> SessionObservedStarted;
        public event Action<FusionSessionFailureInfo> SessionObservedStartFailed;
        public event Action<FusionSessionStopInfo> SessionObservedStopped;
        public event Action<FusionSessionLifecycleState> SessionObservedStateChanged;

        public NetworkRunner Runner => m_Runner;
        public FusionTransportBridge TransportBridge => m_TransportBridge;
        public FusionDefaultLaunchMode DefaultLaunchMode => m_DefaultLaunchMode;
        public string DefaultSessionName => m_DefaultSessionName;
        public string Region => m_Region;
        public bool ForcePhotonRelay => m_ForcePhotonRelay;
        public MonoBehaviour AuthenticationProviderBehaviour =>
            m_AuthenticationProviderBehaviour;
        public IFusionAuthenticationProvider AuthenticationProvider =>
            m_RuntimeAuthenticationProvider ??
            m_AuthenticationProviderBehaviour as IFusionAuthenticationProvider;
        public int MaxPlayers => m_MaxPlayers;
        public bool IsStarting => m_StartInProgress;
        public bool IsRunning => m_Runner != null && m_Runner.IsRunning && !m_Runner.IsShutdown;
        public bool IsExternalRunner => m_Runner != null && !m_OwnsRunner;
        public FusionSessionLifecycleState SessionLifecycleState => m_SessionLifecycleState;
        public bool HasActiveSession => TryGetActiveSession(out _);
        public FusionSessionSnapshot ActiveSession =>
            TryGetActiveSession(out FusionSessionSnapshot current) ? current : m_ActiveSession;
        public bool HasLastStartFailure => m_HasLastStartFailure;
        public FusionSessionFailureInfo LastStartFailure => m_LastStartFailure;
        public bool HasLastStop => m_HasLastStop;
        public FusionSessionStopInfo LastStop => m_LastStop;

        private void Awake()
        {
            if (m_TransportBridge == null)
            {
                m_TransportBridge = GetComponent<FusionTransportBridge>();
            }

            SubscribeTransportObservers();
            if (m_Runner == null && m_TransportBridge?.Runner != null)
            {
                AdoptExternalRunner(m_TransportBridge.Runner);
            }
        }

        private void OnEnable()
        {
            SubscribeTransportObservers();
        }

        private void Update()
        {
            if (m_Runner == null && m_TransportBridge?.Runner != null &&
                !m_StartInProgress && m_SessionLifecycleState != FusionSessionLifecycleState.Stopping)
            {
                AdoptExternalRunner(m_TransportBridge.Runner);
            }

            if (m_Runner != null && !m_OwnsRunner &&
                m_Runner.IsRunning && !m_Runner.IsShutdown &&
                m_SessionLifecycleState == FusionSessionLifecycleState.RunnerBound)
            {
                PublishObservedSessionStarted(m_Runner, false);
            }
        }

        private async void OnDestroy()
        {
            m_Destroying = true;
            try
            {
                await ShutdownAsync();
            }
            catch (Exception)
            {
                // Unity is already destroying this owner; runner shutdown is best-effort.
            }
            finally
            {
                UnsubscribeTransportObservers();
            }
        }

        public Task<StartGameResult> StartHostAsync(string sessionName)
        {
            return StartHostAsync(CreateDefaultStartOptions(sessionName));
        }

        public Task<StartGameResult> StartHostAsync(FusionSessionStartOptions options)
        {
            return BeginStartSession(GameMode.Host, options, true);
        }

        public Task<StartGameResult> JoinHostAsync(string sessionName)
        {
            return JoinHostAsync(CreateDefaultStartOptions(sessionName));
        }

        public Task<StartGameResult> JoinHostAsync(FusionSessionStartOptions options)
        {
            return BeginStartSession(GameMode.Client, options, false);
        }

        /// <summary>
        /// Creates a new Shared session using <paramref name="sessionName"/> as a readable
        /// prefix. Read the resolved join code from <see cref="ActiveSession"/> after success.
        /// </summary>
        public Task<StartGameResult> CreateSharedAsync(string sessionName)
        {
            return CreateSharedAsync(CreateDefaultStartOptions(sessionName));
        }

        /// <summary>
        /// Creates a new Shared session. Fusion's native named Shared start is create-or-join,
        /// so this method first replaces <see cref="FusionSessionStartOptions.SessionName"/>
        /// with a unique backend name. After a successful start, use
        /// <see cref="TryGetActiveSession"/> to obtain the generated name clients must join.
        /// </summary>
        public Task<StartGameResult> CreateSharedAsync(FusionSessionStartOptions options)
        {
            string uniqueSessionName = GenerateUniqueSharedSessionName(options.SessionName);
            return CreateSharedWithExactSessionNameAsync(
                CopyOptionsWithSessionName(options, uniqueSessionName));
        }

        /// <summary>
        /// Creates a new Shared session with the exact requested Photon session name. If that
        /// name already exists in the selected app version, region, and lobby, the start fails
        /// instead of joining the existing session.
        /// </summary>
        public Task<StartGameResult> CreateSharedWithExactSessionNameAsync(string sessionName)
        {
            return CreateSharedWithExactSessionNameAsync(
                CreateDefaultStartOptions(sessionName));
        }

        /// <inheritdoc cref="CreateSharedWithExactSessionNameAsync(string)"/>
        public Task<StartGameResult> CreateSharedWithExactSessionNameAsync(
            FusionSessionStartOptions options)
        {
            return BeginStartSession(
                GameMode.Shared,
                options,
                true,
                true);
        }

        /// <summary>
        /// Starts Shared Mode with an exact Photon session name. Photon joins the existing
        /// session when that name is already present, or creates it when it is absent.
        /// Prefer <see cref="CreateSharedAsync(string)"/> for a true new-session action and
        /// <see cref="JoinSharedAsync(string)"/> for a join-only action.
        /// </summary>
        public Task<StartGameResult> CreateOrJoinSharedAsync(string sessionName)
        {
            return CreateOrJoinSharedAsync(CreateDefaultStartOptions(sessionName));
        }

        /// <inheritdoc cref="CreateOrJoinSharedAsync(string)"/>
        public Task<StartGameResult> CreateOrJoinSharedAsync(FusionSessionStartOptions options)
        {
            return BeginStartSession(GameMode.Shared, options, true);
        }

        public Task<StartGameResult> JoinSharedAsync(string sessionName)
        {
            return JoinSharedAsync(CreateDefaultStartOptions(sessionName));
        }

        public Task<StartGameResult> JoinSharedAsync(FusionSessionStartOptions options)
        {
            return BeginStartSession(GameMode.Shared, options, false);
        }

        /// <summary>
        /// Generates the unique Photon backend name used by a new Shared session. The supplied
        /// value is retained as a readable prefix; it is not used as an exact room identifier.
        /// </summary>
        public static string GenerateUniqueSharedSessionName(string sessionNamePrefix)
        {
            if (string.IsNullOrWhiteSpace(sessionNamePrefix))
            {
                throw new ArgumentException(
                    "A non-empty Fusion Shared session name prefix is required.",
                    nameof(sessionNamePrefix));
            }

            string prefix = sessionNamePrefix.Trim();
            if (prefix.Length > MaxSharedSessionNamePrefixLength)
            {
                prefix = prefix.Substring(0, MaxSharedSessionNamePrefixLength);
            }

            return $"{prefix}-{Guid.NewGuid():N}";
        }

        public Task<StartGameResult> StartDefaultAsync()
        {
            switch (m_DefaultLaunchMode)
            {
                case FusionDefaultLaunchMode.JoinHost:
                    return JoinHostAsync(m_DefaultSessionName);
                case FusionDefaultLaunchMode.Shared:
                    return CreateSharedAsync(m_DefaultSessionName);
                case FusionDefaultLaunchMode.JoinShared:
                    return JoinSharedAsync(m_DefaultSessionName);
                case FusionDefaultLaunchMode.Host:
                default:
                    return StartHostAsync(m_DefaultSessionName);
            }
        }

        public bool BindExistingRunner(NetworkRunner runner)
        {
            if (runner == null ||
                FusionLobbyDiscoveryRunnerMarker.IsDiscoveryRunner(runner) ||
                m_StartInProgress ||
                (m_ActiveShutdownTask != null && !m_ActiveShutdownTask.IsCompleted) ||
                m_Runner != null)
            {
                return false;
            }

            m_ShutdownRequested = false;
            EnsureTransport();
            SubscribeTransportObservers();
            if (m_TransportBridge == null || !m_TransportBridge.Bind(runner))
            {
                if (m_TransportBridge?.Runner == runner) m_TransportBridge.Unbind();
                return false;
            }

            if (!ReferenceEquals(m_Runner, runner))
            {
                m_Runner = runner;
                m_OwnsRunner = false;
                m_ObservedStartedRunner = null;
                m_ObservedStoppedRunner = null;
                InferLaunchMode(runner, out m_ActiveLaunchMode, out m_HasActiveLaunchMode);
            }
            if (runner.IsRunning && !runner.IsShutdown)
            {
                PublishObservedSessionStarted(runner, false);
            }
            else
            {
                SetSessionLifecycleState(FusionSessionLifecycleState.RunnerBound);
            }
            return true;
        }

        /// <summary>
        /// Installs or clears a runtime authentication provider. A runtime provider takes
        /// precedence over the optional provider component assigned in the Inspector.
        /// </summary>
        public void SetAuthenticationProvider(IFusionAuthenticationProvider provider)
        {
            if (m_StartInProgress)
            {
                throw new InvalidOperationException(
                    "The Fusion authentication provider cannot change while a start is in progress.");
            }

            m_RuntimeAuthenticationProvider = provider;
        }

        public Task ShutdownAsync()
        {
            m_ShutdownRequested = true;
            m_StartCancellation?.Cancel();
            if (m_ActiveShutdownTask != null && !m_ActiveShutdownTask.IsCompleted)
            {
                return m_ActiveShutdownTask;
            }

            if (m_Runner != null || m_StartInProgress)
            {
                SetSessionLifecycleState(FusionSessionLifecycleState.Stopping);
            }
            m_ActiveShutdownTask = ShutdownCoreAsync();
            return m_ActiveShutdownTask;
        }

        private async Task ShutdownCoreAsync()
        {
            Task<StartGameResult> pendingStart = m_ActiveStartTask;
            if (pendingStart != null &&
                !pendingStart.IsCompleted &&
                !m_StartAwaitCompleted)
            {
                try
                {
                    await pendingStart;
                }
                catch (Exception)
                {
                    // StartSessionAsync already cleaned up its failed runner.
                }
            }

            NetworkRunner runner = m_Runner;
            if (runner == null)
            {
                m_OwnsRunner = false;
                m_HasActiveSession = false;
                m_HasActiveLaunchMode = false;
                m_PendingSessionName = string.Empty;
                m_PendingRegion = string.Empty;
                SetSessionLifecycleState(FusionSessionLifecycleState.Offline);
                return;
            }

            bool owned = m_OwnsRunner;
            FusionSessionSnapshot sessionBeforeStop = CaptureSessionOrFallback(runner, !owned);
            try
            {
                if (runner.IsRunning && !runner.IsShutdown)
                {
                    await runner.Shutdown();
                }
            }
            finally
            {
                if (m_TransportBridge != null && m_TransportBridge.Runner == runner)
                {
                    m_TransportBridge.Unbind();
                }

                if (owned && runner != null)
                {
                    Destroy(runner.gameObject);
                }

                CompleteObservedStop(
                    runner,
                    sessionBeforeStop,
                    m_Destroying
                        ? FusionSessionStopOrigin.Destroyed
                        : FusionSessionStopOrigin.Requested,
                    ShutdownReason.Ok,
                    true);

                if (!m_Destroying) SessionStopped?.Invoke();
            }
        }

        private Task<StartGameResult> BeginStartSession(
            GameMode gameMode,
            FusionSessionStartOptions options,
            bool allowSessionCreation,
            bool requireNewExactSharedSessionName = false)
        {
            if (requireNewExactSharedSessionName &&
                (gameMode != GameMode.Shared || !allowSessionCreation))
            {
                throw new ArgumentException(
                    "Exact-name create-only matching is supported only for a creating Shared session.",
                    nameof(requireNewExactSharedSessionName));
            }

            if (m_StartInProgress ||
                m_SessionLifecycleState == FusionSessionLifecycleState.Starting)
            {
                throw new InvalidOperationException("A Fusion session start is already in progress.");
            }

            if (m_ActiveShutdownTask != null && !m_ActiveShutdownTask.IsCompleted)
            {
                throw new InvalidOperationException(
                    "The previous Fusion session is still shutting down.");
            }

            if (m_Destroying)
            {
                throw new InvalidOperationException(
                    "The Fusion session bootstrap is being destroyed.");
            }

            if (m_Runner != null)
            {
                throw new InvalidOperationException(
                    "This bootstrap already owns or references a runner. Shut it down before starting again.");
            }

            if (string.IsNullOrWhiteSpace(options.SessionName))
            {
                throw new ArgumentException(
                    "A non-empty Fusion session name is required.",
                    nameof(options));
            }

            string sessionName = options.SessionName.Trim();
            string region = NormalizeRegion(options.Region);
            if (string.IsNullOrEmpty(region))
            {
                Debug.LogWarning(
                    "[FusionSessionBootstrap] Best Region is automatic and may resolve " +
                    "differently on each device. Named sessions are region-scoped; for a " +
                    "direct Host/Join, Join Shared, or Create-or-Join Shared workflow, select " +
                    "the same explicit region on every peer or advertise the creator's " +
                    "resolved region through your lobby/invite service.",
                    this);
            }
            options = new FusionSessionStartOptions(
                sessionName,
                region,
                options.AuthenticationValues,
                options.ForcePhotonRelay,
                options.IsOpen,
                options.IsVisible,
                options.CustomLobbyName,
                options.SessionProperties,
                options.MaxPlayers);

            m_ShutdownRequested = false;
            m_StartAwaitCompleted = false;
            m_StartFailureObservedForAttempt = false;
            m_PendingSessionName = sessionName;
            m_PendingRegion = region;
            m_ActiveLaunchMode = ResolveLaunchMode(gameMode, allowSessionCreation);
            m_HasActiveLaunchMode = true;
            m_HasActiveSession = false;
            m_ObservedStartedRunner = null;
            m_ObservedStoppedRunner = null;
            // Publish Starting only after the task and cancellation source exist. A visual
            // observer may legitimately call ShutdownAsync from the state callback; exposing
            // the transition earlier would let that shutdown miss the pending start.
            SetSessionLifecycleState(FusionSessionLifecycleState.Starting, false);
            m_StartCancellation?.Dispose();
            m_StartCancellation = new CancellationTokenSource();
            m_ActiveStartTask =
                StartSessionAsync(
                    gameMode,
                    options,
                    allowSessionCreation,
                    requireNewExactSharedSessionName,
                    m_StartCancellation);
            PublishSessionLifecycleStateChanged(FusionSessionLifecycleState.Starting);
            return m_ActiveStartTask;
        }

        private static FusionSessionStartOptions CopyOptionsWithSessionName(
            FusionSessionStartOptions options,
            string sessionName)
        {
            return new FusionSessionStartOptions(
                sessionName,
                options.Region,
                options.AuthenticationValues,
                options.ForcePhotonRelay,
                options.IsOpen,
                options.IsVisible,
                options.CustomLobbyName,
                options.SessionProperties,
                options.MaxPlayers);
        }

        private async Task<StartGameResult> StartSessionAsync(
            GameMode gameMode,
            FusionSessionStartOptions options,
            bool allowSessionCreation,
            bool requireNewExactSharedSessionName,
            CancellationTokenSource startCancellation)
        {
            if (m_StartInProgress)
            {
                throw new InvalidOperationException("A Fusion session start is already in progress.");
            }

            if (m_Runner != null)
            {
                throw new InvalidOperationException(
                    "This bootstrap already owns or references a runner. Shut it down before starting again.");
            }

            if (string.IsNullOrWhiteSpace(options.SessionName))
            {
                throw new ArgumentException(
                    "A non-empty Fusion session name is required.",
                    nameof(options));
            }

            m_StartInProgress = true;
            NetworkRunner runner = null;
            IFusionAuthenticationProvider authenticationProvider = null;
            bool authenticationProviderInvoked = false;
            FusionAuthenticationCompletion authenticationCompletion = default;
            bool hasAuthenticationCompletion = false;
            string startStage = "authentication";
            StartDiagnosticContext diagnostic = CreateStartDiagnosticContext(
                gameMode,
                options,
                allowSessionCreation);
            LogStartDiagnostic(
                "request",
                diagnostic,
                null,
                null,
                string.Empty,
                string.Empty,
                startStage);
            try
            {
                Photon.Realtime.AuthenticationValues authenticationValues =
                    options.AuthenticationValues;
                if (authenticationValues == null)
                {
                    authenticationProvider = ResolveAuthenticationProvider();
                    if (authenticationProvider != null)
                    {
                        authenticationProviderInvoked = true;
                        authenticationValues = await authenticationProvider
                            .CreateAuthenticationValuesAsync(startCancellation.Token);
                        if (authenticationValues == null)
                        {
                            throw new InvalidOperationException(
                                "The Fusion authentication provider returned no authentication values.");
                        }
                    }
                }

                startStage = "runner-setup";
                if (m_DontDestroyRunnerOnLoad)
                {
                    DontDestroyOnLoad(transform.root.gameObject);
                }

                runner = CreateFreshRunner();
                m_Runner = runner;
                m_OwnsRunner = true;
                EnsureTransport();
                SubscribeTransportObservers();
                if (m_TransportBridge == null || !m_TransportBridge.Bind(runner))
                {
                    throw new InvalidOperationException("Could not bind the Fusion transport to the new runner.");
                }

                INetworkSceneManager sceneManager = runner.GetComponent<INetworkSceneManager>();
                if (sceneManager == null && m_CreateDefaultSceneManager)
                {
                    sceneManager = runner.gameObject.AddComponent<NetworkSceneManagerDefault>();
                }

                INetworkObjectProvider objectProvider = runner.GetComponent<INetworkObjectProvider>();
                if (objectProvider == null)
                {
                    objectProvider = runner.gameObject.AddComponent<NetworkObjectProviderDefault>();
                }

                NetworkSceneInfo sceneInfo = default;
                if (m_IncludeActiveScene && sceneManager != null)
                {
                    Scene activeScene = SceneManager.GetActiveScene();
                    if (activeScene.IsValid() && activeScene.buildIndex >= 0)
                    {
                        sceneInfo.AddSceneRef(
                            SceneRef.FromIndex(activeScene.buildIndex),
                            LoadSceneMode.Single,
                            LocalPhysicsMode.None,
                            true);
                    }
                }

                EnsureRuntimeProjectConfiguration();

                string startGameSessionName = options.SessionName;
                Func<string> sessionNameGenerator = null;
                Dictionary<string, SessionProperty> sessionProperties =
                    options.CopySessionProperties();
                if (requireNewExactSharedSessionName)
                {
                    // Fusion normally treats a named Shared start as create-or-join. Starting
                    // through random matchmaking with an attempt-unique property prevents a
                    // match, while SessionNameGenerator requests the exact caller-owned ID for
                    // the create step. Photon then reports a collision instead of joining it.
                    string exactSessionName = options.SessionName;
                    string creationClaim = Guid.NewGuid().ToString("N");
                    startGameSessionName = null;
                    sessionNameGenerator = () => exactSessionName;
                    if (sessionProperties == null)
                    {
                        sessionProperties = new Dictionary<string, SessionProperty>();
                    }
                    sessionProperties[ExactSharedCreationClaimPropertyKey] =
                        SessionProperty.Convert(creationClaim);
                }

                var args = new StartGameArgs
                {
                    GameMode = gameMode,
                    SessionName = startGameSessionName,
                    SessionNameGenerator = sessionNameGenerator,
                    PlayerCount = options.MaxPlayers ?? Mathf.Max(1, m_MaxPlayers),
                    EnableClientSessionCreation = allowSessionCreation,
                    SceneManager = sceneManager,
                    ObjectProvider = objectProvider,
                    Scene = sceneInfo,
                    AuthValues = authenticationValues,
                    CustomLobbyName = options.CustomLobbyName,
                    SessionProperties = sessionProperties,
                    DisableNATPunchthrough =
                        gameMode != GameMode.Shared && options.ForcePhotonRelay,
                    StartGameCancellationToken = startCancellation.Token
                };

                // Nullable overrides are deliberately assigned after construction. Leaving
                // either value unset preserves Fusion's SDK default for existing callers.
                if (options.IsOpen.HasValue) args.IsOpen = options.IsOpen.Value;
                if (options.IsVisible.HasValue) args.IsVisible = options.IsVisible.Value;

                // Always provide a copy so an empty bootstrap selection has deterministic
                // semantics: it clears any project-global FixedRegion and asks Photon to choose
                // the best available region. A selected dropdown value pins that exact region.
                FusionAppSettings appSettings =
                    PhotonAppSettings.Global.AppSettings.GetCopy();
                appSettings.UseNameServer = true;
                appSettings.FixedRegion = options.Region;
                args.CustomPhotonAppSettings = appSettings;

                startStage = "matchmaking";
                StartGameResult result = await runner.StartGame(args);
                // From this point the runner can safely be shut down even if ShutdownAsync
                // is called synchronously by a SessionStarted/SessionStartFailed subscriber.
                m_StartAwaitCompleted = true;
                if (result.Ok)
                {
                    LogStartDiagnostic(
                        "success",
                        diagnostic,
                        runner,
                        result.ShutdownReason,
                        result.ErrorMessage,
                        string.Empty,
                        startStage);
                    authenticationCompletion = new FusionAuthenticationCompletion(
                        FusionAuthenticationCompletionStatus.Succeeded,
                        ShutdownReason.Ok,
                        string.Empty,
                        string.Empty);
                    hasAuthenticationCompletion = true;
                    // Shutdown can be requested while Photon is still completing StartGame.
                    // The shutdown path waits for this task, so never publish a transient
                    // started session after that request.
                    if (!m_ShutdownRequested &&
                        !m_Destroying &&
                        m_Runner == runner &&
                        m_OwnsRunner)
                    {
                        SessionStarted?.Invoke(runner);
                        PublishObservedSessionStarted(runner, true);
                    }
                }
                else
                {
                    LogStartDiagnostic(
                        "failure",
                        diagnostic,
                        runner,
                        result.ShutdownReason,
                        result.ErrorMessage,
                        string.Empty,
                        startStage);
                    bool cancelled =
                        result.ShutdownReason == ShutdownReason.OperationCanceled;
                    authenticationCompletion = new FusionAuthenticationCompletion(
                        cancelled
                            ? FusionAuthenticationCompletionStatus.Cancelled
                            : FusionAuthenticationCompletionStatus.Failed,
                        result.ShutdownReason,
                        result.ErrorMessage,
                        string.Empty);
                    hasAuthenticationCompletion = true;
                    CleanupFailedRunner(runner);
                    if (!m_ShutdownRequested && !m_Destroying)
                    {
                        PublishObservedStartFailure(new FusionSessionFailureInfo(
                            m_ActiveLaunchMode,
                            m_HasActiveLaunchMode,
                            m_PendingSessionName,
                            result.ShutdownReason,
                            result.ErrorMessage,
                            result.ShutdownReason == ShutdownReason.OperationCanceled,
                            string.Empty));
                        SessionStartFailed?.Invoke(result);
                    }
                }

                return result;
            }
            catch (Exception exception)
            {
                bool cancelled =
                    exception is OperationCanceledException || m_ShutdownRequested;
                LogStartDiagnostic(
                    "failure",
                    diagnostic,
                    runner,
                    cancelled ? ShutdownReason.OperationCanceled : ShutdownReason.Error,
                    exception.Message,
                    exception.GetType().FullName,
                    startStage);
                authenticationCompletion = new FusionAuthenticationCompletion(
                    cancelled
                        ? FusionAuthenticationCompletionStatus.Cancelled
                        : FusionAuthenticationCompletionStatus.Failed,
                    cancelled ? ShutdownReason.OperationCanceled : ShutdownReason.Error,
                    exception.Message,
                    exception.GetType().FullName);
                hasAuthenticationCompletion = true;
                if (runner != null) CleanupFailedRunner(runner);
                if (!m_Destroying && !m_StartFailureObservedForAttempt)
                {
                    PublishObservedStartFailure(new FusionSessionFailureInfo(
                        m_ActiveLaunchMode,
                        m_HasActiveLaunchMode,
                        m_PendingSessionName,
                        cancelled ? ShutdownReason.OperationCanceled : ShutdownReason.Error,
                        exception.Message,
                        cancelled,
                        exception.GetType().FullName));
                }
                throw;
            }
            finally
            {
                if (authenticationProviderInvoked && authenticationProvider != null)
                {
                    NotifyAuthenticationCompletedBestEffort(
                        authenticationProvider,
                        hasAuthenticationCompletion
                            ? authenticationCompletion
                            : new FusionAuthenticationCompletion(
                                FusionAuthenticationCompletionStatus.Failed,
                                ShutdownReason.Error,
                                "The Fusion start ended without an authentication result.",
                                string.Empty));
                }
                m_StartInProgress = false;
                if (m_SessionLifecycleState == FusionSessionLifecycleState.Starting)
                {
                    SetSessionLifecycleState(
                        IsRunning
                            ? FusionSessionLifecycleState.Running
                            : FusionSessionLifecycleState.Offline);
                }
                if (ReferenceEquals(m_StartCancellation, startCancellation))
                {
                    m_StartCancellation.Dispose();
                    m_StartCancellation = null;
                }
            }
        }

        public bool TryGetActiveSession(out FusionSessionSnapshot session)
        {
            if (FusionSessionSnapshot.TryCapture(
                    m_Runner,
                    m_ActiveLaunchMode,
                    m_HasActiveLaunchMode,
                    IsExternalRunner,
                    out session))
            {
                m_ActiveSession = session;
                m_HasActiveSession = true;
                return true;
            }

            session = m_ActiveSession;
            return m_HasActiveSession &&
                   m_SessionLifecycleState == FusionSessionLifecycleState.Running;
        }

        private void SubscribeTransportObservers()
        {
            if (m_TransportBridge == null) return;
            m_TransportBridge.RunnerObservedBound -= OnRunnerObservedBound;
            m_TransportBridge.RunnerObservedBound += OnRunnerObservedBound;
            m_TransportBridge.RunnerObservedUnbound -= OnRunnerObservedUnbound;
            m_TransportBridge.RunnerObservedUnbound += OnRunnerObservedUnbound;
            m_TransportBridge.RunnerObservedShutdown -= OnRunnerObservedShutdown;
            m_TransportBridge.RunnerObservedShutdown += OnRunnerObservedShutdown;
        }

        private void UnsubscribeTransportObservers()
        {
            if (m_TransportBridge == null) return;
            m_TransportBridge.RunnerObservedBound -= OnRunnerObservedBound;
            m_TransportBridge.RunnerObservedUnbound -= OnRunnerObservedUnbound;
            m_TransportBridge.RunnerObservedShutdown -= OnRunnerObservedShutdown;
        }

        private void OnRunnerObservedBound(FusionRunnerBindingInfo info)
        {
            if (m_Destroying ||
                info.Runner == null ||
                FusionLobbyDiscoveryRunnerMarker.IsDiscoveryRunner(info.Runner))
            {
                return;
            }
            if (m_Runner != null && !ReferenceEquals(m_Runner, info.Runner)) return;
            if (ReferenceEquals(m_Runner, info.Runner) && m_OwnsRunner) return;
            AdoptExternalRunner(info.Runner);
        }

        private void OnRunnerObservedUnbound(FusionRunnerBindingInfo info)
        {
            if (!ReferenceEquals(m_Runner, info.Runner)) return;

            if (m_StartInProgress ||
                m_SessionLifecycleState == FusionSessionLifecycleState.Starting)
            {
                m_Runner = null;
                m_OwnsRunner = false;
                m_HasActiveSession = false;
                return;
            }

            FusionSessionSnapshot session = CaptureSessionOrFallback(
                info.Runner,
                !m_OwnsRunner);
            CompleteObservedStop(
                info.Runner,
                session,
                m_Destroying
                    ? FusionSessionStopOrigin.Destroyed
                    : m_ShutdownRequested
                        ? FusionSessionStopOrigin.Requested
                        : FusionSessionStopOrigin.RunnerUnbound,
                ShutdownReason.Ok,
                m_ShutdownRequested);
        }

        private void OnRunnerObservedShutdown(FusionRunnerShutdownInfo info)
        {
            if (!ReferenceEquals(m_Runner, info.Runner)) return;
            bool destroyOwnedRunner = m_OwnsRunner && !m_ShutdownRequested;
            FusionSessionSnapshot session = CaptureSessionOrFallback(
                info.Runner,
                !m_OwnsRunner);
            CompleteObservedStop(
                info.Runner,
                session,
                m_Destroying
                    ? FusionSessionStopOrigin.Destroyed
                    : m_ShutdownRequested
                        ? FusionSessionStopOrigin.Requested
                        : FusionSessionStopOrigin.RunnerShutdown,
                info.Reason,
                m_ShutdownRequested);
            if (destroyOwnedRunner && info.Runner != null)
            {
                // Fusion runners are single-use. A remotely initiated/spontaneous shutdown
                // has no ShutdownCoreAsync continuation to dispose the owned runner object.
                Destroy(info.Runner.gameObject);
            }
        }

        private void AdoptExternalRunner(NetworkRunner runner)
        {
            if (runner == null ||
                FusionLobbyDiscoveryRunnerMarker.IsDiscoveryRunner(runner) ||
                (m_Runner != null && !ReferenceEquals(m_Runner, runner)))
            {
                return;
            }

            m_Runner = runner;
            m_OwnsRunner = false;
            m_ObservedStartedRunner = null;
            m_ObservedStoppedRunner = null;
            InferLaunchMode(runner, out m_ActiveLaunchMode, out m_HasActiveLaunchMode);
            if (runner.IsRunning && !runner.IsShutdown)
            {
                PublishObservedSessionStarted(runner, false);
            }
            else
            {
                SetSessionLifecycleState(FusionSessionLifecycleState.RunnerBound);
            }
        }

        private void PublishObservedSessionStarted(NetworkRunner runner, bool ownsRunner)
        {
            if (runner == null || ReferenceEquals(m_ObservedStartedRunner, runner)) return;
            if (!FusionSessionSnapshot.TryCapture(
                    runner,
                    m_ActiveLaunchMode,
                    m_HasActiveLaunchMode,
                    !ownsRunner,
                    out FusionSessionSnapshot session))
            {
                return;
            }

            m_ObservedStartedRunner = runner;
            m_ObservedStoppedRunner = null;
            m_ActiveSession = session;
            m_HasActiveSession = true;
            m_PendingSessionName = string.Empty;
            m_PendingRegion = string.Empty;
            SetSessionLifecycleState(FusionSessionLifecycleState.Running);
            if (!ReferenceEquals(m_Runner, runner) ||
                !runner.IsRunning || runner.IsShutdown ||
                m_SessionLifecycleState != FusionSessionLifecycleState.Running)
            {
                return;
            }
            FusionLifecycleEventUtility.InvokeBestEffort(
                SessionObservedStarted,
                session,
                this,
                nameof(SessionObservedStarted));
        }

        private void PublishObservedStartFailure(FusionSessionFailureInfo failure)
        {
            if (m_StartFailureObservedForAttempt) return;
            m_StartFailureObservedForAttempt = true;
            m_LastStartFailure = failure;
            m_HasLastStartFailure = true;
            m_HasActiveSession = false;
            m_PendingSessionName = string.Empty;
            m_PendingRegion = string.Empty;
            if (m_SessionLifecycleState != FusionSessionLifecycleState.Stopping)
            {
                SetSessionLifecycleState(FusionSessionLifecycleState.Offline);
            }
            FusionLifecycleEventUtility.InvokeBestEffort(
                SessionObservedStartFailed,
                failure,
                this,
                nameof(SessionObservedStartFailed));
            m_HasActiveLaunchMode = false;
        }

        private void CompleteObservedStop(
            NetworkRunner runner,
            FusionSessionSnapshot session,
            FusionSessionStopOrigin origin,
            ShutdownReason reason,
            bool wasRequested)
        {
            if (runner == null || ReferenceEquals(m_ObservedStoppedRunner, runner)) return;

            m_ObservedStoppedRunner = runner;
            m_ObservedStartedRunner = null;
            m_LastStop = new FusionSessionStopInfo(
                session,
                origin,
                reason,
                wasRequested);
            m_HasLastStop = true;
            if (ReferenceEquals(m_Runner, runner)) m_Runner = null;
            m_OwnsRunner = false;
            m_HasActiveSession = false;
            m_HasActiveLaunchMode = false;
            m_PendingSessionName = string.Empty;
            m_PendingRegion = string.Empty;
            SetSessionLifecycleState(FusionSessionLifecycleState.Offline);
            FusionLifecycleEventUtility.InvokeBestEffort(
                SessionObservedStopped,
                m_LastStop,
                this,
                nameof(SessionObservedStopped));
        }

        private void SetSessionLifecycleState(
            FusionSessionLifecycleState state,
            bool publishObservation = true)
        {
            if (m_SessionLifecycleState == state) return;
            m_SessionLifecycleState = state;
            if (!publishObservation) return;
            PublishSessionLifecycleStateChanged(state);
        }

        private void PublishSessionLifecycleStateChanged(
            FusionSessionLifecycleState state)
        {
            if (m_SessionLifecycleState != state) return;
            FusionLifecycleEventUtility.InvokeBestEffort(
                SessionObservedStateChanged,
                state,
                this,
                nameof(SessionObservedStateChanged));
        }

        private FusionSessionSnapshot CaptureSessionOrFallback(
            NetworkRunner runner,
            bool isExternalRunner)
        {
            if (FusionSessionSnapshot.TryCapture(
                    runner,
                    m_ActiveLaunchMode,
                    m_HasActiveLaunchMode,
                    isExternalRunner,
                    out FusionSessionSnapshot session))
            {
                m_ActiveSession = session;
                m_HasActiveSession = true;
                return session;
            }

            if (m_HasActiveSession) return m_ActiveSession;
            GameMode gameMode = runner != null ? runner.GameMode : default;
            return new FusionSessionSnapshot(
                runner,
                m_ActiveLaunchMode,
                m_HasActiveLaunchMode,
                m_PendingSessionName,
                m_PendingRegion,
                gameMode,
                0,
                Mathf.Max(1, m_MaxPlayers),
                isExternalRunner);
        }

        private static FusionDefaultLaunchMode ResolveLaunchMode(
            GameMode gameMode,
            bool allowSessionCreation)
        {
            switch (gameMode)
            {
                case GameMode.Shared:
                    return allowSessionCreation
                        ? FusionDefaultLaunchMode.Shared
                        : FusionDefaultLaunchMode.JoinShared;
                case GameMode.Client:
                    return FusionDefaultLaunchMode.JoinHost;
                case GameMode.Host:
                default:
                    return FusionDefaultLaunchMode.Host;
            }
        }

        private static void InferLaunchMode(
            NetworkRunner runner,
            out FusionDefaultLaunchMode launchMode,
            out bool hasLaunchMode)
        {
            launchMode = FusionDefaultLaunchMode.Host;
            hasLaunchMode = runner != null;
            if (runner == null) return;
            switch (runner.GameMode)
            {
                case GameMode.Host:
                    launchMode = FusionDefaultLaunchMode.Host;
                    break;
                case GameMode.Client:
                    launchMode = FusionDefaultLaunchMode.JoinHost;
                    break;
                case GameMode.Shared:
                    launchMode = FusionDefaultLaunchMode.Shared;
                    // An externally owned Shared runner does not expose whether this peer
                    // created or joined the room. Keep the topology value but mark intent
                    // as unknown instead of reporting a fabricated CreateShared launch.
                    hasLaunchMode = false;
                    break;
                default:
                    hasLaunchMode = false;
                    break;
            }
        }

        private NetworkRunner CreateFreshRunner()
        {
            NetworkRunner runner;
            if (m_RunnerPrefab != null)
            {
                runner = Instantiate(m_RunnerPrefab);
                runner.name = "Arawn Fusion Runner";
            }
            else
            {
                var runnerObject = new GameObject("Arawn Fusion Runner");
                runner = runnerObject.AddComponent<NetworkRunner>();
            }

            runner.ProvideInput = true;
            if (runner.GetComponent<FusionRpcRouter>() == null)
            {
                runner.gameObject.AddComponent<FusionRpcRouter>();
            }

            if (m_DontDestroyRunnerOnLoad)
            {
                DontDestroyOnLoad(runner.gameObject);
            }

            return runner;
        }

        private FusionSessionStartOptions CreateDefaultStartOptions(string sessionName)
        {
            return new FusionSessionStartOptions(
                sessionName,
                m_Region,
                null,
                m_ForcePhotonRelay,
                maxPlayers: m_MaxPlayers);
        }

        private IFusionAuthenticationProvider ResolveAuthenticationProvider()
        {
            if (m_RuntimeAuthenticationProvider != null)
            {
                return m_RuntimeAuthenticationProvider;
            }

            if (m_AuthenticationProviderBehaviour == null) return null;
            if (m_AuthenticationProviderBehaviour is IFusionAuthenticationProvider provider)
            {
                return provider;
            }

            throw new InvalidOperationException(
                $"Authentication provider '{m_AuthenticationProviderBehaviour.name}' must implement " +
                $"{nameof(IFusionAuthenticationProvider)}.");
        }

        private void NotifyAuthenticationCompletedBestEffort(
            IFusionAuthenticationProvider provider,
            FusionAuthenticationCompletion completion)
        {
            try
            {
                provider.OnAuthenticationCompleted(completion);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private static string NormalizeRegion(string region)
        {
            return string.IsNullOrWhiteSpace(region)
                ? string.Empty
                : region.Trim().ToLowerInvariant();
        }

        private static void EnsureRuntimeProjectConfiguration()
        {
            // NetworkProjectConfig owns Fusion's runtime NetworkPrefabTable. Never create a
            // JsonUtility copy here: that table is runtime state and is not JSON-serialized, so a
            // copied config cannot translate baked NetworkObject GUIDs during Runner.Spawn.
            // Use Fusion's populated global instance and let NetworkRunner perform its internal
            // configuration copy. This retains the prefab catalog while repairing older projects
            // that still use LatestState input transfer.
            NetworkProjectConfig config = NetworkProjectConfig.Global;
            if (config == null) return;
            config.Simulation.InputTransferMode =
                SimulationConfig.InputTransferModes.Redundancy;
        }

        private static StartDiagnosticContext CreateStartDiagnosticContext(
            GameMode gameMode,
            FusionSessionStartOptions options,
            bool allowSessionCreation)
        {
            string appVersion = string.Empty;
            string configuredRegion = string.Empty;
            string appId = string.Empty;
            try
            {
                if (PhotonAppSettings.TryGetGlobal(out PhotonAppSettings photonSettings) &&
                    photonSettings?.AppSettings != null)
                {
                    appVersion = photonSettings.AppSettings.AppVersion ?? string.Empty;
                    appId = photonSettings.AppSettings.AppIdFusion ?? string.Empty;
                }
            }
            catch (Exception)
            {
                // Diagnostics must never prevent a session attempt. Fusion will report any
                // unusable global settings through the normal StartGame result or exception.
            }

            string requestedRegion = NormalizeRegion(options.Region);
            configuredRegion = requestedRegion;

            return new StartDiagnosticContext(
                Interlocked.Increment(ref s_StartDiagnosticAttempt),
                gameMode,
                options.SessionName,
                allowSessionCreation,
                requestedRegion,
                configuredRegion,
                appVersion,
                options.CustomLobbyName,
                appId,
                GetProcessContext());
        }

        private void LogStartDiagnostic(
            string phase,
            StartDiagnosticContext context,
            NetworkRunner runner,
            ShutdownReason? reason,
            string resultDetail,
            string exceptionType,
            string stage)
        {
            bool isRequest = string.Equals(phase, "request", StringComparison.Ordinal);
            bool isSuccess = string.Equals(phase, "success", StringComparison.Ordinal);
            string resolvedRegion = ResolveDiagnosticRegion(runner, context, isRequest);
            string safeResultDetail = RedactExactValue(resultDetail, context.AppId);
            CaptureActualSessionDiagnostic(
                runner,
                isRequest,
                isSuccess,
                out string actualMode,
                out string actualSession,
                out string actualIsOpen,
                out string actualIsVisible,
                out string actualPlayerCount,
                out string actualMaxPlayers);
            string message =
                $"[FusionSessionBootstrap] start-{phase} " +
                $"utc={DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)} " +
                $"attempt={context.Attempt} " +
                $"process={QuoteDiagnosticValue(context.ProcessContext)} " +
                $"mode={context.GameMode} " +
                $"session={QuoteDiagnosticValue(context.SessionName)} " +
                $"allowSessionCreation={context.AllowSessionCreation} " +
                $"requestedRegion={QuoteDiagnosticValue(ValueOrLabel(context.RequestedRegion, "none"))} " +
                $"resolvedRegion={QuoteDiagnosticValue(resolvedRegion)} " +
                $"appVersion={QuoteDiagnosticValue(ValueOrLabel(context.AppVersion, "default"))} " +
                $"customLobby={QuoteDiagnosticValue(ValueOrLabel(context.CustomLobbyName, "default"))} " +
                $"appId={QuoteDiagnosticValue(context.AppIdDiagnostic)} " +
                $"reason={QuoteDiagnosticValue(reason?.ToString() ?? "Pending")} " +
                $"detail={QuoteDiagnosticValue(ValueOrLabel(safeResultDetail, "none"))} " +
                $"exceptionType={QuoteDiagnosticValue(ValueOrLabel(exceptionType, "none"))} " +
                $"stage={QuoteDiagnosticValue(ValueOrLabel(stage, "unknown"))} " +
                $"actualMode={QuoteDiagnosticValue(actualMode)} " +
                $"actualSession={QuoteDiagnosticValue(actualSession)} " +
                $"isOpen={QuoteDiagnosticValue(actualIsOpen)} " +
                $"isVisible={QuoteDiagnosticValue(actualIsVisible)} " +
                $"playerCount={QuoteDiagnosticValue(actualPlayerCount)} " +
                $"maxPlayers={QuoteDiagnosticValue(actualMaxPlayers)}";

            if (string.Equals(phase, "failure", StringComparison.Ordinal))
            {
                Debug.LogWarning(message, this);
            }
            else
            {
                Debug.Log(message, this);
            }
        }

        private static string ResolveDiagnosticRegion(
            NetworkRunner runner,
            StartDiagnosticContext context,
            bool requestPending)
        {
            try
            {
                if (runner != null)
                {
                    // Region can remain populated while a failed room join has no valid
                    // SessionInfo (and a direct join may never have valid LobbyInfo). Read the
                    // value independently from validity so failure diagnostics retain it when
                    // the SDK makes it available.
                    string sessionRegion = NormalizeRegion(runner.SessionInfo.Region);
                    if (!string.IsNullOrEmpty(sessionRegion)) return sessionRegion;

                    string lobbyRegion = NormalizeRegion(runner.LobbyInfo.Region);
                    if (!string.IsNullOrEmpty(lobbyRegion)) return lobbyRegion;
                }
            }
            catch (Exception)
            {
                // A failed/shutting-down runner may no longer expose SessionInfo.
            }

            if (!string.IsNullOrEmpty(context.ConfiguredRegion))
            {
                return context.ConfiguredRegion;
            }

            return requestPending ? "<auto-pending>" : "<unresolved>";
        }

        private static void CaptureActualSessionDiagnostic(
            NetworkRunner runner,
            bool isRequest,
            bool isSuccess,
            out string actualMode,
            out string actualSession,
            out string isOpen,
            out string isVisible,
            out string playerCount,
            out string maxPlayers)
        {
            string placeholder = isRequest ? "<pending>" : "<unavailable>";
            actualMode = placeholder;
            actualSession = placeholder;
            isOpen = placeholder;
            isVisible = placeholder;
            playerCount = placeholder;
            maxPlayers = placeholder;
            if (!isSuccess || runner == null) return;

            try
            {
                actualMode = runner.GameMode.ToString();
                SessionInfo sessionInfo = runner.SessionInfo;
                if (!sessionInfo.IsValid) return;

                actualSession = ValueOrLabel(sessionInfo.Name, "unreported");
                isOpen = sessionInfo.IsOpen.ToString();
                isVisible = sessionInfo.IsVisible.ToString();
                playerCount = sessionInfo.PlayerCount.ToString(CultureInfo.InvariantCulture);
                maxPlayers = sessionInfo.MaxPlayers.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // A shutdown racing this observational log must not affect session lifecycle.
            }
        }

        private static string GetProcessContext()
        {
            string runtime = Application.isEditor ? "editor" : "player";
            string platform = Application.platform.ToString();
            try
            {
                using (System.Diagnostics.Process process =
                       System.Diagnostics.Process.GetCurrentProcess())
                {
                    return $"{process.ProcessName}:{process.Id}/{runtime}/{platform}";
                }
            }
            catch (Exception)
            {
                return $"unknown:unknown/{runtime}/{platform}";
            }
        }

        private static string FormatAppIdForDiagnostic(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return "sha256=<missing> suffix=<missing>";
            }

            string normalized = appId.Trim().ToLowerInvariant();
            string suffix = normalized.Length > 4
                ? normalized.Substring(normalized.Length - 4)
                : "<short>";
            string fingerprint = "<unavailable>";
            try
            {
                using (SHA256 sha256 = SHA256.Create())
                {
                    byte[] digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                    fingerprint = BitConverter
                        .ToString(digest, 0, 8)
                        .Replace("-", string.Empty)
                        .ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                // The suffix still lets two local logs be compared on platforms without SHA-256.
            }

            return $"sha256={fingerprint} suffix={suffix}";
        }

        private static string RedactExactValue(string value, string valueToRedact)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(valueToRedact))
            {
                return value ?? string.Empty;
            }

            var result = new StringBuilder(value.Length);
            int copiedUntil = 0;
            while (copiedUntil < value.Length)
            {
                int match = value.IndexOf(
                    valueToRedact,
                    copiedUntil,
                    StringComparison.OrdinalIgnoreCase);
                if (match < 0)
                {
                    result.Append(value, copiedUntil, value.Length - copiedUntil);
                    break;
                }

                result.Append(value, copiedUntil, match - copiedUntil);
                result.Append("<app-id-redacted>");
                copiedUntil = match + valueToRedact.Length;
            }

            return result.ToString();
        }

        private static string ValueOrLabel(string value, string label)
        {
            return string.IsNullOrWhiteSpace(value)
                ? $"<{label}>"
                : value.Trim();
        }

        private static string QuoteDiagnosticValue(string value)
        {
            const int maximumInputLength = 512;
            value ??= "<null>";
            if (value.Length > maximumInputLength)
            {
                value = value.Substring(0, maximumInputLength) + "<truncated>";
            }

            var quoted = new StringBuilder(value.Length + 2);
            quoted.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                switch (character)
                {
                    case '\\':
                        quoted.Append("\\\\");
                        break;
                    case '"':
                        quoted.Append("\\\"");
                        break;
                    case '\r':
                        quoted.Append("\\r");
                        break;
                    case '\n':
                        quoted.Append("\\n");
                        break;
                    case '\t':
                        quoted.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(character))
                        {
                            quoted.Append("\\u");
                            quoted.Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            quoted.Append(character);
                        }
                        break;
                }
            }
            quoted.Append('"');
            return quoted.ToString();
        }

        private void EnsureTransport()
        {
            if (m_TransportBridge != null) return;
            m_TransportBridge = GetComponent<FusionTransportBridge>();
            if (m_TransportBridge == null)
            {
                m_TransportBridge = gameObject.AddComponent<FusionTransportBridge>();
            }
        }

        private void CleanupFailedRunner(NetworkRunner runner)
        {
            if (m_TransportBridge != null && m_TransportBridge.Runner == runner)
            {
                m_TransportBridge.Unbind();
            }

            if (m_Runner == runner) m_Runner = null;
            m_OwnsRunner = false;
            if (runner != null) Destroy(runner.gameObject);
        }
    }
}
