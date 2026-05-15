#if GC2_STATS && GC2_MELEE
using Arawn.GameCreator2.Networking.Melee;
using Arawn.GameCreator2.Networking.Stats;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;
using UnityEngine;

using GC2Attribute = GameCreator.Runtime.Stats.Attribute;

namespace Arawn.GameCreator2.Networking.Stats.Melee
{
    /// <summary>
    /// Transport-agnostic bridge that applies authoritative melee hit damage to GC2 Stats.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Stats/Melee Damage Bridge")]
    [DefaultExecutionOrder(-250)]
    public sealed class NetworkMeleeStatsDamageBridge : MonoBehaviour
    {
        [Header("Damage Target")]
        [Tooltip("Attribute reduced when an authoritative melee hit deals damage.")]
        [SerializeField] private GC2Attribute m_HealthAttribute;

        [Tooltip("Fallback attribute ID used when no attribute asset is assigned.")]
        [SerializeField] private string m_FallbackHealthAttributeId = "hp";

        [Header("Debug")]
        [SerializeField] private bool m_LogDamageApplication = false;

        private NetworkMeleeManager m_MeleeManager;

        private void OnEnable()
        {
            WireManager();
        }

        private void Start()
        {
            WireManager();
        }

        private void Update()
        {
            if (m_MeleeManager == null) WireManager();
        }

        private void OnDisable()
        {
            if (m_MeleeManager != null && ReferenceEquals(m_MeleeManager.TryApplyDamageFunc?.Target, this))
            {
                m_MeleeManager.TryApplyDamageFunc = null;
            }

            m_MeleeManager = null;
        }

        private void WireManager()
        {
            var manager = NetworkMeleeManager.Instance != null
                ? NetworkMeleeManager.Instance
                : FindFirstObjectByType<NetworkMeleeManager>();

            if (manager == null) return;
            if (manager.TryApplyDamageFunc != null && !ReferenceEquals(manager.TryApplyDamageFunc.Target, this))
            {
                return;
            }

            m_MeleeManager = manager;
            m_MeleeManager.TryApplyDamageFunc = TryApplyMeleeDamage;
        }

        private bool TryApplyMeleeDamage(NetworkMeleeHitRequest request, float damage)
        {
            if (damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage)) return true;

            NetworkStatsManager statsManager = NetworkStatsManager.Instance;
            if (statsManager == null || !statsManager.IsServer) return false;

            NetworkStatsController target = statsManager.GetController(request.TargetNetworkId);
            if (target == null) return false;

            int healthHash = ResolveHealthAttributeHash();
            if (healthHash == 0) return false;

            var statsRequest = new NetworkAttributeModifyRequest
            {
                RequestId = 0,
                ActorNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.AttackerNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                AttributeHash = healthHash,
                ModificationType = AttributeModificationType.Add,
                Value = -Mathf.Abs(damage),
                Source = StatModificationSource.Combat,
                SourceHash = request.SkillHash != 0 ? request.SkillHash : request.WeaponHash
            };

            NetworkAttributeModifyResponse response = target.ProcessAttributeModifyRequest(
                statsRequest,
                statsRequest.ActorNetworkId);

            if (m_LogDamageApplication)
            {
                Debug.Log(
                    $"[NetworkMeleeStatsDamageBridge] melee damage target={request.TargetNetworkId} " +
                    $"damage={damage:F2} applied={response.Authorized} reason={response.RejectionReason}",
                    this);
            }

            return response.Authorized;
        }

        private int ResolveHealthAttributeHash()
        {
            if (m_HealthAttribute != null) return m_HealthAttribute.ID.Hash;
            if (string.IsNullOrWhiteSpace(m_FallbackHealthAttributeId)) return 0;
            return new IdString(m_FallbackHealthAttributeId.Trim()).Hash;
        }
    }
}
#endif
