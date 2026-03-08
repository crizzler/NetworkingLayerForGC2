using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    public static partial class SecurityIntegration
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABILITIES MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate an Abilities module request.
        /// </summary>
        public static bool ValidateAbilitiesRequest(
            uint clientId,
            uint actorNetworkId,
            uint correlationId,
            string requestType)
        {
            var context = NetworkRequestContext.Create(actorNetworkId, correlationId);
            return ValidateModuleRequest(clientId, in context, "Abilities", requestType);
        }

        /// <summary>
        /// Validate an ability cast.
        /// </summary>
        public static bool ValidateAbilityCast(
            uint casterClientId,
            uint casterCharacterId,
            int abilityHash,
            Vector3 targetPosition,
            uint targetCharacterId)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Abilities",
                    nameof(ValidateAbilityCast),
                    casterClientId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            // Position validation (if targeting a position)
            if (targetCharacterId == 0 && targetPosition != Vector3.zero)
            {
                if (!manager.ValidatePosition(casterClientId, "Abilities", targetPosition, 1000f))
                {
                    return false;
                }
            }

            if (casterCharacterId == 0 || abilityHash == 0)
            {
                manager.RecordViolation(casterClientId, casterCharacterId,
                    SecurityViolationType.InvalidRequest, "Abilities",
                    $"Invalid ability cast payload caster={casterCharacterId}, ability={abilityHash}");
                return false;
            }

            if (targetCharacterId != 0 && targetCharacterId == casterCharacterId)
            {
                manager.RecordViolation(casterClientId, casterCharacterId,
                    SecurityViolationType.InvalidTarget, "Abilities",
                    $"Self-targeted cast rejected for ability={abilityHash}");
                return false;
            }

            return true;
        }
    }
}
