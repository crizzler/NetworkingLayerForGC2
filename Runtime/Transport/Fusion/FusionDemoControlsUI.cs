using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Lightweight zero-setup diagnostics and controls legend for generated demo scenes.
    /// It deliberately has no gameplay input dependency.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Demo Controls UI")]
    [DisallowMultipleComponent]
    public sealed class FusionDemoControlsUI : MonoBehaviour
    {
        [SerializeField] private FusionSessionBootstrap m_SessionBootstrap;
        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private bool m_ShowOverlay = true;
        [SerializeField] private string m_Title = "GC2 Fusion Controls";
        [TextArea(3, 10)]
        [SerializeField] private string m_Controls =
            "Move: your GC2 character bindings\n" +
            "Look/Camera: your GC2 camera bindings\n" +
            "Gameplay is validated by the Host or Shared Master.";
        [Min(240f)]
        [SerializeField] private float m_OverlayWidth = 340f;
        [SerializeField] private Vector2 m_TopRightPadding = new Vector2(16f, 16f);

        public bool IsVisible => m_ShowOverlay;

        private void Awake()
        {
            ResolveDependencies();
        }

        private void Update()
        {
            ResolveDependencies();
        }

        private void OnGUI()
        {
            if (!m_ShowOverlay) return;

            const float height = 174f;
            float x = Mathf.Max(0f, Screen.width - m_OverlayWidth - m_TopRightPadding.x);
            float y = Mathf.Max(0f, m_TopRightPadding.y);
            GUILayout.BeginArea(new Rect(x, y, m_OverlayWidth, height), GUI.skin.box);
            GUILayout.Label(string.IsNullOrWhiteSpace(m_Title) ? "GC2 Fusion Controls" : m_Title);
            GUILayout.Label(DescribeSession());
            GUILayout.Space(4f);
            GUILayout.Label(m_Controls ?? string.Empty);
            GUILayout.EndArea();
        }

        public void SetVisible(bool visible)
        {
            m_ShowOverlay = visible;
        }

        public void ToggleVisible()
        {
            m_ShowOverlay = !m_ShowOverlay;
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

        private string DescribeSession()
        {
            if (m_TransportBridge == null ||
                m_TransportBridge.Runner == null ||
                !m_TransportBridge.Runner.IsRunning)
            {
                return "Session: Offline";
            }

            NetworkRunner runner = m_TransportBridge.Runner;
            string mode = runner.GameMode == GameMode.Shared
                ? "Shared"
                : runner.GameMode == GameMode.Host ? "Host" : "Host Client";
            string role = m_TransportBridge.IsServer
                ? "GC2 Authority"
                : "GC2 Client";

            string client = m_TransportBridge.TryGetLocalClientId(out uint clientId)
                ? clientId.ToString()
                : "pending";
            return $"Session: {mode}  •  Role: {role}  •  Client: {client}";
        }
    }
}
