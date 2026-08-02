using System;
using System.Reflection;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.Events;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Connects an optional Steam lobby provider to PurrNet's SteamTransport.
    /// The component itself has no Steamworks dependency and remains usable as a
    /// clear, non-operational stub when no Steam provider is installed.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Steam Lobby Network")]
    [DefaultExecutionOrder(-500)]
    public sealed class PurrNetSteamLobbyNetwork : MonoBehaviour
    {
        private const string SteamTransportTypeName = "PurrNet.Steam.SteamTransport";
        private const string DefaultProtocolVersion = "1";

        [Header("References")]
        [Tooltip("Optional scene NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("A component implementing IPurrNetSteamLobbyProvider. The optional Steam lobby demo uses the conditionally compiled Steamworks.NET provider.")]
        [SerializeField] private MonoBehaviour m_LobbyProvider;

        [Header("Lobby")]
        [SerializeField] private PurrNetSteamLobbyVisibility m_Visibility =
            PurrNetSteamLobbyVisibility.FriendsOnly;

        [SerializeField, Min(2)] private int m_MaxMembers = 4;

        [Tooltip("Reject lobbies created by another product. Leave empty to use Application.identifier, then Application.productName as a fallback.")]
        [SerializeField] private string m_ProductCompatibilityId = string.Empty;

        [Tooltip("Reject lobbies created with another Application.version.")]
        [SerializeField] private bool m_RequireMatchingBuild = true;

        [Tooltip("Networking protocol marker stored in lobby metadata. Change this when a release is wire-incompatible.")]
        [SerializeField] private string m_ProtocolVersion = DefaultProtocolVersion;

        [Tooltip("Seconds allowed for PurrNet's server and client connection halves to become connected.")]
        [SerializeField, Min(1f)] private float m_ConnectionTimeoutSeconds = 30f;

        [Header("Events")]
        [SerializeField] private UnityEvent m_OnSteamReady = new UnityEvent();
        [SerializeField] private UnityEvent m_OnLobbyCreated = new UnityEvent();
        [SerializeField] private UnityEvent m_OnSessionConnected = new UnityEvent();
        [SerializeField] private UnityEvent m_OnDisconnected = new UnityEvent();
        [SerializeField] private UnityEvent<string> m_OnError = new UnityEvent<string>();

        private IPurrNetSteamLobbyProvider m_Provider;
        private NetworkManager m_HookedManager;
        private PurrNetSteamLobbySessionState m_State;
        private string m_StatusMessage = "Waiting for a Steam lobby provider.";
        private string m_LastError = string.Empty;
        private bool m_ReadyRaised;
        private bool m_ShuttingDown;
        private bool m_RollingBack;
        private bool m_HostServerConnected;
        private bool m_HostClientConnected;
        private float m_ConnectionDeadline;

        public PurrNetSteamLobbySessionState State => m_State;
        public bool IsAvailable => m_Provider != null && m_Provider.IsAvailable;
        public bool IsSteamReady => m_Provider != null && m_Provider.IsInitialized;
        public bool IsBusy => m_State == PurrNetSteamLobbySessionState.InitializingSteam ||
                              m_State == PurrNetSteamLobbySessionState.CreatingLobby ||
                              m_State == PurrNetSteamLobbySessionState.JoiningLobby ||
                              m_State == PurrNetSteamLobbySessionState.StartingHost ||
                              m_State == PurrNetSteamLobbySessionState.StartingClient ||
                              m_State == PurrNetSteamLobbySessionState.Leaving;
        public string StatusMessage => m_StatusMessage;
        public string LastError => m_LastError;
        public string LocalSteamId => m_Provider?.LocalSteamId ?? string.Empty;
        public string CurrentLobbyId => m_Provider?.CurrentLobbyId ?? string.Empty;
        public NetworkManager ActiveNetworkManager => ActiveManager;

        public UnityEvent OnSteamReady => m_OnSteamReady;
        public UnityEvent OnLobbyCreated => m_OnLobbyCreated;
        public UnityEvent OnSessionConnected => m_OnSessionConnected;
        public UnityEvent OnDisconnected => m_OnDisconnected;
        public UnityEvent<string> OnError => m_OnError;

        public event Action StateChanged;
        public event Action<string> Error;

        private NetworkManager ActiveManager =>
            m_NetworkManager != null ? m_NetworkManager : NetworkManager.main;

        private void Awake()
        {
            ResolveProvider();
            HookNetworkManager();
            RefreshAvailabilityState();
        }

        private void OnEnable()
        {
            ResolveProvider();
            SubscribeProvider();
            HookNetworkManager();

            if (m_Provider != null && m_Provider.IsAvailable)
            {
                SetState(
                    m_Provider.IsInitialized
                        ? PurrNetSteamLobbySessionState.Ready
                        : PurrNetSteamLobbySessionState.InitializingSteam,
                    m_Provider.IsInitialized
                        ? "Steam is ready."
                        : "Initializing Steam...");
                m_Provider.Initialize();
            }
            else
            {
                SetState(
                    PurrNetSteamLobbySessionState.Unavailable,
                    m_Provider == null
                        ? "No IPurrNetSteamLobbyProvider component is assigned."
                        : m_Provider.StatusMessage);
            }
        }

        private void Start()
        {
            RaiseReadyIfNeeded();
        }

        private void Update()
        {
            if (m_HookedManager != ActiveManager) HookNetworkManager();
            RaiseReadyIfNeeded();

            if ((m_State == PurrNetSteamLobbySessionState.StartingHost ||
                 m_State == PurrNetSteamLobbySessionState.StartingClient) &&
                m_ConnectionDeadline > 0f &&
                Time.realtimeSinceStartup >= m_ConnectionDeadline)
            {
                AbortActiveSession(
                    m_State == PurrNetSteamLobbySessionState.StartingHost
                        ? "Timed out while starting the PurrNet host."
                        : "Timed out while connecting to the PurrNet host.");
                return;
            }

            if (m_Provider == null || m_Provider.State != PurrNetSteamLobbyProviderState.Error)
                return;

            if (m_State != PurrNetSteamLobbySessionState.Error)
            {
                Fail(m_Provider.StatusMessage);
            }
        }

        private void OnDisable()
        {
            if (!m_ShuttingDown &&
                (m_State == PurrNetSteamLobbySessionState.CreatingLobby ||
                 m_State == PurrNetSteamLobbySessionState.JoiningLobby ||
                 m_State == PurrNetSteamLobbySessionState.StartingHost ||
                 m_State == PurrNetSteamLobbySessionState.StartingClient ||
                 m_State == PurrNetSteamLobbySessionState.Hosting ||
                 m_State == PurrNetSteamLobbySessionState.Connected ||
                 IsNetworkRunning() ||
                 !string.IsNullOrWhiteSpace(CurrentLobbyId)))
            {
                Leave();
            }
            UnsubscribeProvider();
            UnhookNetworkManager();
        }

        private void OnApplicationQuit()
        {
            ShutdownSession();
        }

        private void OnDestroy()
        {
            ShutdownSession();
        }

        public void Host()
        {
            if (!CanBeginSession("host")) return;

            var request = new PurrNetSteamLobbyCreateRequest
            {
                visibility = m_Visibility,
                maxMembers = Mathf.Max(2, m_MaxMembers),
                product = GetProductCompatibilityId(),
                buildVersion = Application.version ?? string.Empty,
                protocolVersion = GetProtocolVersion()
            };

            SetState(
                PurrNetSteamLobbySessionState.CreatingLobby,
                "Creating Steam lobby...");
            m_Provider.CreateLobby(request);
        }

        public void JoinLobby(string lobbyId)
        {
            if (!CanBeginSession("join")) return;

            string trimmedLobbyId = lobbyId?.Trim();
            if (!ulong.TryParse(trimmedLobbyId, out ulong parsedLobbyId) || parsedLobbyId == 0)
            {
                Fail("Enter a valid decimal Steam lobby ID.");
                return;
            }

            var request = new PurrNetSteamLobbyJoinRequest
            {
                lobbyId = trimmedLobbyId,
                expectedProduct = GetProductCompatibilityId(),
                expectedBuildVersion = Application.version ?? string.Empty,
                expectedProtocolVersion = GetProtocolVersion(),
                requireMatchingBuild = m_RequireMatchingBuild
            };

            SetState(
                PurrNetSteamLobbySessionState.JoiningLobby,
                $"Joining Steam lobby {trimmedLobbyId}...");
            m_Provider.JoinLobby(request);
        }

        public void OpenInviteOverlay()
        {
            if (m_Provider == null || !m_Provider.IsInitialized)
            {
                ReportNonFatalError("Steam is not initialized.");
                return;
            }

            if (string.IsNullOrWhiteSpace(m_Provider.CurrentLobbyId))
            {
                ReportNonFatalError(
                    "Join or create a Steam lobby before opening the invite overlay.");
                return;
            }

            m_Provider.OpenInviteOverlay();
        }

        public void Leave()
        {
            if (m_ShuttingDown) return;

            SetState(PurrNetSteamLobbySessionState.Leaving, "Leaving session...");
            StopPurrNet();
            m_Provider?.LeaveLobby();
            m_HostServerConnected = false;
            m_HostClientConnected = false;
            m_ConnectionDeadline = 0f;
            SetState(
                IsSteamReady
                    ? PurrNetSteamLobbySessionState.Ready
                    : PurrNetSteamLobbySessionState.Unavailable,
                IsSteamReady ? "Disconnected. Steam is ready." : "Disconnected.");
            m_OnDisconnected?.Invoke();
        }

        private void HandleLobbyCreated(PurrNetSteamLobbyCreated result)
        {
            if (m_State != PurrNetSteamLobbySessionState.CreatingLobby) return;

            NetworkManager manager = ActiveManager;
            if (!TryConfigureSteamTransport(manager, result.hostSteamId, out string error))
            {
                m_Provider?.LeaveLobby();
                Fail(error);
                return;
            }

            try
            {
                m_HostServerConnected = false;
                m_HostClientConnected = false;
                BeginConnectionTimeout();
                SetState(
                    PurrNetSteamLobbySessionState.StartingHost,
                    "Lobby created. Starting PurrNet host...");
                m_OnLobbyCreated?.Invoke();
                if (m_State != PurrNetSteamLobbySessionState.StartingHost) return;
                manager.StartServer();
            }
            catch (Exception exception)
            {
                AbortActiveSession(
                    $"Could not start the PurrNet host: {exception.Message}");
                Debug.LogException(exception, this);
            }
        }

        private void HandleLobbyJoined(PurrNetSteamLobbyJoined result)
        {
            if (m_State != PurrNetSteamLobbySessionState.JoiningLobby) return;

            NetworkManager manager = ActiveManager;
            if (!TryConfigureSteamTransport(manager, result.hostSteamId, out string error))
            {
                m_Provider?.LeaveLobby();
                Fail(error);
                return;
            }

            try
            {
                BeginConnectionTimeout();
                SetState(
                    PurrNetSteamLobbySessionState.StartingClient,
                    "Lobby joined. Connecting to the PurrNet host...");
                manager.StartClient();
            }
            catch (Exception exception)
            {
                AbortActiveSession(
                    $"Could not start the PurrNet client: {exception.Message}");
                Debug.LogException(exception, this);
            }
        }

        private void HandleJoinRequested(string lobbyId)
        {
            if (IsNetworkRunning() || IsBusy ||
                !string.IsNullOrWhiteSpace(CurrentLobbyId))
            {
                ReportNonFatalError(
                    $"Steam requested lobby {lobbyId}, but a lobby or network operation is " +
                    "already active. Leave the current session before accepting another invite.");
                return;
            }

            JoinLobby(lobbyId);
        }

        private void HandleProviderReady()
        {
            RaiseReadyIfNeeded();
        }

        private void HandleProviderLeft()
        {
            if (m_State == PurrNetSteamLobbySessionState.Leaving) return;
            if (IsNetworkRunning() ||
                m_State == PurrNetSteamLobbySessionState.CreatingLobby ||
                m_State == PurrNetSteamLobbySessionState.JoiningLobby ||
                m_State == PurrNetSteamLobbySessionState.StartingHost ||
                m_State == PurrNetSteamLobbySessionState.StartingClient ||
                m_State == PurrNetSteamLobbySessionState.Hosting ||
                m_State == PurrNetSteamLobbySessionState.Connected)
            {
                AbortActiveSession(
                    "The Steam lobby was left while a lobby or PurrNet operation was active.");
                return;
            }
            StateChanged?.Invoke();
        }

        private void HandleProviderFailed(string message)
        {
            if (m_State == PurrNetSteamLobbySessionState.StartingHost ||
                m_State == PurrNetSteamLobbySessionState.Hosting ||
                m_State == PurrNetSteamLobbySessionState.StartingClient ||
                m_State == PurrNetSteamLobbySessionState.Connected)
            {
                AbortActiveSession(message);
                return;
            }

            Fail(message);
        }

        private void HandleServerConnectionState(ConnectionState state)
        {
            if (state == ConnectionState.Connected)
            {
                m_HostServerConnected = true;
                StartHostLocalClientIfReady();
                TryCompleteHostConnection();
                return;
            }

            if (state == ConnectionState.Disconnected &&
                (m_State == PurrNetSteamLobbySessionState.StartingHost ||
                 m_State == PurrNetSteamLobbySessionState.Hosting) &&
                !m_ShuttingDown &&
                !m_RollingBack)
            {
                AbortActiveSession(
                    "The PurrNet host stopped. The Steam lobby was closed.");
            }
        }

        private void HandleClientConnectionState(ConnectionState state)
        {
            if (state == ConnectionState.Connected)
            {
                if (m_State == PurrNetSteamLobbySessionState.StartingClient)
                {
                    SetState(
                        PurrNetSteamLobbySessionState.Connected,
                        $"Connected through Steam lobby {CurrentLobbyId}.");
                    m_ConnectionDeadline = 0f;
                    m_OnSessionConnected?.Invoke();
                    return;
                }

                if (m_State == PurrNetSteamLobbySessionState.StartingHost ||
                    m_State == PurrNetSteamLobbySessionState.Hosting)
                {
                    m_HostClientConnected = true;
                    TryCompleteHostConnection();
                }
                return;
            }

            if (state == ConnectionState.Disconnected &&
                (m_State == PurrNetSteamLobbySessionState.StartingClient ||
                 m_State == PurrNetSteamLobbySessionState.Connected ||
                 m_State == PurrNetSteamLobbySessionState.StartingHost ||
                 m_State == PurrNetSteamLobbySessionState.Hosting) &&
                !m_ShuttingDown &&
                !m_RollingBack)
            {
                AbortActiveSession(
                    m_State == PurrNetSteamLobbySessionState.StartingHost ||
                    m_State == PurrNetSteamLobbySessionState.Hosting
                        ? "The host's local PurrNet client disconnected. The Steam lobby was closed."
                        : "The PurrNet client disconnected. The Steam lobby was left.");
            }
        }

        private void StartHostLocalClientIfReady()
        {
            if (m_State != PurrNetSteamLobbySessionState.StartingHost ||
                !m_HostServerConnected ||
                m_HostClientConnected ||
                m_RollingBack)
            {
                return;
            }

            NetworkManager manager = ActiveManager;
            if (manager == null)
            {
                AbortActiveSession("The PurrNet NetworkManager disappeared while starting the host.");
                return;
            }

            if (manager.clientState != ConnectionState.Disconnected) return;

            try
            {
                SetState(
                    PurrNetSteamLobbySessionState.StartingHost,
                    "Steam lobby is ready. Connecting the host's local client...");
                manager.StartClient();
            }
            catch (Exception exception)
            {
                AbortActiveSession(
                    $"Could not start the host's local PurrNet client: {exception.Message}");
                Debug.LogException(exception, this);
            }
        }

        private void TryCompleteHostConnection()
        {
            if (!m_HostServerConnected || !m_HostClientConnected) return;

            if (m_State == PurrNetSteamLobbySessionState.StartingHost)
            {
                SetState(
                    PurrNetSteamLobbySessionState.Hosting,
                    $"Hosting Steam lobby {CurrentLobbyId}.");
                m_ConnectionDeadline = 0f;
                m_OnSessionConnected?.Invoke();
            }
        }

        private bool CanBeginSession(string action)
        {
            ResolveProvider();
            HookNetworkManager();

            if (IsBusy)
            {
                ReportNonFatalError(
                    $"Cannot {action}: another Steam or PurrNet startup operation is in progress.");
                return false;
            }

            if (m_Provider == null)
            {
                Fail(
                    "No Steam lobby provider is assigned. Add the built-in " +
                    "SteamworksNetPurrNetLobbyProvider or a custom provider.");
                return false;
            }

            if (!m_Provider.IsAvailable)
            {
                Fail(
                    "Steamworks.NET is not installed, or the current build target does not " +
                    "support Steam. The rest of the Networking Layer remains available.");
                return false;
            }

            if (!m_Provider.IsInitialized)
            {
                m_Provider.Initialize();
                if (!m_Provider.IsInitialized)
                {
                    Fail($"Cannot {action}: {m_Provider.StatusMessage}");
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(m_Provider.CurrentLobbyId) ||
                m_Provider.State == PurrNetSteamLobbyProviderState.CreatingLobby ||
                m_Provider.State == PurrNetSteamLobbyProviderState.JoiningLobby ||
                m_Provider.State == PurrNetSteamLobbyProviderState.InLobby)
            {
                ReportNonFatalError(
                    $"Cannot {action}: leave the current Steam lobby first.");
                return false;
            }

            NetworkManager manager = ActiveManager;
            if (manager == null)
            {
                Fail("No PurrNet NetworkManager is available in the scene.");
                return false;
            }

            if (manager.serverState != ConnectionState.Disconnected ||
                manager.clientState != ConnectionState.Disconnected)
            {
                ReportNonFatalError(
                    $"Cannot {action}: PurrNet is already active " +
                    $"(server {manager.serverState}, client {manager.clientState}).");
                return false;
            }

            if (!TryValidateSteamTransport(manager, out string transportError))
            {
                Fail(transportError);
                return false;
            }

            m_LastError = string.Empty;
            return true;
        }

        private static bool TryConfigureSteamTransport(
            NetworkManager manager,
            string hostSteamId,
            out string error)
        {
            if (!TryValidateSteamTransport(manager, out error)) return false;

            if (!ulong.TryParse(hostSteamId?.Trim(), out ulong parsedId) || parsedId == 0)
            {
                error = "The Steam lobby did not provide a valid host Steam ID.";
                return false;
            }

            GenericTransport transport = manager.transport;
            Type type = transport.GetType();
            try
            {
                SetRequiredProperty(type, transport, "peerToPeer", true);
                SetRequiredProperty(type, transport, "dedicatedServer", false);
                SetRequiredProperty(type, transport, "address", parsedId.ToString());
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not configure SteamTransport: {exception.Message}";
                return false;
            }
        }

        private static bool TryValidateSteamTransport(
            NetworkManager manager,
            out string error)
        {
            if (manager == null)
            {
                error = "No PurrNet NetworkManager is available.";
                return false;
            }

            GenericTransport transport = manager.transport;
            if (transport == null)
            {
                error = "PurrNet NetworkManager.transport is empty.";
                return false;
            }

            if (!string.Equals(
                    transport.GetType().FullName,
                    SteamTransportTypeName,
                    StringComparison.Ordinal))
            {
                error =
                    $"PurrNet NetworkManager.transport must reference {SteamTransportTypeName}; " +
                    $"it currently references {transport.GetType().FullName}.";
                return false;
            }

            if (!transport.isSupported)
            {
                error =
                    "SteamTransport is present but unsupported. Install Steamworks.NET and " +
                    "use a standalone Windows, Linux, or macOS target.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static void SetRequiredProperty(
            Type type,
            object target,
            string propertyName,
            object value)
        {
            PropertyInfo property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanWrite)
                throw new MissingMemberException(type.FullName, propertyName);
            property.SetValue(target, value);
        }

        private void ResolveProvider()
        {
            if (m_LobbyProvider != null &&
                m_LobbyProvider is IPurrNetSteamLobbyProvider assigned)
            {
                if (!ReferenceEquals(m_Provider, assigned))
                {
                    UnsubscribeProvider();
                    m_Provider = assigned;
                    SubscribeProvider();
                }
                return;
            }

            MonoBehaviour[] localComponents = GetComponents<MonoBehaviour>();
            for (int i = 0; i < localComponents.Length; i++)
            {
                if (!(localComponents[i] is IPurrNetSteamLobbyProvider provider)) continue;
                if (!ReferenceEquals(m_Provider, provider))
                {
                    UnsubscribeProvider();
                    m_Provider = provider;
                    SubscribeProvider();
                }
                m_LobbyProvider = localComponents[i];
                return;
            }

            UnsubscribeProvider();
            m_Provider = null;
        }

        private void SubscribeProvider()
        {
            if (m_Provider == null) return;
            m_Provider.Ready -= HandleProviderReady;
            m_Provider.LobbyCreated -= HandleLobbyCreated;
            m_Provider.LobbyJoined -= HandleLobbyJoined;
            m_Provider.JoinRequested -= HandleJoinRequested;
            m_Provider.LeftLobby -= HandleProviderLeft;
            m_Provider.Failed -= HandleProviderFailed;

            m_Provider.Ready += HandleProviderReady;
            m_Provider.LobbyCreated += HandleLobbyCreated;
            m_Provider.LobbyJoined += HandleLobbyJoined;
            m_Provider.JoinRequested += HandleJoinRequested;
            m_Provider.LeftLobby += HandleProviderLeft;
            m_Provider.Failed += HandleProviderFailed;
        }

        private void UnsubscribeProvider()
        {
            if (m_Provider == null) return;
            m_Provider.Ready -= HandleProviderReady;
            m_Provider.LobbyCreated -= HandleLobbyCreated;
            m_Provider.LobbyJoined -= HandleLobbyJoined;
            m_Provider.JoinRequested -= HandleJoinRequested;
            m_Provider.LeftLobby -= HandleProviderLeft;
            m_Provider.Failed -= HandleProviderFailed;
        }

        private void HookNetworkManager()
        {
            NetworkManager manager = ActiveManager;
            if (m_HookedManager == manager) return;
            UnhookNetworkManager();
            m_HookedManager = manager;
            if (m_HookedManager == null) return;
            m_HookedManager.onServerConnectionState += HandleServerConnectionState;
            m_HookedManager.onClientConnectionState += HandleClientConnectionState;
        }

        private void UnhookNetworkManager()
        {
            if (m_HookedManager == null) return;
            m_HookedManager.onServerConnectionState -= HandleServerConnectionState;
            m_HookedManager.onClientConnectionState -= HandleClientConnectionState;
            m_HookedManager = null;
        }

        private void RaiseReadyIfNeeded()
        {
            if (m_ReadyRaised || m_Provider == null || !m_Provider.IsInitialized) return;
            m_ReadyRaised = true;
            if (m_State != PurrNetSteamLobbySessionState.CreatingLobby &&
                m_State != PurrNetSteamLobbySessionState.JoiningLobby &&
                m_State != PurrNetSteamLobbySessionState.StartingHost &&
                m_State != PurrNetSteamLobbySessionState.StartingClient &&
                m_State != PurrNetSteamLobbySessionState.Hosting &&
                m_State != PurrNetSteamLobbySessionState.Connected &&
                m_State != PurrNetSteamLobbySessionState.Leaving)
            {
                SetState(PurrNetSteamLobbySessionState.Ready, "Steam is ready.");
            }
            m_OnSteamReady?.Invoke();
        }

        private void RefreshAvailabilityState()
        {
            if (m_Provider == null || !m_Provider.IsAvailable)
            {
                SetState(
                    PurrNetSteamLobbySessionState.Unavailable,
                    m_Provider == null
                        ? "No Steam lobby provider is assigned."
                        : m_Provider.StatusMessage);
                return;
            }

            SetState(
                m_Provider.IsInitialized
                    ? PurrNetSteamLobbySessionState.Ready
                    : PurrNetSteamLobbySessionState.InitializingSteam,
                m_Provider.StatusMessage);
        }

        private bool IsNetworkRunning()
        {
            NetworkManager manager = ActiveManager;
            return manager != null &&
                   (manager.serverState != ConnectionState.Disconnected ||
                    manager.clientState != ConnectionState.Disconnected);
        }

        private void StopPurrNet()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null) return;

            try
            {
                // StopClient also cancels NetworkManager's one-frame delayed startup
                // coroutine, so it must run even while clientState is still Disconnected.
                manager.StopClient();
                if (manager.serverState != ConnectionState.Disconnected)
                    manager.StopServer();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void BeginConnectionTimeout()
        {
            m_ConnectionDeadline = Time.realtimeSinceStartup +
                                   Mathf.Max(1f, m_ConnectionTimeoutSeconds);
        }

        private void AbortActiveSession(string message)
        {
            if (m_RollingBack || m_ShuttingDown) return;
            m_RollingBack = true;
            try
            {
                StopPurrNet();
                m_Provider?.LeaveLobby();
                m_HostServerConnected = false;
                m_HostClientConnected = false;
                m_ConnectionDeadline = 0f;
                Fail(message);
            }
            finally
            {
                m_RollingBack = false;
            }
            m_OnDisconnected?.Invoke();
        }

        private void ShutdownSession()
        {
            if (m_ShuttingDown) return;
            m_ShuttingDown = true;
            StopPurrNet();
            m_Provider?.LeaveLobby();
            m_Provider?.Shutdown();
        }

        private string GetProductCompatibilityId()
        {
            if (!string.IsNullOrWhiteSpace(m_ProductCompatibilityId))
                return m_ProductCompatibilityId.Trim();
            if (!string.IsNullOrWhiteSpace(Application.identifier))
                return Application.identifier.Trim();
            return (Application.productName ?? string.Empty).Trim();
        }

        private string GetProtocolVersion()
        {
            return string.IsNullOrWhiteSpace(m_ProtocolVersion)
                ? DefaultProtocolVersion
                : m_ProtocolVersion.Trim();
        }

        private void SetState(PurrNetSteamLobbySessionState state, string message)
        {
            bool changed = m_State != state ||
                           !string.Equals(m_StatusMessage, message, StringComparison.Ordinal);
            m_State = state;
            m_StatusMessage = message ?? string.Empty;
            if (changed) StateChanged?.Invoke();
        }

        private void Fail(string message)
        {
            m_LastError = string.IsNullOrWhiteSpace(message)
                ? "An unknown Steam lobby error occurred."
                : message.Trim();
            SetState(PurrNetSteamLobbySessionState.Error, m_LastError);
            Debug.LogError($"[PurrNet Steam Lobby] {m_LastError}", this);
            m_OnError?.Invoke(m_LastError);
            Error?.Invoke(m_LastError);
        }

        private void ReportNonFatalError(string message)
        {
            m_LastError = string.IsNullOrWhiteSpace(message)
                ? "The Steam lobby request could not be completed."
                : message.Trim();
            Debug.LogWarning($"[PurrNet Steam Lobby] {m_LastError}", this);
            m_OnError?.Invoke(m_LastError);
            Error?.Invoke(m_LastError);
        }
    }
}
