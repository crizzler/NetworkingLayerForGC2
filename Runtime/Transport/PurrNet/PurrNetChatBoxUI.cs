using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct GC2PurrNetChatRequestPacket : IPackedAuto
    {
        public string senderName;
        public string message;
        public uint sequence;
    }

    public struct GC2PurrNetChatBroadcastPacket : IPackedAuto
    {
        public ulong senderPlayerId;
        public string senderName;
        public string message;
        public uint sequence;
        public float serverTime;
    }

    /// <summary>
    /// Simple PurrNet-backed UGUI chat box for GC2 demo and prototype scenes.
    /// It uses PurrNet broadcast packets, so the same component works with UDP,
    /// SteamTransport, WebTransport, LocalTransport, or any other PurrNet transport.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Chat Box UI")]
    [DefaultExecutionOrder(-280)]
    public sealed class PurrNetChatBoxUI : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Header("Defaults")]
        [SerializeField] private string m_Title = "Chat";
        [SerializeField] private string m_DefaultDisplayName = "Player";
        [SerializeField] private int m_MaxVisibleMessages = 64;
        [SerializeField] private int m_MaxMessageLength = 160;
        [SerializeField, Min(0f)] private float m_MinSendInterval = 0.25f;
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;
        [SerializeField] private int m_SortingOrder = 980;

        [Header("Scene UI")]
        [Tooltip("When enabled, missing UI references are generated at runtime. Disable this if this component must only use designer-authored scene UI.")]
        [SerializeField] private bool m_CreateMissingUI = true;
        [SerializeField] private Canvas m_Canvas;
        [SerializeField] private GameObject m_Panel;
        [SerializeField] private Text m_HeaderText;
        [SerializeField] private Text m_StatusText;
        [SerializeField] private Text m_MessageText;
        [SerializeField] private RectTransform m_MessageContent;
        [SerializeField] private ScrollRect m_ScrollRect;
        [SerializeField] private InputField m_NameField;
        [SerializeField] private InputField m_MessageField;
        [SerializeField] private Button m_SendButton;

        [Header("Diagnostics")]
        [SerializeField] private bool m_DebugLog = true;

        private static readonly Color BG_PANEL = new Color(0.06f, 0.07f, 0.09f, 0.88f);
        private static readonly Color BG_HEADER = new Color(0.10f, 0.11f, 0.14f, 0.96f);
        private static readonly Color BG_FIELD = new Color(0.14f, 0.15f, 0.19f, 0.98f);
        private static readonly Color BG_BUTTON = new Color(0.26f, 0.46f, 0.82f, 1.00f);
        private static readonly Color TXT_MAIN = new Color(0.93f, 0.95f, 0.98f, 1.00f);
        private static readonly Color TXT_DIM = new Color(0.66f, 0.69f, 0.76f, 1.00f);
        private static readonly Color TXT_SYSTEM = new Color(0.72f, 0.78f, 0.86f, 1.00f);

        private readonly List<string> m_Lines = new();
        private readonly HashSet<string> m_SeenMessages = new();
        private readonly Dictionary<PlayerID, float> m_LastServerMessageTime = new();

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private uint m_LocalSequence;
        private float m_LastLocalSendTime = -100f;
        private float m_LastRefreshTime = -100f;
        private bool m_HasLastInteractableState;
        private bool m_LastConnectedState;
        private string m_LastRoleStatus;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            LogDebug($"Awake. createMissingUI={m_CreateMissingUI}. manager={DescribeManager(m_NetworkManager)}");

            ResolveUIReferences();
            LogUIState("After ResolveUIReferences");

            if (!HasRequiredUI() && m_CreateMissingUI)
            {
                LogDebug("Required UI references are missing. Building fallback chat UI at runtime.");
                BuildUI();
                ResolveUIReferences();
                LogUIState("After fallback BuildUI");
            }
            else if (!HasRequiredUI())
            {
                LogDebug("Required UI references are missing and runtime UI creation is disabled.");
            }

            ConfigureUI();
            LogUIState("After ConfigureUI");
            EnsureEventSystem();
            AppendSystem("Chat ready.");
        }

        private void OnEnable()
        {
            LogDebug("OnEnable.");
            TryHookNetworkManager();
            RefreshInteractable();
        }

        private void Start()
        {
            LogDebug("Start.");
            TryHookNetworkManager();
            RefreshInteractable();
        }

        private void OnDisable()
        {
            LogDebug("OnDisable.");
            UnhookNetworkManager();
        }

        private void Update()
        {
            TryHookNetworkManager();

            if (Time.unscaledTime - m_LastRefreshTime > 0.25f)
            {
                RefreshInteractable();
                m_LastRefreshTime = Time.unscaledTime;
            }
        }

        public void SendCurrentMessage()
        {
            NetworkManager manager = ActiveManager;
            string rawMessage = m_MessageField != null ? m_MessageField.text : string.Empty;
            string rawName = m_NameField != null ? m_NameField.text : m_DefaultDisplayName;

            LogDebug(
                $"SendCurrentMessage invoked. manager={DescribeManager(manager)}, " +
                $"rawName='{rawName}', rawMessageLength={(rawMessage != null ? rawMessage.Length : 0)}, " +
                $"messageField={(m_MessageField != null ? m_MessageField.name : "null")}, " +
                $"sendButton={(m_SendButton != null ? m_SendButton.name : "null")}, " +
                $"sendButtonInteractable={(m_SendButton != null && m_SendButton.interactable)}");

            if (manager == null || (!manager.isClient && !manager.isServer))
            {
                LogDebug("Send aborted: no connected NetworkManager.");
                AppendSystem("Start or join a session before sending chat.");
                return;
            }

            string message = CleanMessage(rawMessage);
            if (string.IsNullOrEmpty(message))
            {
                LogDebug("Send aborted: cleaned message is empty.");
                return;
            }

            float now = Time.unscaledTime;
            if (now - m_LastLocalSendTime < m_MinSendInterval)
            {
                LogDebug($"Send aborted: local cooldown active. elapsed={now - m_LastLocalSendTime:0.000}, min={m_MinSendInterval:0.000}");
                AppendSystem("Please wait before sending another message.");
                return;
            }

            m_LastLocalSendTime = now;
            m_LocalSequence++;

            var request = new GC2PurrNetChatRequestPacket
            {
                senderName = CleanName(rawName),
                message = message,
                sequence = m_LocalSequence
            };

            LogDebug($"Prepared chat request. sequence={request.sequence}, senderName='{request.senderName}', messageLength={request.message.Length}, channel={m_Channel}");

            if (m_MessageField != null)
            {
                m_MessageField.text = string.Empty;
                m_MessageField.ActivateInputField();
            }

            if (manager.isServer)
            {
                PlayerID sender = manager.isLocalPlayerReady ? manager.localPlayer : default;
                LogDebug($"Local manager is server. Handling request directly. sender={DescribePlayer(sender)}");
                HandleChatRequestServer(sender, request, true);
                return;
            }

            try
            {
                LogDebug("Sending chat request to server.");
                manager.SendToServer(request, m_Channel);
                LogDebug("SendToServer completed without exception.");
            }
            catch (Exception e)
            {
                AppendSystem($"Chat send failed: {e.Message}");
                Debug.LogException(e, this);
            }
        }

        private void TryHookNetworkManager()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null) return;

            if (m_HookedManager == manager)
            {
                if (manager.isServer) SubscribeServer(manager);
                if (manager.isClient) SubscribeClient(manager);
                return;
            }

            UnhookNetworkManager();
            m_HookedManager = manager;
            m_HookedManager.onNetworkStarted += OnNetworkStarted;
            m_HookedManager.onNetworkShutdown += OnNetworkShutdown;
            LogDebug($"Hooked NetworkManager. {DescribeManager(manager)}");

            if (manager.isServer) SubscribeServer(manager);
            if (manager.isClient) SubscribeClient(manager);
        }

        private void UnhookNetworkManager()
        {
            if (m_HookedManager == null) return;

            UnsubscribeServer();
            UnsubscribeClient();
            m_HookedManager.onNetworkStarted -= OnNetworkStarted;
            m_HookedManager.onNetworkShutdown -= OnNetworkShutdown;
            LogDebug($"Unhooked NetworkManager. {DescribeManager(m_HookedManager)}");
            m_HookedManager = null;
        }

        private void OnNetworkStarted(NetworkManager manager, bool asServer)
        {
            LogDebug($"OnNetworkStarted(asServer={asServer}). {DescribeManager(manager)}");
            if (asServer) SubscribeServer(manager);
            else SubscribeClient(manager);

            AppendSystem(asServer ? "Chat server online." : "Chat connected.");
            RefreshInteractable();
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            LogDebug($"OnNetworkShutdown(asServer={asServer}). {DescribeManager(manager)}");
            if (asServer) UnsubscribeServer();
            else UnsubscribeClient();

            if (!manager.isServer && !manager.isClient)
            {
                m_LastServerMessageTime.Clear();
                AppendSystem("Chat disconnected.");
            }

            RefreshInteractable();
        }

        private void SubscribeServer(NetworkManager manager)
        {
            if (m_SubscribedServer || manager == null || !manager.isServer) return;
            manager.Subscribe<GC2PurrNetChatRequestPacket>(HandleChatRequestServer, true);
            m_SubscribedServer = true;
            LogDebug($"Subscribed server chat request handler. {DescribeManager(manager)}");
        }

        private void SubscribeClient(NetworkManager manager)
        {
            if (m_SubscribedClient || manager == null || !manager.isClient) return;
            manager.Subscribe<GC2PurrNetChatBroadcastPacket>(HandleChatBroadcastClient, false);
            m_SubscribedClient = true;
            LogDebug($"Subscribed client chat broadcast handler. {DescribeManager(manager)}");
        }

        private void UnsubscribeServer()
        {
            if (!m_SubscribedServer || m_HookedManager == null) return;
            m_HookedManager.Unsubscribe<GC2PurrNetChatRequestPacket>(HandleChatRequestServer, true);
            m_SubscribedServer = false;
            LogDebug("Unsubscribed server chat request handler.");
        }

        private void UnsubscribeClient()
        {
            if (!m_SubscribedClient || m_HookedManager == null) return;
            m_HookedManager.Unsubscribe<GC2PurrNetChatBroadcastPacket>(HandleChatBroadcastClient, false);
            m_SubscribedClient = false;
            LogDebug("Unsubscribed client chat broadcast handler.");
        }

        private void HandleChatRequestServer(
            PlayerID sender,
            GC2PurrNetChatRequestPacket request,
            bool asServer)
        {
            LogDebug(
                $"HandleChatRequestServer invoked. asServer={asServer}, sender={DescribePlayer(sender)}, " +
                $"sequence={request.sequence}, senderName='{request.senderName}', messageLength={(request.message != null ? request.message.Length : 0)}");

            if (!asServer)
            {
                LogDebug("Server request ignored: callback was not marked as server.");
                return;
            }

            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer)
            {
                LogDebug($"Server request ignored: manager is not server. manager={DescribeManager(manager)}");
                return;
            }

            float now = Time.unscaledTime;
            if (m_LastServerMessageTime.TryGetValue(sender, out float lastTime) &&
                now - lastTime < m_MinSendInterval)
            {
                LogDebug($"Server request ignored: sender cooldown active. elapsed={now - lastTime:0.000}, min={m_MinSendInterval:0.000}");
                return;
            }

            string message = CleanMessage(request.message);
            if (string.IsNullOrEmpty(message))
            {
                LogDebug("Server request ignored: cleaned message is empty.");
                return;
            }

            m_LastServerMessageTime[sender] = now;

            var broadcast = new GC2PurrNetChatBroadcastPacket
            {
                senderPlayerId = sender.id,
                senderName = CleanName(request.senderName),
                message = message,
                sequence = request.sequence,
                serverTime = manager.tickModule != null
                    ? (float)manager.tickModule.PreciseTickToTime(manager.tickModule.syncedPreciseTick)
                    : Time.time
            };

            LogDebug(
                $"Broadcasting chat. senderPlayerId={broadcast.senderPlayerId}, sequence={broadcast.sequence}, " +
                $"senderName='{broadcast.senderName}', messageLength={broadcast.message.Length}, channel={m_Channel}");

            DisplayBroadcast(broadcast);

            try
            {
                manager.SendToAll(broadcast, m_Channel);
                LogDebug("SendToAll completed without exception.");
            }
            catch (Exception e)
            {
                LogDebug($"SendToAll failed: {e.GetType().Name}: {e.Message}");
                Debug.LogException(e, this);
            }
        }

        private void HandleChatBroadcastClient(
            PlayerID sender,
            GC2PurrNetChatBroadcastPacket broadcast,
            bool asServer)
        {
            LogDebug(
                $"HandleChatBroadcastClient invoked. asServer={asServer}, sender={DescribePlayer(sender)}, " +
                $"broadcastSender={broadcast.senderPlayerId}, sequence={broadcast.sequence}, " +
                $"senderName='{broadcast.senderName}', messageLength={(broadcast.message != null ? broadcast.message.Length : 0)}");

            if (asServer)
            {
                LogDebug("Client broadcast ignored: callback was marked as server.");
                return;
            }

            DisplayBroadcast(broadcast);
        }

        private void DisplayBroadcast(GC2PurrNetChatBroadcastPacket broadcast)
        {
            string key = $"{broadcast.senderPlayerId}:{broadcast.sequence}";
            if (!m_SeenMessages.Add(key))
            {
                LogDebug($"DisplayBroadcast ignored duplicate message. key={key}");
                return;
            }

            string sender = CleanName(broadcast.senderName);
            string message = CleanMessage(broadcast.message);
            if (string.IsNullOrEmpty(message))
            {
                LogDebug($"DisplayBroadcast ignored empty message. key={key}");
                return;
            }

            LogDebug($"Displaying chat message. key={key}, sender='{sender}', messageLength={message.Length}");
            AppendLine($"{EscapeRichText(sender)}: {EscapeRichText(message)}");
        }

        private void AppendSystem(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            AppendLine($"[{EscapeRichText(message.Trim())}]", TXT_SYSTEM);
        }

        private void AppendLine(string line)
        {
            AppendLine(line, TXT_MAIN);
        }

        private void AppendLine(string line, Color color)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            string colorHex = ColorUtility.ToHtmlStringRGB(color);
            m_Lines.Add($"<color=#{colorHex}>{line}</color>");
            while (m_Lines.Count > Mathf.Max(1, m_MaxVisibleMessages))
            {
                m_Lines.RemoveAt(0);
            }

            RefreshMessages();
        }

        private void RefreshMessages()
        {
            if (m_MessageText == null)
            {
                LogDebug("RefreshMessages aborted: m_MessageText is null.");
                return;
            }

            m_MessageText.text = string.Join("\n", m_Lines);
            LogDebug($"RefreshMessages applied. lineCount={m_Lines.Count}, textLength={m_MessageText.text.Length}, messageText='{m_MessageText.name}'");
            Canvas.ForceUpdateCanvases();

            if (m_MessageContent != null)
            {
                float viewportHeight = 132f;
                if (m_ScrollRect != null && m_ScrollRect.viewport != null)
                {
                    viewportHeight = Mathf.Max(viewportHeight, m_ScrollRect.viewport.rect.height);
                }

                float height = Mathf.Max(viewportHeight, m_MessageText.preferredHeight + 12f);
                m_MessageContent.sizeDelta = new Vector2(m_MessageContent.sizeDelta.x, height);
                m_MessageContent.anchoredPosition = Vector2.zero;
            }

            if (m_ScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                m_ScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private void RefreshInteractable()
        {
            NetworkManager manager = ActiveManager;
            bool connected = manager != null && (manager.isClient || manager.isServer);

            if (m_MessageField != null) m_MessageField.interactable = connected;
            if (m_SendButton != null) m_SendButton.interactable = connected;

            if (m_StatusText != null)
            {
                string status = "Offline";
                if (manager != null)
                {
                    status = manager.isHost ? "Host"
                        : manager.isServer ? "Server"
                        : manager.isClient ? "Client"
                        : "Offline";
                }

                m_StatusText.text = status;

                if (!m_HasLastInteractableState ||
                    m_LastConnectedState != connected ||
                    !string.Equals(m_LastRoleStatus, status, StringComparison.Ordinal))
                {
                    LogDebug(
                        $"Interactable state changed. connected={connected}, status={status}, " +
                        $"messageFieldInteractable={(m_MessageField != null && m_MessageField.interactable)}, " +
                        $"sendButtonInteractable={(m_SendButton != null && m_SendButton.interactable)}");
                    m_HasLastInteractableState = true;
                    m_LastConnectedState = connected;
                    m_LastRoleStatus = status;
                }
            }
        }

        private string CleanMessage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            string cleaned = value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            int max = Mathf.Max(1, m_MaxMessageLength);
            return cleaned.Length <= max ? cleaned : cleaned.Substring(0, max);
        }

        private string CleanName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) value = m_DefaultDisplayName;

            string cleaned = value
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            if (string.IsNullOrEmpty(cleaned)) cleaned = "Player";
            return cleaned.Length <= 24 ? cleaned : cleaned.Substring(0, 24);
        }

        private static string EscapeRichText(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        private bool HasRequiredUI()
        {
            return m_MessageText != null &&
                   m_MessageField != null &&
                   m_SendButton != null;
        }

        private void ResolveUIReferences()
        {
            if (m_Canvas == null) m_Canvas = GetComponent<Canvas>();
            if (m_Panel == null) m_Panel = transform.Find("Chat Panel")?.gameObject;
            if (m_ScrollRect == null) m_ScrollRect = GetComponentInChildren<ScrollRect>(true);
            if (m_MessageContent == null && m_ScrollRect != null) m_MessageContent = m_ScrollRect.content;

            if (m_HeaderText == null) m_HeaderText = FindText("Title");
            if (m_StatusText == null) m_StatusText = FindText("Status");
            if (m_MessageText == null)
            {
                Transform messageTransform = transform.Find("Chat Panel/Messages/Viewport/Content/Text");
                m_MessageText = messageTransform != null ? messageTransform.GetComponent<Text>() : null;
            }

            InputField[] fields = GetComponentsInChildren<InputField>(true);
            for (int i = 0; i < fields.Length; i++)
            {
                InputField field = fields[i];
                if (field == null) continue;

                if (m_NameField == null &&
                    field.name.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    m_NameField = field;
                }

                if (m_MessageField == null &&
                    field.name.IndexOf("Message", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    m_MessageField = field;
                }
            }

            if (m_SendButton == null)
            {
                Button[] buttons = GetComponentsInChildren<Button>(true);
                for (int i = 0; i < buttons.Length; i++)
                {
                    Button button = buttons[i];
                    if (button != null &&
                        button.name.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        m_SendButton = button;
                        break;
                    }
                }
            }
        }

        private Text FindText(string textName)
        {
            Text[] texts = GetComponentsInChildren<Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                Text text = texts[i];
                if (text != null && text.name == textName) return text;
            }

            return null;
        }

        private void ConfigureUI()
        {
            if (m_Canvas != null)
            {
                m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                m_Canvas.sortingOrder = m_SortingOrder;
            }

            if (m_HeaderText != null) m_HeaderText.text = m_Title;

            if (m_NameField != null)
            {
                m_NameField.characterLimit = 24;
                if (string.IsNullOrWhiteSpace(m_NameField.text))
                {
                    m_NameField.text = m_DefaultDisplayName;
                }
            }

            if (m_MessageField != null)
            {
                m_MessageField.characterLimit = Mathf.Max(1, m_MaxMessageLength);
                m_MessageField.lineType = InputField.LineType.SingleLine;
            }

            ConfigureMessageViewport();

            if (m_SendButton != null)
            {
                bool hasPersistentListener = HasPersistentSendListener(m_SendButton);
                m_SendButton.onClick.RemoveListener(SendCurrentMessage);
                m_SendButton.onClick.AddListener(SendCurrentMessage);
                LogDebug(
                    $"Configured Send button runtime listener. persistentListenerToThis={hasPersistentListener}, " +
                    $"persistentEventCount={m_SendButton.onClick.GetPersistentEventCount()}");
            }
        }

        private bool HasPersistentSendListener(Button button)
        {
            if (button == null) return false;

            int count = button.onClick.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (button.onClick.GetPersistentTarget(i) == this &&
                    button.onClick.GetPersistentMethodName(i) == nameof(SendCurrentMessage))
                {
                    return true;
                }
            }

            return false;
        }

        private void LogUIState(string context)
        {
            LogDebug(
                $"{context}. hasRequiredUI={HasRequiredUI()}, " +
                $"canvas={(m_Canvas != null ? m_Canvas.name : "null")}, " +
                $"panel={(m_Panel != null ? m_Panel.name : "null")}, " +
                $"headerText={(m_HeaderText != null ? m_HeaderText.name : "null")}, " +
                $"statusText={(m_StatusText != null ? m_StatusText.name : "null")}, " +
                $"messageText={(m_MessageText != null ? m_MessageText.name : "null")}, " +
                $"messageContent={(m_MessageContent != null ? m_MessageContent.name : "null")}, " +
                $"scrollRect={(m_ScrollRect != null ? m_ScrollRect.name : "null")}, " +
                $"nameField={(m_NameField != null ? m_NameField.name : "null")}, " +
                $"messageField={(m_MessageField != null ? m_MessageField.name : "null")}, " +
                $"sendButton={(m_SendButton != null ? m_SendButton.name : "null")}");
        }

        private void LogDebug(string message)
        {
            if (!m_DebugLog) return;
            Debug.Log($"[PurrNetChatBoxUI:{name}:{GetInstanceID()}] {message}", this);
        }

        private static string DescribeManager(NetworkManager manager)
        {
            if (manager == null) return "null";

            string transport = manager.transport != null ? manager.transport.GetType().Name : "null";
            string localPlayer = manager.isLocalPlayerReady ? manager.localPlayer.id.ToString() : "not-ready";
            return
                $"name='{manager.name}', isHost={manager.isHost}, isServer={manager.isServer}, isClient={manager.isClient}, " +
                $"serverState={manager.serverState}, clientState={manager.clientState}, localPlayer={localPlayer}, transport={transport}";
        }

        private static string DescribePlayer(PlayerID player)
        {
            return $"id={player.id}";
        }

        private void BuildUI()
        {
            m_Canvas = gameObject.GetComponent<Canvas>();
            if (m_Canvas == null) m_Canvas = gameObject.AddComponent<Canvas>();
            m_Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_Canvas.sortingOrder = m_SortingOrder;

            var scaler = gameObject.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (gameObject.GetComponent<GraphicRaycaster>() == null)
            {
                gameObject.AddComponent<GraphicRaycaster>();
            }

            m_Panel = NewUI(
                "Chat Panel",
                transform,
                BG_PANEL,
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(24f, 24f),
                new Vector2(430f, 260f));

            GameObject header = NewUI(
                "Header",
                m_Panel.transform,
                BG_HEADER,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                Vector2.zero,
                new Vector2(0f, 36f));

            m_HeaderText = NewText(
                "Title",
                header.transform,
                m_Title,
                15,
                FontStyle.Bold,
                TXT_MAIN,
                Vector2.zero,
                Vector2.one,
                new Vector2(14f, 0f),
                new Vector2(-104f, 0f),
                TextAnchor.MiddleLeft);

            m_StatusText = NewText(
                "Status",
                header.transform,
                "Offline",
                11,
                FontStyle.Bold,
                TXT_DIM,
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(-96f, 0f),
                new Vector2(-14f, 0f),
                TextAnchor.MiddleRight);

            m_ScrollRect = NewScrollArea(m_Panel.transform);

            m_NameField = NewInput(
                "Name Field",
                m_Panel.transform,
                BG_FIELD,
                m_DefaultDisplayName,
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(12f, 12f),
                new Vector2(132f, 42f));
            m_NameField.characterLimit = 24;

            m_MessageField = NewInput(
                "Message Field",
                m_Panel.transform,
                BG_FIELD,
                "Type message",
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(142f, 12f),
                new Vector2(-82f, 42f));
            m_MessageField.characterLimit = Mathf.Max(1, m_MaxMessageLength);
            m_MessageField.lineType = InputField.LineType.SingleLine;

            m_SendButton = NewButton(
                "Send Button",
                m_Panel.transform,
                BG_BUTTON,
                "Send",
                new Vector2(1f, 0f),
                new Vector2(1f, 0f),
                new Vector2(-72f, 12f),
                new Vector2(-12f, 42f));

            if (m_HeaderText != null) m_HeaderText.text = m_Title;
        }

        private ScrollRect NewScrollArea(Transform parent)
        {
            GameObject root = NewUI(
                "Messages",
                parent,
                new Color(0.03f, 0.035f, 0.045f, 0.50f),
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);

            RectTransform rootRect = root.GetComponent<RectTransform>();
            rootRect.offsetMin = new Vector2(12f, 52f);
            rootRect.offsetMax = new Vector2(-12f, -46f);

            var scroll = root.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewport = NewUI(
                "Viewport",
                root.transform,
                new Color(0f, 0f, 0f, 0f),
                Vector2.zero,
                Vector2.one,
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                Vector2.zero);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.offsetMin = new Vector2(8f, 6f);
            viewportRect.offsetMax = new Vector2(-8f, -6f);
            viewport.AddComponent<RectMask2D>();

            GameObject content = new GameObject("Content", typeof(RectTransform));
            content.transform.SetParent(viewport.transform, false);
            m_MessageContent = content.GetComponent<RectTransform>();
            m_MessageContent.anchorMin = new Vector2(0f, 1f);
            m_MessageContent.anchorMax = new Vector2(1f, 1f);
            m_MessageContent.pivot = new Vector2(0.5f, 1f);
            m_MessageContent.anchoredPosition = Vector2.zero;
            m_MessageContent.sizeDelta = new Vector2(0f, 132f);

            m_MessageText = NewText(
                "Text",
                content.transform,
                string.Empty,
                13,
                FontStyle.Normal,
                TXT_MAIN,
                Vector2.zero,
                Vector2.one,
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                TextAnchor.UpperLeft);
            m_MessageText.supportRichText = true;
            m_MessageText.horizontalOverflow = HorizontalWrapMode.Wrap;
            m_MessageText.verticalOverflow = VerticalWrapMode.Overflow;

            scroll.viewport = viewportRect;
            scroll.content = m_MessageContent;
            return scroll;
        }

        private static GameObject NewUI(
            string name,
            Transform parent,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPos,
            Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPos;
            rect.sizeDelta = size;

            var image = go.GetComponent<Image>();
            image.color = color;
            return go;
        }

        private static Text NewText(
            string name,
            Transform parent,
            string content,
            int fontSize,
            FontStyle style,
            Color color,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax,
            TextAnchor align)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var text = go.GetComponent<Text>();
            text.text = content;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = align;
            text.font = GetBuiltinRuntimeFont();
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static InputField NewInput(
            string name,
            Transform parent,
            Color bg,
            string placeholderText,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(InputField));
            root.transform.SetParent(parent, false);

            var rect = (RectTransform)root.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var image = root.GetComponent<Image>();
            image.color = bg;

            Text text = NewText(
                "Text",
                root.transform,
                string.Empty,
                13,
                FontStyle.Normal,
                TXT_MAIN,
                Vector2.zero,
                Vector2.one,
                new Vector2(10f, 2f),
                new Vector2(-10f, -2f),
                TextAnchor.MiddleLeft);
            text.supportRichText = false;

            Text placeholder = NewText(
                "Placeholder",
                root.transform,
                placeholderText,
                13,
                FontStyle.Italic,
                TXT_DIM,
                Vector2.zero,
                Vector2.one,
                new Vector2(10f, 2f),
                new Vector2(-10f, -2f),
                TextAnchor.MiddleLeft);

            var input = root.GetComponent<InputField>();
            input.targetGraphic = image;
            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        private static Button NewButton(
            string name,
            Transform parent,
            Color bg,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            root.transform.SetParent(parent, false);

            var rect = (RectTransform)root.transform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;

            var image = root.GetComponent<Image>();
            image.color = bg;

            var button = root.GetComponent<Button>();
            button.targetGraphic = image;

            ColorBlock colors = button.colors;
            colors.normalColor = bg;
            colors.highlightedColor = Color.Lerp(bg, Color.white, 0.12f);
            colors.pressedColor = Color.Lerp(bg, Color.black, 0.16f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(bg.r, bg.g, bg.b, 0.45f);
            colors.fadeDuration = 0.10f;
            button.colors = colors;

            NewText(
                "Label",
                root.transform,
                label,
                13,
                FontStyle.Bold,
                Color.white,
                Vector2.zero,
                Vector2.one,
                Vector2.zero,
                Vector2.zero,
                TextAnchor.MiddleCenter);

            return button;
        }

        private static Font GetBuiltinRuntimeFont()
        {
            Font font = TryGetBuiltinFont("LegacyRuntime.ttf");
            return font != null ? font : TryGetBuiltinFont("Arial.ttf");
        }

        private static Font TryGetBuiltinFont(string fontName)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(fontName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void EnsureEventSystem()
        {
#if UNITY_2023_1_OR_NEWER
            EventSystem existing = UnityEngine.Object.FindFirstObjectByType<EventSystem>();
#else
            EventSystem existing = UnityEngine.Object.FindObjectOfType<EventSystem>();
#endif
            if (existing == null)
            {
                GameObject eventSystem = new GameObject("EventSystem");
                existing = eventSystem.AddComponent<EventSystem>();
            }

            EnsureCompatibleInputModule(existing.gameObject);
        }

        private static void EnsureCompatibleInputModule(GameObject eventSystem)
        {
            if (eventSystem == null) return;

            Type inputSystemModuleType = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

            if (inputSystemModuleType != null)
            {
                if (eventSystem.GetComponent(inputSystemModuleType) == null)
                {
                    eventSystem.AddComponent(inputSystemModuleType);
                }

                StandaloneInputModule[] legacyModules = eventSystem.GetComponents<StandaloneInputModule>();
                for (int i = 0; i < legacyModules.Length; i++)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(legacyModules[i]);
                    else UnityEngine.Object.DestroyImmediate(legacyModules[i]);
                }

                return;
            }

            if (eventSystem.GetComponent<BaseInputModule>() == null)
            {
                eventSystem.AddComponent<StandaloneInputModule>();
            }
        }

        private void ConfigureMessageViewport()
        {
            if (m_MessageText != null)
            {
                m_MessageText.enabled = true;
                m_MessageText.supportRichText = true;
                m_MessageText.alignment = TextAnchor.UpperLeft;
                m_MessageText.horizontalOverflow = HorizontalWrapMode.Wrap;
                m_MessageText.verticalOverflow = VerticalWrapMode.Overflow;
                m_MessageText.raycastTarget = false;

                RectTransform textRect = m_MessageText.rectTransform;
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
            }

            if (m_MessageContent != null)
            {
                m_MessageContent.anchorMin = new Vector2(0f, 1f);
                m_MessageContent.anchorMax = new Vector2(1f, 1f);
                m_MessageContent.pivot = new Vector2(0.5f, 1f);
                m_MessageContent.anchoredPosition = Vector2.zero;
            }

            if (m_ScrollRect == null) return;

            m_ScrollRect.horizontal = false;
            m_ScrollRect.vertical = true;
            m_ScrollRect.movementType = ScrollRect.MovementType.Clamped;
            if (m_MessageContent != null) m_ScrollRect.content = m_MessageContent;

            RectTransform viewport = m_ScrollRect.viewport;
            if (viewport == null && m_MessageContent != null && m_MessageContent.parent is RectTransform parent)
            {
                viewport = parent;
                m_ScrollRect.viewport = viewport;
            }

            if (viewport == null) return;

            Mask stencilMask = viewport.GetComponent<Mask>();
            if (stencilMask != null)
            {
                stencilMask.enabled = false;
            }

            if (viewport.GetComponent<RectMask2D>() == null)
            {
                viewport.gameObject.AddComponent<RectMask2D>();
            }

            Image image = viewport.GetComponent<Image>();
            if (image != null)
            {
                image.raycastTarget = false;
                image.color = new Color(0f, 0f, 0f, 0f);
            }

            LogDebug(
                $"Configured message viewport. viewport={viewport.name}, " +
                $"disabledStencilMask={(stencilMask != null)}, rectMask2D=True");
        }
    }
}
