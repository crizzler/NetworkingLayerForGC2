using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Selects the nearest living, authenticated PlayerOwned character for an authoritative NPC.
    /// Registry iteration is allocation-free after startup and NetworkId breaks equal-distance ties.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Network NPC Target Selector")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Character))]
    [RequireComponent(typeof(NetworkCharacter))]
    public sealed class NetworkNpcTargetSelector : MonoBehaviour
    {
        private const int FollowPriority = 1;

        [Min(0.02f)]
        [SerializeField] private float m_RefreshInterval = 0.25f;
        [Min(0f)]
        [SerializeField] private float m_MaxTargetDistance;

        [Header("GC2 Follow")]
        [SerializeField] private bool m_FollowSelectedTarget = true;
        [Min(0f)]
        [SerializeField] private float m_MinFollowDistance = 4f;
        [Min(0f)]
        [SerializeField] private float m_MaxFollowDistance = 5f;

        private readonly List<NetworkCharacter> m_Candidates = new(64);
        private NetworkCharacter m_NetworkCharacter;
        private Character m_Character;
        private NetworkCharacter m_SelectedTarget;
        private float m_NextRefreshTime;
        private bool m_RetainCurrentTarget;

        public NetworkCharacter SelectedTarget => m_SelectedTarget;
        public bool IsTargetLocked => m_RetainCurrentTarget;

        private void Awake()
        {
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            m_Character = GetComponent<Character>();
        }

        private void OnEnable()
        {
            if (m_NetworkCharacter == null) m_NetworkCharacter = GetComponent<NetworkCharacter>();
            if (m_Character == null) m_Character = GetComponent<Character>();
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleReset -= ClearTarget;
                m_NetworkCharacter.OnRoleReset += ClearTarget;
            }

            m_NextRefreshTime = 0f;
        }

        private void OnDisable()
        {
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleReset -= ClearTarget;
            }

            ClearTarget();
        }

        private void Update()
        {
            if (Time.unscaledTime < m_NextRefreshTime) return;
            m_NextRefreshTime = Time.unscaledTime + Mathf.Max(0.02f, m_RefreshInterval);

            if (!CanSelectTargets())
            {
                ClearTarget();
                return;
            }

            RefreshTarget();
        }

        public void RefreshTarget()
        {
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge == null || !bridge.IsServer)
            {
                ClearTarget();
                return;
            }

            // Combat integrations can retain an authenticated target for the duration of an
            // attack/counter window. This prevents nearest-target polling from changing the
            // request contract mid-telegraph. Death, disconnect, deactivation, and an authored
            // maximum-distance violation still break the lock immediately.
            if (m_RetainCurrentTarget && IsRetainedTargetValid(m_SelectedTarget)) return;

            bridge.CopyRegisteredCharacters(m_Candidates);
            NetworkCharacter best = null;
            float bestDistanceSquared = m_MaxTargetDistance > 0f
                ? m_MaxTargetDistance * m_MaxTargetDistance
                : float.PositiveInfinity;
            uint bestNetworkId = uint.MaxValue;
            Vector3 origin = transform.position;

            for (int i = 0; i < m_Candidates.Count; i++)
            {
                NetworkCharacter candidate = m_Candidates[i];
                if (!IsValidTarget(candidate)) continue;

                float distanceSquared = (candidate.transform.position - origin).sqrMagnitude;
                uint candidateId = candidate.NetworkId;
                if (distanceSquared > bestDistanceSquared) continue;
                if (Mathf.Approximately(distanceSquared, bestDistanceSquared) &&
                    candidateId >= bestNetworkId)
                {
                    continue;
                }

                best = candidate;
                bestDistanceSquared = distanceSquared;
                bestNetworkId = candidateId;
            }

            SetTarget(best);
        }

        public void SetTargetLocked(bool state)
        {
            if (m_RetainCurrentTarget == state) return;
            m_RetainCurrentTarget = state;
            if (!state) m_NextRefreshTime = 0f;
        }

        private bool CanSelectTargets()
        {
            return m_NetworkCharacter != null &&
                m_Character != null &&
                !m_Character.IsDead &&
                m_NetworkCharacter.IsServerAuthoritativeNPC &&
                m_NetworkCharacter.HasSimulationAuthority;
        }

        private bool IsValidTarget(NetworkCharacter candidate)
        {
            return candidate != null &&
                candidate != m_NetworkCharacter &&
                candidate.NetworkId != 0 &&
                candidate.IsPlayerOwnedActor &&
                candidate.HasAuthenticatedPlayerOwner &&
                candidate.Character != null &&
                !candidate.Character.IsDead &&
                candidate.gameObject.activeInHierarchy;
        }

        private bool IsRetainedTargetValid(NetworkCharacter candidate)
        {
            if (!IsValidTarget(candidate)) return false;
            if (m_MaxTargetDistance <= 0f) return true;

            float maximumDistanceSquared = m_MaxTargetDistance * m_MaxTargetDistance;
            return (candidate.transform.position - transform.position).sqrMagnitude <=
                   maximumDistanceSquared;
        }

        private void SetTarget(NetworkCharacter target)
        {
            if (m_SelectedTarget == target) return;

            if (m_FollowSelectedTarget && m_Character != null)
            {
                m_Character.Motion.StopFollowingTarget(FollowPriority);
            }

            m_SelectedTarget = target;
            if (m_Character == null) return;

            m_Character.Combat.Targets.Primary = target != null ? target.gameObject : null;
            if (target != null && m_FollowSelectedTarget)
            {
                m_Character.Motion.StartFollowingTarget(
                    target.transform,
                    Mathf.Min(m_MinFollowDistance, m_MaxFollowDistance),
                    Mathf.Max(m_MinFollowDistance, m_MaxFollowDistance),
                    FollowPriority);
            }
        }

        private void ClearTarget()
        {
            m_RetainCurrentTarget = false;
            SetTarget(null);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            m_RefreshInterval = Mathf.Max(0.02f, m_RefreshInterval);
            m_MaxTargetDistance = Mathf.Max(0f, m_MaxTargetDistance);
            m_MinFollowDistance = Mathf.Max(0f, m_MinFollowDistance);
            m_MaxFollowDistance = Mathf.Max(m_MinFollowDistance, m_MaxFollowDistance);
        }
#endif
    }
}
