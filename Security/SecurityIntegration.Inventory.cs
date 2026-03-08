using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    public static partial class SecurityIntegration
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVENTORY MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Validate an Inventory module request.
        /// </summary>
        public static bool ValidateInventoryRequest(
            uint clientId,
            uint actorNetworkId,
            uint correlationId,
            string requestType)
        {
            var context = NetworkRequestContext.Create(actorNetworkId, correlationId);
            return ValidateModuleRequest(clientId, in context, "Inventory", requestType);
        }

        /// <summary>
        /// Validate an item transfer (add/remove).
        /// </summary>
        public static bool ValidateItemTransfer(
            uint clientId,
            uint characterNetworkId,
            int itemHash,
            int quantity,
            bool isAdd)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    "Inventory",
                    nameof(ValidateItemTransfer),
                    clientId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            // Check for negative quantities
            if (quantity < 0)
            {
                manager.RecordViolation(clientId, characterNetworkId,
                    SecurityViolationType.OutOfBoundsValue, "Inventory",
                    $"Negative quantity: {quantity}");
                return false;
            }

            // Check for excessively large quantities
            int maxItemsPerRequest = ResolveConfiguredMaxQuantity(
                manager,
                "Inventory",
                DEFAULT_MAX_INVENTORY_QUANTITY_PER_REQUEST);

            if (quantity > maxItemsPerRequest)
            {
                manager.RecordViolation(clientId, characterNetworkId,
                    SecurityViolationType.OutOfBoundsValue, "Inventory",
                    $"Excessive quantity: {quantity} (max: {maxItemsPerRequest})");
                return false;
            }

            return true;
        }
    }
}
