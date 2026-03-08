using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    public static partial class SecurityIntegration
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOOTER MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate a Shooter module request.
        /// </summary>
        public static bool ValidateShooterRequest(
            uint clientId,
            uint actorNetworkId,
            uint correlationId,
            string requestType)
        {
            var context = NetworkRequestContext.Create(actorNetworkId, correlationId);
            return ValidateModuleRequest(clientId, in context, "Shooter", requestType);
        }

        /// <summary>
        /// Validate a shot/projectile.
        /// </summary>
        public static bool ValidateShot(
            uint shooterClientId,
            uint shooterCharacterId,
            int weaponHash,
            Vector3 origin,
            Vector3 direction,
            float chargeRatio)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Shooter",
                    nameof(ValidateShot),
                    shooterClientId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            // Position validation
            if (!manager.ValidatePosition(shooterClientId, "Shooter", origin, 1000f))
            {
                return false;
            }

            // Direction validation (should be normalized)
            if (direction.sqrMagnitude < 0.5f || direction.sqrMagnitude > 1.5f)
            {
                manager.RecordViolation(shooterClientId, shooterCharacterId,
                    SecurityViolationType.OutOfBoundsValue, "Shooter",
                    $"Invalid shot direction magnitude: {direction.magnitude}");
                return false;
            }

            // Charge ratio validation
            if (!manager.ValidateValueRange(shooterClientId, "Shooter", "chargeRatio",
                    chargeRatio, 0f, 1f))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validate a projectile hit.
        /// </summary>
        public static bool ValidateProjectileHit(
            uint shooterClientId,
            uint shooterCharacterId,
            uint victimCharacterId,
            int weaponHash,
            float damage,
            Vector3 hitPoint)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Shooter",
                    nameof(ValidateProjectileHit),
                    shooterClientId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            // Position validation
            if (!manager.ValidatePosition(shooterClientId, "Shooter", hitPoint, 10000f))
            {
                return false;
            }

            // Damage bounds check
            float maxDamage = ResolveConfiguredMaxValue(
                manager,
                "Shooter",
                DEFAULT_MAX_SHOOTER_DAMAGE_PER_REQUEST);

            if (!manager.ValidateValueRange(shooterClientId, "Shooter", "damage", damage, 0f, maxDamage))
            {
                return false;
            }

            return true;
        }
    }
}
