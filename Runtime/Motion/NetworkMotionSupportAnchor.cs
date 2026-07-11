using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Marks a moving transform as a platform/support frame for networked characters.
    /// Characters standing on this support can be synchronized in support-local space.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Motion/Network Motion Support Anchor")]
    public sealed class NetworkMotionSupportAnchor : MonoBehaviour
    {
        [Header("Identity")]
        [Tooltip("Use a stable scene/hierarchy based support id. Disable for spawned platforms and assign an id manually.")]
        [SerializeField] private bool m_UseAutomaticSupportId = true;

        [Tooltip("Manual support id used when automatic ids are disabled. Must match on all peers.")]
        [SerializeField] private uint m_ManualSupportId = 0;

        [Tooltip("Optional salt to disambiguate duplicated scene platform hierarchies.")]
        [SerializeField] private string m_SupportIdSalt = string.Empty;

        private static readonly Dictionary<uint, NetworkMotionSupportAnchor> s_Registry = new(64);

        private uint m_RuntimeSupportId;
        private bool m_IsRegistered;

        public uint SupportId => m_RuntimeSupportId != 0 ? m_RuntimeSupportId : ResolveSupportId();

        public static bool TryResolve(uint supportId, out NetworkMotionSupportAnchor anchor)
        {
            anchor = null;
            if (supportId == 0) return false;

            if (!s_Registry.TryGetValue(supportId, out anchor) || anchor != null)
            {
                return anchor != null;
            }

            s_Registry.Remove(supportId);
            return false;
        }

        public static bool TryResolveFromHit(RaycastHit hit, out NetworkMotionSupportAnchor anchor)
        {
            anchor = null;
            Collider hitCollider = hit.collider;
            if (hitCollider == null) return false;

            Rigidbody rigidbody = hitCollider.attachedRigidbody;
            if (rigidbody != null &&
                TryResolveFromComponent(rigidbody, out anchor))
            {
                return true;
            }

            return TryResolveFromComponent(hitCollider, out anchor);
        }

        public void SetManualSupportId(uint supportId)
        {
            Unregister();
            m_UseAutomaticSupportId = false;
            m_ManualSupportId = supportId == 0 ? 1u : supportId;
            RefreshSupportId();
            Register();
        }

        public void RefreshSupportId()
        {
            uint previous = m_RuntimeSupportId;
            m_RuntimeSupportId = ResolveSupportId();
            if (!m_IsRegistered || previous == m_RuntimeSupportId) return;

            Unregister(previous);
            Register();
        }

        private void Awake()
        {
            RefreshSupportId();
        }

        private void OnEnable()
        {
            RefreshSupportId();
            Register();
        }

        private void OnDisable()
        {
            Unregister();
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!m_UseAutomaticSupportId && m_ManualSupportId == 0)
            {
                m_ManualSupportId = 1;
            }

            if (Application.isPlaying)
            {
                RefreshSupportId();
            }
        }
#endif

        private static bool TryResolveFromComponent(Component component, out NetworkMotionSupportAnchor anchor)
        {
            anchor = component != null
                ? component.GetComponentInParent<NetworkMotionSupportAnchor>()
                : null;

            return anchor != null && anchor.SupportId != 0;
        }

        private uint ResolveSupportId()
        {
            if (!m_UseAutomaticSupportId)
            {
                return m_ManualSupportId == 0 ? 1u : m_ManualSupportId;
            }

            string scenePath = gameObject.scene.path;
            string hierarchyPath = BuildHierarchyPath(transform);
            string key = $"{scenePath}|{hierarchyPath}|{m_SupportIdSalt}|{nameof(NetworkMotionSupportAnchor)}";
            uint hash = unchecked((uint)StableHashUtility.GetStableHash(key));
            if (hash != 0) return hash;

            int instanceId = transform.GetInstanceID();
            return (uint)(Mathf.Abs(instanceId) + 1);
        }

        private static string BuildHierarchyPath(Transform current)
        {
            if (current == null) return string.Empty;

            string path = current.name;
            Transform parent = current.parent;
            while (parent != null)
            {
                path = $"{parent.name}/{path}";
                parent = parent.parent;
            }

            return path;
        }

        private void Register()
        {
            uint supportId = SupportId;
            if (supportId == 0) return;

            if (s_Registry.TryGetValue(supportId, out NetworkMotionSupportAnchor existing) &&
                existing != null &&
                existing != this)
            {
                Debug.LogWarning(
                    $"[NetworkMotionSupportAnchor] Duplicate SupportId {supportId} for '{name}' and '{existing.name}'. " +
                    "The newest anchor will replace the previous registry entry.",
                    this);
            }

            s_Registry[supportId] = this;
            m_IsRegistered = true;
        }

        private void Unregister()
        {
            Unregister(m_RuntimeSupportId);
        }

        private void Unregister(uint supportId)
        {
            if (supportId != 0 &&
                s_Registry.TryGetValue(supportId, out NetworkMotionSupportAnchor existing) &&
                existing == this)
            {
                s_Registry.Remove(supportId);
            }

            m_IsRegistered = false;
        }
    }
}
