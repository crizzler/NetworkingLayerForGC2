using System;
using System.Threading.Tasks;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Optional uGUI smoke-test surface. Production matchmaking can call
    /// <see cref="FusionSessionBootstrap"/> directly and omit this component.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Demo Session UI")]
    public class FusionDemoSessionUI : MonoBehaviour
    {
        [SerializeField] private FusionSessionBootstrap m_SessionBootstrap;
        [SerializeField] private InputField m_SessionName;
        [SerializeField] private Button m_StartHost;
        [SerializeField] private Button m_JoinHost;
        [SerializeField] private Button m_CreateShared;
        [SerializeField] private Button m_JoinShared;
        [SerializeField] private Button m_Shutdown;
        [SerializeField] private Text m_Status;

        private bool m_Busy;
        private string m_RuntimeSessionName;
        private string m_RuntimeStatus = "Offline";

        [Header("Zero-Setup Overlay")]
        [SerializeField] private bool m_ShowOverlayWhenControlsAreUnassigned = true;
        [SerializeField] private Vector2 m_OverlayPosition = new Vector2(16f, 16f);
        [Min(220f)]
        [SerializeField] private float m_OverlayWidth = 300f;

        private void Awake()
        {
            if (m_SessionBootstrap == null)
            {
                m_SessionBootstrap = FindFirstObjectByType<FusionSessionBootstrap>();
            }
            m_RuntimeSessionName =
                m_SessionBootstrap != null
                    ? m_SessionBootstrap.DefaultSessionName
                    : "GC2-Fusion";

            m_StartHost?.onClick.AddListener(StartHost);
            m_JoinHost?.onClick.AddListener(JoinHost);
            m_CreateShared?.onClick.AddListener(CreateShared);
            m_JoinShared?.onClick.AddListener(JoinShared);
            m_Shutdown?.onClick.AddListener(Shutdown);
            if (!HasLiveAppId())
            {
                SetStatus("Offline setup only: configure a Fusion App Id to start a live session.");
            }
            RefreshInteractable();
        }

        private void Update()
        {
            RefreshInteractable();
        }

        private void OnGUI()
        {
            if (!m_ShowOverlayWhenControlsAreUnassigned || HasAssignedControls()) return;

            GUILayout.BeginArea(
                new Rect(m_OverlayPosition.x, m_OverlayPosition.y, m_OverlayWidth, 238f),
                GUI.skin.box);
            GUILayout.Label("Fusion Multiplayer");
            m_RuntimeSessionName = GUILayout.TextField(m_RuntimeSessionName ?? string.Empty);

            bool previousEnabled = GUI.enabled;
            bool canStart = !m_Busy &&
                            m_SessionBootstrap != null &&
                            !m_SessionBootstrap.IsRunning &&
                            HasLiveAppId();
            GUI.enabled = canStart;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Host")) StartHost();
            if (GUILayout.Button("Join Host")) JoinHost();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Create Shared")) CreateShared();
            if (GUILayout.Button("Join Shared")) JoinShared();
            GUILayout.EndHorizontal();

            GUI.enabled = !m_Busy &&
                          m_SessionBootstrap != null &&
                          m_SessionBootstrap.IsRunning;
            if (GUILayout.Button("Stop Session")) Shutdown();
            GUI.enabled = previousEnabled;
            GUILayout.Label(m_RuntimeStatus);
            GUILayout.EndArea();
        }

        private void OnDestroy()
        {
            m_StartHost?.onClick.RemoveListener(StartHost);
            m_JoinHost?.onClick.RemoveListener(JoinHost);
            m_CreateShared?.onClick.RemoveListener(CreateShared);
            m_JoinShared?.onClick.RemoveListener(JoinShared);
            m_Shutdown?.onClick.RemoveListener(Shutdown);
        }

        private void StartHost() => RunStart(
            name => m_SessionBootstrap.StartHostAsync(name),
            "Starting Host");

        private void JoinHost() => RunStart(
            name => m_SessionBootstrap.JoinHostAsync(name),
            "Joining Host");

        private void CreateShared() => RunStart(
            name => m_SessionBootstrap.CreateSharedAsync(name),
            "Creating Shared session");

        private void JoinShared() => RunStart(
            name => m_SessionBootstrap.JoinSharedAsync(name),
            "Joining Shared session");

        private async void Shutdown()
        {
            if (m_SessionBootstrap == null || m_Busy) return;
            if (!HasLiveAppId())
            {
                SetStatus("Configure a Fusion App Id before starting a live session.");
                return;
            }
            m_Busy = true;
            SetStatus("Stopping session...");
            try
            {
                await m_SessionBootstrap.ShutdownAsync();
                SetStatus("Offline");
            }
            catch (Exception exception)
            {
                SetStatus($"Shutdown failed: {exception.Message}");
                Debug.LogException(exception, this);
            }
            finally
            {
                m_Busy = false;
                RefreshInteractable();
            }
        }

        private async void RunStart(
            Func<string, Task<StartGameResult>> start,
            string operation)
        {
            if (m_SessionBootstrap == null || m_Busy) return;
            string sessionName = m_SessionName != null ? m_SessionName.text.Trim() : string.Empty;
            if (m_SessionName == null)
            {
                sessionName = m_RuntimeSessionName?.Trim() ?? string.Empty;
            }
            if (string.IsNullOrEmpty(sessionName))
            {
                SetStatus("Enter a session name.");
                return;
            }

            m_Busy = true;
            SetStatus($"{operation}...");
            try
            {
                StartGameResult result = await start(sessionName);
                SetStatus(result.Ok
                    ? $"Connected: {sessionName}"
                    : $"Connection failed: {result.ShutdownReason} {result.ErrorMessage}");
            }
            catch (Exception exception)
            {
                SetStatus($"Connection failed: {exception.Message}");
                Debug.LogException(exception, this);
            }
            finally
            {
                m_Busy = false;
                RefreshInteractable();
            }
        }

        private void RefreshInteractable()
        {
            bool canStart =
                !m_Busy &&
                m_SessionBootstrap != null &&
                !m_SessionBootstrap.IsRunning &&
                HasLiveAppId();
            bool canStop =
                !m_Busy && m_SessionBootstrap != null && m_SessionBootstrap.IsRunning;
            if (m_StartHost != null) m_StartHost.interactable = canStart;
            if (m_JoinHost != null) m_JoinHost.interactable = canStart;
            if (m_CreateShared != null) m_CreateShared.interactable = canStart;
            if (m_JoinShared != null) m_JoinShared.interactable = canStart;
            if (m_Shutdown != null) m_Shutdown.interactable = canStop;
            if (m_SessionName != null) m_SessionName.interactable = canStart;
        }

        private void SetStatus(string value)
        {
            m_RuntimeStatus = value;
            if (m_Status != null) m_Status.text = value;
        }

        private bool HasAssignedControls()
        {
            return m_StartHost != null ||
                   m_JoinHost != null ||
                   m_CreateShared != null ||
                   m_JoinShared != null ||
                   m_Shutdown != null;
        }

        private static bool HasLiveAppId()
        {
            return PhotonAppSettings.TryGetGlobal(out PhotonAppSettings settings) &&
                   settings?.AppSettings != null &&
                   !string.IsNullOrWhiteSpace(settings.AppSettings.AppIdFusion);
        }
    }

    /// <summary>Wizard-facing name retained for parity with the PurrNet demo canvas.</summary>
    public sealed class FusionDemoCanvasUI : FusionDemoSessionUI
    {
    }
}
