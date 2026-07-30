using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Modern UGUI host / join overlay for PurrNet demo scenes.
    ///
    /// Builds its entire UI hierarchy programmatically on Awake so the demo scene
    /// only needs this single component on a GameObject (no Canvas / EventSystem
    /// wiring required by the user). Dark theme, accent buttons, status pill.
    ///
    /// One user clicks "Host" to start as a hosting client. The other user enters
    /// the host's IP and clicks "Join" to direct-connect. Status updates live so
    /// the PurrNet transport bridge can be quickly validated.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Demo Canvas UI")]
    [DefaultExecutionOrder(-300)]
    public sealed class PurrNetDemoCanvasUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Header("Defaults")]
        [SerializeField] private string m_DefaultAddress = "127.0.0.1";
        [SerializeField] private ushort m_DefaultPort = 5000;
        [SerializeField] private string m_Title = "PurrNet Demo";
        [SerializeField] private string m_Subtitle = "Game Creator 2 Networking";

        [Header("Behaviour")]
        [Tooltip("Hide the panel once a client is fully connected. Status pill stays visible.")]
        [SerializeField] private bool m_CollapseWhenConnected = true;

        [Tooltip("Build a sortingOrder=1000 Canvas so it always renders on top of game UI.")]
        [SerializeField] private int m_SortingOrder = 1000;

        // Theme
        private static readonly Color BG_PANEL    = new Color(0.08f, 0.09f, 0.11f, 0.94f);
        private static readonly Color BG_HEADER   = new Color(0.11f, 0.12f, 0.16f, 1.00f);
        private static readonly Color BG_FIELD    = new Color(0.16f, 0.17f, 0.21f, 1.00f);
        private static readonly Color BG_BTN      = new Color(0.22f, 0.24f, 0.30f, 1.00f);
        private static readonly Color BG_BTN_PRI  = new Color(0.32f, 0.55f, 0.92f, 1.00f); // host accent
        private static readonly Color BG_BTN_OK   = new Color(0.27f, 0.74f, 0.50f, 1.00f); // join accent
        private static readonly Color BG_BTN_DNG  = new Color(0.86f, 0.32f, 0.34f, 1.00f); // disconnect
        private static readonly Color TXT_MAIN    = new Color(0.92f, 0.94f, 0.97f, 1.00f);
        private static readonly Color TXT_DIM     = new Color(0.66f, 0.69f, 0.76f, 1.00f);
        private static readonly Color PILL_IDLE   = new Color(0.30f, 0.32f, 0.38f, 1.00f);
        private static readonly Color PILL_LIVE   = new Color(0.27f, 0.74f, 0.50f, 1.00f);
        private static readonly Color PILL_BUSY   = new Color(0.92f, 0.65f, 0.20f, 1.00f);
        private static readonly Color PILL_FAIL   = new Color(0.86f, 0.32f, 0.34f, 1.00f);

        private Canvas m_Canvas;
        private GameObject m_Panel;
        private GameObject m_AddressLabel;
        private GameObject m_PortLabel;
        private InputField m_AddressField;
        private InputField m_PortField;
        private Button m_HostButton;
        private Button m_JoinButton;
        private Button m_DisconnectButton;
        private Text m_StatusText;
        private Text m_StatusPillText;
        private Image m_StatusPill;
        private Text m_RoleText;
        private bool m_StartRequestInFlight;
        private bool m_DisconnectRequestInFlight;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            BuildUI();
            EnsureEventSystem();
        }

        private void OnEnable()
        {
            var nm = ActiveManager;
            if (nm == null) return;
            nm.onServerConnectionState += OnServerState;
            nm.onClientConnectionState += OnClientState;
            RefreshUI();
        }

        private void OnDisable()
        {
            var nm = ActiveManager;
            if (nm == null) return;
            nm.onServerConnectionState -= OnServerState;
            nm.onClientConnectionState -= OnClientState;
        }

        private void Update()
        {
            if (!m_StartRequestInFlight && !m_DisconnectRequestInFlight) return;
            RefreshUI();
        }

        private void OnServerState(ConnectionState state) { RefreshUI(); }
        private void OnClientState(ConnectionState state) { RefreshUI(); }

        // -------------------------------------------------------------
        // UI Build
        // -------------------------------------------------------------

        private void BuildUI()
        {
            // Canvas
            m_Canvas = gameObject.GetComponent<Canvas>();
            if (m_Canvas == null) m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = m_SortingOrder;

            var scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
                gameObject.AddComponent<GraphicRaycaster>();

            // Panel (top-left card)
            m_Panel = NewUI("Panel", transform, BG_PANEL,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(0f, 1f),
                pivot: new Vector2(0f, 1f),
                anchoredPos: new Vector2(24f, -24f),
                size: new Vector2(360f, 280f));

            // Header
            var header = NewUI("Header", m_Panel.transform, BG_HEADER,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                pivot: new Vector2(0.5f, 1f),
                anchoredPos: new Vector2(0f, 0f),
                size: new Vector2(0f, 56f));
            NewText("Title", header.transform, m_Title, 18, FontStyle.Bold, TXT_MAIN,
                anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(16f, 22f), offsetMax: new Vector2(-16f, -6f),
                align: TextAnchor.MiddleLeft);
            NewText("Subtitle", header.transform, m_Subtitle, 11, FontStyle.Normal, TXT_DIM,
                anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(16f, 4f), offsetMax: new Vector2(-16f, -28f),
                align: TextAnchor.MiddleLeft);

            // Status pill (top right of header)
            m_StatusPill = NewUI("StatusPill", header.transform, PILL_IDLE,
                anchorMin: new Vector2(1f, 0.5f), anchorMax: new Vector2(1f, 0.5f),
                pivot: new Vector2(1f, 0.5f),
                anchoredPos: new Vector2(-12f, 0f),
                size: new Vector2(82f, 22f)).GetComponent<Image>();
            m_StatusPillText = NewText("PillText", m_StatusPill.transform, "Idle", 10, FontStyle.Bold, Color.white,
                anchorMin: Vector2.zero, anchorMax: Vector2.one,
                offsetMin: Vector2.zero, offsetMax: Vector2.zero,
                align: TextAnchor.MiddleCenter);

            // Address row
            var addrLabel = NewText("AddressLabel", m_Panel.transform, "Host Address", 11, FontStyle.Bold, TXT_DIM,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(18f, -78f), offsetMax: new Vector2(-18f, -64f),
                align: TextAnchor.MiddleLeft);
            m_AddressLabel = addrLabel.gameObject;
            m_AddressField = NewInput("AddressField", m_Panel.transform, BG_FIELD, m_DefaultAddress,
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(18f, -110f), offsetMax: new Vector2(-110f, -82f));

            var portLabel = NewText("PortLabel", m_Panel.transform, "Port", 11, FontStyle.Bold, TXT_DIM,
                anchorMin: new Vector2(1f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(-92f, -78f), offsetMax: new Vector2(-18f, -64f),
                align: TextAnchor.MiddleLeft);
            m_PortLabel = portLabel.gameObject;
            m_PortField = NewInput("PortField", m_Panel.transform, BG_FIELD, m_DefaultPort.ToString(),
                anchorMin: new Vector2(1f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(-92f, -110f), offsetMax: new Vector2(-18f, -82f));
            m_PortField.contentType = InputField.ContentType.IntegerNumber;

            // Buttons row 1
            m_HostButton = NewButton("HostButton", m_Panel.transform, BG_BTN_PRI, "Host",
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(0.5f, 1f),
                offsetMin: new Vector2(18f, -158f), offsetMax: new Vector2(-6f, -120f));
            m_HostButton.onClick.AddListener(OnHostClicked);

            m_JoinButton = NewButton("JoinButton", m_Panel.transform, BG_BTN_OK, "Join",
                anchorMin: new Vector2(0.5f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(6f, -158f), offsetMax: new Vector2(-18f, -120f));
            m_JoinButton.onClick.AddListener(OnJoinClicked);

            // Disconnect (full width, only visible when connected)
            m_DisconnectButton = NewButton("DisconnectButton", m_Panel.transform, BG_BTN_DNG, "Disconnect",
                anchorMin: new Vector2(0f, 1f), anchorMax: new Vector2(1f, 1f),
                offsetMin: new Vector2(18f, -158f), offsetMax: new Vector2(-18f, -120f));
            m_DisconnectButton.onClick.AddListener(OnDisconnectClicked);
            m_DisconnectButton.gameObject.SetActive(false);

            // Role + Status footer
            m_RoleText = NewText("Role", m_Panel.transform, "Role: Offline", 12, FontStyle.Bold, TXT_MAIN,
                anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(1f, 0f),
                offsetMin: new Vector2(18f, 50f), offsetMax: new Vector2(-18f, 76f),
                align: TextAnchor.MiddleLeft);

            m_StatusText = NewText("Status", m_Panel.transform, "Idle. Click Host or Join to start.", 11, FontStyle.Normal, TXT_DIM,
                anchorMin: new Vector2(0f, 0f), anchorMax: new Vector2(1f, 0f),
                offsetMin: new Vector2(18f, 14f), offsetMax: new Vector2(-18f, 48f),
                align: TextAnchor.MiddleLeft);
        }

        private void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER || UNITY_6000
            var es = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
#else
            var es = UnityEngine.Object.FindObjectOfType<EventSystem>();
#endif
            GameObject go;
            if (es != null)
            {
                go = es.gameObject;
            }
            else
            {
                go = new GameObject("EventSystem");
                go.AddComponent<EventSystem>();
            }

            EnsureCompatibleInputModule(go);
        }

        private static void EnsureCompatibleInputModule(GameObject go)
        {
            if (go == null) return;

            // Prefer the new Input System UI module when the package is present so the
            // overlay works on projects with Active Input Handling = Input System Package
            // (the legacy StandaloneInputModule is a no-op there).
            Type newModuleType = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (newModuleType != null)
            {
                if (go.GetComponent(newModuleType) == null)
                {
                    go.AddComponent(newModuleType);
                }

                StandaloneInputModule[] legacyModules = go.GetComponents<StandaloneInputModule>();
                for (int i = 0; i < legacyModules.Length; i++)
                {
                    legacyModules[i].enabled = false;
                    UnityEngine.Object.Destroy(legacyModules[i]);
                }

                return;
            }

#pragma warning disable CS0618 // legacy fallback when InputSystem package isn't installed
            if (go.GetComponent<BaseInputModule>() == null)
            {
                go.AddComponent<StandaloneInputModule>();
            }
#pragma warning restore CS0618
        }

        // -------------------------------------------------------------
        // Button handlers
        // -------------------------------------------------------------

        private void OnHostClicked()
        {
            var nm = ActiveManager;
            if (nm == null) { SetStatus("No NetworkManager in scene.", PILL_FAIL, "Error"); return; }
            if (!CanStartSession(nm, "host")) return;
            if (!CanBindHostPort()) return;

            ApplyAddressPortToTransport(nm);
            m_DisconnectRequestInFlight = false;
            m_StartRequestInFlight = true;
            SetStartButtonsInteractable(false);
            SetStatus("Starting host…", PILL_BUSY, "Hosting");
            try
            {
                nm.StartHost();
            }
            catch (Exception e)
            {
                m_StartRequestInFlight = false;
                SetStartButtonsInteractable(true);
                SetStatus($"Failed to start host: {e.Message}", PILL_FAIL, "Error");
                Debug.LogException(e, this);
            }
        }

        private void OnJoinClicked()
        {
            var nm = ActiveManager;
            if (nm == null) { SetStatus("No NetworkManager in scene.", PILL_FAIL, "Error"); return; }
            if (!CanStartSession(nm, "join")) return;

            ApplyAddressPortToTransport(nm);
            m_DisconnectRequestInFlight = false;
            m_StartRequestInFlight = true;
            SetStartButtonsInteractable(false);
            SetStatus($"Connecting to {m_AddressField.text}:{m_PortField.text}…", PILL_BUSY, "Joining");
            try
            {
                nm.StartClient();
            }
            catch (Exception e)
            {
                m_StartRequestInFlight = false;
                SetStartButtonsInteractable(true);
                SetStatus($"Failed to join: {e.Message}", PILL_FAIL, "Error");
                Debug.LogException(e, this);
            }
        }

        private void OnDisconnectClicked()
        {
            var nm = ActiveManager;
            if (nm == null) return;
            m_StartRequestInFlight = false;
            m_DisconnectRequestInFlight = true;
            if (nm.clientState != ConnectionState.Disconnected) nm.StopClient();
            if (nm.serverState != ConnectionState.Disconnected) nm.StopServer();
            RefreshUI();
        }

        // -------------------------------------------------------------
        // Status
        // -------------------------------------------------------------

        private void RefreshUI()
        {
            var nm = ActiveManager;
            if (nm == null)
            {
                SetRole("Role: Offline (no NetworkManager)");
                SetStatus("Add a NetworkManager to the scene.", PILL_FAIL, "Error");
                ToggleConnectedView(false);
                return;
            }

            bool serverActive = nm.serverState != ConnectionState.Disconnected;
            bool clientActive = nm.clientState != ConnectionState.Disconnected;
            bool anyActive = serverActive || clientActive;
            bool disconnectWasInFlight = m_DisconnectRequestInFlight;
            if (!anyActive)
            {
                m_StartRequestInFlight = false;
                m_DisconnectRequestInFlight = false;
            }

            bool connected = nm.clientState == ConnectionState.Connected || (nm.isHost && nm.serverState == ConnectionState.Connected);

            if (disconnectWasInFlight)
            {
                SetRole(anyActive ? "Role: Disconnecting" : "Role: Offline");
                string disconnectStatus = anyActive
                    ? $"Disconnecting… Server: {nm.serverState} · Client: {nm.clientState}"
                    : "Disconnected.";
                SetStatus(disconnectStatus, anyActive ? PILL_BUSY : PILL_IDLE, anyActive ? "Stopping" : "Idle");
                ToggleConnectedView(false);
                return;
            }

            string role = nm.isHost ? "Host" : serverActive ? "Server" : clientActive ? "Client" : "Offline";
            SetRole($"Role: {role}");

            string status = $"Server: {nm.serverState} · Client: {nm.clientState}";
            Color pill = connected ? PILL_LIVE : anyActive ? PILL_BUSY : PILL_IDLE;
            string pillText = connected ? "Live" : anyActive ? "Busy" : "Idle";
            SetStatus(status, pill, pillText);

            ToggleConnectedView(anyActive);
        }

        private void ToggleConnectedView(bool active)
        {
            if (m_HostButton != null) m_HostButton.gameObject.SetActive(!active);
            if (m_JoinButton != null) m_JoinButton.gameObject.SetActive(!active);
            if (m_DisconnectButton != null) m_DisconnectButton.gameObject.SetActive(active);
            if (!active) SetStartButtonsInteractable(!m_StartRequestInFlight && !m_DisconnectRequestInFlight);

            // Hide the address / port rows entirely when connected so they don't collide
            // with the footer (role + status text). Showing them disabled-but-rendered led
            // to visible overlap on the join client because the panel is intentionally
            // compact.
            if (m_AddressLabel != null) m_AddressLabel.SetActive(!active);
            if (m_PortLabel != null) m_PortLabel.SetActive(!active);
            if (m_AddressField != null) m_AddressField.gameObject.SetActive(!active);
            if (m_PortField != null) m_PortField.gameObject.SetActive(!active);

            if (m_Panel == null) return;
            var rt = m_Panel.GetComponent<RectTransform>();

            if (m_CollapseWhenConnected && active)
            {
                var nm = ActiveManager;
                if (nm != null && nm.clientState == ConnectionState.Connected && !nm.isHost)
                {
                    rt.sizeDelta = new Vector2(rt.sizeDelta.x, 200f);
                    return;
                }
            }

            rt.sizeDelta = new Vector2(rt.sizeDelta.x, 280f);
        }

        private void SetStatus(string message, Color pillColor, string pillLabel)
        {
            if (m_StatusText != null) m_StatusText.text = message;
            if (m_StatusPill != null) m_StatusPill.color = pillColor;
            if (m_StatusPillText != null) m_StatusPillText.text = pillLabel;
        }

        private void SetRole(string text)
        {
            if (m_RoleText != null) m_RoleText.text = text;
        }

        private bool CanStartSession(NetworkManager nm, string action)
        {
            if (m_StartRequestInFlight)
            {
                SetStatus("A network start request is already in progress.", PILL_BUSY, "Busy");
                return false;
            }

            if (nm.serverState == ConnectionState.Disconnected &&
                nm.clientState == ConnectionState.Disconnected)
            {
                return true;
            }

            SetStatus($"Cannot {action}: Server {nm.serverState}, Client {nm.clientState}.", PILL_BUSY, "Busy");
            RefreshUI();
            return false;
        }

        private bool CanBindHostPort()
        {
            ushort port = GetConfiguredPort();
            if (IsUdpPortAvailable(port)) return true;

            SetStatus($"Port {port} is already in use. Use Join on clients or choose a free host port.", PILL_FAIL, "Error");
            return false;
        }

        private ushort GetConfiguredPort()
        {
            ushort port;
            return m_PortField != null && ushort.TryParse(m_PortField.text, out port)
                ? port
                : m_DefaultPort;
        }

        private void SetStartButtonsInteractable(bool interactable)
        {
            if (m_HostButton != null) m_HostButton.interactable = interactable;
            if (m_JoinButton != null) m_JoinButton.interactable = interactable;
        }

        private static bool IsUdpPortAvailable(ushort port)
        {
            if (CanBindUdp(AddressFamily.InterNetworkV6, IPAddress.IPv6Any, port, true))
                return true;

            return CanBindUdp(AddressFamily.InterNetwork, IPAddress.Any, port, false);
        }

        private static bool CanBindUdp(AddressFamily family, IPAddress address, ushort port, bool dualMode)
        {
            try
            {
                using (var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp))
                {
                    if (family == AddressFamily.InterNetworkV6)
                    {
                        socket.DualMode = dualMode;
                    }

                    socket.Bind(new IPEndPoint(address, port));
                    return true;
                }
            }
            catch (SocketException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        // -------------------------------------------------------------
        // Transport reflection (kept consistent with PurrNetHostJoinUI)
        // -------------------------------------------------------------

        private void ApplyAddressPortToTransport(NetworkManager nm)
        {
            var t = nm.transport;
            if (t == null) return;

            ushort port;
            if (!ushort.TryParse(m_PortField.text, out port)) port = m_DefaultPort;

            var type = t.GetType();
            var addressProp = type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
            var portProp = type.GetProperty("serverPort", BindingFlags.Public | BindingFlags.Instance);

            if (addressProp != null && addressProp.CanWrite && !string.IsNullOrEmpty(m_AddressField.text))
                addressProp.SetValue(t, m_AddressField.text);

            if (portProp != null && portProp.CanWrite)
                portProp.SetValue(t, port);
        }

        // -------------------------------------------------------------
        // UI helpers
        // -------------------------------------------------------------

        private static GameObject NewUI(string name, Transform parent, Color color,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            var img = go.GetComponent<Image>();
            img.color = color;
            return go;
        }

        private static Text NewText(string name, Transform parent, string content,
            int fontSize, FontStyle style, Color color,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax,
            TextAnchor align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            var txt = go.GetComponent<Text>();
            txt.text = content;
            txt.fontSize = fontSize;
            txt.fontStyle = style;
            txt.color = color;
            txt.alignment = align;
            txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                    ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            return txt;
        }

        private static InputField NewInput(string name, Transform parent, Color bg, string defaultText,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            root.transform.SetParent(parent, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            var img = root.GetComponent<Image>();
            img.color = bg;

            var text = NewText("Text", root.transform, "", 13, FontStyle.Normal, new Color(0.92f, 0.94f, 0.97f, 1f),
                anchorMin: Vector2.zero, anchorMax: Vector2.one,
                offsetMin: new Vector2(10f, 4f), offsetMax: new Vector2(-10f, -4f),
                align: TextAnchor.MiddleLeft);
            text.supportRichText = false;

            var placeholder = NewText("Placeholder", root.transform, defaultText, 13, FontStyle.Italic, new Color(0.55f, 0.58f, 0.66f, 1f),
                anchorMin: Vector2.zero, anchorMax: Vector2.one,
                offsetMin: new Vector2(10f, 4f), offsetMax: new Vector2(-10f, -4f),
                align: TextAnchor.MiddleLeft);

            var input = root.GetComponent<InputField>();
            input.targetGraphic = img;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.text = defaultText;
            return input;
        }

        private static Button NewButton(string name, Transform parent, Color bg, string label,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            root.transform.SetParent(parent, false);
            var rt = (RectTransform)root.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
            var img = root.GetComponent<Image>();
            img.color = bg;
            var btn = root.GetComponent<Button>();
            btn.targetGraphic = img;

            ColorBlock cb = btn.colors;
            cb.normalColor = bg;
            cb.highlightedColor = Color.Lerp(bg, Color.white, 0.10f);
            cb.pressedColor = Color.Lerp(bg, Color.black, 0.15f);
            cb.selectedColor = cb.highlightedColor;
            cb.disabledColor = new Color(bg.r, bg.g, bg.b, 0.5f);
            cb.fadeDuration = 0.10f;
            btn.colors = cb;

            NewText("Label", root.transform, label, 14, FontStyle.Bold, Color.white,
                anchorMin: Vector2.zero, anchorMax: Vector2.one,
                offsetMin: Vector2.zero, offsetMax: Vector2.zero,
                align: TextAnchor.MiddleCenter);
            return btn;
        }
    }
}
