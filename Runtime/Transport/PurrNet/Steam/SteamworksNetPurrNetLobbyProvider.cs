#if STEAMWORKS_NET
#define ARAWN_STEAMWORKS_NET_PACKAGE
#endif

#if ARAWN_STEAMWORKS_NET_PACKAGE && (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX || UNITY_STANDALONE_OSX || STEAMWORKS_WIN || STEAMWORKS_LIN_OSX)
#define ARAWN_STEAMWORKS_LOBBY_AVAILABLE
#endif

#if !ARAWN_STEAMWORKS_LOBBY_AVAILABLE
#pragma warning disable CS0067
#pragma warning disable CS0414
#endif

using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using UnityEngine;

#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
using Steamworks;
#endif

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Steam
{
    /// <summary>
    /// Direct Steamworks.NET lobby backend. The component and its serialized data
    /// remain present when Steamworks.NET is absent; only the SDK implementation is
    /// conditionally compiled.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Steamworks.NET Lobby Provider")]
    [DefaultExecutionOrder(-1000)]
    public sealed class SteamworksNetPurrNetLobbyProvider :
        MonoBehaviour,
        IPurrNetSteamLobbyProvider
    {
        private const string HostSteamIdKey = "arawn_host_steam_id";
        private const string LegacyHostSteamIdKey = "hostSteamId";
        private const string ProductKey = "arawn_product";
        private const string BuildVersionKey = "arawn_build";
        private const string ProtocolVersionKey = "arawn_protocol";
        private const string TransportKey = "arawn_transport";
        private const string TransportValue = "purrnet-steam-p2p";
        private const string RichPresenceConnectKey = "connect";
        private const string ConnectLobbyArgument = "+connect_lobby";

        [Header("Steam API Ownership")]
        [Tooltip("Initialize Steamworks.NET when no other Steam bootstrap owns it. Disable this when Heathen, SteamManager, or another wrapper initializes Steam.")]
        [SerializeField] private bool m_InitializeSteamApi = true;

        [Tooltip("Run Steam callbacks every frame when this component initialized Steam.")]
        [SerializeField] private bool m_PumpOwnedCallbacks = true;

        [Tooltip("Normally an external Steam bootstrap also pumps callbacks. Enable only if it initializes Steam but deliberately leaves callback pumping to this component.")]
        [SerializeField] private bool m_PumpExternallyOwnedCallbacks;

        [Header("Networking")]
        [Tooltip("Warm Steam Datagram Relay access at initialization. This is non-blocking and does not force every connection through relay.")]
        [SerializeField] private bool m_WarmRelayAccess = true;

        [Tooltip("Seconds allowed for Steam to complete a lobby create or join request.")]
        [SerializeField, Min(1f)] private float m_LobbyOperationTimeoutSeconds = 20f;

        private PurrNetSteamLobbyProviderState m_State =
            PurrNetSteamLobbyProviderState.Unavailable;
        private string m_StatusMessage =
            "Steamworks.NET is not installed or this target is unsupported.";
        private string m_LocalSteamId = string.Empty;
        private string m_CurrentLobbyId = string.Empty;
        private bool m_OwnsSteamApi;
        private bool m_OwnsConnectRichPresence;
        private bool m_Shutdown;
        private string m_PendingJoinRequest;
        private int m_EarliestJoinDispatchFrame;
        private int m_LobbyOperationGeneration;
        private float m_LobbyOperationDeadline;

#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
        private CSteamID m_CurrentLobby;
        private CallResult<LobbyCreated_t> m_CreateLobbyResult;
        private CallResult<LobbyEnter_t> m_JoinLobbyResult;
        private readonly List<CallResult<LobbyCreated_t>> m_StaleCreateResults =
            new List<CallResult<LobbyCreated_t>>();
        private readonly List<CallResult<LobbyEnter_t>> m_StaleJoinResults =
            new List<CallResult<LobbyEnter_t>>();
        private Callback<GameLobbyJoinRequested_t> m_LobbyJoinRequested;
        private Callback<GameRichPresenceJoinRequested_t> m_RichPresenceJoinRequested;
#endif

        public bool IsAvailable
        {
            get
            {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
                return true;
#else
                return false;
#endif
            }
        }

        public bool IsInitialized
        {
            get
            {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
                return CallbackDispatcher.IsInitialized &&
                       !string.IsNullOrWhiteSpace(m_LocalSteamId);
#else
                return false;
#endif
            }
        }

        public PurrNetSteamLobbyProviderState State => m_State;
        public string StatusMessage => m_StatusMessage;
        public string LocalSteamId => m_LocalSteamId;
        public string CurrentLobbyId => m_CurrentLobbyId;

        public event Action Ready;
        public event Action<PurrNetSteamLobbyCreated> LobbyCreated;
        public event Action<PurrNetSteamLobbyJoined> LobbyJoined;
        public event Action<string> JoinRequested;
        public event Action LeftLobby;
        public event Action<string> Failed;

        private void Awake()
        {
            if (m_InitializeSteamApi) Initialize();
            else SetState(
                IsAvailable
                    ? PurrNetSteamLobbyProviderState.Initializing
                    : PurrNetSteamLobbyProviderState.Unavailable,
                IsAvailable
                    ? "Waiting for an external Steam initializer..."
                    : "Steamworks.NET is not installed or this target is unsupported.");
        }

        private void OnEnable()
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (CallbackDispatcher.IsInitialized) CompleteInitialization(m_OwnsSteamApi);
#endif
        }

        private void Start()
        {
            QueueColdStartInvite();
        }

        private void Update()
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (!IsInitialized && CallbackDispatcher.IsInitialized)
                CompleteInitialization(m_OwnsSteamApi);

            if (IsInitialized &&
                ((m_OwnsSteamApi && m_PumpOwnedCallbacks) ||
                 (!m_OwnsSteamApi && m_PumpExternallyOwnedCallbacks)))
            {
                try
                {
                    SteamAPI.RunCallbacks();
                }
                catch (Exception exception)
                {
                    Fail($"Steam callback dispatch failed: {exception.Message}");
                }
            }

            DisposeCompletedStaleOperations();
#endif

            if (IsInitialized &&
                !string.IsNullOrWhiteSpace(m_PendingJoinRequest) &&
                Time.frameCount >= m_EarliestJoinDispatchFrame)
            {
                string lobbyId = m_PendingJoinRequest;
                m_PendingJoinRequest = null;
                JoinRequested?.Invoke(lobbyId);
            }

            if ((m_State == PurrNetSteamLobbyProviderState.CreatingLobby ||
                 m_State == PurrNetSteamLobbyProviderState.JoiningLobby) &&
                m_LobbyOperationDeadline > 0f &&
                Time.realtimeSinceStartup >= m_LobbyOperationDeadline)
            {
                CancelPendingLobbyOperation(
                    m_State == PurrNetSteamLobbyProviderState.CreatingLobby
                        ? "Steam timed out while creating the lobby."
                        : "Steam timed out while joining the lobby.");
            }
        }

        private void OnDisable()
        {
            if (!m_Shutdown) LeaveLobby();
            DisposeCallbacks();
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        public void Initialize()
        {
            if (m_Shutdown)
            {
                Fail("This Steam lobby provider has already shut down.");
                return;
            }

#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (CallbackDispatcher.IsInitialized)
            {
                CompleteInitialization(m_OwnsSteamApi);
                return;
            }

            if (!m_InitializeSteamApi)
            {
                SetState(
                    PurrNetSteamLobbyProviderState.Initializing,
                    "Waiting for an external Steam initializer...");
                return;
            }

            SetState(
                PurrNetSteamLobbyProviderState.Initializing,
                "Initializing Steamworks.NET...");
            try
            {
                ESteamAPIInitResult result = SteamAPI.InitEx(out string errorMessage);
                if (result != ESteamAPIInitResult.k_ESteamAPIInitResult_OK)
                {
                    Fail(
                        $"SteamAPI.InitEx failed ({result}). " +
                        (string.IsNullOrWhiteSpace(errorMessage)
                            ? "Confirm Steam is running and the App ID is configured."
                            : errorMessage));
                    return;
                }

                m_OwnsSteamApi = true;
                CompleteInitialization(ownsSteamApi: true);
            }
            catch (Exception exception)
            {
                Fail($"Steam initialization failed: {exception.Message}");
            }
#else
            SetState(
                PurrNetSteamLobbyProviderState.Unavailable,
                "Steamworks.NET is not installed or this target is unsupported.");
#endif
        }

        public void CreateLobby(PurrNetSteamLobbyCreateRequest request)
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (!CanStartLobbyOperation("create a lobby")) return;

            try
            {
                int maxMembers = Mathf.Clamp(request.maxMembers, 2, 250);
                ELobbyType lobbyType = ToSteamLobbyType(request.visibility);
                SetState(
                    PurrNetSteamLobbyProviderState.CreatingLobby,
                    "Creating Steam lobby...");
                SteamAPICall_t apiCall =
                    SteamMatchmaking.CreateLobby(lobbyType, maxMembers);
                if (apiCall == SteamAPICall_t.Invalid)
                {
                    Fail("Steam rejected the lobby creation request before it started.");
                    return;
                }

                int operationGeneration = ++m_LobbyOperationGeneration;
                m_LobbyOperationDeadline = Time.realtimeSinceStartup +
                                           Mathf.Max(1f, m_LobbyOperationTimeoutSeconds);
                m_CreateLobbyResult.Set(
                    apiCall,
                    (result, ioFailure) => HandleLobbyCreated(
                        result,
                        ioFailure,
                        request,
                        operationGeneration));
            }
            catch (Exception exception)
            {
                AbandonPendingLobbyOperations();
                Fail($"Could not start Steam lobby creation: {exception.Message}");
            }
#else
            ReportUnavailable("create a lobby");
#endif
        }

        public void JoinLobby(PurrNetSteamLobbyJoinRequest request)
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (!CanStartLobbyOperation("join a lobby")) return;
            if (!TryParseSteamId(request.lobbyId, out CSteamID lobbyId))
            {
                Fail("Enter a valid decimal Steam lobby ID.");
                return;
            }

            try
            {
                SetState(
                    PurrNetSteamLobbyProviderState.JoiningLobby,
                    $"Joining Steam lobby {lobbyId.m_SteamID}...");
                SteamAPICall_t apiCall = SteamMatchmaking.JoinLobby(lobbyId);
                if (apiCall == SteamAPICall_t.Invalid)
                {
                    Fail("Steam rejected the lobby join request before it started.");
                    return;
                }

                int operationGeneration = ++m_LobbyOperationGeneration;
                m_LobbyOperationDeadline = Time.realtimeSinceStartup +
                                           Mathf.Max(1f, m_LobbyOperationTimeoutSeconds);
                m_JoinLobbyResult.Set(
                    apiCall,
                    (result, ioFailure) => HandleLobbyEntered(
                        result,
                        ioFailure,
                        request,
                        operationGeneration));
            }
            catch (Exception exception)
            {
                AbandonPendingLobbyOperations();
                Fail($"Could not start the Steam lobby join: {exception.Message}");
            }
#else
            ReportUnavailable("join a lobby");
#endif
        }

        public void OpenInviteOverlay()
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (!IsInitialized)
            {
                Debug.LogWarning(
                    "[PurrNet Steam Lobby] Steam is not initialized; the invite overlay " +
                    "cannot be opened.",
                    this);
                return;
            }
            if (!m_CurrentLobby.IsValid())
            {
                Debug.LogWarning(
                    "[PurrNet Steam Lobby] Create or join a Steam lobby before opening " +
                    "the invite overlay.",
                    this);
                return;
            }
            try
            {
                SteamFriends.ActivateGameOverlayInviteDialog(m_CurrentLobby);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[PurrNet Steam Lobby] Could not open the Steam invite overlay: " +
                    exception.Message,
                    this);
            }
#else
            ReportUnavailable("open the invite overlay");
#endif
        }

        public void LeaveLobby()
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            AbandonPendingLobbyOperations();

            if (CallbackDispatcher.IsInitialized && m_CurrentLobby.IsValid())
            {
                try
                {
                    CSteamID owner = SteamMatchmaking.GetLobbyOwner(m_CurrentLobby);
                    if (owner == SteamUser.GetSteamID())
                        SteamMatchmaking.SetLobbyJoinable(m_CurrentLobby, false);
                    SteamMatchmaking.LeaveLobby(m_CurrentLobby);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        $"[PurrNet Steam Lobby] Could not leave the Steam lobby cleanly: " +
                        exception.Message,
                        this);
                }
            }

            ClearOwnedRichPresence();
            m_CurrentLobby = CSteamID.Nil;
#endif
            bool hadLobby = !string.IsNullOrWhiteSpace(m_CurrentLobbyId);
            m_CurrentLobbyId = string.Empty;
            if (IsInitialized)
                SetState(PurrNetSteamLobbyProviderState.Ready, "Steam is ready.");
            else if (!IsAvailable)
                SetState(
                    PurrNetSteamLobbyProviderState.Unavailable,
                    "Steamworks.NET is not installed or this target is unsupported.");
            if (hadLobby) LeftLobby?.Invoke();
        }

        public void Shutdown()
        {
            if (m_Shutdown) return;
            LeaveLobby();
            DisposeCallbacks(forceOperationResults: m_OwnsSteamApi);

#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            if (m_OwnsSteamApi && CallbackDispatcher.IsInitialized)
            {
                try
                {
                    SteamAPI.Shutdown();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        $"[PurrNet Steam Lobby] Steam shutdown failed: {exception.Message}",
                        this);
                }
            }
#endif
            m_OwnsSteamApi = false;
            m_LocalSteamId = string.Empty;
            m_Shutdown = true;
        }

#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
        private void CompleteInitialization(bool ownsSteamApi)
        {
            if (!CallbackDispatcher.IsInitialized || m_Shutdown) return;

            try
            {
                CSteamID localId = SteamUser.GetSteamID();
                if (!localId.IsValid())
                {
                    Fail("Steam initialized, but the local Steam user ID is invalid.");
                    return;
                }

                m_OwnsSteamApi |= ownsSteamApi;
                m_LocalSteamId = localId.m_SteamID.ToString();
                EnsureCallbacks();
                if (m_WarmRelayAccess) SteamNetworkingUtils.InitRelayNetworkAccess();

                bool wasReady = m_State == PurrNetSteamLobbyProviderState.Ready;
                SetState(PurrNetSteamLobbyProviderState.Ready, "Steam is ready.");
                if (!wasReady) Ready?.Invoke();
            }
            catch (Exception exception)
            {
                Fail($"Steam became available but could not be queried: {exception.Message}");
            }
        }

        private void EnsureCallbacks()
        {
            if (m_CreateLobbyResult == null)
                m_CreateLobbyResult = CallResult<LobbyCreated_t>.Create();
            if (m_JoinLobbyResult == null)
                m_JoinLobbyResult = CallResult<LobbyEnter_t>.Create();
            if (m_LobbyJoinRequested == null)
                m_LobbyJoinRequested =
                    Callback<GameLobbyJoinRequested_t>.Create(HandleLobbyJoinRequested);
            if (m_RichPresenceJoinRequested == null)
                m_RichPresenceJoinRequested =
                    Callback<GameRichPresenceJoinRequested_t>.Create(
                        HandleRichPresenceJoinRequested);
        }

        private void HandleLobbyCreated(
            LobbyCreated_t result,
            bool ioFailure,
            PurrNetSteamLobbyCreateRequest request,
            int operationGeneration)
        {
            if (operationGeneration != m_LobbyOperationGeneration)
            {
                LeaveStaleCreatedLobby(result, ioFailure);
                return;
            }

            m_LobbyOperationDeadline = 0f;
            try
            {
                if (ioFailure || result.m_eResult != EResult.k_EResultOK)
                {
                    Fail(
                        ioFailure
                            ? "Steam I/O failed while creating the lobby."
                            : $"Steam could not create the lobby: {result.m_eResult}.");
                    return;
                }

                m_CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);
                if (!m_CurrentLobby.IsValid())
                {
                    Fail("Steam returned an invalid lobby ID after creation.");
                    return;
                }

                string localId = m_LocalSteamId;
                bool metadataWritten =
                    SteamMatchmaking.SetLobbyData(
                        m_CurrentLobby,
                        HostSteamIdKey,
                        localId) &&
                    SteamMatchmaking.SetLobbyData(
                        m_CurrentLobby,
                        ProductKey,
                        request.product ?? string.Empty) &&
                    SteamMatchmaking.SetLobbyData(
                        m_CurrentLobby,
                        BuildVersionKey,
                        request.buildVersion ?? string.Empty) &&
                    SteamMatchmaking.SetLobbyData(
                        m_CurrentLobby,
                        ProtocolVersionKey,
                        request.protocolVersion ?? string.Empty) &&
                    SteamMatchmaking.SetLobbyData(
                        m_CurrentLobby,
                        TransportKey,
                        TransportValue);

                if (!metadataWritten)
                {
                    LeaveLobby();
                    Fail("Steam created the lobby but rejected required lobby metadata.");
                    return;
                }

                if (!SteamMatchmaking.SetLobbyJoinable(m_CurrentLobby, true))
                {
                    LeaveLobby();
                    Fail("Steam created the lobby but could not make it joinable.");
                    return;
                }

                string lobbyId = m_CurrentLobby.m_SteamID.ToString();
                m_OwnsConnectRichPresence = SteamFriends.SetRichPresence(
                    RichPresenceConnectKey,
                    $"{ConnectLobbyArgument} {lobbyId}");

                m_CurrentLobbyId = lobbyId;
                SetState(
                    PurrNetSteamLobbyProviderState.InLobby,
                    $"Created Steam lobby {lobbyId}.");
                LobbyCreated?.Invoke(new PurrNetSteamLobbyCreated(lobbyId, localId));
            }
            catch (Exception exception)
            {
                LeaveLobby();
                Fail($"Steam lobby setup failed after creation: {exception.Message}");
            }
        }

        private void HandleLobbyEntered(
            LobbyEnter_t result,
            bool ioFailure,
            PurrNetSteamLobbyJoinRequest request,
            int operationGeneration)
        {
            if (operationGeneration != m_LobbyOperationGeneration)
            {
                LeaveStaleJoinedLobby(result, ioFailure);
                return;
            }

            m_LobbyOperationDeadline = 0f;
            try
            {
                EChatRoomEnterResponse response =
                    (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse;
                if (ioFailure ||
                    response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
                {
                    Fail(
                        ioFailure
                            ? "Steam I/O failed while joining the lobby."
                            : $"Steam could not join the lobby: {response}.");
                    return;
                }

                m_CurrentLobby = new CSteamID(result.m_ulSteamIDLobby);
                if (!m_CurrentLobby.IsValid())
                {
                    Fail("Steam returned an invalid lobby ID after joining.");
                    return;
                }

                if (!ValidateLobbyMetadata(
                        request,
                        out string hostSteamId,
                        out string error))
                {
                    LeaveLobby();
                    Fail(error);
                    return;
                }

                string lobbyId = m_CurrentLobby.m_SteamID.ToString();
                m_CurrentLobbyId = lobbyId;
                SetState(
                    PurrNetSteamLobbyProviderState.InLobby,
                    $"Joined Steam lobby {lobbyId}.");
                LobbyJoined?.Invoke(new PurrNetSteamLobbyJoined(lobbyId, hostSteamId));
            }
            catch (Exception exception)
            {
                LeaveLobby();
                Fail($"Steam lobby validation failed after joining: {exception.Message}");
            }
        }

        private void LeaveStaleCreatedLobby(
            LobbyCreated_t result,
            bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK) return;
            LeaveStaleLobby(new CSteamID(result.m_ulSteamIDLobby));
        }

        private void LeaveStaleJoinedLobby(
            LobbyEnter_t result,
            bool ioFailure)
        {
            if (ioFailure ||
                (EChatRoomEnterResponse)result.m_EChatRoomEnterResponse !=
                EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                return;
            }
            LeaveStaleLobby(new CSteamID(result.m_ulSteamIDLobby));
        }

        private void LeaveStaleLobby(CSteamID lobby)
        {
            if (!CallbackDispatcher.IsInitialized || !lobby.IsValid()) return;
            if (m_CurrentLobby.IsValid() && m_CurrentLobby == lobby &&
                !string.IsNullOrWhiteSpace(m_CurrentLobbyId))
            {
                return;
            }
            try
            {
                SteamMatchmaking.LeaveLobby(lobby);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[PurrNet Steam Lobby] Could not leave a completed stale lobby: " +
                    exception.Message);
            }
        }

        private bool ValidateLobbyMetadata(
            PurrNetSteamLobbyJoinRequest request,
            out string hostSteamId,
            out string error)
        {
            hostSteamId = SteamMatchmaking.GetLobbyData(
                m_CurrentLobby,
                HostSteamIdKey);
            if (string.IsNullOrWhiteSpace(hostSteamId))
            {
                hostSteamId = SteamMatchmaking.GetLobbyData(
                    m_CurrentLobby,
                    LegacyHostSteamIdKey);
            }

            CSteamID owner = SteamMatchmaking.GetLobbyOwner(m_CurrentLobby);
            if (!owner.IsValid())
            {
                error = "The Steam lobby has no valid owner.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(hostSteamId))
                hostSteamId = owner.m_SteamID.ToString();
            if (!TryParseSteamId(hostSteamId, out CSteamID metadataHost) ||
                metadataHost != owner)
            {
                error = "The lobby host metadata does not match the Steam lobby owner.";
                return false;
            }

            string transport = SteamMatchmaking.GetLobbyData(
                m_CurrentLobby,
                TransportKey);
            if (!string.Equals(transport, TransportValue, StringComparison.Ordinal))
            {
                error =
                    "The lobby was not created for the GC2 PurrNet Steam transport.";
                return false;
            }

            string product = SteamMatchmaking.GetLobbyData(m_CurrentLobby, ProductKey);
            if (!string.Equals(
                    product,
                    request.expectedProduct ?? string.Empty,
                    StringComparison.Ordinal))
            {
                error = "The lobby belongs to a different product.";
                return false;
            }

            string protocol = SteamMatchmaking.GetLobbyData(
                m_CurrentLobby,
                ProtocolVersionKey);
            if (!string.Equals(
                    protocol,
                    request.expectedProtocolVersion ?? string.Empty,
                    StringComparison.Ordinal))
            {
                error =
                    $"The lobby uses networking protocol '{protocol}', but this build " +
                    $"requires '{request.expectedProtocolVersion}'.";
                return false;
            }

            if (request.requireMatchingBuild)
            {
                string build = SteamMatchmaking.GetLobbyData(
                    m_CurrentLobby,
                    BuildVersionKey);
                if (!string.Equals(
                        build,
                        request.expectedBuildVersion ?? string.Empty,
                        StringComparison.Ordinal))
                {
                    error =
                        $"The lobby uses build '{build}', but this client uses " +
                        $"'{request.expectedBuildVersion}'.";
                    return false;
                }
            }

            hostSteamId = metadataHost.m_SteamID.ToString();
            error = string.Empty;
            return true;
        }

        private void HandleLobbyJoinRequested(GameLobbyJoinRequested_t request)
        {
            QueueJoinRequest(request.m_steamIDLobby.m_SteamID.ToString());
        }

        private void HandleRichPresenceJoinRequested(
            GameRichPresenceJoinRequested_t request)
        {
            if (TryParseConnectLobbyArgument(request.m_rgchConnect, out string lobbyId))
                QueueJoinRequest(lobbyId);
        }

        private bool CanStartLobbyOperation(string operation)
        {
            if (!IsInitialized)
            {
                Fail($"Cannot {operation}: Steam is not initialized.");
                return false;
            }
            if (m_CurrentLobby.IsValid())
            {
                Fail($"Cannot {operation}: this client is already in a Steam lobby.");
                return false;
            }
            if ((m_CreateLobbyResult != null && m_CreateLobbyResult.IsActive()) ||
                (m_JoinLobbyResult != null && m_JoinLobbyResult.IsActive()))
            {
                Fail($"Cannot {operation}: another Steam lobby request is in progress.");
                return false;
            }
            EnsureCallbacks();
            return true;
        }

        private static ELobbyType ToSteamLobbyType(
            PurrNetSteamLobbyVisibility visibility)
        {
            switch (visibility)
            {
                case PurrNetSteamLobbyVisibility.Private:
                    return ELobbyType.k_ELobbyTypePrivate;
                case PurrNetSteamLobbyVisibility.Public:
                    return ELobbyType.k_ELobbyTypePublic;
                case PurrNetSteamLobbyVisibility.Invisible:
                    return ELobbyType.k_ELobbyTypeInvisible;
                default:
                    return ELobbyType.k_ELobbyTypeFriendsOnly;
            }
        }

        private static bool TryParseSteamId(string value, out CSteamID steamId)
        {
            if (ulong.TryParse(value?.Trim(), out ulong parsed) && parsed != 0)
            {
                steamId = new CSteamID(parsed);
                return steamId.IsValid();
            }
            steamId = CSteamID.Nil;
            return false;
        }

        private void ClearOwnedRichPresence()
        {
            if (!m_OwnsConnectRichPresence) return;
            m_OwnsConnectRichPresence = false;
            if (!CallbackDispatcher.IsInitialized) return;
            try
            {
                SteamFriends.SetRichPresence(RichPresenceConnectKey, string.Empty);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[PurrNet Steam Lobby] Could not clear join presence: " +
                    exception.Message,
                    this);
            }
        }
#endif

        private void QueueColdStartInvite()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (string.Equals(
                        arguments[i],
                        ConnectLobbyArgument,
                        StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < arguments.Length &&
                    ulong.TryParse(arguments[i + 1], out ulong lobbyId) &&
                    lobbyId != 0)
                {
                    QueueJoinRequest(lobbyId.ToString());
                    return;
                }

                if (TryParseConnectLobbyArgument(arguments[i], out string embeddedLobbyId))
                {
                    QueueJoinRequest(embeddedLobbyId);
                    return;
                }
            }
        }

        private static bool TryParseConnectLobbyArgument(
            string value,
            out string lobbyId)
        {
            lobbyId = string.Empty;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string[] tokens = value.Trim().Split(
                (char[])null,
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2 ||
                !string.Equals(
                    tokens[0],
                    ConnectLobbyArgument,
                    StringComparison.OrdinalIgnoreCase) ||
                !ulong.TryParse(tokens[1], out ulong parsed) ||
                parsed == 0)
            {
                return false;
            }

            lobbyId = parsed.ToString();
            return true;
        }

        private void QueueJoinRequest(string lobbyId)
        {
            if (!ulong.TryParse(lobbyId, out ulong parsed) || parsed == 0) return;
            m_PendingJoinRequest = parsed.ToString();
            m_EarliestJoinDispatchFrame = Time.frameCount + 1;
        }

        private void DisposeCallbacks(bool forceOperationResults = false)
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            DisposeOperationResults(forceOperationResults);
            m_LobbyJoinRequested?.Dispose();
            m_LobbyJoinRequested = null;
            m_RichPresenceJoinRequested?.Dispose();
            m_RichPresenceJoinRequested = null;
#endif
        }

#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
        private void DisposeOperationResults(bool force)
        {
            if (force)
            {
                m_CreateLobbyResult?.Dispose();
                m_CreateLobbyResult = null;
                m_JoinLobbyResult?.Dispose();
                m_JoinLobbyResult = null;
                DisposeAll(m_StaleCreateResults);
                DisposeAll(m_StaleJoinResults);
                return;
            }

            if (m_CreateLobbyResult != null && !m_CreateLobbyResult.IsActive())
            {
                m_CreateLobbyResult.Dispose();
                m_CreateLobbyResult = null;
            }
            if (m_JoinLobbyResult != null && !m_JoinLobbyResult.IsActive())
            {
                m_JoinLobbyResult.Dispose();
                m_JoinLobbyResult = null;
            }
            DisposeCompletedStaleOperations();
        }

        private void AbandonPendingLobbyOperations()
        {
            ++m_LobbyOperationGeneration;
            m_LobbyOperationDeadline = 0f;
            MoveToStale(ref m_CreateLobbyResult, m_StaleCreateResults);
            MoveToStale(ref m_JoinLobbyResult, m_StaleJoinResults);
        }

        private void DisposeCompletedStaleOperations()
        {
            DisposeCompleted(m_StaleCreateResults);
            DisposeCompleted(m_StaleJoinResults);
        }

        private static void MoveToStale<T>(
            ref CallResult<T> result,
            List<CallResult<T>> staleResults)
        {
            if (result == null) return;
            if (result.IsActive()) staleResults.Add(result);
            else result.Dispose();
            result = null;
        }

        private static void DisposeCompleted<T>(List<CallResult<T>> results)
        {
            for (int i = results.Count - 1; i >= 0; i--)
            {
                CallResult<T> result = results[i];
                if (result != null && result.IsActive()) continue;
                result?.Dispose();
                results.RemoveAt(i);
            }
        }

        private static void DisposeAll<T>(List<CallResult<T>> results)
        {
            for (int i = 0; i < results.Count; i++) results[i]?.Dispose();
            results.Clear();
        }
#endif

        private void CancelPendingLobbyOperation(string message)
        {
#if ARAWN_STEAMWORKS_LOBBY_AVAILABLE
            AbandonPendingLobbyOperations();
#else
            ++m_LobbyOperationGeneration;
            m_LobbyOperationDeadline = 0f;
#endif
            Fail(message);
        }

        private void ReportUnavailable(string operation)
        {
            Fail(
                $"Cannot {operation}: Steamworks.NET is not installed or this " +
                "build target is unsupported.");
        }

        private void SetState(
            PurrNetSteamLobbyProviderState state,
            string statusMessage)
        {
            m_State = state;
            m_StatusMessage = statusMessage ?? string.Empty;
        }

        private void Fail(string message)
        {
            string resolved = string.IsNullOrWhiteSpace(message)
                ? "An unknown Steam lobby error occurred."
                : message.Trim();
            SetState(PurrNetSteamLobbyProviderState.Error, resolved);
            Debug.LogError($"[PurrNet Steam Lobby] {resolved}", this);
            Failed?.Invoke(resolved);
        }
    }
}
