using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Lobby
{
    /// <summary>
    /// Provider-neutral lobby menu. The hierarchy is generated at runtime so a demo
    /// scene only needs this component and a component implementing
    /// <see cref="INetworkLobbyService"/>.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Lobby/Network Lobby Canvas UI")]
    [DefaultExecutionOrder(-250)]
    public sealed class NetworkLobbyCanvasUI : MonoBehaviour
    {
        [Header("Lobby Service")]
        [Tooltip("Component implementing INetworkLobbyService. Leave empty to find the first active service in the scene.")]
        [SerializeField] private MonoBehaviour m_ServiceBehaviour;

        [Header("Presentation")]
        [SerializeField] private string m_Title = "Multiplayer";
        [SerializeField] private string m_Subtitle = "Find a session or create your own";
        [SerializeField] private int m_SortingOrder = 1000;

        [Header("Defaults")]
        [SerializeField] private string m_DefaultPlayerName = "Player";
        [SerializeField] private string m_DefaultSessionName = "My Game";
        [SerializeField] private string m_DefaultJoinCode = string.Empty;
        [SerializeField] private string m_DefaultRegion = string.Empty;
        [SerializeField] private string m_DefaultAddress = "127.0.0.1";
        [SerializeField] private ushort m_DefaultPort = 7777;
        [Min(1)] [SerializeField] private int m_DefaultMaxPlayers = 8;
        [SerializeField] private bool m_DefaultVisible = true;
        [SerializeField] private NetworkLobbyTopology m_DefaultTopology =
            NetworkLobbyTopology.ClientServer;

        [Header("Behaviour")]
        [SerializeField] private bool m_InitializeOnEnable = true;
        [SerializeField] private bool m_RefreshAfterInitialize = true;
        [SerializeField] private bool m_ShowIncompatibleSessions;

        private static readonly Color ColorBackdrop = new Color(0.025f, 0.03f, 0.045f, 0.72f);
        private static readonly Color ColorPanel = new Color(0.075f, 0.085f, 0.115f, 0.985f);
        private static readonly Color ColorHeader = new Color(0.105f, 0.12f, 0.165f, 1f);
        private static readonly Color ColorField = new Color(0.145f, 0.16f, 0.205f, 1f);
        private static readonly Color ColorRow = new Color(0.115f, 0.13f, 0.17f, 1f);
        private static readonly Color ColorRowSelected = new Color(0.20f, 0.35f, 0.61f, 1f);
        private static readonly Color ColorPrimary = new Color(0.27f, 0.52f, 0.91f, 1f);
        private static readonly Color ColorPositive = new Color(0.22f, 0.69f, 0.47f, 1f);
        private static readonly Color ColorDanger = new Color(0.83f, 0.29f, 0.33f, 1f);
        private static readonly Color ColorNeutral = new Color(0.22f, 0.25f, 0.32f, 1f);
        private static readonly Color ColorText = new Color(0.94f, 0.96f, 0.99f, 1f);
        private static readonly Color ColorDim = new Color(0.64f, 0.69f, 0.78f, 1f);
        private static readonly Color ColorWarning = new Color(0.96f, 0.67f, 0.25f, 1f);

        private INetworkLobbyService m_Service;
        private CancellationTokenSource m_LifetimeCancellation;
        private bool m_LocalOperationInFlight;
        private string m_LocalError = string.Empty;
        private volatile bool m_RenderPending;
        private bool m_InitializePending;
        private NetworkLobbyTopology m_Topology;
        private string m_SelectedSessionId = string.Empty;

        private Canvas m_Canvas;
        private GameObject m_RuntimeRoot;
        private Image m_RuntimeBackdrop;
        private GameObject m_Card;
        private GameObject m_SetupRoot;
        private GameObject m_PlayerNameRow;
        private GameObject m_CreateNameRow;
        private GameObject m_RegionTopologyRow;
        private GameObject m_RegionGroup;
        private GameObject m_TopologyGroup;
        private GameObject m_CapacityVisibilityRow;
        private GameObject m_CapacityGroup;
        private GameObject m_CreateActionsRow;
        private GameObject m_JoinCodeRow;
        private GameObject m_DirectAddressRow;
        private GameObject m_BrowserHeaderRow;
        private GameObject m_BrowserRoot;
        private GameObject m_ConnectedRoot;
        private RectTransform m_SessionContent;
        private Text m_ServiceText;
        private Text m_StatusText;
        private Image m_StatusPill;
        private Text m_StatusPillText;
        private Text m_ErrorText;
        private Text m_EmptyText;
        private Text m_SelectedDetailsText;
        private InputField m_PlayerNameField;
        private InputField m_SessionNameField;
        private InputField m_JoinCodeField;
        private InputField m_RegionField;
        private InputField m_AddressField;
        private InputField m_PortField;
        private InputField m_MaxPlayersField;
        private Toggle m_VisibilityToggle;
        private Button m_TopologyButton;
        private Text m_TopologyButtonText;
        private Button m_CreateButton;
        private Button m_QuickJoinButton;
        private Button m_JoinCodeButton;
        private Button m_JoinAddressButton;
        private Button m_RefreshButton;
        private Button m_JoinSelectedButton;
        private Button m_LeaveButton;
        private Text m_CurrentSessionText;
        private Text m_ConnectedStatusText;

        public MonoBehaviour ServiceBehaviour => m_ServiceBehaviour;
        public INetworkLobbyService Service => m_Service;
        public string SelectedSessionId => m_SelectedSessionId;
        public NetworkLobbyEntry SelectedSession => FindSession(m_SelectedSessionId);
        public string PlayerName
        {
            get
            {
                string value = TextOf(m_PlayerNameField).Trim();
                return string.IsNullOrEmpty(value) ? "Player" : value;
            }
        }
        public bool IsBusy => m_LocalOperationInFlight ||
                              (m_Service != null && NetworkLobbyServiceUtility.IsBusy(m_Service.State));

        private void Awake()
        {
            m_Topology = m_DefaultTopology;
            BuildUI();
            EnsureEventSystem();
            ResolveAndSubscribe();
        }

        private void OnEnable()
        {
            m_LifetimeCancellation?.Dispose();
            m_LifetimeCancellation = new CancellationTokenSource();

            if (m_RuntimeRoot == null)
            {
                BuildUI();
                EnsureEventSystem();
            }

            ResolveAndSubscribe();
            m_RenderPending = true;

            if (m_InitializeOnEnable && m_Service != null &&
                (m_Service.State == NetworkLobbyState.Offline ||
                 m_Service.State == NetworkLobbyState.Unavailable))
            {
                // Defer until Update so every provider component has completed Awake
                // and OnEnable, regardless of Script Execution Order.
                m_InitializePending = true;
            }
        }

        private void OnDisable()
        {
            Unsubscribe();
            m_LifetimeCancellation?.Cancel();
            m_LifetimeCancellation?.Dispose();
            m_LifetimeCancellation = null;
            m_LocalOperationInFlight = false;
            m_InitializePending = false;
        }

        private void Update()
        {
            if (m_InitializePending)
            {
                m_InitializePending = false;
                InitializeLobby();
            }

            if (!m_RenderPending) return;
            m_RenderPending = false;
            Render();
        }

        private void OnValidate()
        {
            m_DefaultMaxPlayers = Mathf.Max(1, m_DefaultMaxPlayers);
            if (m_ServiceBehaviour != null && !(m_ServiceBehaviour is INetworkLobbyService))
            {
                Debug.LogWarning(
                    $"[{nameof(NetworkLobbyCanvasUI)}] {m_ServiceBehaviour.GetType().Name} " +
                    "does not implement INetworkLobbyService.",
                    this);
            }
        }

        public bool BindService(MonoBehaviour serviceBehaviour)
        {
            if (serviceBehaviour != null && !(serviceBehaviour is INetworkLobbyService))
            {
                Debug.LogError(
                    $"[{nameof(NetworkLobbyCanvasUI)}] {serviceBehaviour.GetType().Name} " +
                    "does not implement INetworkLobbyService.",
                    this);
                return false;
            }

            Unsubscribe();
            m_ServiceBehaviour = serviceBehaviour;
            ResolveAndSubscribe();
            m_RenderPending = true;
            return m_Service != null;
        }

        public void Configure(MonoBehaviour serviceBehaviour, string title, string subtitle = "")
        {
            if (!string.IsNullOrWhiteSpace(title)) m_Title = title.Trim();
            if (!string.IsNullOrWhiteSpace(subtitle)) m_Subtitle = subtitle.Trim();
            BindService(serviceBehaviour);
        }

        public void InitializeLobby()
        {
            RunOperation(
                token => m_Service.InitializeAsync(token),
                async token =>
                {
                    if (!m_RefreshAfterInitialize ||
                        !NetworkLobbyServiceUtility.HasCapability(
                            m_Service,
                            NetworkLobbyCapabilities.Refresh)) return;

                    await m_Service.RefreshAsync(BuildQuery(), token);
                });
        }

        public void RefreshSessions()
        {
            RunOperation(token => m_Service.RefreshAsync(BuildQuery(), token));
        }

        public void CreateSession()
        {
            RunOperation(token => m_Service.CreateAsync(BuildCreateRequest(), token));
        }

        public void QuickJoin()
        {
            RunOperation(token => m_Service.QuickJoinAsync(BuildQuery(), token));
        }

        public void JoinByCode()
        {
            string code = TextOf(m_JoinCodeField).Trim();
            if (string.IsNullOrEmpty(code))
            {
                ShowLocalError("Enter a session or room code first.");
                return;
            }

            var request = new NetworkLobbyJoinRequest(
                null,
                code,
                string.Empty,
                0,
                RegionValue,
                m_Topology,
                PlayerName);
            RunOperation(token => m_Service.JoinAsync(request, token));
        }

        public void JoinByAddress()
        {
            string address = TextOf(m_AddressField).Trim();
            if (string.IsNullOrEmpty(address))
            {
                ShowLocalError("Enter the host address first.");
                return;
            }

            var request = new NetworkLobbyJoinRequest(
                null,
                string.Empty,
                address,
                ParsePort(m_PortField, m_DefaultPort),
                RegionValue,
                m_Topology,
                PlayerName);
            RunOperation(token => m_Service.JoinAsync(request, token));
        }

        public void JoinSelectedSession()
        {
            NetworkLobbyEntry entry = SelectedSession;
            if (entry == null)
            {
                ShowLocalError("Select a session first.");
                return;
            }

            if (!entry.CanJoin)
            {
                string reason = !entry.IsCompatible
                    ? entry.CompatibilityMessage
                    : entry.IsFull ? "This session is full." : "This session is closed.";
                ShowLocalError(string.IsNullOrWhiteSpace(reason)
                    ? "The selected session cannot be joined."
                    : reason);
                return;
            }

            var request = new NetworkLobbyJoinRequest(
                entry,
                entry.JoinCode,
                entry.Address,
                entry.Port,
                entry.Region,
                entry.Topology,
                PlayerName);
            RunOperation(token => m_Service.JoinAsync(request, token));
        }

        public void LeaveSession()
        {
            RunOperation(token => m_Service.LeaveAsync(token));
        }

        public void SelectSession(string sessionId)
        {
            m_SelectedSessionId = sessionId ?? string.Empty;
            m_LocalError = string.Empty;
            RebuildSessionRows();
            RenderSelection();
            RenderInteractability();
        }

        private async void RunOperation(
            Func<CancellationToken, Task<NetworkLobbyOperationResult>> operation,
            Func<CancellationToken, Task> afterSuccess = null)
        {
            if (m_Service == null)
            {
                ShowLocalError("No lobby service is assigned or active in this scene.");
                return;
            }

            if (IsBusy) return;

            CancellationToken token = m_LifetimeCancellation?.Token ?? CancellationToken.None;
            m_LocalOperationInFlight = true;
            m_LocalError = string.Empty;
            m_RenderPending = true;

            try
            {
                NetworkLobbyOperationResult result = await operation(token);
                if (!result.Succeeded)
                {
                    m_LocalError = string.IsNullOrWhiteSpace(result.Message)
                        ? "The lobby operation failed."
                        : result.Message;
                }
                else if (afterSuccess != null && !token.IsCancellationRequested)
                {
                    await afterSuccess(token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The UI was disabled or destroyed. Cancellation is an expected exit.
            }
            catch (Exception exception)
            {
                m_LocalError = exception.Message;
                Debug.LogException(exception, this);
            }
            finally
            {
                m_LocalOperationInFlight = false;
                m_RenderPending = true;
            }
        }

        private NetworkLobbyQuery BuildQuery()
        {
            return new NetworkLobbyQuery(
                RegionValue,
                m_Topology,
                m_ShowIncompatibleSessions,
                PlayerName);
        }

        private NetworkLobbyCreateRequest BuildCreateRequest()
        {
            string sessionName = TextOf(m_SessionNameField).Trim();
            if (string.IsNullOrEmpty(sessionName)) sessionName = "New Session";

            return new NetworkLobbyCreateRequest(
                sessionName,
                TextOf(m_JoinCodeField).Trim(),
                RegionValue,
                m_Topology,
                ParsePositiveInt(m_MaxPlayersField, m_DefaultMaxPlayers),
                m_VisibilityToggle == null || m_VisibilityToggle.isOn,
                TextOf(m_AddressField).Trim(),
                ParsePort(m_PortField, m_DefaultPort),
                PlayerName);
        }

        private string RegionValue
        {
            get
            {
                string value = TextOf(m_RegionField).Trim();
                return string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : value;
            }
        }

        private void ResolveAndSubscribe()
        {
            INetworkLobbyService resolved = NetworkLobbyServiceUtility.Resolve(
                m_ServiceBehaviour,
                gameObject);
            if (ReferenceEquals(resolved, m_Service)) return;

            Unsubscribe();
            m_Service = resolved;
            if (m_Service == null) return;

            if (m_ServiceBehaviour == null) m_ServiceBehaviour = m_Service as MonoBehaviour;
            m_Service.StateChanged += OnServiceStateChanged;
            m_Service.SessionsChanged += OnSessionsChanged;
        }

        private void Unsubscribe()
        {
            if (m_Service == null) return;
            m_Service.StateChanged -= OnServiceStateChanged;
            m_Service.SessionsChanged -= OnSessionsChanged;
            m_Service = null;
        }

        private void OnServiceStateChanged()
        {
            m_RenderPending = true;
        }

        private void OnSessionsChanged()
        {
            m_RenderPending = true;
        }

        private void ShowLocalError(string message)
        {
            m_LocalError = message ?? string.Empty;
            m_RenderPending = true;
        }

        private void Render()
        {
            if (m_RuntimeRoot == null) return;

            if (m_Service == null) ResolveAndSubscribe();

            NetworkLobbyCapabilities capabilities = m_Service?.Capabilities ??
                                                    NetworkLobbyCapabilities.None;
            bool connected = m_Service != null &&
                             (m_Service.State == NetworkLobbyState.Connected ||
                              m_Service.State == NetworkLobbyState.Leaving ||
                              !string.IsNullOrWhiteSpace(m_Service.CurrentSessionId));

            m_Card.SetActive(!connected);
            m_SetupRoot.SetActive(!connected);
            m_ConnectedRoot.SetActive(connected);
            m_RuntimeBackdrop.color = connected
                ? new Color(0f, 0f, 0f, 0f)
                : ColorBackdrop;
            m_RuntimeBackdrop.raycastTarget = !connected;

            if (!connected)
            {
                m_CreateNameRow.SetActive(Has(capabilities, NetworkLobbyCapabilities.Create));
                m_PlayerNameRow.SetActive(true);
                m_RegionTopologyRow.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.RegionSelection) ||
                    Has(capabilities, NetworkLobbyCapabilities.TopologySelection));
                m_RegionGroup.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.RegionSelection));
                m_TopologyGroup.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.TopologySelection));
                m_CapacityVisibilityRow.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.PlayerCapacity) ||
                    Has(capabilities, NetworkLobbyCapabilities.Visibility));
                m_CapacityGroup.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.PlayerCapacity));
                m_VisibilityToggle.gameObject.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.Visibility));
                m_CreateActionsRow.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.Create) ||
                    Has(capabilities, NetworkLobbyCapabilities.QuickJoin));
                m_CreateButton.gameObject.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.Create));
                m_QuickJoinButton.gameObject.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.QuickJoin));
                m_JoinCodeRow.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.JoinByCode));
                m_DirectAddressRow.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.DirectAddress));
                m_BrowserHeaderRow.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.Browse));
                m_BrowserRoot.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.Browse));
                m_RefreshButton.gameObject.SetActive(
                    Has(capabilities, NetworkLobbyCapabilities.Refresh));

                ReflowCapabilityControls();
                ReflowSetupRows();
                RebuildSessionRows();
                RenderSelection();
            }

            string serviceName = m_Service?.ServiceName;
            m_ServiceText.text = string.IsNullOrWhiteSpace(serviceName)
                ? "No lobby service"
                : serviceName;

            RenderStatus();
            RenderInteractability();

            if (connected)
            {
                string sessionName = string.IsNullOrWhiteSpace(m_Service.CurrentSessionName)
                    ? m_Service.CurrentSessionId
                    : m_Service.CurrentSessionName;
                m_CurrentSessionText.text = string.IsNullOrWhiteSpace(sessionName)
                    ? "Connected to a multiplayer session"
                    : $"Connected to  {sessionName}";
                m_ConnectedStatusText.text = string.IsNullOrWhiteSpace(m_Service.StatusMessage)
                    ? "Gameplay transport is active"
                    : m_Service.StatusMessage;
            }
        }

        private void RenderStatus()
        {
            NetworkLobbyState state = m_Service?.State ?? NetworkLobbyState.Unavailable;
            string status = m_Service == null
                ? "Assign a component that implements INetworkLobbyService."
                : m_Service.StatusMessage;

            if (string.IsNullOrWhiteSpace(status)) status = state.ToString();
            m_StatusText.text = status;
            m_StatusPillText.text = IsBusy ? "BUSY" : state.ToString().ToUpperInvariant();

            Color color;
            if (IsBusy)
            {
                color = ColorWarning;
            }
            else switch (state)
            {
                case NetworkLobbyState.Connected:
                    color = ColorPositive;
                    break;
                case NetworkLobbyState.Error:
                case NetworkLobbyState.Unavailable:
                    color = ColorDanger;
                    break;
                case NetworkLobbyState.Initializing:
                case NetworkLobbyState.Browsing:
                case NetworkLobbyState.Creating:
                case NetworkLobbyState.Joining:
                case NetworkLobbyState.Leaving:
                    color = ColorWarning;
                    break;
                default:
                    color = ColorNeutral;
                    break;
            }

            m_StatusPill.color = color;

            string error = !string.IsNullOrWhiteSpace(m_LocalError)
                ? m_LocalError
                : m_Service?.LastError;
            m_ErrorText.gameObject.SetActive(!string.IsNullOrWhiteSpace(error));
            m_ErrorText.text = error ?? string.Empty;
        }

        private void RenderInteractability()
        {
            NetworkLobbyCapabilities capabilities = m_Service?.Capabilities ??
                                                    NetworkLobbyCapabilities.None;
            bool ready = m_Service != null && !IsBusy &&
                         m_Service.State != NetworkLobbyState.Unavailable;

            SetInteractable(m_CreateButton,
                ready && Has(capabilities, NetworkLobbyCapabilities.Create));
            SetInteractable(m_QuickJoinButton,
                ready && Has(capabilities, NetworkLobbyCapabilities.QuickJoin));
            SetInteractable(m_JoinCodeButton,
                ready && Has(capabilities, NetworkLobbyCapabilities.JoinByCode));
            SetInteractable(m_JoinAddressButton,
                ready && Has(capabilities, NetworkLobbyCapabilities.DirectAddress));
            SetInteractable(m_RefreshButton,
                ready && Has(capabilities, NetworkLobbyCapabilities.Refresh));
            SetInteractable(m_JoinSelectedButton,
                ready && SelectedSession != null && SelectedSession.CanJoin);
            SetInteractable(m_LeaveButton, ready);

            SetInteractable(m_SessionNameField, ready);
            SetInteractable(m_PlayerNameField, ready);
            SetInteractable(m_JoinCodeField, ready);
            SetInteractable(m_RegionField,
                ready && Has(capabilities, NetworkLobbyCapabilities.RegionSelection));
            SetInteractable(m_AddressField, ready);
            SetInteractable(m_PortField, ready);
            SetInteractable(m_MaxPlayersField,
                ready && Has(capabilities, NetworkLobbyCapabilities.PlayerCapacity));
            if (m_VisibilityToggle != null)
            {
                m_VisibilityToggle.interactable = ready &&
                                                  Has(capabilities, NetworkLobbyCapabilities.Visibility);
            }
            SetInteractable(m_TopologyButton,
                ready && Has(capabilities, NetworkLobbyCapabilities.TopologySelection));
        }

        private void RenderSelection()
        {
            NetworkLobbyEntry selected = SelectedSession;
            bool hasSelected = selected != null;
            m_SelectedDetailsText.gameObject.SetActive(hasSelected);

            if (!hasSelected)
            {
                m_SelectedDetailsText.text = string.Empty;
                return;
            }

            string availability = selected.CanJoin
                ? "Ready to join"
                : !selected.IsCompatible
                    ? selected.CompatibilityMessage
                    : selected.IsFull ? "Session is full" : "Session is closed";
            string region = string.IsNullOrWhiteSpace(selected.Region)
                ? "Automatic region"
                : selected.Region;
            m_SelectedDetailsText.text =
                $"{region}  ·  {selected.ConnectionKind}  ·  {availability}";
        }

        private void RebuildSessionRows()
        {
            if (m_SessionContent == null) return;

            for (int i = m_SessionContent.childCount - 1; i >= 0; i--)
            {
                GameObject child = m_SessionContent.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            IReadOnlyList<NetworkLobbyEntry> sessions = m_Service?.Sessions;
            int count = sessions?.Count ?? 0;
            bool selectionStillExists = false;
            float y = 0f;

            for (int i = 0; i < count; i++)
            {
                NetworkLobbyEntry entry = sessions[i];
                if (entry == null) continue;
                if (!m_ShowIncompatibleSessions && !entry.IsCompatible) continue;

                if (string.Equals(entry.Id, m_SelectedSessionId, StringComparison.Ordinal))
                {
                    selectionStillExists = true;
                }

                CreateSessionRow(entry, y);
                y -= 50f;
            }

            if (!selectionStillExists) m_SelectedSessionId = string.Empty;
            m_SessionContent.sizeDelta = new Vector2(0f, Mathf.Max(0f, -y));

            bool searching = m_Service != null &&
                             m_Service.State == NetworkLobbyState.Browsing;
            m_EmptyText.gameObject.SetActive(y >= -0.1f);
            m_EmptyText.text = searching
                ? "Looking for sessions…"
                : "No compatible sessions found.\nCreate one, or refresh the list.";
        }

        private void CreateSessionRow(NetworkLobbyEntry entry, float y)
        {
            bool selected = string.Equals(
                entry.Id,
                m_SelectedSessionId,
                StringComparison.Ordinal);
            Color background = selected ? ColorRowSelected : ColorRow;
            Button button = NewButton(
                "Session_" + SafeObjectName(entry.Id),
                m_SessionContent,
                background,
                string.Empty,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            button.interactable = !IsBusy;
            RectTransform rect = (RectTransform)button.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(0f, 44f);

            string title = string.IsNullOrWhiteSpace(entry.Name) ? "Unnamed session" : entry.Name;
            NewText(
                "Name",
                button.transform,
                title,
                14,
                FontStyle.Bold,
                entry.IsCompatible ? ColorText : ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(12f, 18f),
                new Vector2(-125f, -4f),
                TextAnchor.MiddleLeft);

            string detail = string.IsNullOrWhiteSpace(entry.Region)
                ? entry.ConnectionKind.ToString()
                : $"{entry.Region} · {entry.ConnectionKind}";
            NewText(
                "Detail",
                button.transform,
                detail,
                10,
                FontStyle.Normal,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(12f, 3f),
                new Vector2(-125f, -22f),
                TextAnchor.MiddleLeft);

            string capacity = entry.MaxPlayers > 0
                ? $"{entry.PlayerCount} / {entry.MaxPlayers}"
                : entry.PlayerCount.ToString();
            string state = entry.CanJoin
                ? capacity
                : entry.IsFull ? "FULL" : !entry.IsCompatible ? "VERSION" : "CLOSED";
            NewText(
                "Availability",
                button.transform,
                state,
                11,
                FontStyle.Bold,
                entry.CanJoin ? ColorPositive : ColorWarning,
                new Vector2(1f, 0f),
                Vector2.one,
                new Vector2(-112f, 0f),
                new Vector2(-12f, 0f),
                TextAnchor.MiddleRight);

            string capturedId = entry.Id;
            button.onClick.AddListener(() => SelectSession(capturedId));
        }

        private NetworkLobbyEntry FindSession(string id)
        {
            if (string.IsNullOrEmpty(id) || m_Service?.Sessions == null) return null;

            IReadOnlyList<NetworkLobbyEntry> sessions = m_Service.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                NetworkLobbyEntry entry = sessions[i];
                if (entry != null && string.Equals(entry.Id, id, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        private void ToggleTopology()
        {
            m_Topology = m_Topology == NetworkLobbyTopology.ClientServer
                ? NetworkLobbyTopology.Shared
                : NetworkLobbyTopology.ClientServer;
            UpdateTopologyLabel();
        }

        private void UpdateTopologyLabel()
        {
            if (m_TopologyButtonText == null) return;
            m_TopologyButtonText.text = m_Topology == NetworkLobbyTopology.ClientServer
                ? "Client / Server"
                : "Shared";
        }

        private void ReflowSetupRows()
        {
            float top = -4f;
            PositionActiveRow(m_PlayerNameRow, ref top, 40f);
            PositionActiveRow(m_CreateNameRow, ref top, 40f);
            PositionActiveRow(m_RegionTopologyRow, ref top, 40f);
            PositionActiveRow(m_CapacityVisibilityRow, ref top, 40f);
            PositionActiveRow(m_CreateActionsRow, ref top, 40f);
            PositionActiveRow(m_JoinCodeRow, ref top, 40f);
            PositionActiveRow(m_DirectAddressRow, ref top, 40f);
            PositionActiveRow(m_BrowserHeaderRow, ref top, 34f);

            RectTransform browserRect = (RectTransform)m_BrowserRoot.transform;
            browserRect.offsetMax = new Vector2(0f, top);
            browserRect.offsetMin = new Vector2(0f, 64f);
        }

        private void ReflowCapabilityControls()
        {
            ReflowPair(m_RegionGroup, m_TopologyGroup);
            ReflowPair(m_CapacityGroup, m_VisibilityToggle.gameObject);
            ReflowPair(m_CreateButton.gameObject, m_QuickJoinButton.gameObject);
        }

        private static void ReflowPair(GameObject left, GameObject right)
        {
            if (left == null || right == null) return;

            bool leftActive = left.activeSelf;
            bool rightActive = right.activeSelf;
            if (!leftActive && !rightActive) return;

            if (leftActive && rightActive)
            {
                SetHorizontalRect(left, 0f, 0.5f, 0f, -5f);
                SetHorizontalRect(right, 0.5f, 1f, 5f, 0f);
            }
            else
            {
                SetHorizontalRect(leftActive ? left : right, 0f, 1f, 0f, 0f);
            }
        }

        private static void SetHorizontalRect(
            GameObject target,
            float anchorMin,
            float anchorMax,
            float leftOffset,
            float rightOffset)
        {
            if (target == null) return;
            RectTransform rect = (RectTransform)target.transform;
            rect.anchorMin = new Vector2(anchorMin, 0f);
            rect.anchorMax = new Vector2(anchorMax, 1f);
            rect.offsetMin = new Vector2(leftOffset, 0f);
            rect.offsetMax = new Vector2(rightOffset, 0f);
        }

        private static void PositionActiveRow(GameObject row, ref float top, float height)
        {
            if (row == null || !row.activeSelf) return;

            RectTransform rect = (RectTransform)row.transform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(0f, top - height);
            rect.offsetMax = new Vector2(0f, top);
            top -= height + 8f;
        }

        private void BuildUI()
        {
            m_Canvas = GetComponent<Canvas>();
            if (m_Canvas == null) m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = m_SortingOrder;

            CanvasScaler scaler = GetComponent<CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

            Transform stale = transform.Find("NetworkLobbyRuntimeUI");
            if (stale != null)
            {
                stale.gameObject.SetActive(false);
                Destroy(stale.gameObject);
            }

            m_RuntimeRoot = NewPanel(
                "NetworkLobbyRuntimeUI",
                transform,
                ColorBackdrop,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            m_RuntimeBackdrop = m_RuntimeRoot.GetComponent<Image>();

            m_Card = NewPanel(
                "LobbyCard",
                m_RuntimeRoot.transform,
                ColorPanel,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-310f, -380f),
                new Vector2(310f, 380f));

            GameObject header = NewPanel(
                "Header",
                m_Card.transform,
                ColorHeader,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(0f, -76f),
                Vector2.zero);

            NewText(
                "Title",
                header.transform,
                m_Title,
                24,
                FontStyle.Bold,
                ColorText,
                Vector2.zero,
                Vector2.one,
                new Vector2(22f, 31f),
                new Vector2(-160f, -8f),
                TextAnchor.MiddleLeft);
            NewText(
                "Subtitle",
                header.transform,
                m_Subtitle,
                11,
                FontStyle.Normal,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(22f, 8f),
                new Vector2(-160f, -39f),
                TextAnchor.MiddleLeft);

            m_StatusPill = NewPanel(
                "StatusPill",
                header.transform,
                ColorNeutral,
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(-139f, -12f),
                new Vector2(-20f, 12f)).GetComponent<Image>();
            m_StatusPillText = NewText(
                "Status",
                m_StatusPill.transform,
                "OFFLINE",
                10,
                FontStyle.Bold,
                Color.white,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                TextAnchor.MiddleCenter);

            GameObject statusArea = NewPanel(
                "StatusArea",
                m_Card.transform,
                new Color(0f, 0f, 0f, 0f),
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(18f, -126f),
                new Vector2(-18f, -82f));
            statusArea.GetComponent<Image>().raycastTarget = false;
            m_ServiceText = NewText(
                "Service",
                statusArea.transform,
                "Lobby service",
                11,
                FontStyle.Bold,
                ColorText,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                new Vector2(0f, -21f),
                TextAnchor.MiddleLeft);
            m_StatusText = NewText(
                "Message",
                statusArea.transform,
                "Offline",
                11,
                FontStyle.Normal,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(160f, 0f),
                Vector2.zero,
                TextAnchor.MiddleRight);

            m_ErrorText = NewText(
                "Error",
                m_Card.transform,
                string.Empty,
                11,
                FontStyle.Bold,
                new Color(1f, 0.48f, 0.5f, 1f),
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(18f, -153f),
                new Vector2(-18f, -128f),
                TextAnchor.MiddleLeft);

            m_SetupRoot = NewEmpty(
                "Setup",
                m_Card.transform,
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(18f, 20f),
                new Vector2(-18f, -158f));

            BuildSetupRows();
            BuildConnectedView(m_RuntimeRoot.transform);
            UpdateTopologyLabel();
        }

        private void BuildSetupRows()
        {
            m_PlayerNameRow = NewEmpty("PlayerName", m_SetupRoot.transform);
            NewText("Label", m_PlayerNameRow.transform, "PLAYER NAME", 10, FontStyle.Bold, ColorDim,
                Vector2.zero, new Vector2(0.25f, 1f), Vector2.zero, new Vector2(-8f, 0f), TextAnchor.MiddleLeft);
            m_PlayerNameField = NewInput("PlayerName", m_PlayerNameRow.transform, m_DefaultPlayerName,
                new Vector2(0.25f, 0f), Vector2.one, Vector2.zero, Vector2.zero, "Your display name");

            m_CreateNameRow = NewEmpty("CreateName", m_SetupRoot.transform);
            NewText("Label", m_CreateNameRow.transform, "SESSION NAME", 10, FontStyle.Bold, ColorDim,
                Vector2.zero, new Vector2(0.25f, 1f), Vector2.zero, new Vector2(-8f, 0f), TextAnchor.MiddleLeft);
            m_SessionNameField = NewInput("SessionName", m_CreateNameRow.transform, m_DefaultSessionName,
                new Vector2(0.25f, 0f), Vector2.one, Vector2.zero, Vector2.zero);

            m_RegionTopologyRow = NewEmpty("RegionTopology", m_SetupRoot.transform);
            m_RegionGroup = NewEmpty("RegionGroup", m_RegionTopologyRow.transform,
                Vector2.zero, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-5f, 0f));
            NewText("Label", m_RegionGroup.transform, "REGION", 10, FontStyle.Bold, ColorDim,
                Vector2.zero, new Vector2(0.34f, 1f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft);
            m_RegionField = NewInput("Region", m_RegionGroup.transform,
                string.IsNullOrWhiteSpace(m_DefaultRegion) ? "Auto" : m_DefaultRegion,
                new Vector2(0.34f, 0f), Vector2.one, Vector2.zero, Vector2.zero);

            m_TopologyGroup = NewEmpty("TopologyGroup", m_RegionTopologyRow.transform,
                new Vector2(0.5f, 0f), Vector2.one, new Vector2(5f, 0f), Vector2.zero);
            NewText("Label", m_TopologyGroup.transform, "TOPOLOGY", 10, FontStyle.Bold, ColorDim,
                Vector2.zero, new Vector2(0.36f, 1f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft);
            m_TopologyButton = NewButton("Topology", m_TopologyGroup.transform, ColorField, string.Empty,
                new Vector2(0.36f, 0f), Vector2.one, Vector2.zero, Vector2.zero);
            m_TopologyButtonText = m_TopologyButton.GetComponentInChildren<Text>();
            m_TopologyButton.onClick.AddListener(ToggleTopology);

            m_CapacityVisibilityRow = NewEmpty("CapacityVisibility", m_SetupRoot.transform);
            m_CapacityGroup = NewEmpty("CapacityGroup", m_CapacityVisibilityRow.transform,
                Vector2.zero, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-5f, 0f));
            NewText("Label", m_CapacityGroup.transform, "MAX PLAYERS", 10, FontStyle.Bold, ColorDim,
                Vector2.zero, new Vector2(0.5f, 1f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft);
            m_MaxPlayersField = NewInput("MaxPlayers", m_CapacityGroup.transform, m_DefaultMaxPlayers.ToString(),
                new Vector2(0.5f, 0f), Vector2.one, Vector2.zero, Vector2.zero);
            m_MaxPlayersField.contentType = InputField.ContentType.IntegerNumber;

            m_VisibilityToggle = NewToggle(
                "Visible",
                m_CapacityVisibilityRow.transform,
                "Publicly visible",
                m_DefaultVisible,
                new Vector2(0.54f, 0f),
                Vector2.one,
                Vector2.zero,
                Vector2.zero);

            m_CreateActionsRow = NewEmpty("CreateActions", m_SetupRoot.transform);
            m_CreateButton = NewButton("Create", m_CreateActionsRow.transform, ColorPrimary, "Create Game",
                Vector2.zero, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(-5f, 0f));
            m_CreateButton.onClick.AddListener(CreateSession);
            m_QuickJoinButton = NewButton("QuickJoin", m_CreateActionsRow.transform, ColorPositive, "Quick Join",
                new Vector2(0.5f, 0f), Vector2.one, new Vector2(5f, 0f), Vector2.zero);
            m_QuickJoinButton.onClick.AddListener(QuickJoin);

            m_JoinCodeRow = NewEmpty("JoinCode", m_SetupRoot.transform);
            m_JoinCodeField = NewInput("JoinCode", m_JoinCodeRow.transform, m_DefaultJoinCode,
                Vector2.zero, new Vector2(0.72f, 1f), Vector2.zero, new Vector2(-5f, 0f), "Session or room code");
            m_JoinCodeButton = NewButton("Join", m_JoinCodeRow.transform, ColorNeutral, "Join Code",
                new Vector2(0.72f, 0f), Vector2.one, new Vector2(5f, 0f), Vector2.zero);
            m_JoinCodeButton.onClick.AddListener(JoinByCode);

            m_DirectAddressRow = NewEmpty("DirectAddress", m_SetupRoot.transform);
            m_AddressField = NewInput("Address", m_DirectAddressRow.transform, m_DefaultAddress,
                Vector2.zero, new Vector2(0.55f, 1f), Vector2.zero, new Vector2(-5f, 0f), "Host address");
            m_PortField = NewInput("Port", m_DirectAddressRow.transform, m_DefaultPort.ToString(),
                new Vector2(0.55f, 0f), new Vector2(0.72f, 1f), new Vector2(5f, 0f), new Vector2(-5f, 0f), "Port");
            m_PortField.contentType = InputField.ContentType.IntegerNumber;
            m_JoinAddressButton = NewButton("Join", m_DirectAddressRow.transform, ColorNeutral, "Join Address",
                new Vector2(0.72f, 0f), Vector2.one, new Vector2(5f, 0f), Vector2.zero);
            m_JoinAddressButton.onClick.AddListener(JoinByAddress);

            m_BrowserHeaderRow = NewEmpty("BrowserHeader", m_SetupRoot.transform);
            NewText("Title", m_BrowserHeaderRow.transform, "AVAILABLE GAMES", 10, FontStyle.Bold, ColorDim,
                Vector2.zero, new Vector2(0.7f, 1f), Vector2.zero, Vector2.zero, TextAnchor.MiddleLeft);
            m_RefreshButton = NewButton("Refresh", m_BrowserHeaderRow.transform, ColorNeutral, "Refresh",
                new Vector2(0.78f, 0f), Vector2.one, Vector2.zero, Vector2.zero);
            m_RefreshButton.onClick.AddListener(RefreshSessions);

            BuildBrowser();
        }

        private void BuildBrowser()
        {
            m_BrowserRoot = NewPanel(
                "Browser",
                m_SetupRoot.transform,
                new Color(0.055f, 0.065f, 0.09f, 1f),
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);

            GameObject viewport = NewPanel(
                "Viewport",
                m_BrowserRoot.transform,
                new Color(0f, 0f, 0f, 0f),
                Vector2.zero,
                Vector2.one,
                new Vector2(5f, 50f),
                new Vector2(-5f, -5f));
            viewport.AddComponent<RectMask2D>();
            viewport.GetComponent<Image>().raycastTarget = true;

            GameObject content = NewEmpty(
                "Content",
                viewport.transform,
                new Vector2(0f, 1f),
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            m_SessionContent = (RectTransform)content.transform;
            m_SessionContent.pivot = new Vector2(0.5f, 1f);

            ScrollRect scrollRect = m_BrowserRoot.AddComponent<ScrollRect>();
            scrollRect.viewport = (RectTransform)viewport.transform;
            scrollRect.content = m_SessionContent;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            m_EmptyText = NewText(
                "Empty",
                viewport.transform,
                "No compatible sessions found.",
                12,
                FontStyle.Normal,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(20f, 12f),
                new Vector2(-20f, -12f),
                TextAnchor.MiddleCenter);

            m_SelectedDetailsText = NewText(
                "SelectedDetails",
                m_BrowserRoot.transform,
                string.Empty,
                10,
                FontStyle.Normal,
                ColorDim,
                Vector2.zero,
                new Vector2(0.68f, 0f),
                new Vector2(10f, 7f),
                new Vector2(-5f, 43f),
                TextAnchor.MiddleLeft);
            m_JoinSelectedButton = NewButton(
                "JoinSelected",
                m_BrowserRoot.transform,
                ColorPositive,
                "Join Selected",
                new Vector2(0.7f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0f, 7f),
                new Vector2(-7f, 43f));
            m_JoinSelectedButton.onClick.AddListener(JoinSelectedSession);
        }

        private void BuildConnectedView(Transform card)
        {
            m_ConnectedRoot = NewPanel(
                "Connected",
                card,
                ColorPanel,
                Vector2.one,
                Vector2.one,
                new Vector2(-440f, -190f),
                new Vector2(-20f, -20f));

            GameObject accent = NewPanel(
                "Accent",
                m_ConnectedRoot.transform,
                ColorPositive,
                Vector2.zero,
                new Vector2(0f, 1f),
                Vector2.zero,
                new Vector2(5f, 0f));
            accent.GetComponent<Image>().raycastTarget = false;

            NewText(
                "Heading",
                m_ConnectedRoot.transform,
                "CONNECTED",
                11,
                FontStyle.Bold,
                ColorPositive,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(22f, -43f),
                new Vector2(-18f, -14f),
                TextAnchor.MiddleLeft);
            m_CurrentSessionText = NewText(
                "Session",
                m_ConnectedRoot.transform,
                "Connected to a multiplayer session",
                18,
                FontStyle.Bold,
                ColorText,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(22f, -78f),
                new Vector2(-18f, -42f),
                TextAnchor.MiddleLeft);
            m_ConnectedStatusText = NewText(
                "Status",
                m_ConnectedRoot.transform,
                "Gameplay transport is active",
                11,
                FontStyle.Normal,
                ColorDim,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(22f, -106f),
                new Vector2(-18f, -78f),
                TextAnchor.MiddleLeft);
            m_LeaveButton = NewButton(
                "Leave",
                m_ConnectedRoot.transform,
                ColorDanger,
                "Leave Session",
                new Vector2(1f, 0f),
                new Vector2(1f, 0f),
                new Vector2(-158f, 16f),
                new Vector2(-18f, 54f));
            m_LeaveButton.onClick.AddListener(LeaveSession);
            m_ConnectedRoot.SetActive(false);
        }

        private static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER || UNITY_6000
            EventSystem eventSystem = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
#else
            EventSystem eventSystem = UnityEngine.Object.FindObjectOfType<EventSystem>();
#endif
            GameObject eventSystemObject;
            if (eventSystem != null)
            {
                eventSystemObject = eventSystem.gameObject;
            }
            else
            {
                eventSystemObject = new GameObject("EventSystem");
                eventSystemObject.AddComponent<EventSystem>();
            }

            Type inputSystemModule = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null)
            {
                if (eventSystemObject.GetComponent(inputSystemModule) == null)
                {
                    eventSystemObject.AddComponent(inputSystemModule);
                }

                StandaloneInputModule[] legacy =
                    eventSystemObject.GetComponents<StandaloneInputModule>();
                for (int i = 0; i < legacy.Length; i++)
                {
                    legacy[i].enabled = false;
                    Destroy(legacy[i]);
                }

                return;
            }

#pragma warning disable CS0618
            if (eventSystemObject.GetComponent<BaseInputModule>() == null)
            {
                eventSystemObject.AddComponent<StandaloneInputModule>();
            }
#pragma warning restore CS0618
        }

        private static GameObject NewEmpty(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)gameObject.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            return gameObject;
        }

        private static GameObject NewEmpty(string name, Transform parent)
        {
            return NewEmpty(name, parent, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
        }

        private static GameObject NewPanel(
            string name,
            Transform parent,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            var gameObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)gameObject.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            Image image = gameObject.GetComponent<Image>();
            image.color = color;
            return gameObject;
        }

        private static Text NewText(
            string name,
            Transform parent,
            string value,
            int fontSize,
            FontStyle fontStyle,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            TextAnchor alignment)
        {
            var gameObject = new GameObject(
                name,
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Text));
            gameObject.transform.SetParent(parent, false);
            RectTransform rect = (RectTransform)gameObject.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            Text text = gameObject.GetComponent<Text>();
            text.text = value ?? string.Empty;
            text.font = GetBuiltinFont();
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = color;
            text.alignment = alignment;
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static InputField NewInput(
            string name,
            Transform parent,
            string value,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            string placeholderValue = "")
        {
            GameObject root = NewPanel(
                name,
                parent,
                ColorField,
                anchorMin,
                anchorMax,
                offsetMin,
                offsetMax);
            InputField input = root.AddComponent<InputField>();
            Text text = NewText(
                "Text",
                root.transform,
                string.Empty,
                13,
                FontStyle.Normal,
                ColorText,
                Vector2.zero,
                Vector2.one,
                new Vector2(11f, 4f),
                new Vector2(-11f, -4f),
                TextAnchor.MiddleLeft);
            Text placeholder = NewText(
                "Placeholder",
                root.transform,
                string.IsNullOrEmpty(placeholderValue) ? value : placeholderValue,
                13,
                FontStyle.Italic,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(11f, 4f),
                new Vector2(-11f, -4f),
                TextAnchor.MiddleLeft);
            input.targetGraphic = root.GetComponent<Image>();
            input.textComponent = text;
            input.placeholder = placeholder;
            input.text = value ?? string.Empty;
            return input;
        }

        private static Button NewButton(
            string name,
            Transform parent,
            Color color,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            GameObject root = NewPanel(
                name,
                parent,
                color,
                anchorMin,
                anchorMax,
                offsetMin,
                offsetMax);
            Button button = root.AddComponent<Button>();
            button.targetGraphic = root.GetComponent<Image>();

            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.11f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(color.r, color.g, color.b, 0.35f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            NewText(
                "Label",
                root.transform,
                label,
                12,
                FontStyle.Bold,
                Color.white,
                Vector2.zero,
                Vector2.one,
                new Vector2(5f, 2f),
                new Vector2(-5f, -2f),
                TextAnchor.MiddleCenter);
            return button;
        }

        private static Toggle NewToggle(
            string name,
            Transform parent,
            string label,
            bool value,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            GameObject root = NewEmpty(name, parent, anchorMin, anchorMax, offsetMin, offsetMax);
            Toggle toggle = root.AddComponent<Toggle>();
            GameObject box = NewPanel(
                "Box",
                root.transform,
                ColorField,
                new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f),
                new Vector2(0f, -10f),
                new Vector2(20f, 10f));
            GameObject check = NewPanel(
                "Checkmark",
                box.transform,
                ColorPositive,
                Vector2.zero,
                Vector2.one,
                new Vector2(4f, 4f),
                new Vector2(-4f, -4f));
            toggle.targetGraphic = box.GetComponent<Image>();
            toggle.graphic = check.GetComponent<Image>();
            toggle.isOn = value;
            NewText(
                "Label",
                root.transform,
                label,
                11,
                FontStyle.Bold,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(29f, 0f),
                Vector2.zero,
                TextAnchor.MiddleLeft);
            return toggle;
        }

        private static Font GetBuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font != null
                ? font
                : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static bool Has(
            NetworkLobbyCapabilities capabilities,
            NetworkLobbyCapabilities value)
        {
            return (capabilities & value) == value;
        }

        private static void SetInteractable(Selectable selectable, bool value)
        {
            if (selectable != null) selectable.interactable = value;
        }

        private static string TextOf(InputField input)
        {
            return input != null ? input.text ?? string.Empty : string.Empty;
        }

        private static int ParsePositiveInt(InputField input, int fallback)
        {
            return int.TryParse(TextOf(input), out int value) && value > 0
                ? value
                : Mathf.Max(1, fallback);
        }

        private static ushort ParsePort(InputField input, ushort fallback)
        {
            return ushort.TryParse(TextOf(input), out ushort value) && value > 0
                ? value
                : fallback;
        }

        private static string SafeObjectName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "Unknown";
            return value.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
        }
    }
}
