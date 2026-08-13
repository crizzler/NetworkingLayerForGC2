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

        private Rect RuntimeRect => new(
            Mathf.Max(12f, (Screen.width - 420f) * 0.5f),
            Mathf.Max(12f, Screen.height - 168f),
            Mathf.Min(420f, Mathf.Max(180f, Screen.width - 24f)),
            156f);

        private void OnGUI()
        {
            Rect rect = RuntimeRect;
            GUILayout.BeginArea(rect, GUI.skin.box);
            GUILayout.Label(m_Title);
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            bool ready = bridge != null && bridge.IsRunning &&
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
            if (GUILayout.Button("Open Door")) m_OpenActions?.Invoke(gameObject);
            if (GUILayout.Button("Close Door")) m_CloseActions?.Invoke(gameObject);
            GUILayout.EndHorizontal();
            GUI.enabled = previous;
            if (!ready) GUILayout.Label("Start/join a session and wait for the local player.");
            GUILayout.EndArea();
        }
    }
}
