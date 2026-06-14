using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Minimal runtime IMGUI overlay to host / join a PurrNet session without any
    /// UGUI prefab plumbing. Intended as a quick-start helper for projects set up
    /// by the PurrNet Scene Setup Wizard. For production builds replace this with
    /// your own menu UI.
    ///
    /// Reflectively reads and writes <c>address</c> / <c>serverPort</c> on the
    /// active <see cref="GenericTransport"/> (e.g. UDPTransport, WebTransport) so
    /// the same component works regardless of which concrete transport the
    /// NetworkManager uses.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Host-Join UI")]
    [DefaultExecutionOrder(-300)]
    public sealed class PurrNetHostJoinUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Header("Defaults")]
        [Tooltip("Initial address used when joining as a client. Ignored for server / host.")]
        [SerializeField] private string m_DefaultAddress = "127.0.0.1";

        [Tooltip("Initial port written into the transport when either hosting or joining.")]
        [SerializeField] private ushort m_DefaultPort = 5000;

        [Header("Display")]
        [Tooltip("Anchor corner of the overlay.")]
        [SerializeField] private Anchor m_Anchor = Anchor.TopLeft;

        [Tooltip("Pixel margin from the anchor corner.")]
        [SerializeField] private Vector2 m_Margin = new Vector2(16f, 16f);

        [Tooltip("Overall width of the overlay panel.")]
        [SerializeField, Min(180f)] private float m_Width = 260f;

        [Tooltip("Hide the overlay once a client session is fully connected.")]
        [SerializeField] private bool m_HideWhenConnected = false;

        [Tooltip("Disable the UI entirely (useful for production builds while keeping the component on the prefab).")]
        [SerializeField] private bool m_ShowOverlay = true;

        [Tooltip("Menu title shown at the top of the overlay.")]
        [SerializeField] private string m_Title = "Multiplayer";

        [Tooltip("Show the advanced 'Server-only' button. Hidden by default so the overlay reads as a player-facing direct-connect menu.")]
        [SerializeField] private bool m_ShowServerOnlyButton = false;

        [Tooltip("Toggle the overlay with a key. Set to None to always keep it visible.")]
        [SerializeField] private KeyCode m_ToggleKey = KeyCode.None;

        public enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight }

        private string m_Address;
        private string m_PortText;
        private string m_Status = "Idle";
        private bool m_RuntimeVisible = true;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            m_Address = m_DefaultAddress;
            m_PortText = m_DefaultPort.ToString();
        }

        private void OnEnable()
        {
            var nm = ActiveManager;
            if (nm == null) return;

            // Prevent automatic server/client start from hijacking wizard-driven sessions.
            // Users can re-enable explicit flags on the NetworkManager if desired.
            nm.onServerConnectionState += OnServerState;
            nm.onClientConnectionState += OnClientState;

            TryReadFromTransport(nm);
        }

        private void OnDisable()
        {
            var nm = ActiveManager;
            if (nm == null) return;

            nm.onServerConnectionState -= OnServerState;
            nm.onClientConnectionState -= OnClientState;
        }

        private void OnServerState(ConnectionState state) => m_Status = $"Server: {state} | Client: {GetClientState()}";
        private void OnClientState(ConnectionState state) => m_Status = $"Server: {GetServerState()} | Client: {state}";

        private ConnectionState GetServerState()
        {
            var nm = ActiveManager;
            return nm != null ? nm.serverState : ConnectionState.Disconnected;
        }

        private ConnectionState GetClientState()
        {
            var nm = ActiveManager;
            return nm != null ? nm.clientState : ConnectionState.Disconnected;
        }

        private void Update()
        {
            if (m_ToggleKey != KeyCode.None && Input.GetKeyDown(m_ToggleKey))
            {
                m_RuntimeVisible = !m_RuntimeVisible;
            }
        }

        private void OnGUI()
        {
            if (!m_ShowOverlay || !m_RuntimeVisible) return;

            var nm = ActiveManager;
            if (nm == null)
            {
                DrawNoManagerHint();
                return;
            }

            var serverActive = nm.serverState != ConnectionState.Disconnected;
            var clientActive = nm.clientState != ConnectionState.Disconnected;
            var connected = nm.clientState == ConnectionState.Connected || serverActive;

            if (m_HideWhenConnected && nm.clientState == ConnectionState.Connected && !nm.isHost)
                return;

            var rect = GetAnchoredRect(m_Width, connected ? 130f : 200f);
            GUILayout.BeginArea(rect, GUI.skin.box);

            GUILayout.Label(m_Title, EditorStylesLike.Bold);
            GUILayout.Space(2f);

            if (!serverActive && !clientActive)
            {
                DrawDirectConnectMenu(nm);
            }
            else
            {
                DrawSessionStatus(nm, serverActive, clientActive);
            }

            GUILayout.EndArea();
        }

        private void DrawDirectConnectMenu(NetworkManager nm)
        {
            GUILayout.Label("Host a session or enter a host's IP to join.", EditorStylesLike.WordWrap);
            GUILayout.Space(4f);

            if (GUILayout.Button("Host Game", GUILayout.Height(28f)))
            {
                StartHost(nm);
            }

            GUILayout.Space(6f);
            GUILayout.Label("Join by IP", EditorStylesLike.Bold);

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP", GUILayout.Width(32f));
            m_Address = GUILayout.TextField(m_Address ?? string.Empty);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Port", GUILayout.Width(32f));
            m_PortText = GUILayout.TextField(m_PortText ?? string.Empty, GUILayout.Width(72f));
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Join", GUILayout.Width(90f), GUILayout.Height(24f)))
            {
                StartClient(nm);
            }
            GUILayout.EndHorizontal();

            if (m_ShowServerOnlyButton)
            {
                GUILayout.Space(4f);
                if (GUILayout.Button("Server-only (dedicated)"))
                {
                    StartServerOnly(nm);
                }
            }
        }

        private void StartHost(NetworkManager nm)
        {
            if (!CanStartSession(nm, "host")) return;
            if (!CanBindHostPort()) return;

            ApplyAddressPortToTransport(nm);
            m_Status = "Starting host...";
            try
            {
                nm.StartHost();
            }
            catch (Exception e)
            {
                m_Status = $"Failed to start host: {e.Message}";
                Debug.LogException(e, this);
            }
        }

        private void StartClient(NetworkManager nm)
        {
            if (!CanStartSession(nm, "join")) return;

            ApplyAddressPortToTransport(nm);
            m_Status = $"Connecting to {m_Address}:{m_PortText}...";
            try
            {
                nm.StartClient();
            }
            catch (Exception e)
            {
                m_Status = $"Failed to join: {e.Message}";
                Debug.LogException(e, this);
            }
        }

        private void StartServerOnly(NetworkManager nm)
        {
            if (!CanStartSession(nm, "start server")) return;
            if (!CanBindHostPort()) return;

            ApplyAddressPortToTransport(nm);
            m_Status = "Starting server...";
            try
            {
                nm.StartServer();
            }
            catch (Exception e)
            {
                m_Status = $"Failed to start server: {e.Message}";
                Debug.LogException(e, this);
            }
        }

        private bool CanStartSession(NetworkManager nm, string action)
        {
            if (nm.serverState == ConnectionState.Disconnected &&
                nm.clientState == ConnectionState.Disconnected)
            {
                return true;
            }

            m_Status = $"Cannot {action}: Server {nm.serverState}, Client {nm.clientState}.";
            return false;
        }

        private bool CanBindHostPort()
        {
            ushort port = GetConfiguredPort();
            if (IsUdpPortAvailable(port)) return true;

            m_Status = $"Port {port} is already in use. Use Join on clients or choose a free host port.";
            return false;
        }

        private ushort GetConfiguredPort()
        {
            ushort port;
            return ushort.TryParse(m_PortText, out port) ? port : m_DefaultPort;
        }

        private void DrawSessionStatus(NetworkManager nm, bool serverActive, bool clientActive)
        {
            string role = nm.isHost ? "Host" : serverActive ? "Server" : "Client";
            GUILayout.Label($"Role: {role}");
            GUILayout.Label($"Server: {nm.serverState}");
            GUILayout.Label($"Client: {nm.clientState}");
            GUILayout.Space(4f);

            if (GUILayout.Button("Disconnect", GUILayout.Height(26f)))
            {
                if (clientActive) nm.StopClient();
                if (serverActive) nm.StopServer();
            }
        }

        private void DrawNoManagerHint()
        {
            var rect = GetAnchoredRect(m_Width, 80f);
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label(m_Title, EditorStylesLike.Bold);
            GUILayout.Label("No NetworkManager found in scene.", EditorStylesLike.WordWrap);
            GUILayout.EndArea();
        }

        private Rect GetAnchoredRect(float width, float height)
        {
            float x, y;
            switch (m_Anchor)
            {
                default:
                case Anchor.TopLeft:
                    x = m_Margin.x;
                    y = m_Margin.y;
                    break;
                case Anchor.TopRight:
                    x = Screen.width - width - m_Margin.x;
                    y = m_Margin.y;
                    break;
                case Anchor.BottomLeft:
                    x = m_Margin.x;
                    y = Screen.height - height - m_Margin.y;
                    break;
                case Anchor.BottomRight:
                    x = Screen.width - width - m_Margin.x;
                    y = Screen.height - height - m_Margin.y;
                    break;
            }
            return new Rect(x, y, width, height);
        }

        // ------------------------------------------------------------------
        // Transport reflection helpers
        // ------------------------------------------------------------------

        private void TryReadFromTransport(NetworkManager nm)
        {
            var t = nm.transport;
            if (t == null) return;

            var type = t.GetType();
            var addressProp = type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
            var portProp = type.GetProperty("serverPort", BindingFlags.Public | BindingFlags.Instance);

            if (addressProp != null && addressProp.CanRead)
            {
                var v = addressProp.GetValue(t) as string;
                if (!string.IsNullOrEmpty(v)) m_Address = v;
            }

            if (portProp != null && portProp.CanRead)
            {
                var v = portProp.GetValue(t);
                if (v is ushort u) m_PortText = u.ToString();
            }
        }

        private void ApplyAddressPortToTransport(NetworkManager nm)
        {
            var t = nm.transport;
            if (t == null) return;

            ushort port;
            if (!ushort.TryParse(m_PortText, out port)) port = m_DefaultPort;

            var type = t.GetType();
            var addressProp = type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
            var portProp = type.GetProperty("serverPort", BindingFlags.Public | BindingFlags.Instance);

            if (addressProp != null && addressProp.CanWrite && !string.IsNullOrEmpty(m_Address))
                addressProp.SetValue(t, m_Address);

            if (portProp != null && portProp.CanWrite)
                portProp.SetValue(t, port);
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

        private static class EditorStylesLike
        {
            public static GUIStyle Bold
            {
                get
                {
                    if (s_Bold == null)
                    {
                        s_Bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
                    }
                    return s_Bold;
                }
            }

            public static GUIStyle WordWrap
            {
                get
                {
                    if (s_WordWrap == null)
                    {
                        s_WordWrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
                    }
                    return s_WordWrap;
                }
            }

            private static GUIStyle s_Bold;
            private static GUIStyle s_WordWrap;
        }
    }
}
