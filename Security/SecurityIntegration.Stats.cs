using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    public static partial class SecurityIntegration
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATS MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate a Stats module modification request.
        /// </summary>
        public static bool ValidateStatsRequest(
            uint clientId,
            uint actorNetworkId,
            uint correlationId,
            string requestType,
            int statHash,
            float value)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Stats",
                    requestType,
                    clientId,
                    actorNetworkId))
            {
                return false;
            }

            var context = NetworkRequestContext.Create(actorNetworkId, correlationId);
            if (!ValidateModuleRequest(clientId, in context, "Stats", requestType))
            {
                return false;
            }

            if (manager == null || !manager.IsServer)
            {
                return true;
            }

            // Value bounds check (configurable max stat change)
            var config = manager.GetConfigForModule("Stats");
            float maxChange = config != null ? config.MaxValueChangePerRequest : 0f;

            if (maxChange > 0 && !manager.ValidateValueRange(clientId, "Stats", $"statChange_{statHash}",
                    Mathf.Abs(value), 0f, maxChange))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Record a Stats state for validation.
        /// </summary>
        public static void RecordStatsState(
            uint characterNetworkId,
            int statHash,
            float baseValue,
            float computedValue)
        {
            // State recording is handled by the NetworkSecurityManager's state validators
            // This method is a convenience wrapper that can be extended
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return;

            // Log state change for debugging
            if (manager.GlobalConfig.EnableStateValidation)
            {
                Debug.Log(
                    $"[SecurityIntegration] Recording stat state: char={characterNetworkId}, stat={statHash}, base={baseValue}, computed={computedValue}");
            }
        }
    }
}
