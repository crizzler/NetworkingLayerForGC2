using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    public static partial class SecurityIntegration
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MELEE MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate a Melee module combat request.
        /// </summary>
        public static bool ValidateMeleeRequest(
            uint clientId,
            uint actorNetworkId,
            uint correlationId,
            string requestType)
        {
            var context = NetworkRequestContext.Create(actorNetworkId, correlationId);
            return ValidateModuleRequest(clientId, in context, "Melee", requestType);
        }

        /// <summary>
        /// Validate a melee hit.
        /// </summary>
        public static bool ValidateMeleeHit(
            uint attackerClientId,
            uint attackerCharacterId,
            uint victimCharacterId,
            int skillHash,
            float damage,
            Vector3 hitPoint)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Melee",
                    nameof(ValidateMeleeHit),
                    attackerClientId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            // Position validation
            if (!manager.ValidatePosition(attackerClientId, "Melee", hitPoint, 1000f))
            {
                return false;
            }

            // Damage bounds check
            float maxDamage = ResolveConfiguredMaxValue(
                manager,
                "Melee",
                DEFAULT_MAX_MELEE_DAMAGE_PER_REQUEST);

            if (!manager.ValidateValueRange(attackerClientId, "Melee", "damage", damage, 0f, maxDamage))
            {
                return false;
            }

            // Basic target sanity
            if (attackerCharacterId == 0 || victimCharacterId == 0 || attackerCharacterId == victimCharacterId)
            {
                manager.RecordViolation(attackerClientId, attackerCharacterId,
                    SecurityViolationType.InvalidTarget, "Melee",
                    $"Invalid melee target pair attacker={attackerCharacterId}, victim={victimCharacterId}");
                return false;
            }

            // Skill hash must be present for deterministic validation.
            if (skillHash == 0)
            {
                manager.RecordViolation(attackerClientId, attackerCharacterId,
                    SecurityViolationType.InvalidRequest, "Melee",
                    "Missing skill hash in melee hit validation");
                return false;
            }

            if (float.IsNaN(damage) || float.IsInfinity(damage))
            {
                manager.RecordViolation(attackerClientId, attackerCharacterId,
                    SecurityViolationType.OutOfBoundsValue, "Melee",
                    $"Invalid damage value: {damage}");
                return false;
            }

            return true;
        }
    }
}
