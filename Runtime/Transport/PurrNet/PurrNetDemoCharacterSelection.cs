using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Arawn.GameCreator2.Networking;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct PurrNetDemoCharacterSelectionPacket : IPackedAuto
    {
        public int characterIndex;
    }

    /// <summary>
    /// Runtime controller for the generated PurrNet character selection demo.
    /// Selection buttons call this through GC2 ButtonInstructions.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Demo Character Selection")]
    [DefaultExecutionOrder(-250)]
    public sealed class PurrNetDemoCharacterSelection : MonoBehaviour, INetworkDemoCharacterSelectionTarget
    {
        [Header("References")]
        [SerializeField] private NetworkManager m_NetworkManager;
        [SerializeField] private List<GameObject> m_PlayerPrefabs = new();
        [SerializeField] private string[] m_DisplayNames = Array.Empty<string>();

        [Header("UI")]
        [SerializeField] private GameObject m_SelectionRoot;
        [SerializeField] private GameObject m_InGameRoot;
        [SerializeField] private InputField m_AddressInput;
        [SerializeField] private InputField m_PortInput;
        [SerializeField] private Text m_StatusText;
        [SerializeField] private Text[] m_StatusTexts = Array.Empty<Text>();
        [SerializeField] private Text m_SelectedText;
        [SerializeField] private Graphic[] m_SelectionHighlights = Array.Empty<Graphic>();

        [Header("Defaults")]
        [SerializeField] private int m_SelectedIndex;
        [SerializeField] private string m_DefaultAddress = "127.0.0.1";
        [SerializeField] private ushort m_DefaultPort = 5000;

        [Header("Selection Sync")]
        [SerializeField, Min(0.25f)] private float m_PublishSelectionTimeout = 8f;

        private readonly Dictionary<PlayerID, int> m_ServerSelections = new();

        private NetworkManager m_HookedManager;
        private Coroutine m_PublishRoutine;
        private bool m_SubscribedServer;
        private string m_Status = "Choose a character.";

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        public int SelectedIndex => ClampIndex(m_SelectedIndex);

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            InitializeInputs();
            UpdateVisualState();
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            SchedulePublishSelection();
            UpdateVisualState();
        }

        private void Start()
        {
            TryHookNetworkManager();
            SchedulePublishSelection();
            UpdateVisualState();
        }

        private void OnDisable()
        {
            StopPublishRoutine();
            UnhookNetworkManager();
        }

        private void Update()
        {
            TryHookNetworkManager();
            UpdateVisualState();
        }

        public void SelectCharacter(int index)
        {
            m_SelectedIndex = ClampIndex(index);
            SetStatus($"Selected {GetCharacterName(m_SelectedIndex)}.");
            SchedulePublishSelection();
            UpdateVisualState();
        }

        public void StartHost()
        {
            NetworkManager manager = ActiveManager;
            if (!CanStartSession(manager, "host")) return;
            if (!CanBindHostPort()) return;

            ApplyAddressPortToTransport(manager);
            SetStatus($"Starting host as {GetCharacterName(m_SelectedIndex)}...");

            try
            {
                manager.StartHost();
                SchedulePublishSelection();
            }
            catch (Exception e)
            {
                SetStatus($"Failed to host: {e.Message}");
                Debug.LogException(e, this);
            }
        }

        public void JoinServer()
        {
            NetworkManager manager = ActiveManager;
            if (!CanStartSession(manager, "join")) return;

            ApplyAddressPortToTransport(manager);
            SetStatus($"Joining {ReadAddress()}:{ReadPort()} as {GetCharacterName(m_SelectedIndex)}...");

            try
            {
                manager.StartClient();
                SchedulePublishSelection();
            }
            catch (Exception e)
            {
                SetStatus($"Failed to join: {e.Message}");
                Debug.LogException(e, this);
            }
        }

        public void StopSession()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null)
            {
                SetStatus("No NetworkManager found.");
                return;
            }

            if (manager.isClient) manager.StopClient();
            if (manager.isServer) manager.StopServer();

            m_ServerSelections.Clear();
            SetStatus("Session stopped.");
            UpdateVisualState();
        }

        public bool TryGetPlayerPrefab(PlayerID player, out GameObject prefab)
        {
            prefab = null;
            if (!TryGetSelection(player, out int index)) return false;
            return TryGetPrefab(index, out prefab);
        }

        public GameObject GetDefaultPrefab()
        {
            return TryGetPrefab(m_SelectedIndex, out GameObject prefab)
                ? prefab
                : FirstValidPrefab();
        }

        public bool HasSelection(PlayerID player)
        {
            return TryGetSelection(player, out _);
        }

        private void TryHookNetworkManager()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null) return;

            if (m_HookedManager == manager)
            {
                if (manager.isServer) TrySubscribeServer(manager);
                return;
            }

            UnhookNetworkManager();
            m_HookedManager = manager;
            m_HookedManager.onNetworkStarted += OnNetworkStarted;
            m_HookedManager.onNetworkShutdown += OnNetworkShutdown;

            TryReadFromTransport(manager);
            if (manager.isServer) TrySubscribeServer(manager);
        }

        private void UnhookNetworkManager()
        {
            if (m_HookedManager == null) return;

            UnsubscribeServer();
            m_HookedManager.onNetworkStarted -= OnNetworkStarted;
            m_HookedManager.onNetworkShutdown -= OnNetworkShutdown;
            m_HookedManager = null;
        }

        private void OnNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer) TrySubscribeServer(manager);
            SchedulePublishSelection();
            UpdateVisualState();
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer) UnsubscribeServer();
            if (!manager.isServer && !manager.isClient) m_ServerSelections.Clear();
            UpdateVisualState();
        }

        private void TrySubscribeServer(NetworkManager manager)
        {
            if (m_SubscribedServer || manager == null || !manager.isServer) return;
            manager.Subscribe<PurrNetDemoCharacterSelectionPacket>(OnSelectionPacketServer, true);
            m_SubscribedServer = true;
        }

        private void UnsubscribeServer()
        {
            if (!m_SubscribedServer || m_HookedManager == null) return;
            m_HookedManager.Unsubscribe<PurrNetDemoCharacterSelectionPacket>(OnSelectionPacketServer, true);
            m_SubscribedServer = false;
        }

        private void OnSelectionPacketServer(
            PlayerID player,
            PurrNetDemoCharacterSelectionPacket packet,
            bool asServer)
        {
            if (!asServer) return;
            StoreSelection(player, packet.characterIndex);
        }

        private void SchedulePublishSelection()
        {
            if (!isActiveAndEnabled || m_PublishRoutine != null) return;
            m_PublishRoutine = StartCoroutine(PublishSelectionRoutine());
        }

        private void StopPublishRoutine()
        {
            if (m_PublishRoutine == null) return;
            StopCoroutine(m_PublishRoutine);
            m_PublishRoutine = null;
        }

        private IEnumerator PublishSelectionRoutine()
        {
            float deadline = Time.unscaledTime + Mathf.Max(0.25f, m_PublishSelectionTimeout);

            while (Time.unscaledTime < deadline)
            {
                TryHookNetworkManager();
                if (TryPublishLocalSelection())
                {
                    m_PublishRoutine = null;
                    yield break;
                }

                yield return null;
            }

            m_PublishRoutine = null;
        }

        private bool TryPublishLocalSelection()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isLocalPlayerReady) return false;

            var packet = new PurrNetDemoCharacterSelectionPacket
            {
                characterIndex = SelectedIndex
            };

            if (manager.isServer)
            {
                StoreSelection(manager.localPlayer, packet.characterIndex);
            }

            if (!manager.isClient || manager.isServer) return true;

            try
            {
                manager.SendToServer(packet, Channel.ReliableOrdered);
                return true;
            }
            catch (Exception e)
            {
                SetStatus($"Selection sync pending: {e.Message}");
                return false;
            }
        }

        private void StoreSelection(PlayerID player, int index)
        {
            m_ServerSelections[player] = ClampIndex(index);
        }

        private bool TryGetSelection(PlayerID player, out int index)
        {
            if (m_ServerSelections.TryGetValue(player, out index))
            {
                index = ClampIndex(index);
                return true;
            }

            NetworkManager manager = ActiveManager;
            if (manager != null &&
                manager.isHost &&
                manager.isLocalPlayerReady &&
                player == manager.localPlayer)
            {
                index = SelectedIndex;
                return true;
            }

            index = 0;
            return false;
        }

        private bool TryGetPrefab(int index, out GameObject prefab)
        {
            prefab = null;
            if (m_PlayerPrefabs == null || m_PlayerPrefabs.Count == 0) return false;

            index = Mathf.Clamp(index, 0, m_PlayerPrefabs.Count - 1);
            prefab = m_PlayerPrefabs[index];
            return prefab != null;
        }

        private GameObject FirstValidPrefab()
        {
            if (m_PlayerPrefabs == null) return null;

            for (int i = 0; i < m_PlayerPrefabs.Count; i++)
            {
                if (m_PlayerPrefabs[i] != null) return m_PlayerPrefabs[i];
            }

            return null;
        }

        private int ClampIndex(int index)
        {
            int max = m_PlayerPrefabs != null && m_PlayerPrefabs.Count > 0
                ? m_PlayerPrefabs.Count - 1
                : 0;

            return Mathf.Clamp(index, 0, max);
        }

        private string GetCharacterName(int index)
        {
            if (m_DisplayNames != null &&
                index >= 0 &&
                index < m_DisplayNames.Length &&
                !string.IsNullOrEmpty(m_DisplayNames[index]))
            {
                return m_DisplayNames[index];
            }

            return TryGetPrefab(index, out GameObject prefab) && prefab != null
                ? prefab.name
                : $"Character {index + 1}";
        }

        private bool CanStartSession(NetworkManager manager, string action)
        {
            if (manager == null)
            {
                SetStatus("No NetworkManager found.");
                return false;
            }

            if (manager.serverState == ConnectionState.Disconnected &&
                manager.clientState == ConnectionState.Disconnected)
            {
                return true;
            }

            SetStatus($"Cannot {action}: Server {manager.serverState}, Client {manager.clientState}.");
            return false;
        }

        private bool CanBindHostPort()
        {
            ushort port = ReadPort();
            if (IsUdpPortAvailable(port)) return true;

            SetStatus($"Port {port} is already in use. Join an existing host or choose a free port.");
            return false;
        }

        private void InitializeInputs()
        {
            if (m_AddressInput != null && string.IsNullOrEmpty(m_AddressInput.text))
            {
                m_AddressInput.text = m_DefaultAddress;
            }

            if (m_PortInput != null && string.IsNullOrEmpty(m_PortInput.text))
            {
                m_PortInput.text = m_DefaultPort.ToString();
            }
        }

        private string ReadAddress()
        {
            string address = m_AddressInput != null ? m_AddressInput.text : m_DefaultAddress;
            return string.IsNullOrWhiteSpace(address) ? m_DefaultAddress : address;
        }

        private ushort ReadPort()
        {
            string text = m_PortInput != null ? m_PortInput.text : null;
            return ushort.TryParse(text, out ushort port) ? port : m_DefaultPort;
        }

        private void TryReadFromTransport(NetworkManager manager)
        {
            GenericTransport transport = manager != null ? manager.transport : null;
            if (transport == null) return;

            Type type = transport.GetType();
            PropertyInfo addressProp = type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo portProp = type.GetProperty("serverPort", BindingFlags.Public | BindingFlags.Instance);

            if (addressProp != null && addressProp.CanRead)
            {
                string address = addressProp.GetValue(transport) as string;
                if (!string.IsNullOrEmpty(address) && m_AddressInput != null) m_AddressInput.text = address;
            }

            if (portProp != null && portProp.CanRead)
            {
                object value = portProp.GetValue(transport);
                if (value is ushort port && m_PortInput != null) m_PortInput.text = port.ToString();
            }
        }

        private void ApplyAddressPortToTransport(NetworkManager manager)
        {
            GenericTransport transport = manager != null ? manager.transport : null;
            if (transport == null) return;

            Type type = transport.GetType();
            PropertyInfo addressProp = type.GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
            PropertyInfo portProp = type.GetProperty("serverPort", BindingFlags.Public | BindingFlags.Instance);

            if (addressProp != null && addressProp.CanWrite)
            {
                addressProp.SetValue(transport, ReadAddress());
            }

            if (portProp != null && portProp.CanWrite)
            {
                portProp.SetValue(transport, ReadPort());
            }
        }

        private void UpdateVisualState()
        {
            NetworkManager manager = ActiveManager;
            bool connected = manager != null &&
                             (manager.serverState != ConnectionState.Disconnected ||
                              manager.clientState != ConnectionState.Disconnected);

            if (m_SelectionRoot != null) m_SelectionRoot.SetActive(!connected);
            if (m_InGameRoot != null) m_InGameRoot.SetActive(connected);

            if (m_SelectedText != null)
            {
                m_SelectedText.text = $"Selected: {GetCharacterName(m_SelectedIndex)}";
            }

            if (m_SelectionHighlights != null)
            {
                for (int i = 0; i < m_SelectionHighlights.Length; i++)
                {
                    Graphic graphic = m_SelectionHighlights[i];
                    if (graphic == null) continue;
                    graphic.color = i == SelectedIndex
                        ? new Color(0.22f, 0.58f, 1f, 0.92f)
                        : new Color(0.08f, 0.09f, 0.10f, 0.82f);
                }
            }

            string status;
            if (manager == null)
            {
                status = $"{m_Status}\nNo NetworkManager found.";
                SetStatusText(status);
                return;
            }

            string role = manager.isHost ? "Host" : manager.isServer ? "Server" : manager.isClient ? "Client" : "Offline";
            status = $"{m_Status}\nRole: {role} | Server: {manager.serverState} | Client: {manager.clientState}";
            SetStatusText(status);
        }

        private void SetStatusText(string value)
        {
            if (m_StatusText != null) m_StatusText.text = value;

            if (m_StatusTexts == null) return;
            for (int i = 0; i < m_StatusTexts.Length; i++)
            {
                if (m_StatusTexts[i] != null) m_StatusTexts[i].text = value;
            }
        }

        private void SetStatus(string status)
        {
            m_Status = status;
            UpdateVisualState();
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
                    if (family == AddressFamily.InterNetworkV6) socket.DualMode = dualMode;
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
    }
}
