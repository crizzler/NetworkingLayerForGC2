using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Keeps explicitly selected GC2 AI roots disabled until this peer has authoritative NPC
    /// simulation. Deactivation immediately cancels running Trigger instruction lists through
    /// their normal OnDisable lifecycle.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Network Character Authority Gate")]
    [DefaultExecutionOrder(-10000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkCharacter))]
    public sealed class NetworkCharacterAuthorityGate : MonoBehaviour
    {
        [Tooltip("The exact child roots containing authority-only GC2 Triggers and AI logic. " +
                 "Do not assign the character root that contains this gate.")]
        [SerializeField] private GameObject[] m_AuthorityOnlyRoots = Array.Empty<GameObject>();

        [SerializeField] private bool m_LogAuthorityChanges;

        private NetworkCharacter m_NetworkCharacter;
        private bool[] m_AuthoredActiveStates = Array.Empty<bool>();
        private bool m_AuthorityEnabled;

        public GameObject[] AuthorityOnlyRoots => m_AuthorityOnlyRoots;
        public bool AuthorityEnabled => m_AuthorityEnabled;

        private void Awake()
        {
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            CaptureAuthoredStates();
            ApplyAuthority(false);
        }

        private void OnEnable()
        {
            if (m_NetworkCharacter == null)
            {
                m_NetworkCharacter = GetComponent<NetworkCharacter>();
            }

            if (m_NetworkCharacter == null) return;
            m_NetworkCharacter.OnRoleAssigned -= OnRoleAssigned;
            m_NetworkCharacter.OnRoleAssigned += OnRoleAssigned;
            m_NetworkCharacter.OnRoleReset -= OnRoleReset;
            m_NetworkCharacter.OnRoleReset += OnRoleReset;

            RefreshAuthority();
        }

        private void OnDisable()
        {
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleAssigned -= OnRoleAssigned;
                m_NetworkCharacter.OnRoleReset -= OnRoleReset;
            }

            ApplyAuthority(false);
        }

        private void OnRoleAssigned(NetworkCharacter.NetworkRole role)
        {
            RefreshAuthority();
        }

        private void OnRoleReset()
        {
            ApplyAuthority(false);
        }

        public void RefreshAuthority()
        {
            bool allow = m_NetworkCharacter != null &&
                m_NetworkCharacter.IsServerAuthoritativeNPC &&
                m_NetworkCharacter.HasSimulationAuthority;
            ApplyAuthority(allow);
        }

        private void CaptureAuthoredStates()
        {
            int count = m_AuthorityOnlyRoots?.Length ?? 0;
            m_AuthoredActiveStates = new bool[count];
            for (int i = 0; i < count; i++)
            {
                GameObject root = m_AuthorityOnlyRoots[i];
                m_AuthoredActiveStates[i] = root != null && root.activeSelf;
            }
        }

        private void ApplyAuthority(bool allow)
        {
            m_AuthorityEnabled = allow;
            int count = m_AuthorityOnlyRoots?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                GameObject root = m_AuthorityOnlyRoots[i];
                if (root == null || root == gameObject) continue;
                if (transform.IsChildOf(root.transform)) continue;

                bool active = allow &&
                    i < m_AuthoredActiveStates.Length &&
                    m_AuthoredActiveStates[i];
                if (root.activeSelf != active)
                {
                    root.SetActive(active);
                }
            }

            if (m_LogAuthorityChanges)
            {
                Debug.Log(
                    $"[NetworkCharacterAuthorityGate] '{name}' authority={allow} " +
                    $"role={m_NetworkCharacter?.Role}",
                    this);
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (m_AuthorityOnlyRoots == null)
            {
                m_AuthorityOnlyRoots = Array.Empty<GameObject>();
            }
        }
#endif
    }
}
