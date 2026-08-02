using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Lobby;
using PurrNet;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby
{
    /// <summary>
    /// Runtime UGUI presentation for <see cref="PurrNetStagingRoomController"/>.
    /// It deliberately leaves chat transport and rendering to <see cref="PurrNetChatBoxUI"/>.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Lobby/PurrNet Staging Room UI")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-240)]
    public sealed class PurrNetStagingRoomUI : MonoBehaviour
    {
        private sealed class PlayerRow
        {
            public GameObject Root;
            public Image Background;
            public Text Slot;
            public Text Name;
            public Text Tags;
            public Image ReadyBackground;
            public Text ReadyText;
        }

        [Header("References")]
        [SerializeField] private PurrNetStagingRoomController m_Controller;
        [SerializeField] private NetworkManager m_NetworkManager;
        [SerializeField] private PurrNetLobbyService m_LobbyService;
        [SerializeField] private PurrNetChatBoxUI m_ChatBox;

        [Tooltip("Optional shared session browser. Only its Canvas is hidden while the staging room is visible; its connected/Leave card returns during gameplay.")]
        [SerializeField] private NetworkLobbyCanvasUI m_SessionLobbyUI;

        [Header("Presentation")]
        [SerializeField] private string m_Title = "Strategy Room";
        [SerializeField] private string m_Subtitle =
            "Choose your name, coordinate in chat, and ready up before the match";
        [SerializeField] private int m_SortingOrder = 1010;
        [SerializeField] private int m_ChatSortingOrder = 1020;
        [SerializeField] private bool m_HideChatWhileOffline = true;

        private static readonly Color ColorBackdrop = new(0.018f, 0.025f, 0.035f, 0.80f);
        private static readonly Color ColorPanel = new(0.07f, 0.085f, 0.105f, 0.985f);
        private static readonly Color ColorHeader = new(0.10f, 0.13f, 0.16f, 1f);
        private static readonly Color ColorField = new(0.13f, 0.155f, 0.19f, 1f);
        private static readonly Color ColorRoster = new(0.035f, 0.047f, 0.06f, 0.92f);
        private static readonly Color ColorRow = new(0.095f, 0.115f, 0.14f, 1f);
        private static readonly Color ColorRowAlternate = new(0.08f, 0.10f, 0.125f, 1f);
        private static readonly Color ColorRowLocal = new(0.13f, 0.23f, 0.30f, 1f);
        private static readonly Color ColorPrimary = new(0.22f, 0.51f, 0.87f, 1f);
        private static readonly Color ColorPositive = new(0.21f, 0.68f, 0.43f, 1f);
        private static readonly Color ColorWarning = new(0.91f, 0.60f, 0.20f, 1f);
        private static readonly Color ColorDanger = new(0.78f, 0.27f, 0.30f, 1f);
        private static readonly Color ColorNeutral = new(0.22f, 0.26f, 0.31f, 1f);
        private static readonly Color ColorText = new(0.94f, 0.96f, 0.98f, 1f);
        private static readonly Color ColorDim = new(0.64f, 0.70f, 0.76f, 1f);

        private readonly List<PlayerRow> m_PlayerRows = new();

        private Canvas m_Canvas;
        private GameObject m_RuntimeRoot;
        private RectTransform m_RosterContent;
        private Text m_StatusText;
        private Text m_RolePillText;
        private Image m_RolePill;
        private Text m_CountText;
        private Text m_HelpText;
        private Text m_OperationText;
        private InputField m_NameField;
        private Button m_ApplyNameButton;
        private Button m_ReadyButton;
        private Text m_ReadyButtonText;
        private Button m_StartButton;
        private Button m_LeaveButton;

        private PurrNetStagingRoomController m_SubscribedController;
        private Canvas m_SessionCanvas;
        private bool m_SessionCanvasStateCaptured;
        private bool m_SessionCanvasOriginalEnabled;
        private Canvas m_ChatCanvas;
        private bool m_ChatCanvasStateCaptured;
        private bool m_ChatCanvasOriginalEnabled;
        private bool m_RenderPending = true;
        private bool m_LeaveInFlight;
        private string m_LocalOperationMessage = string.Empty;
        private float m_NextConnectionPoll;

        private NetworkManager ActiveManager =>
            m_NetworkManager != null ? m_NetworkManager : NetworkManager.main;

        public PurrNetStagingRoomController Controller => m_Controller;
        public Canvas RuntimeCanvas => m_Canvas;
        public bool IsVisible => m_RuntimeRoot != null && m_RuntimeRoot.activeSelf;

        private void Awake()
        {
            ResolveReferences();
            BuildUI();
            EnsureEventSystem();
            SubscribeController();
            Render();
        }

        private void OnEnable()
        {
            ResolveReferences();
            SubscribeController();
            m_RenderPending = true;
        }

        private void Update()
        {
            if (m_Controller == null || m_SubscribedController != m_Controller)
            {
                ResolveReferences();
                SubscribeController();
                m_RenderPending = true;
            }

            if (Time.unscaledTime >= m_NextConnectionPoll)
            {
                m_NextConnectionPoll = Time.unscaledTime + 0.20f;
                m_RenderPending = true;
            }

            if (!m_RenderPending) return;
            m_RenderPending = false;
            Render();
        }

        private void OnDisable()
        {
            UnsubscribeController();
            RestoreSessionLobbyCanvas();
            RestoreChatCanvas();
        }

        private void OnDestroy()
        {
            UnsubscribeController();
            RestoreSessionLobbyCanvas();
            RestoreChatCanvas();
        }

        private void OnValidate()
        {
            m_SortingOrder = Mathf.Clamp(m_SortingOrder, -32768, 32767);
            m_ChatSortingOrder = Mathf.Clamp(m_ChatSortingOrder, -32768, 32767);
        }

        /// <summary>
        /// Binds all optional scene services without requiring access to serialized fields.
        /// The UI hierarchy is generated on demand, so this is safe to call from editor setup code
        /// before entering Play Mode or from project bootstrap code at runtime.
        /// </summary>
        public void Configure(
            PurrNetStagingRoomController controller,
            NetworkManager networkManager = null,
            PurrNetLobbyService lobbyService = null,
            PurrNetChatBoxUI chatBox = null,
            NetworkLobbyCanvasUI sessionLobbyUI = null)
        {
            UnsubscribeController();
            RestoreSessionLobbyCanvas();
            RestoreChatCanvas();

            m_Controller = controller;
            m_NetworkManager = networkManager;
            m_LobbyService = lobbyService;
            m_ChatBox = chatBox;
            m_SessionLobbyUI = sessionLobbyUI;

            ResolveReferences();
            if (Application.isPlaying)
            {
                if (m_RuntimeRoot == null) BuildUI();
                SubscribeController();
            }
            m_RenderPending = true;
        }

        public void SetPresentation(string title, string subtitle)
        {
            if (!string.IsNullOrWhiteSpace(title)) m_Title = title.Trim();
            if (!string.IsNullOrWhiteSpace(subtitle)) m_Subtitle = subtitle.Trim();
            if (Application.isPlaying && m_RuntimeRoot != null) BuildUI();
            m_RenderPending = true;
        }

        public void SubmitDisplayName()
        {
            SubmitDisplayName(m_NameField != null ? m_NameField.text : string.Empty);
        }

        public void ToggleReady()
        {
            if (m_LeaveInFlight || m_Controller == null) return;
            if (!m_Controller.ToggleReady())
                m_LocalOperationMessage = "Ready state is available after your player joins the roster.";
            m_RenderPending = true;
        }

        public void StartMatch()
        {
            if (m_LeaveInFlight || m_Controller == null) return;
            if (!m_Controller.StartMatch())
                m_LocalOperationMessage = m_Controller.IsLocalHost
                    ? "The configured start requirements are not met yet."
                    : "Only the room host can start the match.";
            m_RenderPending = true;
        }

        public async void LeaveSession()
        {
            if (m_LeaveInFlight) return;

            m_LeaveInFlight = true;
            m_LocalOperationMessage = "Leaving session...";
            m_RenderPending = true;

            try
            {
                // A lobby service should only tear down sessions it created or joined.
                // This keeps the staging UI usable in older/direct-connect demo scenes too.
                if (m_LobbyService != null &&
                    (m_LobbyService.State == NetworkLobbyState.Connected ||
                     m_LobbyService.State == NetworkLobbyState.Leaving))
                {
                    NetworkLobbyOperationResult result = await m_LobbyService.LeaveAsync();
                    m_LocalOperationMessage = result.Succeeded
                        ? string.Empty
                        : string.IsNullOrWhiteSpace(result.Message)
                            ? "Could not leave the session."
                            : result.Message;
                }
                else
                {
                    StopNetworkWithoutLobbyService();
                    await Task.Yield();
                    m_LocalOperationMessage = string.Empty;
                }
            }
            catch (Exception exception)
            {
                m_LocalOperationMessage = $"Could not leave the session: {exception.Message}";
                Debug.LogException(exception, this);
            }
            finally
            {
                m_LeaveInFlight = false;
                if (!IsConnected()) RestoreSessionLobbyCanvas();
                m_RenderPending = true;
            }
        }

        private void SubmitDisplayName(string value)
        {
            if (m_LeaveInFlight || m_Controller == null) return;
            m_Controller.SetDisplayName(value);
            if (m_NameField != null) m_NameField.text = m_Controller.LocalDisplayName;
            m_LocalOperationMessage = string.Empty;
            m_RenderPending = true;
        }

        private void ResolveReferences()
        {
            if (m_Controller == null)
            {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER || UNITY_6000
                m_Controller = FindFirstObjectByType<PurrNetStagingRoomController>();
#else
                m_Controller = FindObjectOfType<PurrNetStagingRoomController>();
#endif
            }

            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            if (m_LobbyService == null && m_Controller != null)
            {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER || UNITY_6000
                m_LobbyService = FindFirstObjectByType<PurrNetLobbyService>();
#else
                m_LobbyService = FindObjectOfType<PurrNetLobbyService>();
#endif
            }
        }

        private void SubscribeController()
        {
            if (m_SubscribedController == m_Controller) return;
            UnsubscribeController();
            m_SubscribedController = m_Controller;
            if (m_SubscribedController == null) return;

            m_SubscribedController.PlayersChanged += RequestRender;
            m_SubscribedController.StateChanged += RequestRender;
            m_SubscribedController.MatchStartedEvent += RequestRender;
        }

        private void UnsubscribeController()
        {
            if (m_SubscribedController == null) return;
            m_SubscribedController.PlayersChanged -= RequestRender;
            m_SubscribedController.StateChanged -= RequestRender;
            m_SubscribedController.MatchStartedEvent -= RequestRender;
            m_SubscribedController = null;
        }

        private void RequestRender()
        {
            m_RenderPending = true;
        }

        private void Render()
        {
            if (m_RuntimeRoot == null) return;

            bool connected = IsConnected();
            bool hasController = m_Controller != null;
            bool matchStarted = hasController && m_Controller.MatchStarted;
            bool showStaging = connected && hasController && !matchStarted;

            if (m_RuntimeRoot.activeSelf != showStaging) m_RuntimeRoot.SetActive(showStaging);
            // The shared lobby's connected view is a useful, unobtrusive Leave card
            // during gameplay. Suppress it only while this staging card owns the screen.
            UpdateSessionLobbyCanvas(showStaging);
            UpdateChatCanvas(connected);

            if (!showStaging) return;

            bool localHost = m_Controller.IsLocalHost;
            bool localReady = m_Controller.LocalReady;
            int playerCount = m_Controller.PlayerCount;
            int readyCount = m_Controller.ReadyPlayerCount;
            int requiredCount = Mathf.Max(1, m_Controller.RequiredPlayerCount);

            if (m_StatusText != null)
                m_StatusText.text = string.IsNullOrWhiteSpace(m_Controller.StatusMessage)
                    ? "Waiting in the staging room."
                    : m_Controller.StatusMessage;

            if (m_CountText != null)
                m_CountText.text = $"PLAYERS  {playerCount}/{requiredCount}     READY  {readyCount}/{playerCount}";

            if (m_RolePillText != null) m_RolePillText.text = localHost ? "ROOM HOST" : "PLAYER";
            if (m_RolePill != null) m_RolePill.color = localHost ? ColorWarning : ColorPrimary;

            if (m_HelpText != null)
            {
                m_HelpText.text = m_Controller.StartPolicy switch
                {
                    PurrNetStagingStartPolicy.AutomaticPlayerThreshold =>
                        "The server starts automatically when the player target and configured ready requirement are met.",
                    PurrNetStagingStartPolicy.AutomaticAllReady =>
                        "The server starts automatically once the minimum roster is present and everyone is ready.",
                    _ => localHost
                        ? "You control the match start. Every player can use the separate chat panel while waiting."
                        : "Ready up when your setup is complete. The room host controls the match start."
                };
            }

            if (m_OperationText != null)
                m_OperationText.text = string.IsNullOrWhiteSpace(m_LocalOperationMessage)
                    ? "Max Players is session capacity; the room's start rule decides when gameplay begins."
                    : m_LocalOperationMessage;

            if (m_NameField != null && !m_NameField.isFocused &&
                !string.Equals(m_NameField.text, m_Controller.LocalDisplayName, StringComparison.Ordinal))
            {
                m_NameField.text = m_Controller.LocalDisplayName;
            }

            bool localPlayerReady = ActiveManager != null && ActiveManager.isLocalPlayerReady;
            SetInteractable(m_NameField, localPlayerReady && !m_LeaveInFlight);
            SetInteractable(m_ApplyNameButton, localPlayerReady && !m_LeaveInFlight);
            SetInteractable(m_ReadyButton, localPlayerReady && !m_LeaveInFlight);
            if (m_ReadyButtonText != null) m_ReadyButtonText.text = localReady ? "Set Not Ready" : "I'm Ready";
            SetButtonColor(m_ReadyButton, localReady ? ColorNeutral : ColorPositive);

            if (m_StartButton != null)
            {
                m_StartButton.gameObject.SetActive(
                    localHost && m_Controller.StartPolicy == PurrNetStagingStartPolicy.HostManual);
                m_StartButton.interactable = !m_LeaveInFlight && m_Controller.CanLocalStartMatch;
            }

            SetInteractable(m_LeaveButton, !m_LeaveInFlight);
            RenderPlayerRows();
        }

        private void RenderPlayerRows()
        {
            IReadOnlyList<PurrNetStagingPlayer> players = m_Controller.Players;
            EnsurePlayerRowCount(players.Count);

            NetworkManager manager = ActiveManager;
            bool hasLocalPlayer = manager != null && manager.isLocalPlayerReady;
            PlayerID localPlayer = hasLocalPlayer ? manager.localPlayer : default;
            const float rowHeight = 50f;

            for (int i = 0; i < m_PlayerRows.Count; i++)
            {
                PlayerRow row = m_PlayerRows[i];
                bool active = i < players.Count;
                row.Root.SetActive(active);
                if (!active) continue;

                PurrNetStagingPlayer player = players[i];
                bool local = hasLocalPlayer && player.PlayerId == localPlayer;
                RectTransform rect = (RectTransform)row.Root.transform;
                rect.anchorMin = new Vector2(0f, 1f);
                rect.anchorMax = new Vector2(1f, 1f);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.offsetMin = new Vector2(6f, -(i + 1) * rowHeight + 3f);
                rect.offsetMax = new Vector2(-6f, -i * rowHeight - 3f);

                row.Background.color = local
                    ? ColorRowLocal
                    : i % 2 == 0 ? ColorRow : ColorRowAlternate;
                row.Slot.text = (i + 1).ToString("00");
                row.Name.text = string.IsNullOrWhiteSpace(player.DisplayName)
                    ? $"Player {player.PlayerId.id.value}"
                    : player.DisplayName;
                row.Tags.text = player.Host && local ? "HOST  ·  YOU"
                    : player.Host ? "HOST"
                    : local ? "YOU"
                    : string.Empty;
                row.ReadyBackground.color = player.Ready ? ColorPositive : ColorNeutral;
                row.ReadyText.text = player.Ready ? "READY" : "WAITING";
            }

            if (m_RosterContent != null)
                m_RosterContent.sizeDelta = new Vector2(0f, Mathf.Max(252f, players.Count * rowHeight));
        }

        private void EnsurePlayerRowCount(int count)
        {
            while (m_PlayerRows.Count < count)
                m_PlayerRows.Add(CreatePlayerRow(m_PlayerRows.Count));
        }

        private PlayerRow CreatePlayerRow(int index)
        {
            GameObject root = NewPanel(
                $"Player {index + 1}",
                m_RosterContent,
                ColorRow,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(6f, -50f),
                new Vector2(-6f, -3f));

            var row = new PlayerRow
            {
                Root = root,
                Background = root.GetComponent<Image>()
            };

            row.Slot = NewText(
                "Slot",
                root.transform,
                "01",
                11,
                FontStyle.Bold,
                ColorDim,
                Vector2.zero,
                new Vector2(0f, 1f),
                new Vector2(13f, 0f),
                new Vector2(45f, 0f),
                TextAnchor.MiddleLeft);
            row.Name = NewText(
                "Name",
                root.transform,
                "Player",
                14,
                FontStyle.Bold,
                ColorText,
                Vector2.zero,
                new Vector2(0.62f, 1f),
                new Vector2(48f, 0f),
                new Vector2(-6f, 0f),
                TextAnchor.MiddleLeft);
            row.Tags = NewText(
                "Tags",
                root.transform,
                string.Empty,
                10,
                FontStyle.Bold,
                ColorWarning,
                new Vector2(0.62f, 0f),
                new Vector2(0.82f, 1f),
                new Vector2(4f, 0f),
                new Vector2(-4f, 0f),
                TextAnchor.MiddleRight);

            GameObject ready = NewPanel(
                "Ready",
                root.transform,
                ColorNeutral,
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(-116f, -13f),
                new Vector2(-12f, 13f));
            row.ReadyBackground = ready.GetComponent<Image>();
            row.ReadyText = NewText(
                "Label",
                ready.transform,
                "WAITING",
                10,
                FontStyle.Bold,
                Color.white,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                TextAnchor.MiddleCenter);
            return row;
        }

        private void UpdateSessionLobbyCanvas(bool shouldSuppress)
        {
            ResolveSessionCanvas();
            if (m_SessionCanvas == null || m_SessionCanvas == m_Canvas) return;

            if (shouldSuppress)
            {
                if (!m_SessionCanvasStateCaptured)
                {
                    m_SessionCanvasOriginalEnabled = m_SessionCanvas.enabled;
                    m_SessionCanvasStateCaptured = true;
                }

                m_SessionCanvas.enabled = false;
                return;
            }

            RestoreSessionLobbyCanvas();
        }

        private void RestoreSessionLobbyCanvas()
        {
            if (m_SessionCanvas != null && m_SessionCanvasStateCaptured)
                m_SessionCanvas.enabled = m_SessionCanvasOriginalEnabled;

            m_SessionCanvasStateCaptured = false;
            m_SessionCanvas = null;
        }

        private void ResolveSessionCanvas()
        {
            if (m_SessionCanvas != null || m_SessionLobbyUI == null) return;
            m_SessionCanvas = m_SessionLobbyUI.GetComponent<Canvas>();
            if (m_SessionCanvas == null) m_SessionCanvas = m_SessionLobbyUI.GetComponentInParent<Canvas>();
        }

        private void UpdateChatCanvas(bool connected)
        {
            ResolveChatCanvas();
            if (m_ChatCanvas == null || m_ChatCanvas == m_Canvas) return;

            m_ChatCanvas.sortingOrder = Mathf.Max(m_ChatSortingOrder, m_SortingOrder + 1);
            if (!m_HideChatWhileOffline) return;

            if (!m_ChatCanvasStateCaptured)
            {
                m_ChatCanvasOriginalEnabled = m_ChatCanvas.enabled;
                m_ChatCanvasStateCaptured = true;
            }

            m_ChatCanvas.enabled = connected && m_ChatCanvasOriginalEnabled;
        }

        private void RestoreChatCanvas()
        {
            if (m_ChatCanvas != null && m_ChatCanvasStateCaptured)
                m_ChatCanvas.enabled = m_ChatCanvasOriginalEnabled;

            m_ChatCanvasStateCaptured = false;
            m_ChatCanvas = null;
        }

        private void ResolveChatCanvas()
        {
            if (m_ChatCanvas != null || m_ChatBox == null) return;
            m_ChatCanvas = m_ChatBox.GetComponent<Canvas>();
            if (m_ChatCanvas == null) m_ChatCanvas = m_ChatBox.GetComponentInParent<Canvas>();
        }

        private bool IsConnected()
        {
            NetworkManager manager = ActiveManager;
            return manager != null && (manager.isClient || manager.isServer);
        }

        private void StopNetworkWithoutLobbyService()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null) return;
            if (manager.isClient) manager.StopClient();
            if (manager.isServer) manager.StopServer();
        }

        private void BuildUI()
        {
            m_PlayerRows.Clear();

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

            Transform stale = transform.Find("PurrNetStagingRuntimeUI");
            if (stale != null)
            {
                stale.gameObject.SetActive(false);
                Destroy(stale.gameObject);
            }

            m_RuntimeRoot = NewPanel(
                "PurrNetStagingRuntimeUI",
                transform,
                ColorBackdrop,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero);

            GameObject card = NewPanel(
                "StagingCard",
                m_RuntimeRoot.transform,
                ColorPanel,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-240f, -360f),
                new Vector2(540f, 360f));

            GameObject header = NewPanel(
                "Header",
                card.transform,
                ColorHeader,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(0f, -88f),
                Vector2.zero);
            NewText(
                "Title",
                header.transform,
                m_Title,
                25,
                FontStyle.Bold,
                ColorText,
                Vector2.zero,
                Vector2.one,
                new Vector2(22f, 34f),
                new Vector2(-170f, -8f),
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
                new Vector2(22f, 9f),
                new Vector2(-170f, -43f),
                TextAnchor.MiddleLeft);
            m_RolePill = NewPanel(
                "Role",
                header.transform,
                ColorPrimary,
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(-144f, -15f),
                new Vector2(-20f, 15f)).GetComponent<Image>();
            m_RolePillText = NewText(
                "Label",
                m_RolePill.transform,
                "PLAYER",
                10,
                FontStyle.Bold,
                Color.white,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                TextAnchor.MiddleCenter);

            m_StatusText = NewText(
                "Status",
                card.transform,
                "Waiting for the staging-room roster...",
                13,
                FontStyle.Bold,
                ColorText,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(22f, -134f),
                new Vector2(-22f, -98f),
                TextAnchor.MiddleLeft);
            m_HelpText = NewText(
                "Help",
                card.transform,
                string.Empty,
                11,
                FontStyle.Normal,
                ColorDim,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(22f, -164f),
                new Vector2(-22f, -135f),
                TextAnchor.MiddleLeft);

            NewText(
                "NameLabel",
                card.transform,
                "DISPLAY NAME",
                10,
                FontStyle.Bold,
                ColorDim,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(22f, -218f),
                new Vector2(144f, -176f),
                TextAnchor.MiddleLeft);
            m_NameField = NewInput(
                "DisplayName",
                card.transform,
                m_Controller != null ? m_Controller.LocalDisplayName : "Player",
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(148f, -216f),
                new Vector2(-144f, -178f),
                "Your player name");
            m_NameField.characterLimit = 48;
            m_NameField.onEndEdit.AddListener(SubmitDisplayName);
            m_ApplyNameButton = NewButton(
                "ApplyName",
                card.transform,
                ColorNeutral,
                "Apply",
                new Vector2(1f, 1f),
                new Vector2(1f, 1f),
                new Vector2(-134f, -216f),
                new Vector2(-22f, -178f));
            m_ApplyNameButton.onClick.AddListener(SubmitDisplayName);

            m_CountText = NewText(
                "RosterHeading",
                card.transform,
                "PLAYERS  0/8     READY  0/0",
                10,
                FontStyle.Bold,
                ColorDim,
                new Vector2(0f, 1f),
                Vector2.one,
                new Vector2(22f, -255f),
                new Vector2(-22f, -225f),
                TextAnchor.MiddleLeft);

            GameObject roster = NewPanel(
                "Roster",
                card.transform,
                ColorRoster,
                Vector2.zero,
                Vector2.one,
                new Vector2(18f, 126f),
                new Vector2(-18f, -258f));
            GameObject viewport = NewPanel(
                "Viewport",
                roster.transform,
                new Color(0f, 0f, 0f, 0f),
                Vector2.zero,
                Vector2.one,
                new Vector2(4f, 4f),
                new Vector2(-4f, -4f));
            viewport.AddComponent<RectMask2D>();

            GameObject content = NewEmpty(
                "Content",
                viewport.transform,
                new Vector2(0f, 1f),
                Vector2.one,
                Vector2.zero,
                Vector2.zero);
            m_RosterContent = (RectTransform)content.transform;
            m_RosterContent.pivot = new Vector2(0.5f, 1f);
            m_RosterContent.sizeDelta = new Vector2(0f, 252f);

            ScrollRect scroll = roster.AddComponent<ScrollRect>();
            scroll.viewport = (RectTransform)viewport.transform;
            scroll.content = m_RosterContent;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 22f;

            m_OperationText = NewText(
                "RuleHint",
                card.transform,
                "Max Players is session capacity; the room's start rule decides when gameplay begins.",
                10,
                FontStyle.Normal,
                ColorDim,
                Vector2.zero,
                Vector2.one,
                new Vector2(22f, 91f),
                new Vector2(-22f, 121f),
                TextAnchor.MiddleLeft);

            m_ReadyButton = NewButton(
                "Ready",
                card.transform,
                ColorPositive,
                "I'm Ready",
                Vector2.zero,
                Vector2.zero,
                new Vector2(22f, 24f),
                new Vector2(224f, 74f));
            m_ReadyButtonText = m_ReadyButton.GetComponentInChildren<Text>();
            m_ReadyButton.onClick.AddListener(ToggleReady);

            m_StartButton = NewButton(
                "StartMatch",
                card.transform,
                ColorPrimary,
                "Start Match",
                new Vector2(0.36f, 0f),
                new Vector2(0.70f, 0f),
                new Vector2(4f, 24f),
                new Vector2(-4f, 74f));
            m_StartButton.onClick.AddListener(StartMatch);

            m_LeaveButton = NewButton(
                "Leave",
                card.transform,
                ColorDanger,
                "Leave",
                Vector2.one,
                Vector2.one,
                new Vector2(-142f, -696f),
                new Vector2(-22f, -646f));
            m_LeaveButton.onClick.AddListener(LeaveSession);

            m_RuntimeRoot.SetActive(false);
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
            string placeholderValue)
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
                placeholderValue,
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
            input.lineType = InputField.LineType.SingleLine;
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
            SetButtonColor(button, color);
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

        private static void SetButtonColor(Button button, Color color)
        {
            if (button == null) return;
            Image image = button.targetGraphic as Image;
            if (image != null) image.color = color;

            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.12f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(color.r, color.g, color.b, 0.36f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;
        }

        private static void SetInteractable(Selectable selectable, bool value)
        {
            if (selectable != null) selectable.interactable = value;
        }

        private static Font GetBuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            return font != null ? font : Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        private static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER || UNITY_6000
            EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
#else
            EventSystem eventSystem = FindObjectOfType<EventSystem>();
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
                    eventSystemObject.AddComponent(inputSystemModule);

                StandaloneInputModule[] legacy = eventSystemObject.GetComponents<StandaloneInputModule>();
                for (int i = 0; i < legacy.Length; i++)
                {
                    legacy[i].enabled = false;
                    Destroy(legacy[i]);
                }

                return;
            }

#pragma warning disable CS0618
            if (eventSystemObject.GetComponent<BaseInputModule>() == null)
                eventSystemObject.AddComponent<StandaloneInputModule>();
#pragma warning restore CS0618
        }
    }
}
