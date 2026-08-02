using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Marks a runner that only listens for Photon session-list updates. Discovery runners
    /// are not gameplay peers and must never be adopted by the GC2 transport bridge.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class FusionLobbyDiscoveryRunnerMarker : MonoBehaviour
    {
        internal int Generation { get; private set; }

        internal void Initialize(int generation)
        {
            Generation = generation;
        }

        public static bool IsDiscoveryRunner(NetworkRunner runner)
        {
            return runner != null &&
                   runner.GetComponent<FusionLobbyDiscoveryRunnerMarker>() != null;
        }
    }
}
