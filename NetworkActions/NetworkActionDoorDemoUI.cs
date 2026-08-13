using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Small transport-neutral demo surface. The buttons invoke authored GC2 Actions; this
    /// component never sends transport messages itself.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Demo/Network Action Door UI")]
    public sealed class NetworkActionDoorDemoUI : MonoBehaviour
    {
        [SerializeField] private Actions m_OpenActions;
        [SerializeField] private Actions m_CloseActions;
        [SerializeField] private NetworkActionEndpoint m_DoorEndpoint;
        [SerializeField] private NetworkActionDefinition m_DoorState;
        [SerializeField] private string m_Title = "GC2 Network Action Door";

        private string m_LastRequestStatus = "No request sent yet";

        private Rect RuntimeRect => new(
            Mathf.Max(12f, (Screen.width - 420f) * 0.5f),
            Mathf.Max(12f, Screen.height - 198f),
            Mathf.Min(420f, Mathf.Max(180f, Screen.width - 24f)),
            186f);

        private void OnEnable()
        {
            NetworkActionEvents.Approved += HandleRequestResponse;
            NetworkActionEvents.Rejected += HandleRequestResponse;
        }

        private void OnDisable()
        {
            NetworkActionEvents.Approved -= HandleRequestResponse;
            NetworkActionEvents.Rejected -= HandleRequestResponse;
        }

        private void OnGUI()
        {
            Rect rect = RuntimeRect;
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label(m_Title);
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            bool ready = bridge != null && bridge.IsRunning &&
                         bridge.IsLocalGameplayReady &&
                         bridge.TryGetLocalPlayer(out GameObject player) && player != null &&
                         m_DoorEndpoint != null && m_DoorEndpoint.NetworkId != 0 &&
                         m_DoorState != null && NetworkActionManager.Instance != null;
            NetworkActionPayload payload = default;
            uint revision = 0;
            bool hasState = m_DoorEndpoint != null && m_DoorState != null &&
                            NetworkActionManager.Instance != null &&
                            NetworkActionManager.Instance.TryGetPersistentState(
                                m_DoorEndpoint,
                                m_DoorState,
                                out payload,
                                out revision);
            string state = hasState && payload.Type == NetworkActionPayloadType.Boolean
                ? payload.BooleanValue ? "OPEN" : "CLOSED"
                : "waiting for authoritative state";
            GUILayout.Label($"Door: {state}" + (hasState ? $"  revision {revision}" : ""));
            GUILayout.Label("These buttons run GC2 Action lists. Late joiners receive final state.");

            bool previous = GUI.enabled;
            GUI.enabled = ready;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Door"))
            {
                m_LastRequestStatus = "Requesting OPEN…";
                m_OpenActions?.Invoke(gameObject);
            }
            if (GUILayout.Button("Close Door"))
            {
                m_LastRequestStatus = "Requesting CLOSED…";
                m_CloseActions?.Invoke(gameObject);
            }
            GUILayout.EndHorizontal();
            GUI.enabled = previous;
            GUILayout.Label($"Last request: {m_LastRequestStatus}");
            if (!ready)
                GUILayout.Label("Start/join and wait for gameplay synchronization to complete.");
            GUILayout.EndArea();
        }

        private void HandleRequestResponse(NetworkActionExecutionContext context)
        {
            if (m_DoorEndpoint == null || m_DoorState == null) return;
            if (context.TargetNetworkId != m_DoorEndpoint.NetworkId ||
                context.ActionHash != m_DoorState.ActionHash ||
                context.ActionId != m_DoorState.ActionId) return;

            m_LastRequestStatus = context.IsApproved
                ? $"Approved — revision {context.Revision}"
                : $"Rejected — {context.RejectReason}";
        }
    }
}
