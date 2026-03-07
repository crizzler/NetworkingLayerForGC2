using System;
using System.Collections.Generic;
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
        private static readonly HashSet<string> s_ServerModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_MissingSecurityManagerWarningLogged;

        private static bool IsAuthoritativeServerContext()
        {
            var bridge = NetworkTransportBridge.Active;
            if (bridge != null && bridge.IsServer)
            {
                return true;
            }

            if (s_ServerModules.Count > 0)
            {
                return true;
            }

            var manager = NetworkSecurityManager.Instance;
            return manager != null && manager.IsServer;
        }

        /// <summary>
        /// Registers whether a module is currently running in server-authoritative mode.
        /// Modules should call this during init/teardown to avoid fail-open security when
        /// requests arrive before transport bridge context is available.
        /// </summary>
        public static void SetModuleServerContext(string module, bool isServer)
        {
            if (string.IsNullOrWhiteSpace(module)) return;

            if (isServer)
            {
                s_ServerModules.Add(module);
            }
            else
            {
                s_ServerModules.Remove(module);
            }
        }

        /// <summary>
        /// Clears module server context registrations.
        /// Primarily intended for tests.
        /// </summary>
        public static void ClearModuleServerContexts()
        {
            s_ServerModules.Clear();
            s_MissingSecurityManagerWarningLogged = false;
        }

        /// <summary>
        /// Ensures the security manager exists and is initialized with the correct role.
        /// </summary>
        public static bool EnsureSecurityManagerInitialized(bool isServer, Func<float> getServerTime = null)
        {
            NetworkSecurityManager manager = NetworkSecurityManager.Instance;
            if (manager == null)
            {
                if (isServer && !s_MissingSecurityManagerWarningLogged)
                {
                    s_MissingSecurityManagerWarningLogged = true;
                    Debug.LogWarning(
                        "[SecurityIntegration] Server mode is active but no NetworkSecurityManager instance exists. " +
                        "Add one to the scene or use the Setup Wizard security option.");
                }

                return !isServer;
            }

            Func<float> timeProvider = getServerTime ?? (() => Time.time);
            if (!manager.IsInitialized || manager.IsServer != isServer)
            {
                manager.Initialize(isServer, timeProvider);
            }

            return true;
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
        /// Returns true when protocol v2 context is malformed or does not match the actor.
        /// </summary>
        public static bool IsProtocolContextMismatch(uint actorNetworkId, uint correlationId)
        {
            if (actorNetworkId == 0 || correlationId == 0)
            {
                return true;
            }

            if (NetworkCorrelation.ExtractRequestId(correlationId) == 0)
            {
                return true;
            }

            return !NetworkCorrelation.MatchesActor(correlationId, actorNetworkId);
        }

        /// <summary>
        /// Returns true when protocol v2 context is malformed or does not match the actor.
        /// </summary>
        public static bool IsProtocolContextMismatch(in NetworkRequestContext context)
        {
            return IsProtocolContextMismatch(context.ActorNetworkId, context.CorrelationId);
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

            if (IsProtocolContextMismatch(in context))
            {
                ushort actorSegment = NetworkCorrelation.ExtractActorSegment(context.CorrelationId);
                ushort requestSegment = NetworkCorrelation.ExtractRequestId(context.CorrelationId);
                manager.RecordViolation(
                    senderClientId,
                    context.ActorNetworkId,
                    SecurityViolationType.ProtocolMismatch,
                    module,
                    $"Invalid protocol v2 context for {requestType}: " +
                    $"actor={context.ActorNetworkId}, correlation={context.CorrelationId}, " +
                    $"corrActorSegment={actorSegment}, corrRequestSegment={requestSegment}");
                return false;
            }

            if (!manager.CheckRateLimit(senderClientId, module))
            {
                return false;
            }

            ushort sequence = NetworkCorrelation.ExtractRequestId(context.CorrelationId);
            if (!manager.ValidateSequence(senderClientId, context.ActorNetworkId, sequence, module))
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

        /// <summary>
        /// Validates that sender owns the target entity and (when mapped) that it belongs to the same actor.
        /// </summary>
        public static bool ValidateTargetEntityOwnership(
            uint senderClientId,
            uint actorNetworkId,
            uint targetEntityNetworkId,
            string module,
            string requestType)
        {
            var manager = NetworkSecurityManager.Instance;
            if (ShouldFailClosedNoSecurityManager(manager, module, requestType))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            if (targetEntityNetworkId == 0)
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Missing target entity for {requestType}");
                return false;
            }

            if (!OwnershipResolver.TryResolveOwnerClientIdForEntity(targetEntityNetworkId, out uint ownerClientId))
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Unresolved target ownership entity={targetEntityNetworkId} for {requestType}");
                return false;
            }

            if (ownerClientId != senderClientId)
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.UnauthorizedAction,
                    module,
                    $"Target entity ownership mismatch entity={targetEntityNetworkId}, expectedOwner={ownerClientId}, sender={senderClientId}");
                return false;
            }

            if (OwnershipResolver.TryResolveActorNetworkIdForEntity(targetEntityNetworkId, out uint mappedActorNetworkId) &&
                mappedActorNetworkId != 0 &&
                actorNetworkId != 0 &&
                mappedActorNetworkId != actorNetworkId)
            {
                manager.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Target entity actor mismatch entity={targetEntityNetworkId}, mappedActor={mappedActorNetworkId}, actor={actorNetworkId}");
                return false;
            }

            return true;
        }

        private static NetworkRequestContext BuildLegacyContext(uint actorNetworkId, ushort requestId)
        {
            uint correlationId = actorNetworkId != 0 && requestId != 0
                ? NetworkCorrelation.Compose(actorNetworkId, requestId)
                : 0u;

            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private static bool RequireLegacyServerSecurityManager(
            string module,
            string requestType,
            out NetworkSecurityManager manager)
        {
            manager = NetworkSecurityManager.Instance;
            if (manager == null)
            {
                Debug.LogError(
                    $"[SecurityIntegration] Rejecting legacy {module}/{requestType}: " +
                    "NetworkSecurityManager is missing.");
                return false;
            }

            if (!manager.IsServer)
            {
                Debug.LogError(
                    $"[SecurityIntegration] Rejecting legacy {module}/{requestType}: " +
                    "NetworkSecurityManager is not initialized for server mode.");
                return false;
            }

            return true;
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
            if (!RequireLegacyServerSecurityManager("Core", requestType, out _))
            {
                return false;
            }

            var context = BuildLegacyContext(characterNetworkId, requestId);
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
            if (ShouldFailClosedNoSecurityManager(manager, "Core", nameof(ValidateCorePositionUpdate)))
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
            if (!RequireLegacyServerSecurityManager("Stats", requestType, out var manager))
            {
                return false;
            }

            var context = BuildLegacyContext(characterNetworkId, requestId);
            if (!ValidateModuleRequest(clientId, in context, "Stats", requestType))
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
            if (!RequireLegacyServerSecurityManager("Inventory", requestType, out _))
            {
                return false;
            }

            var context = BuildLegacyContext(characterNetworkId, requestId);
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
            if (ShouldFailClosedNoSecurityManager(manager, "Inventory", nameof(ValidateItemTransfer)))
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
            if (!RequireLegacyServerSecurityManager("Melee", requestType, out _))
            {
                return false;
            }

            var context = BuildLegacyContext(characterNetworkId, requestId);
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
            if (ShouldFailClosedNoSecurityManager(manager, "Melee", nameof(ValidateMeleeHit)))
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
            if (!RequireLegacyServerSecurityManager("Shooter", requestType, out _))
            {
                return false;
            }

            var context = BuildLegacyContext(characterNetworkId, requestId);
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
            if (ShouldFailClosedNoSecurityManager(manager, "Shooter", nameof(ValidateShot)))
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
            if (ShouldFailClosedNoSecurityManager(manager, "Shooter", nameof(ValidateProjectileHit)))
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
            if (!RequireLegacyServerSecurityManager("Abilities", requestType, out _))
            {
                return false;
            }

            var context = BuildLegacyContext(characterNetworkId, requestId);
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
            if (ShouldFailClosedNoSecurityManager(manager, "Abilities", nameof(ValidateAbilityCast)))
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
