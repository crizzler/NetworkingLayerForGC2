using System;
using UnityEngine;
using Arawn.GameCreator2.Networking;

namespace Arawn.GameCreator2.Networking.Security
{
    /// <summary>
    /// Static helper to integrate NetworkSecurityManager with GC2 networking controllers.
    /// This class provides validation wrappers that can be called from controllers
    /// to add rate limiting, sequence validation, and state tracking.
    /// </summary>
    public static class SecurityIntegration
    {
        private static INetworkOwnershipResolver s_OwnershipResolver = new NetworkOwnershipResolver();

        private static bool IsAuthoritativeServerContext()
        {
            var bridge = NetworkTransportBridge.Active;
            return bridge != null && bridge.IsServer;
        }

        private static bool ShouldFailClosedNoSecurityManager(NetworkSecurityManager manager, string module, string requestType)
        {
            if (!IsAuthoritativeServerContext())
            {
                return false;
            }

            if (manager == null)
            {
                Debug.LogError(
                    $"[SecurityIntegration] Rejecting {module}/{requestType}: " +
                    "NetworkSecurityManager is missing while running in server context.");
                return true;
            }

            if (!manager.IsServer)
            {
                Debug.LogError(
                    $"[SecurityIntegration] Rejecting {module}/{requestType}: " +
                    "NetworkSecurityManager is not initialized for server mode.");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Shared ownership resolver used by all gameplay modules.
        /// </summary>
        public static INetworkOwnershipResolver OwnershipResolver
        {
            get => s_OwnershipResolver ??= new NetworkOwnershipResolver();
            set => s_OwnershipResolver = value;
        }

        public static void RegisterEntityOwner(uint entityNetworkId, uint ownerClientId)
        {
            OwnershipResolver.RegisterEntityOwner(entityNetworkId, ownerClientId);
        }

        public static void RegisterEntityActor(uint entityNetworkId, uint actorNetworkId)
        {
            OwnershipResolver.RegisterEntityActor(entityNetworkId, actorNetworkId);
        }

        public static void UnregisterEntity(uint entityNetworkId)
        {
            OwnershipResolver.UnregisterEntity(entityNetworkId);
        }

        /// <summary>
        /// Validates the shared v2 request context (rate limit + sequence + strict ownership).
        /// </summary>
        public static bool ValidateModuleRequest(
            uint senderClientId,
            in NetworkRequestContext context,
            string module,
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(manager, module, requestType))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            if (context.ActorNetworkId == 0 || context.CorrelationId == 0)
            {
                manager.RecordViolation(
                    senderClientId,
                    context.ActorNetworkId,
                    SecurityViolationType.ProtocolMismatch,
                    module,
                    $"Missing protocol v2 context for {requestType}");
                return false;
            }

            if (!manager.CheckRateLimit(senderClientId, module))
            {
                return false;
            }

            ushort sequence = NetworkCorrelation.ExtractRequestId(context.CorrelationId);
            if (!manager.ValidateSequence(senderClientId, sequence, module))
            {
                return false;
            }

            return ValidateOwnership(senderClientId, context.ActorNetworkId, module);
        }

        /// <summary>
        /// Strictly validates sender ownership of the actor network entity.
        /// </summary>
        public static bool ValidateOwnership(uint senderClientId, uint actorNetworkId, string module)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(manager, module, "ValidateOwnership"))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            if (senderClientId == 0 || actorNetworkId == 0)
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Invalid ownership context sender={senderClientId}, actor={actorNetworkId}");
                return false;
            }

            if (!OwnershipResolver.TryResolveOwnerClientId(actorNetworkId, out uint ownerClientId))
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Unresolved ownership for actor {actorNetworkId}");
                return false;
            }

            if (ownerClientId != senderClientId)
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.UnauthorizedAction,
                    module,
                    $"Ownership mismatch actor={actorNetworkId}, expected={ownerClientId}, sender={senderClientId}");
                return false;
            }

            return true;
        }

        public static bool ValidateOwnership(ulong senderClientId, uint actorNetworkId, string module)
        {
            if (senderClientId > uint.MaxValue)
            {
                var manager = NetworkSecurityManager.Instance;
                manager?.RecordViolation(
                    0,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Sender id {senderClientId} exceeds uint range");
                return false;
            }

            return ValidateOwnership((uint)senderClientId, actorNetworkId, module);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CORE MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a Core module request (movement, position sync).
        /// </summary>
        public static bool ValidateCoreRequest(
            uint clientId, 
            uint characterNetworkId,
            ushort requestId, 
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return true;
            
            // Rate limit check
            if (!manager.CheckRateLimit(clientId, "Core"))
            {
                return false;
            }
            
            // Sequence validation
            if (!manager.ValidateSequence(clientId, requestId, "Core"))
            {
                return false;
            }
            
            return true;
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATS MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a Stats module modification request.
        /// </summary>
        public static bool ValidateStatsRequest(
            uint clientId,
            uint characterNetworkId,
            ushort requestId,
            string requestType,
            int statHash,
            float value)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return true;
            
            // Rate limit check
            if (!manager.CheckRateLimit(clientId, "Stats"))
            {
                return false;
            }
            
            // Sequence validation
            if (!manager.ValidateSequence(clientId, requestId, "Stats"))
            {
                return false;
            }
            
            // Value bounds check (configurable max stat change)
            var config = manager.GetConfigForModule("Stats");
            float maxChange = config.MaxValueChangePerRequest;
            
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
                Debug.Log($"[SecurityIntegration] Recording stat state: char={characterNetworkId}, stat={statHash}, base={baseValue}, computed={computedValue}");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVENTORY MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate an Inventory module request.
        /// </summary>
        public static bool ValidateInventoryRequest(
            uint clientId,
            uint characterNetworkId,
            ushort requestId,
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return true;
            
            // Rate limit check
            if (!manager.CheckRateLimit(clientId, "Inventory"))
            {
                return false;
            }
            
            // Sequence validation
            if (!manager.ValidateSequence(clientId, requestId, "Inventory"))
            {
                return false;
            }
            
            return true;
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
            var config = manager.GetConfigForModule("Inventory");
            int maxItemsPerRequest = 99; // Configurable
            
            if (quantity > maxItemsPerRequest)
            {
                manager.RecordViolation(clientId, characterNetworkId,
                    SecurityViolationType.OutOfBoundsValue, "Inventory",
                    $"Excessive quantity: {quantity} (max: {maxItemsPerRequest})");
                return false;
            }
            
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MELEE MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a Melee module combat request.
        /// </summary>
        public static bool ValidateMeleeRequest(
            uint clientId,
            uint characterNetworkId,
            ushort requestId,
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return true;
            
            // Rate limit check (combat actions may need tighter limits)
            if (!manager.CheckRateLimit(clientId, "Melee"))
            {
                return false;
            }
            
            // Sequence validation
            if (!manager.ValidateSequence(clientId, requestId, "Melee"))
            {
                return false;
            }
            
            return true;
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
            if (manager == null || !manager.IsServer) return true;
            
            // Position validation
            if (!manager.ValidatePosition(attackerClientId, "Melee", hitPoint, 1000f))
            {
                return false;
            }
            
            // Damage bounds check
            var config = manager.GetConfigForModule("Melee");
            float maxDamage = 10000f; // Should be configurable
            
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOOTER MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a Shooter module request.
        /// </summary>
        public static bool ValidateShooterRequest(
            uint clientId,
            uint characterNetworkId,
            ushort requestId,
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return true;
            
            // Rate limit check
            if (!manager.CheckRateLimit(clientId, "Shooter"))
            {
                return false;
            }
            
            // Sequence validation
            if (!manager.ValidateSequence(clientId, requestId, "Shooter"))
            {
                return false;
            }
            
            return true;
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
            if (manager == null || !manager.IsServer) return true;
            
            // Position validation
            if (!manager.ValidatePosition(shooterClientId, "Shooter", hitPoint, 10000f))
            {
                return false;
            }
            
            // Damage bounds check
            float maxDamage = 100000f; // Should be configurable
            
            if (!manager.ValidateValueRange(shooterClientId, "Shooter", "damage", damage, 0f, maxDamage))
            {
                return false;
            }
            
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABILITIES MODULE INTEGRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate an Abilities module request.
        /// </summary>
        public static bool ValidateAbilitiesRequest(
            uint clientId,
            uint characterNetworkId,
            ushort requestId,
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return true;
            
            // Rate limit check
            if (!manager.CheckRateLimit(clientId, "Abilities"))
            {
                return false;
            }
            
            // Sequence validation
            if (!manager.ValidateSequence(clientId, requestId, "Abilities"))
            {
                return false;
            }
            
            return true;
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // GENERIC VIOLATION RECORDING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Record a custom security violation.
        /// </summary>
        public static void RecordViolation(
            uint clientId,
            uint characterNetworkId,
            SecurityViolationType type,
            string module,
            string details)
        {
            var manager = NetworkSecurityManager.Instance;
            if (manager == null || !manager.IsServer) return;
            
            manager.RecordViolation(clientId, characterNetworkId, type, module, details);
        }
    }
}
