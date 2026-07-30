#if GC2_STATS && GC2_SHOOTER
using Arawn.GameCreator2.Networking.Shooter;
using Arawn.GameCreator2.Networking.Stats;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;
using UnityEngine;

using GC2Attribute = GameCreator.Runtime.Stats.Attribute;

namespace Arawn.GameCreator2.Networking.Stats.Shooter
{
    /// <summary>
    /// Transport-agnostic bridge that applies authoritative Shooter damage to GC2 Stats.
    /// It handles health only; NetworkShooterManager always processes the target reaction separately.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Stats/Shooter Damage Bridge")]
    [DefaultExecutionOrder(-250)]
    public sealed class NetworkShooterStatsDamageBridge : MonoBehaviour
    {
        [Header("Damage Target")]
        [Tooltip("Attribute reduced when an authoritative Shooter hit deals damage.")]
        [SerializeField] private GC2Attribute m_HealthAttribute;

        [Tooltip("Fallback attribute ID used when no attribute asset is assigned.")]
        [SerializeField] private string m_FallbackHealthAttributeId = "hp";

        [Header("Debug")]
        [SerializeField] private bool m_LogDamageApplication;

        private NetworkShooterManager m_ShooterManager;

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
            if (m_ShooterManager == null) WireManager();
        }

        private void OnDisable()
        {
            if (m_ShooterManager != null &&
                ReferenceEquals(m_ShooterManager.TryApplyDamageFunc?.Target, this))
            {
                m_ShooterManager.TryApplyDamageFunc = null;
            }

            m_ShooterManager = null;
        }

        private void WireManager()
        {
            NetworkShooterManager manager = NetworkShooterManager.Instance != null
                ? NetworkShooterManager.Instance
                : FindFirstObjectByType<NetworkShooterManager>();

            if (manager == null) return;
            if (manager.TryApplyDamageFunc != null &&
                !ReferenceEquals(manager.TryApplyDamageFunc.Target, this))
            {
                return;
            }

            m_ShooterManager = manager;
            m_ShooterManager.TryApplyDamageFunc = TryApplyShooterDamage;
        }

        private bool TryApplyShooterDamage(NetworkShooterHitRequest request, float damage)
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
                ActorNetworkId = request.ActorNetworkId != 0
                    ? request.ActorNetworkId
                    : request.ShooterNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                AttributeHash = healthHash,
                ModificationType = AttributeModificationType.Add,
                Value = -Mathf.Abs(damage),
                Source = StatModificationSource.Combat,
                SourceHash = request.WeaponHash
            };

            NetworkAttributeModifyResponse response = target.ProcessAttributeModifyRequest(
                statsRequest,
                statsRequest.ActorNetworkId);

            if (m_LogDamageApplication)
            {
                Debug.Log(
                    $"[NetworkShooterStatsDamageBridge] shooter damage target={request.TargetNetworkId} " +
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
