#if GC2_INVENTORY
using Arawn.GameCreator2.Networking.Transport.Fusion;
using global::Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory.Transport.Fusion
{
    /// <summary>
    /// Exposes a master/host-owned Fusion object to the transport-independent runtime pickup flow.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [RequireComponent(typeof(FusionNetworkIdentity))]
    [AddComponentMenu("Game Creator/Network/Inventory/Fusion Runtime Pickup Identity")]
    public sealed class FusionInventoryRuntimePickupIdentityAdapter : MonoBehaviour,
        INetworkInventoryRuntimePickupIdentity
    {
        [SerializeField] private NetworkObject m_NetworkObject;
        [SerializeField] private FusionNetworkIdentity m_Identity;

        private NetworkObject NetworkObject =>
            m_NetworkObject != null && m_NetworkObject.gameObject == gameObject
                ? m_NetworkObject
                : null;

        public uint NetworkPickupId
        {
            get
            {
                NetworkObject networkObject = NetworkObject;
                return networkObject != null && networkObject.IsValid
                    ? networkObject.Id.Raw
                    : 0;
            }
        }

        public bool IsSpawned
        {
            get
            {
                NetworkObject networkObject = NetworkObject;
                return networkObject != null &&
                       networkObject.IsValid &&
                       networkObject.Runner != null &&
                       networkObject.Runner.IsRunning;
            }
        }

        public bool TryServerConsume()
        {
            NetworkObject networkObject = NetworkObject;
            FusionNetworkIdentity identity = m_Identity;
            if (networkObject == null || !networkObject.IsValid ||
                networkObject.Runner == null || !networkObject.Runner.IsRunning ||
                identity == null || !identity.IsLogicalAuthority ||
                !networkObject.HasStateAuthority) return false;

            NetworkRunner runner = networkObject.Runner;
            runner.Despawn(networkObject);
            return networkObject == null || !networkObject.IsValid;
        }

        private void Reset()
        {
            m_NetworkObject = GetComponent<NetworkObject>();
            m_Identity = GetComponent<FusionNetworkIdentity>();
        }

        private void OnValidate()
        {
            if (m_NetworkObject == null || m_NetworkObject.gameObject != gameObject)
                m_NetworkObject = GetComponent<NetworkObject>();
            if (m_Identity == null || m_Identity.gameObject != gameObject)
                m_Identity = GetComponent<FusionNetworkIdentity>();
        }
    }
}
#endif
