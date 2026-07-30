using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Demo helper that keeps GC2 combat target properties pointed at the nearest
    /// remote network character. This avoids stock melee combo conditions resolving
    /// Target Position as world origin when no target has been selected.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/PurrNet/Demo Combat Targeter")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Character))]
    [RequireComponent(typeof(NetworkCharacter))]
    public class PurrNetDemoCombatTargeter : MonoBehaviour
    {
        [SerializeField] private bool m_OnlyLocalOwner = true;
        [SerializeField] private float m_TargetRadius = 20f;
        [SerializeField] private float m_RefreshInterval = 0.25f;
        [SerializeField] private bool m_ClearWhenNoTarget = false;
        [SerializeField] private bool m_LogTargetChanges = false;

        private Character m_Character;
        private NetworkCharacter m_NetworkCharacter;
        private float m_NextRefreshTime;

        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }

        private void OnEnable()
        {
            m_NextRefreshTime = 0f;
        }

        private void Update()
        {
            if (m_Character == null || m_NetworkCharacter == null) return;
            if (m_OnlyLocalOwner && !m_NetworkCharacter.IsOwnerInstance) return;

            if (Time.unscaledTime < m_NextRefreshTime) return;
            m_NextRefreshTime = Time.unscaledTime + Mathf.Max(0.05f, m_RefreshInterval);

            RefreshTarget();
        }

        private void RefreshTarget()
        {
            GameObject current = m_Character.Combat.Targets.Primary;
            if (IsUsableTarget(current)) return;

            NetworkCharacter target = FindNearestTarget();
            GameObject nextTarget = target != null ? target.gameObject : null;

            if (nextTarget == null && !m_ClearWhenNoTarget) return;
            if (current == nextTarget) return;

            m_Character.Combat.Targets.Primary = nextTarget;

            if (m_LogTargetChanges)
            {
                Debug.Log(
                    $"[PurrNetDemoCombatTargeter] {name} target={(nextTarget != null ? nextTarget.name : "none")}",
                    this);
            }
        }

        private bool IsUsableTarget(GameObject target)
        {
            if (target == null) return false;
            if (target == gameObject) return false;
            if (target.transform.IsChildOf(transform)) return false;

            NetworkCharacter targetNetworkCharacter = target.GetComponentInParent<NetworkCharacter>();
            if (targetNetworkCharacter == null) return false;
            if (targetNetworkCharacter == m_NetworkCharacter) return false;
            if (targetNetworkCharacter.NetworkId != 0 &&
                targetNetworkCharacter.NetworkId == m_NetworkCharacter.NetworkId)
            {
                return false;
            }

            if (m_TargetRadius <= 0f) return true;

            float sqrDistance = (targetNetworkCharacter.transform.position - transform.position).sqrMagnitude;
            return sqrDistance <= m_TargetRadius * m_TargetRadius;
        }

        private NetworkCharacter FindNearestTarget()
        {
            NetworkCharacter[] candidates = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            NetworkCharacter nearest = null;
            float nearestSqrDistance = float.PositiveInfinity;
            float maxSqrDistance = m_TargetRadius > 0f
                ? m_TargetRadius * m_TargetRadius
                : float.PositiveInfinity;

            for (int i = 0; i < candidates.Length; i++)
            {
                NetworkCharacter candidate = candidates[i];
                if (candidate == null) continue;
                if (candidate == m_NetworkCharacter) continue;
                if (candidate.gameObject == gameObject) continue;
                if (candidate.NetworkId != 0 && candidate.NetworkId == m_NetworkCharacter.NetworkId) continue;
                if (candidate.GetComponent<Character>() == null) continue;

                float sqrDistance = (candidate.transform.position - transform.position).sqrMagnitude;
                if (sqrDistance > maxSqrDistance) continue;
                if (sqrDistance >= nearestSqrDistance) continue;

                nearest = candidate;
                nearestSqrDistance = sqrDistance;
            }

            return nearest;
        }
    }
}
