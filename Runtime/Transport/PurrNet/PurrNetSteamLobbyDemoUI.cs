using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Zero-prefab IMGUI menu for the generated Steam lobby demo. Production games
    /// can call the same PurrNetSteamLobbyNetwork API from their own GC2 menu.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Demo/PurrNet Steam Lobby Demo UI")]
    [DefaultExecutionOrder(-300)]
    public sealed class PurrNetSteamLobbyDemoUI : MonoBehaviour
    {
        [SerializeField] private PurrNetSteamLobbyNetwork m_LobbyNetwork;
        [SerializeField] private bool m_ShowOverlay = true;
        [SerializeField] private KeyCode m_ToggleKey = KeyCode.None;
        [SerializeField] private Vector2 m_Margin = new Vector2(16f, 16f);
        [SerializeField, Min(280f)] private float m_Width = 390f;

        private string m_LobbyIdInput = string.Empty;
        private bool m_RuntimeVisible = true;

        private void Awake()
        {
            ResolveNetwork();
        }

        private void Update()
        {
            if (m_ToggleKey != KeyCode.None && Input.GetKeyDown(m_ToggleKey))
                m_RuntimeVisible = !m_RuntimeVisible;
            if (m_LobbyNetwork == null) ResolveNetwork();
        }

        private void OnGUI()
        {
            if (!m_ShowOverlay || !m_RuntimeVisible) return;

            float height = m_LobbyNetwork != null &&
                           !string.IsNullOrWhiteSpace(m_LobbyNetwork.CurrentLobbyId)
                ? 270f
                : 320f;
            var rect = new Rect(m_Margin.x, m_Margin.y, m_Width, height);

            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label("PurrNet Steam Lobby", BoldStyle);
            GUILayout.Label(
                "Steam lobby discovery and invites; gameplay uses PurrNet SteamTransport.",
                WordWrapStyle);
            GUILayout.Space(6f);

            if (m_LobbyNetwork == null)
            {
                GUILayout.Label(
                    "No PurrNetSteamLobbyNetwork exists in this scene.",
                    ErrorStyle);
                GUILayout.EndArea();
                return;
            }

            DrawIdentity();
            GUILayout.Space(6f);

            if (!m_LobbyNetwork.IsAvailable)
            {
                GUILayout.Label(
                    "Steamworks.NET is not installed, or this target is unsupported. " +
                    "The optional lobby sample is disabled; non-Steam Networking Layer " +
                    "features continue to compile normally.",
                    ErrorStyle);
            }
            else if (string.IsNullOrWhiteSpace(m_LobbyNetwork.CurrentLobbyId))
            {
                DrawOfflineMenu();
            }
            else
            {
                DrawLobbyMenu();
            }

            GUILayout.FlexibleSpace();
            GUILayout.Label(
                $"State: {m_LobbyNetwork.State}\n{m_LobbyNetwork.StatusMessage}",
                WordWrapStyle);
            GUILayout.EndArea();
        }

        private void DrawIdentity()
        {
            string steamId = m_LobbyNetwork.LocalSteamId;
            GUILayout.Label(
                string.IsNullOrWhiteSpace(steamId)
                    ? "Steam ID: unavailable"
                    : $"Steam ID: {steamId}");
        }

        private void DrawOfflineMenu()
        {
            bool previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled &&
                          m_LobbyNetwork.IsSteamReady &&
                          !m_LobbyNetwork.IsBusy &&
                          IsPurrNetOffline();

            if (GUILayout.Button("Host Steam Lobby", GUILayout.Height(30f)))
                m_LobbyNetwork.Host();

            GUILayout.Space(7f);
            GUILayout.Label("Join by lobby ID", BoldStyle);
            GUILayout.BeginHorizontal();
            m_LobbyIdInput = GUILayout.TextField(m_LobbyIdInput ?? string.Empty);
            if (GUILayout.Button("Paste", GUILayout.Width(58f)))
                m_LobbyIdInput = GUIUtility.systemCopyBuffer?.Trim() ?? string.Empty;
            if (GUILayout.Button("Join", GUILayout.Width(70f), GUILayout.Height(24f)))
                m_LobbyNetwork.JoinLobby(m_LobbyIdInput);
            GUILayout.EndHorizontal();

            GUI.enabled = previousEnabled;
        }

        private void DrawLobbyMenu()
        {
            GUILayout.Label($"Lobby ID: {m_LobbyNetwork.CurrentLobbyId}", BoldStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy Lobby ID"))
                GUIUtility.systemCopyBuffer = m_LobbyNetwork.CurrentLobbyId;
            if (GUILayout.Button("Invite Friends"))
                m_LobbyNetwork.OpenInviteOverlay();
            GUILayout.EndHorizontal();

            GUILayout.Space(7f);
            if (GUILayout.Button("Leave", GUILayout.Height(28f)))
                m_LobbyNetwork.Leave();
        }

        private bool IsPurrNetOffline()
        {
            NetworkManager manager = m_LobbyNetwork.ActiveNetworkManager;
            return manager == null ||
                   (manager.serverState == ConnectionState.Disconnected &&
                    manager.clientState == ConnectionState.Disconnected);
        }

        private void ResolveNetwork()
        {
#if UNITY_2023_1_OR_NEWER
            m_LobbyNetwork = FindFirstObjectByType<PurrNetSteamLobbyNetwork>(
                FindObjectsInactive.Include);
#else
            m_LobbyNetwork = FindObjectOfType<PurrNetSteamLobbyNetwork>(true);
#endif
        }

        private static GUIStyle s_BoldStyle;
        private static GUIStyle s_WordWrapStyle;
        private static GUIStyle s_ErrorStyle;

        private static GUIStyle BoldStyle => s_BoldStyle ??=
            new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };

        private static GUIStyle WordWrapStyle => s_WordWrapStyle ??=
            new GUIStyle(GUI.skin.label) { wordWrap = true };

        private static GUIStyle ErrorStyle => s_ErrorStyle ??=
            new GUIStyle(WordWrapStyle)
            {
                normal = { textColor = new Color(1f, 0.45f, 0.4f) }
            };
    }
}
