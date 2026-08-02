using System;
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
        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private NetworkRunner m_RunnerPrefab;
        [SerializeField] private FusionDefaultLaunchMode m_DefaultLaunchMode =
            FusionDefaultLaunchMode.Shared;
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

        public Task<StartGameResult> CreateSharedAsync(string sessionName)
        {
            return CreateSharedAsync(CreateDefaultStartOptions(sessionName));
        }

        public Task<StartGameResult> CreateSharedAsync(FusionSessionStartOptions options)
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
            bool allowSessionCreation)
        {
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
                    m_StartCancellation);
            PublishSessionLifecycleStateChanged(FusionSessionLifecycleState.Starting);
            return m_ActiveStartTask;
        }

        private async Task<StartGameResult> StartSessionAsync(
            GameMode gameMode,
            FusionSessionStartOptions options,
            bool allowSessionCreation,
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

                var args = new StartGameArgs
                {
                    GameMode = gameMode,
                    SessionName = options.SessionName,
                    PlayerCount = options.MaxPlayers ?? Mathf.Max(1, m_MaxPlayers),
                    EnableClientSessionCreation = allowSessionCreation,
                    SceneManager = sceneManager,
                    ObjectProvider = objectProvider,
                    Scene = sceneInfo,
                    AuthValues = authenticationValues,
                    CustomLobbyName = options.CustomLobbyName,
                    SessionProperties = options.CopySessionProperties(),
                    DisableNATPunchthrough =
                        gameMode != GameMode.Shared && options.ForcePhotonRelay,
                    StartGameCancellationToken = startCancellation.Token
                };

                // Nullable overrides are deliberately assigned after construction. Leaving
                // either value unset preserves Fusion's SDK default for existing callers.
                if (options.IsOpen.HasValue) args.IsOpen = options.IsOpen.Value;
                if (options.IsVisible.HasValue) args.IsVisible = options.IsVisible.Value;

                if (!string.IsNullOrWhiteSpace(options.Region))
                {
                    FusionAppSettings appSettings =
                        PhotonAppSettings.Global.AppSettings.GetCopy();
                    appSettings.UseNameServer = true;
                    appSettings.FixedRegion = options.Region;
                    args.CustomPhotonAppSettings = appSettings;
                }

                StartGameResult result = await runner.StartGame(args);
                // From this point the runner can safely be shut down even if ShutdownAsync
                // is called synchronously by a SessionStarted/SessionStartFailed subscriber.
                m_StartAwaitCompleted = true;
                if (result.Ok)
                {
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
