using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Scene scaffold for transport-agnostic projects.
    /// The setup wizard creates this so integrators
    /// can wire their own NetworkTransportBridge implementation explicitly.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Custom Transport Bridge Placeholder")]
    public sealed class CustomTransportBridgePlaceholder : MonoBehaviour
    {
        [Tooltip("Assign your concrete NetworkTransportBridge implementation here once added to the scene.")]
        [SerializeField] private NetworkTransportBridge m_AssignedBridge;

        [TextArea(4, 8)]
        [SerializeField] private string m_Notes =
            "This scene is configured for transport-agnostic networking.\n" +
            "Add your own NetworkTransportBridge implementation (FishNet/Mirror/custom) and assign it here.\n" +
            "Use NetworkTransportBridge.TryConvertSenderClientId(...) for inbound sender IDs (0-based IDs are supported).\n" +
            "Keep NetworkSecurityManager active for strict ownership + sequence validation.";

        public NetworkTransportBridge AssignedBridge => m_AssignedBridge;
        public string Notes => m_Notes;

        public bool HasAssignedBridge => m_AssignedBridge != null;

        private void Reset()
        {
            if (m_AssignedBridge == null)
            {
                m_AssignedBridge = FindFirstObjectByType<NetworkTransportBridge>();
            }
        }
    }
}
