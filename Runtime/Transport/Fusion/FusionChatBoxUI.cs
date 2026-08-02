using System;
using System.Collections.Generic;
using System.Text;
using Fusion;
using UnityEngine;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Small authority-relayed demo chat. Requests carry display text only; the validated
    /// RPC source supplies the sender ID. The authority applies length, character and rate
    /// limits before broadcasting.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Chat Box UI")]
    [DefaultExecutionOrder(-280)]
    [DisallowMultipleComponent]
    public sealed class FusionChatBoxUI : MonoBehaviour, IFusionFullSnapshotProducer
    {
        private const ushort ChatRequestMessage = 1;
        private const ushort ChatBroadcastMessage = 2;
        private const int MaximumRequestBytes = 1024;

        [Header("References")]
        [SerializeField] private FusionSessionBootstrap m_SessionBootstrap;
        [SerializeField] private FusionTransportBridge m_TransportBridge;

        [Header("Limits")]
        [SerializeField] private string m_DefaultDisplayName = "Player";
        [Min(1)]
        [SerializeField] private int m_MaxDisplayNameLength = 24;
        [Min(1)]
        [SerializeField] private int m_MaxMessageLength = 160;
        [Min(1)]
        [SerializeField] private int m_MaxVisibleMessages = 64;
        [Min(0f)]
        [SerializeField] private float m_MinSendInterval = 0.25f;

        [Header("Optional Designer UI")]
        [Tooltip("Optional authored uGUI references. Field names match the PurrNet demo " +
                 "component so its chat canvas and persistent Send callback migrate intact.")]
        [SerializeField] private Canvas m_Canvas;
        [SerializeField] private GameObject m_Panel;
        [SerializeField] private Text m_StatusText;
        [SerializeField] private Text m_MessageText;
        [SerializeField] private RectTransform m_MessageContent;
        [SerializeField] private ScrollRect m_ScrollRect;
        [SerializeField] private InputField m_NameField;
        [SerializeField] private InputField m_MessageField;
        [SerializeField] private Button m_SendButton;

        [Header("Zero-Setup Overlay")]
        [SerializeField] private bool m_ShowOverlay = true;
        [SerializeField] private Vector2 m_OverlayPosition = new Vector2(16f, 270f);
        [Min(280f)]
        [SerializeField] private float m_OverlayWidth = 420f;
        [Min(160f)]
        [SerializeField] private float m_OverlayHeight = 250f;

        private readonly List<string> m_Lines = new List<string>();
        private readonly Dictionary<uint, float> m_LastAuthorityMessageTimes =
            new Dictionary<uint, float>();

        private FusionTransportBridge m_BoundBridge;
        private string m_DisplayName;
        private string m_DraftMessage = string.Empty;
        private string m_Status = "Offline";
        private Vector2 m_Scroll;
        private float m_LastLocalSendTime = -100f;
        private float m_NextBindAttempt;

        public ushort FullSnapshotModuleId => FusionProtocol.DemoChatModuleId;
        public string FullSnapshotProducerName => "Demo Chat";

        public FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context)
        {
            if (context == null || context.TransportBridge != m_BoundBridge ||
                context.ModuleId != FusionProtocol.DemoChatModuleId ||
                m_BoundBridge == null || !m_BoundBridge.IsServer)
            {
                return context != null
                    ? context.Fail("Demo Chat is not bound as the current authority.")
                    : default;
            }

            // Demo chat intentionally has no authoritative history to replay.
            return context.Complete();
        }

        private void Awake()
        {
            m_DisplayName = string.IsNullOrWhiteSpace(m_DefaultDisplayName)
                ? "Player"
                : m_DefaultDisplayName;
            if (m_NameField != null && !string.IsNullOrWhiteSpace(m_NameField.text))
            {
                m_DisplayName = m_NameField.text;
            }
            ResolveDependencies();
            TryBindTransport(true);
            AppendSystem("Chat ready.");
            RefreshDesignerUI();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            TryBindTransport(true);
        }

        private void Update()
        {
            ResolveDependencies();
            if (ResolveTransport() != m_BoundBridge)
            {
                TryBindTransport(false);
            }

            m_Status = DescribeStatus();
            RefreshDesignerUI();
        }

        private void OnDisable()
        {
            UnbindTransport();
        }

        private void OnGUI()
        {
            if (!m_ShowOverlay || m_Panel != null) return;

            GUILayout.BeginArea(
                new Rect(
                    m_OverlayPosition.x,
                    m_OverlayPosition.y,
                    m_OverlayWidth,
                    m_OverlayHeight),
                GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Fusion Chat");
            GUILayout.FlexibleSpace();
            GUILayout.Label(m_Status);
            GUILayout.EndHorizontal();

            m_Scroll = GUILayout.BeginScrollView(m_Scroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < m_Lines.Count; i++)
            {
                GUILayout.Label(m_Lines[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Name", GUILayout.Width(40f));
            m_DisplayName = GUILayout.TextField(
                m_DisplayName ?? string.Empty,
                Mathf.Max(1, m_MaxDisplayNameLength + 8),
                GUILayout.Width(110f));
            m_DraftMessage = GUILayout.TextField(
                m_DraftMessage ?? string.Empty,
                Mathf.Max(1, m_MaxMessageLength + 16),
                GUILayout.ExpandWidth(true));

            bool previousEnabled = GUI.enabled;
            GUI.enabled = m_BoundBridge != null && m_BoundBridge.IsClient;
            if (GUILayout.Button("Send", GUILayout.Width(52f)))
            {
                SendCurrentMessage();
            }
            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        public void SendCurrentMessage()
        {
            if (m_BoundBridge == null ||
                !m_BoundBridge.IsClient ||
                !m_BoundBridge.TryGetLocalClientId(out _))
            {
                AppendSystem("Start or join a session before sending chat.");
                return;
            }

            float now = Time.unscaledTime;
            if (now - m_LastLocalSendTime < Mathf.Max(0f, m_MinSendInterval))
            {
                AppendSystem("Please wait before sending another message.");
                return;
            }

            string name = SanitizeText(
                m_NameField != null ? m_NameField.text : m_DisplayName,
                Mathf.Max(1, m_MaxDisplayNameLength),
                m_DefaultDisplayName);
            string message = SanitizeText(
                m_MessageField != null ? m_MessageField.text : m_DraftMessage,
                Mathf.Max(1, m_MaxMessageLength),
                string.Empty);
            if (string.IsNullOrEmpty(message)) return;

            var writer = new FusionPacketWriter(64 + message.Length * 2);
            writer.WriteString(name);
            writer.WriteString(message);
            if (!m_BoundBridge.SendModuleToAuthority(
                    FusionProtocol.DemoChatModuleId,
                    ChatRequestMessage,
                    writer.ToArray(),
                    true))
            {
                AppendSystem("Chat is not connected.");
                return;
            }

            m_DisplayName = name;
            m_DraftMessage = string.Empty;
            if (m_NameField != null) m_NameField.text = name;
            if (m_MessageField != null)
            {
                m_MessageField.text = string.Empty;
                m_MessageField.ActivateInputField();
            }
            m_LastLocalSendTime = now;
            RefreshDesignerUI();
        }

        public void ClearMessages()
        {
            m_Lines.Clear();
            RefreshDesignerUI();
        }

        private void ResolveDependencies()
        {
            if (m_SessionBootstrap == null)
            {
                m_SessionBootstrap = FindFirstObjectByType<FusionSessionBootstrap>();
            }

            if (m_TransportBridge == null)
            {
                m_TransportBridge =
                    NetworkTransportBridge.Active as FusionTransportBridge ??
                    FindFirstObjectByType<FusionTransportBridge>();
            }
        }

        private FusionTransportBridge ResolveTransport()
        {
            if (m_TransportBridge != null) return m_TransportBridge;
            return NetworkTransportBridge.Active as FusionTransportBridge;
        }

        private void TryBindTransport(bool immediately)
        {
            FusionTransportBridge candidate = ResolveTransport();
            if (candidate == m_BoundBridge) return;
            if (candidate == null)
            {
                UnbindTransport();
                return;
            }

            if (!immediately && Time.unscaledTime < m_NextBindAttempt) return;
            UnbindTransport();
            if (!candidate.RegisterModuleHandler(
                    FusionProtocol.DemoChatModuleId,
                    OnTransportMessage))
            {
                m_NextBindAttempt = Time.unscaledTime + 1f;
                return;
            }

            m_BoundBridge = candidate;
            m_TransportBridge = candidate;
            if (!m_BoundBridge.RegisterFullSnapshotProducer(this))
            {
                m_BoundBridge.UnregisterModuleHandler(
                    FusionProtocol.DemoChatModuleId,
                    OnTransportMessage);
                m_BoundBridge = null;
                m_NextBindAttempt = Time.unscaledTime + 1f;
                return;
            }
            m_BoundBridge.AuthorityChanged += OnAuthorityChanged;
            m_BoundBridge.RunnerShutdown += OnRunnerShutdown;
        }

        private void UnbindTransport()
        {
            if (m_BoundBridge == null) return;
            m_BoundBridge.UnregisterFullSnapshotProducer(this);
            m_BoundBridge.AuthorityChanged -= OnAuthorityChanged;
            m_BoundBridge.RunnerShutdown -= OnRunnerShutdown;
            m_BoundBridge.UnregisterModuleHandler(
                FusionProtocol.DemoChatModuleId,
                OnTransportMessage);
            m_BoundBridge = null;
            m_LastAuthorityMessageTimes.Clear();
        }

        private void OnAuthorityChanged(bool isAuthority, uint epoch)
        {
            m_LastAuthorityMessageTimes.Clear();
            AppendSystem(isAuthority
                ? $"This peer is the logical authority (epoch {epoch})."
                : $"Authority changed (epoch {epoch}).");
        }

        private void OnRunnerShutdown(NetworkRunner runner, ShutdownReason reason)
        {
            m_LastAuthorityMessageTimes.Clear();
            AppendSystem($"Session stopped: {reason}.");
        }

        private void OnTransportMessage(FusionModuleMessage message)
        {
            if (message.MessageType == ChatRequestMessage &&
                !message.FromAuthority &&
                m_BoundBridge != null &&
                m_BoundBridge.IsServer)
            {
                ReceiveChatRequest(message);
                return;
            }

            if (message.MessageType == ChatBroadcastMessage && message.FromAuthority)
            {
                ReceiveChatBroadcast(message.Payload);
            }
        }

        private void ReceiveChatRequest(FusionModuleMessage message)
        {
            if (message.Payload.Length <= 0 || message.Payload.Length > MaximumRequestBytes) return;

            float now = Time.unscaledTime;
            if (m_LastAuthorityMessageTimes.TryGetValue(
                    message.SenderClientId,
                    out float lastTime) &&
                now - lastTime < Mathf.Max(0f, m_MinSendInterval))
            {
                return;
            }

            try
            {
                var reader = new FusionPacketReader(message.Payload);
                string name = SanitizeText(
                    reader.ReadString(),
                    Mathf.Max(1, m_MaxDisplayNameLength),
                    $"Player {message.SenderClientId}");
                string text = SanitizeText(
                    reader.ReadString(),
                    Mathf.Max(1, m_MaxMessageLength),
                    string.Empty);
                if (!reader.End || string.IsNullOrEmpty(text)) return;

                m_LastAuthorityMessageTimes[message.SenderClientId] = now;
                var writer = new FusionPacketWriter(72 + text.Length * 2);
                writer.WriteUInt32(message.SenderClientId);
                writer.WriteString(name);
                writer.WriteString(text);
                writer.WriteSingle(m_BoundBridge.ServerTime);
                m_BoundBridge.BroadcastModule(
                    FusionProtocol.DemoChatModuleId,
                    ChatBroadcastMessage,
                    writer.ToArray(),
                    true);
            }
            catch (FormatException exception)
            {
                Debug.LogWarning(
                    $"[FusionChat] Rejected malformed chat request from " +
                    $"{message.SenderClientId}: {exception.Message}",
                    this);
            }
        }

        private void ReceiveChatBroadcast(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length <= 0 || payload.Length > MaximumRequestBytes + 16) return;

            try
            {
                var reader = new FusionPacketReader(payload);
                uint senderClientId = reader.ReadUInt32();
                string name = SanitizeText(
                    reader.ReadString(),
                    Mathf.Max(1, m_MaxDisplayNameLength),
                    $"Player {senderClientId}");
                string text = SanitizeText(
                    reader.ReadString(),
                    Mathf.Max(1, m_MaxMessageLength),
                    string.Empty);
                reader.ReadSingle(); // Authority time is retained on wire for diagnostics/replays.
                if (!reader.End || string.IsNullOrEmpty(text)) return;
                AppendLine($"{name} [{senderClientId}]: {text}");
            }
            catch (FormatException exception)
            {
                Debug.LogWarning(
                    $"[FusionChat] Rejected malformed authority broadcast: {exception.Message}",
                    this);
            }
        }

        private string DescribeStatus()
        {
            if (m_BoundBridge == null || m_BoundBridge.Runner == null) return "Offline";
            if (!m_BoundBridge.IsClient) return "Connecting";
            return m_BoundBridge.IsServer ? "Authority" : "Connected";
        }

        private void AppendSystem(string value)
        {
            AppendLine($"• {value}");
        }

        private void AppendLine(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            m_Lines.Add(value);
            int maximum = Mathf.Max(1, m_MaxVisibleMessages);
            while (m_Lines.Count > maximum)
            {
                m_Lines.RemoveAt(0);
            }

            m_Scroll.y = float.MaxValue;
            RefreshDesignerUI();
        }

        private void RefreshDesignerUI()
        {
            if (m_StatusText != null) m_StatusText.text = m_Status;
            if (m_MessageText != null) m_MessageText.text = string.Join("\n", m_Lines);

            bool connected = m_BoundBridge != null && m_BoundBridge.IsClient;
            if (m_SendButton != null) m_SendButton.interactable = connected;
            if (m_NameField != null) m_NameField.interactable = true;
            if (m_MessageField != null) m_MessageField.interactable = connected;
            if (m_Panel != null && !m_Panel.activeSelf) m_Panel.SetActive(true);
            if (m_Canvas != null && !m_Canvas.enabled) m_Canvas.enabled = true;

            if (m_MessageContent != null)
            {
                LayoutRebuilder.MarkLayoutForRebuild(m_MessageContent);
            }
            if (m_ScrollRect != null)
            {
                Canvas.ForceUpdateCanvases();
                m_ScrollRect.verticalNormalizedPosition = 0f;
            }
        }

        private static string SanitizeText(string value, int maximumLength, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) value = fallback ?? string.Empty;
            maximumLength = Mathf.Max(1, maximumLength);

            var builder = new StringBuilder(Mathf.Min(value.Length, maximumLength));
            bool previousSpace = false;
            for (int i = 0; i < value.Length && builder.Length < maximumLength; i++)
            {
                char character = value[i];
                bool space = char.IsWhiteSpace(character) || char.IsControl(character);
                if (space)
                {
                    if (previousSpace || builder.Length == 0) continue;
                    builder.Append(' ');
                    previousSpace = true;
                    continue;
                }

                // Avoid malformed UTF-16 if a maximum cuts a surrogate pair.
                if (char.IsHighSurrogate(character))
                {
                    if (i + 1 >= value.Length ||
                        !char.IsLowSurrogate(value[i + 1]) ||
                        builder.Length + 2 > maximumLength)
                    {
                        continue;
                    }

                    builder.Append(character);
                    builder.Append(value[++i]);
                    previousSpace = false;
                    continue;
                }

                if (char.IsLowSurrogate(character)) continue;
                builder.Append(character);
                previousSpace = false;
            }

            return builder.ToString().Trim();
        }
    }
}
