using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    public static partial class SecurityIntegration
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CORE MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate a Core module request (movement, position sync).
        /// </summary>
        public static bool ValidateCoreRequest(
            uint clientId,
            uint actorNetworkId,
            uint correlationId,
            string requestType)
        {
            var context = NetworkRequestContext.Create(actorNetworkId, correlationId);
            return ValidateModuleRequest(clientId, in context, "Core", requestType);
        }

        /// <summary>
        /// Validate a Core module position update.
        /// </summary>
        public static bool ValidateCorePositionUpdate(
            uint clientId,
            uint characterNetworkId,
            Vector3 position,
            Vector3 velocity,
            float maxSpeed = 50f)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Core",
                    nameof(ValidateCorePositionUpdate),
                    clientId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            // Position validation
            if (!manager.ValidatePosition(clientId, "Core", position))
            {
                return false;
            }

            // Velocity magnitude check
            if (!manager.ValidateValueRange(clientId, "Core", "velocity", velocity.magnitude, 0f, maxSpeed))
            {
                return false;
            }

            return true;
        }
    }
}
