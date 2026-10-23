using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkCharacter
    {
        [Tooltip(
            "Warn once when Malbers' MAnimal is present but the optional networking bridge " +
            "cannot be attached. Disable when intentionally omitting the companion.")]
        [SerializeField] private bool m_WarnIfMalbersBridgeMissing = true;

        private bool m_WarnedMalbersBridgeMissing;

        /// <summary>
        /// Attaches the optional Malbers Animal Controller companion when both Malbers and its
        /// GC2 integration are installed. The companion lives in Assembly-CSharp and is found by
        /// type name so the main package keeps no compile-time Malbers dependency.
        /// </summary>
        private void TryAttachMalbersMotorBridge()
        {
            if (!NetworkMalbersIntegrationPolicy.HasMalbersAnimal(gameObject)) return;

            MonoBehaviour bridge = NetworkMalbersIntegrationPolicy.EnsureMotorBridge(gameObject);
            NetworkMalbersIntegrationPolicy.EnsureStateBridge(gameObject);
            if (bridge != null ||
                !m_WarnIfMalbersBridgeMissing ||
                m_WarnedMalbersBridgeMissing)
            {
                return;
            }

            m_WarnedMalbersBridgeMissing = true;
            Debug.LogWarning(
                $"[NetworkCharacter] MAnimal was found on '{name}', but the optional Malbers " +
                "networking bridge could not be attached. Enable ARAWN_GC2_MALBERS for the " +
                "active build target and keep Assets/Arawn/NetworkingLayerForGC2.Malbers free " +
                "of asmdefs.",
                this);
        }
    }
}
