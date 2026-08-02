using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.UI;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Demo character chooser with an authority-bound selection protocol. Clients send
    /// only a prefab index; the authoritative bridge supplies the real sender client ID
    /// and this component validates the index against the authority's configured spawner.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Demo Character Selection")]
    [DefaultExecutionOrder(-300)]
    [DisallowMultipleComponent]
    public sealed class FusionDemoCharacterSelection :
        MonoBehaviour,
        INetworkDemoCharacterSelectionTarget
    {
        private const ushort SelectionRequestMessage = 1;

        [Header("References")]
        [SerializeField] private FusionSessionBootstrap m_SessionBootstrap;
        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private FusionPlayerSpawner m_PlayerSpawner;
        [SerializeField] private string[] m_DisplayNames = Array.Empty<string>();

        [Header("Selection")]
        [SerializeField] private int m_SelectedIndex;
        [SerializeField] private bool m_RequireSelectionBeforeSpawn = true;

        [Header("Optional Designer UI")]
        [Tooltip("Optional authored selection UI. These fields intentionally match the " +
                 "PurrNet demo component so existing demo canvases migrate without losing references.")]
        [SerializeField] private GameObject m_SelectionRoot;
        [SerializeField] private GameObject m_InGameRoot;
        [SerializeField] private InputField m_AddressInput;
        [SerializeField] private InputField m_PortInput;
        [SerializeField] private Text m_StatusText;
        [SerializeField] private Text[] m_StatusTexts = Array.Empty<Text>();
        [SerializeField] private Text m_SelectedText;
        [SerializeField] private Graphic[] m_SelectionHighlights = Array.Empty<Graphic>();

        [Header("Zero-Setup Overlay")]
        [SerializeField] private bool m_ShowOverlay = true;
        [SerializeField] private Vector2 m_OverlayPosition = new Vector2(332f, 16f);
        [Min(220f)]
        [SerializeField] private float m_OverlayWidth = 280f;

        private readonly Dictionary<uint, int> m_AuthoritySelections =
            new Dictionary<uint, int>();
        private readonly List<uint> m_ClientScratch = new List<uint>();

        private FusionTransportBridge m_BoundBridge;
        private NetworkRunner m_LastPublishedRunner;
        private uint m_LastPublishedEpoch;
        private int m_LastPublishedIndex = int.MinValue;
        private bool m_PublishPending = true;
        private bool m_SessionOperationInProgress;
        private float m_NextBindAttempt;
        private string m_Status = "Choose a character.";

        public event Action<uint, int> AuthoritySelectionAccepted;

        public int SelectedIndex => NormalizeSelectionIndex(m_SelectedIndex);
        public bool RequireSelectionBeforeSpawn => m_RequireSelectionBeforeSpawn;
        public int ChoiceCount => m_PlayerSpawner != null ? m_PlayerSpawner.SelectionSlotCount : 0;

        private void Awake()
        {
            ResolveDependencies();
            AttachToSpawner();
            m_SelectedIndex = NormalizeSelectionIndex(m_SelectedIndex);
            InitializeDesignerUI();
            UpdateDesignerUI();
        }

        private void OnEnable()
        {
            ResolveDependencies();
            AttachToSpawner();
            TryBindTransport(true);
            m_PublishPending = true;
        }

        private void Update()
        {
            ResolveDependencies();
            AttachToSpawner();

            FusionTransportBridge candidate = ResolveTransport();
            if (candidate != m_BoundBridge)
            {
                TryBindTransport(false);
            }

            TryPublishLocalSelection();
            if (m_BoundBridge != null && m_BoundBridge.IsServer)
            {
                RemoveDisconnectedSelections();
            }

            UpdateDesignerUI();
        }

        private void OnDisable()
        {
            UnbindTransport();
            if (m_PlayerSpawner != null)
            {
                m_PlayerSpawner.DetachCharacterSelection(this);
            }
        }

        private void OnGUI()
        {
            if (!m_ShowOverlay || m_SelectionRoot != null) return;

            int count = ChoiceCount;
            float height = Mathf.Max(112f, 82f + count * 28f);
            GUILayout.BeginArea(
                new Rect(m_OverlayPosition.x, m_OverlayPosition.y, m_OverlayWidth, height),
                GUI.skin.box);
            GUILayout.Label("Character Selection");

            if (count == 0)
            {
                GUILayout.Label("Assign a player prefab on FusionPlayerSpawner.");
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    bool valid = m_PlayerSpawner.TryGetSelectablePrefab(i, out _);
                    bool previousEnabled = GUI.enabled;
                    GUI.enabled = valid;
                    string selected = i == SelectedIndex ? "  ✓" : string.Empty;
                    if (GUILayout.Button(GetDisplayName(i) + selected))
                    {
                        SelectCharacter(i);
                    }
                    GUI.enabled = previousEnabled;
                }
            }

            GUILayout.Label(m_Status);
            GUILayout.EndArea();
        }

        public void AttachPlayerSpawner(FusionPlayerSpawner spawner)
        {
            if (spawner == null || m_PlayerSpawner == spawner)
            {
                if (spawner != null)
                {
                    spawner.AttachCharacterSelection(this, m_RequireSelectionBeforeSpawn);
                }
                return;
            }

            if (m_PlayerSpawner != null)
            {
                m_PlayerSpawner.DetachCharacterSelection(this);
            }

            m_PlayerSpawner = spawner;
            m_PlayerSpawner.AttachCharacterSelection(this, m_RequireSelectionBeforeSpawn);
            m_SelectedIndex = NormalizeSelectionIndex(m_SelectedIndex);
            m_PublishPending = true;
        }

        public void SelectCharacter(int index)
        {
            if (m_PlayerSpawner == null ||
                !m_PlayerSpawner.TryGetSelectablePrefab(index, out NetworkObject prefab))
            {
                m_Status = $"Selection {index} is not configured on the authority spawner.";
                UpdateDesignerUI();
                return;
            }

            m_SelectedIndex = index;
            m_PublishPending = true;
            m_Status = $"Selected {GetDisplayName(index)}.";
            TryPublishLocalSelection();
            UpdateDesignerUI();
        }

        public async void StartHost()
        {
            if (!TryBeginSessionOperation()) return;
            m_Status = "Starting Fusion Host...";
            try
            {
                StartGameResult result = await m_SessionBootstrap.StartHostAsync(
                    m_SessionBootstrap.DefaultSessionName);
                m_Status = result.Ok
                    ? "Fusion Host connected."
                    : $"Host failed: {result.ShutdownReason} {result.ErrorMessage}";
            }
            catch (Exception exception)
            {
                m_Status = $"Host failed: {exception.Message}";
                Debug.LogException(exception, this);
            }
            finally
            {
                m_SessionOperationInProgress = false;
                UpdateDesignerUI();
            }
        }

        public async void JoinServer()
        {
            if (!TryBeginSessionOperation()) return;
            bool shared =
                m_SessionBootstrap.DefaultLaunchMode == FusionDefaultLaunchMode.Shared ||
                m_SessionBootstrap.DefaultLaunchMode == FusionDefaultLaunchMode.JoinShared;
            m_Status = shared ? "Joining Fusion Shared session..." : "Joining Fusion Host...";
            try
            {
                StartGameResult result = shared
                    ? await m_SessionBootstrap.JoinSharedAsync(m_SessionBootstrap.DefaultSessionName)
                    : await m_SessionBootstrap.JoinHostAsync(m_SessionBootstrap.DefaultSessionName);
                m_Status = result.Ok
                    ? "Fusion session connected."
                    : $"Join failed: {result.ShutdownReason} {result.ErrorMessage}";
            }
            catch (Exception exception)
            {
                m_Status = $"Join failed: {exception.Message}";
                Debug.LogException(exception, this);
            }
            finally
            {
                m_SessionOperationInProgress = false;
                UpdateDesignerUI();
            }
        }

        public async void StopSession()
        {
            ResolveDependencies();
            if (m_SessionBootstrap == null || m_SessionOperationInProgress) return;

            m_SessionOperationInProgress = true;
            m_Status = "Stopping Fusion session...";
            try
            {
                await m_SessionBootstrap.ShutdownAsync();
                m_Status = "Session stopped.";
            }
            catch (Exception exception)
            {
                m_Status = $"Stop failed: {exception.Message}";
                Debug.LogException(exception, this);
            }
            finally
            {
                m_SessionOperationInProgress = false;
                UpdateDesignerUI();
            }
        }

        public bool TryGetAuthoritySelection(uint clientId, out int index)
        {
            if (m_AuthoritySelections.TryGetValue(clientId, out index) &&
                m_PlayerSpawner != null &&
                m_PlayerSpawner.IsValidSelectionIndex(index))
            {
                return true;
            }

            index = -1;
            return false;
        }

        public bool TryGetAuthorityPlayerPrefab(uint clientId, out NetworkObject prefab)
        {
            prefab = null;
            return TryGetAuthoritySelection(clientId, out int index) &&
                   m_PlayerSpawner != null &&
                   m_PlayerSpawner.TryGetSelectablePrefab(index, out prefab);
        }

        public void ForgetAuthoritySelection(uint clientId)
        {
            m_AuthoritySelections.Remove(clientId);
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

            if (m_PlayerSpawner == null)
            {
                m_PlayerSpawner = FindFirstObjectByType<FusionPlayerSpawner>();
            }
        }

        private bool TryBeginSessionOperation()
        {
            ResolveDependencies();
            if (m_SessionBootstrap == null)
            {
                m_Status = "No FusionSessionBootstrap found.";
                return false;
            }

            if (m_SessionOperationInProgress)
            {
                m_Status = "A session operation is already in progress.";
                return false;
            }

            if (m_SessionBootstrap.IsRunning)
            {
                m_Status = "Stop the current session before starting another.";
                return false;
            }

            m_SessionOperationInProgress = true;
            return true;
        }

        private FusionTransportBridge ResolveTransport()
        {
            if (m_TransportBridge != null) return m_TransportBridge;
            return NetworkTransportBridge.Active as FusionTransportBridge;
        }

        private void AttachToSpawner()
        {
            if (m_PlayerSpawner != null)
            {
                m_PlayerSpawner.AttachCharacterSelection(this, m_RequireSelectionBeforeSpawn);
            }
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
            if (!candidate.RegisterPreGameplayModuleHandler(
                    FusionProtocol.DemoCharacterSelectionModuleId,
                    SelectionRequestMessage,
                    OnTransportMessage))
            {
                m_NextBindAttempt = Time.unscaledTime + 1f;
                return;
            }

            m_BoundBridge = candidate;
            m_TransportBridge = candidate;
            m_BoundBridge.AuthorityChanged += OnAuthorityChanged;
            m_BoundBridge.RunnerShutdown += OnRunnerShutdown;
            m_PublishPending = true;
        }

        private void UnbindTransport()
        {
            if (m_BoundBridge == null) return;
            m_BoundBridge.AuthorityChanged -= OnAuthorityChanged;
            m_BoundBridge.RunnerShutdown -= OnRunnerShutdown;
            m_BoundBridge.UnregisterPreGameplayModuleHandler(
                FusionProtocol.DemoCharacterSelectionModuleId,
                SelectionRequestMessage,
                OnTransportMessage);
            m_BoundBridge = null;
        }

        private void OnAuthorityChanged(bool isAuthority, uint epoch)
        {
            m_AuthoritySelections.Clear();
            m_LastPublishedRunner = null;
            m_LastPublishedEpoch = 0;
            m_LastPublishedIndex = int.MinValue;
            m_PublishPending = true;
        }

        private void OnRunnerShutdown(NetworkRunner runner, ShutdownReason reason)
        {
            m_AuthoritySelections.Clear();
            m_LastPublishedRunner = null;
            m_LastPublishedEpoch = 0;
            m_LastPublishedIndex = int.MinValue;
            m_PublishPending = true;
            m_Status = $"Session stopped: {reason}.";
        }

        private void TryPublishLocalSelection()
        {
            if (!m_PublishPending || m_BoundBridge == null ||
                !m_BoundBridge.IsClient || m_BoundBridge.Runner == null ||
                m_PlayerSpawner == null ||
                !m_BoundBridge.TryGetLocalClientId(out _))
            {
                return;
            }

            int index = NormalizeSelectionIndex(m_SelectedIndex);
            if (!m_PlayerSpawner.IsValidSelectionIndex(index)) return;

            NetworkRunner runner = m_BoundBridge.Runner;
            if (runner == m_LastPublishedRunner &&
                m_LastPublishedEpoch == m_BoundBridge.AuthorityEpoch &&
                m_LastPublishedIndex == index)
            {
                m_PublishPending = false;
                return;
            }

            var writer = new FusionPacketWriter(4);
            writer.WriteInt32(index);
            if (!m_BoundBridge.SendModuleToAuthority(
                    FusionProtocol.DemoCharacterSelectionModuleId,
                    SelectionRequestMessage,
                    writer.ToArray(),
                    true))
            {
                return;
            }

            m_LastPublishedRunner = runner;
            m_LastPublishedEpoch = m_BoundBridge.AuthorityEpoch;
            m_LastPublishedIndex = index;
            m_PublishPending = false;
            m_Status = $"Selection sent: {GetDisplayName(index)}.";
        }

        private void OnTransportMessage(FusionModuleMessage message)
        {
            if (message.FromAuthority ||
                message.MessageType != SelectionRequestMessage ||
                m_BoundBridge == null ||
                !m_BoundBridge.IsServer ||
                message.Payload.Length != 4)
            {
                return;
            }

            try
            {
                var reader = new FusionPacketReader(message.Payload);
                int index = reader.ReadInt32();
                if (!reader.End ||
                    m_PlayerSpawner == null ||
                    !m_PlayerSpawner.IsValidSelectionIndex(index))
                {
                    Debug.LogWarning(
                        $"[FusionCharacterSelection] Rejected invalid selection index {index} " +
                        $"from client {message.SenderClientId}.",
                        this);
                    return;
                }

                // SenderClientId comes exclusively from the validated RpcInfo.Source in
                // FusionTransportBridge; the payload cannot nominate another player.
                m_AuthoritySelections[message.SenderClientId] = index;
                AuthoritySelectionAccepted?.Invoke(message.SenderClientId, index);
            }
            catch (FormatException exception)
            {
                Debug.LogWarning(
                    $"[FusionCharacterSelection] Rejected malformed selection: {exception.Message}",
                    this);
            }
        }

        private int NormalizeSelectionIndex(int index)
        {
            if (m_PlayerSpawner == null || m_PlayerSpawner.SelectionSlotCount == 0) return 0;
            if (m_PlayerSpawner.IsValidSelectionIndex(index)) return index;

            for (int i = 0; i < m_PlayerSpawner.SelectionSlotCount; i++)
            {
                if (m_PlayerSpawner.IsValidSelectionIndex(i)) return i;
            }

            return 0;
        }

        private string GetDisplayName(int index)
        {
            if (m_DisplayNames != null &&
                index >= 0 &&
                index < m_DisplayNames.Length &&
                !string.IsNullOrWhiteSpace(m_DisplayNames[index]))
            {
                return m_DisplayNames[index].Trim();
            }

            return m_PlayerSpawner != null &&
                   m_PlayerSpawner.TryGetSelectablePrefab(index, out NetworkObject prefab) &&
                   prefab != null
                ? prefab.name
                : $"Character {index + 1}";
        }

        private void RemoveDisconnectedSelections()
        {
            if (m_AuthoritySelections.Count == 0 || m_BoundBridge == null) return;

            m_ClientScratch.Clear();
            foreach (uint clientId in m_AuthoritySelections.Keys)
            {
                if (!IsConnected(clientId)) m_ClientScratch.Add(clientId);
            }

            for (int i = 0; i < m_ClientScratch.Count; i++)
            {
                m_AuthoritySelections.Remove(m_ClientScratch[i]);
            }
            m_ClientScratch.Clear();
        }

        private bool IsConnected(uint clientId)
        {
            if (m_BoundBridge == null) return false;
            foreach (uint connectedClientId in m_BoundBridge.ConnectedClientIds)
            {
                if (connectedClientId == clientId) return true;
            }

            return false;
        }

        private void InitializeDesignerUI()
        {
            // The old demo labelled this field as an address. For Fusion it is a session
            // name; replace the stock loopback value while preserving any authored value.
            if (m_AddressInput != null &&
                (string.IsNullOrWhiteSpace(m_AddressInput.text) ||
                 string.Equals(m_AddressInput.text.Trim(), "127.0.0.1", StringComparison.Ordinal)))
            {
                m_AddressInput.text = m_SessionBootstrap != null
                    ? m_SessionBootstrap.DefaultSessionName
                    : "GC2-Fusion-Selection";
            }

            if (m_PortInput != null)
            {
                m_PortInput.interactable = false;
            }
        }

        private void UpdateDesignerUI()
        {
            bool connected = m_SessionBootstrap != null && m_SessionBootstrap.IsRunning;
            if (m_SelectionRoot != null) m_SelectionRoot.SetActive(!connected);
            if (m_InGameRoot != null) m_InGameRoot.SetActive(connected);

            if (m_SelectedText != null)
            {
                m_SelectedText.text = $"Selected: {GetDisplayName(SelectedIndex)}";
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

            string role = m_BoundBridge == null || m_BoundBridge.Runner == null
                ? "Offline"
                : m_BoundBridge.IsServer
                    ? "Authority"
                    : m_BoundBridge.IsClient ? "Client" : "Connecting";
            SetDesignerStatus($"{m_Status}\nFusion role: {role}");
        }

        private void SetDesignerStatus(string value)
        {
            if (m_StatusText != null) m_StatusText.text = value;
            if (m_StatusTexts == null) return;
            for (int i = 0; i < m_StatusTexts.Length; i++)
            {
                if (m_StatusTexts[i] != null) m_StatusTexts[i].text = value;
            }
        }
    }
}
