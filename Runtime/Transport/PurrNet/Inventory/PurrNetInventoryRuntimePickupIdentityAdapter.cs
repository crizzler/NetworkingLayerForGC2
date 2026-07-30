#if GC2_INVENTORY
using PurrNet;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet
{
    /// <summary>
    /// Exposes a server-spawned PurrNet identity to the transport-independent Inventory pickup
    /// flow. The encoded ID reserves zero for "unresolved", matching the other PurrNet bridges.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity))]
    [AddComponentMenu("Game Creator/Network/Inventory/PurrNet Runtime Pickup Identity")]
    public sealed class PurrNetInventoryRuntimePickupIdentityAdapter : MonoBehaviour,
        INetworkInventoryRuntimePickupIdentity
    {
        [Tooltip("Required pickup-owned PurrNet identity. Runtime lookup intentionally fails closed when this is unassigned.")]
        [SerializeField] private NetworkIdentity m_Identity;

        private NetworkIdentity Identity
        {
            get => m_Identity != null && m_Identity.gameObject == gameObject ? m_Identity : null;
        }

        public uint NetworkPickupId
        {
            get
            {
                NetworkIdentity identity = Identity;
                if (!TryResolveNetworkIdentity(identity, out NetworkID networkId))
                {
                    return 0;
                }

                ulong objectId = networkId.id.value;
                return objectId < uint.MaxValue ? (uint)(objectId + 1UL) : 0;
            }
        }

        public bool IsSpawned
        {
            get
            {
                NetworkIdentity identity = Identity;
                if (identity == null || identity.isSceneObject || identity.networkManager == null)
                {
                    return false;
                }

                bool asServer = identity.networkManager.isServer;
                return identity.IsSpawned(asServer) && IsServerScoped(identity.GetNetworkID(asServer));
            }
        }

        public bool TryServerConsume()
        {
            NetworkIdentity identity = Identity;
            if (identity == null || identity.isSceneObject || identity.networkManager == null ||
                !identity.networkManager.isServer)
            {
                return false;
            }

            if (!identity.IsSpawned(true) || !IsServerScoped(identity.GetNetworkID(true))) return false;
            if (!identity.HasDespawnAuthority(PlayerID.Server, true)) return false;

            identity.Despawn();
            return identity == null || !identity.IsSpawned(true);
        }

        private static bool TryResolveNetworkIdentity(NetworkIdentity identity, out NetworkID networkId)
        {
            networkId = default;
            if (identity == null || identity.isSceneObject || identity.networkManager == null) return false;

            bool asServer = identity.networkManager.isServer;
            if (!identity.IsSpawned(asServer)) return false;

            NetworkID? candidate = identity.GetNetworkID(asServer);
            if (!IsServerScoped(candidate)) return false;

            networkId = candidate.Value;
            return true;
        }

        private static bool IsServerScoped(NetworkID? networkId)
        {
            return networkId.HasValue && networkId.Value.scope == PlayerID.Server;
        }

        private void Reset()
        {
            m_Identity = GetComponent<NetworkIdentity>();
        }

        private void OnValidate()
        {
            if (m_Identity == null || m_Identity.gameObject != gameObject)
            {
                m_Identity = GetComponent<NetworkIdentity>();
            }
        }
    }
}
#endif
