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
    public static partial class SecurityIntegration
    {
        private static INetworkOwnershipResolver s_OwnershipResolver = new NetworkOwnershipResolver();
        private static readonly HashSet<string> s_ServerModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_MissingSecurityManagerWarningLogged;
        private static bool s_EnforceSecurityManagerForServerLikeRequests = true;
        private const int DEFAULT_MAX_INVENTORY_QUANTITY_PER_REQUEST = 99;
        private const float DEFAULT_MAX_MELEE_DAMAGE_PER_REQUEST = 10000f;
        private const float DEFAULT_MAX_SHOOTER_DAMAGE_PER_REQUEST = 100000f;

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
            s_EnforceSecurityManagerForServerLikeRequests = true;
        }

        /// <summary>
        /// When enabled (default), requests that look server-side (non-zero sender + actor)
        /// fail closed if no server-initialized security manager is present.
        /// </summary>
        public static bool EnforceSecurityManagerForServerLikeRequests
        {
            get => s_EnforceSecurityManagerForServerLikeRequests;
            set => s_EnforceSecurityManagerForServerLikeRequests = value;
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

        private static bool ShouldFailClosedNoSecurityManager(
            NetworkSecurityManager manager,
            string module,
            string requestType,
            uint senderClientId = NetworkTransportBridge.InvalidClientId,
            uint actorNetworkId = 0)
        {
            bool hasAuthoritativeContext = IsAuthoritativeServerContext();
            bool hasSenderContext = NetworkTransportBridge.IsValidClientId(senderClientId);
            bool looksLikeServerRequest =
                hasSenderContext &&
                (actorNetworkId != 0 || !string.IsNullOrWhiteSpace(module));

            if (!hasAuthoritativeContext &&
                (!s_EnforceSecurityManagerForServerLikeRequests || !looksLikeServerRequest))
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

        public static void RegisterActorOwnership(uint actorNetworkId, uint ownerClientId)
        {
            if (actorNetworkId == 0 || !NetworkTransportBridge.IsValidClientId(ownerClientId)) return;

            OwnershipResolver.RegisterEntityActor(actorNetworkId, actorNetworkId);
            OwnershipResolver.RegisterEntityOwner(actorNetworkId, ownerClientId);
        }

        public static void RegisterEntityActor(uint entityNetworkId, uint actorNetworkId)
        {
            OwnershipResolver.RegisterEntityActor(entityNetworkId, actorNetworkId);
        }

        public static bool TryResolveActorOwner(uint actorNetworkId, out uint ownerClientId)
        {
            return OwnershipResolver.TryResolveOwnerClientId(actorNetworkId, out ownerClientId);
        }

        public static void UnregisterEntity(uint entityNetworkId)
        {
            OwnershipResolver.UnregisterEntity(entityNetworkId);
        }

        public static void UnregisterActorOwnership(uint actorNetworkId)
        {
            if (actorNetworkId == 0) return;
            OwnershipResolver.UnregisterEntity(actorNetworkId);
            NetworkCorrelation.ClearComposeState(actorNetworkId);
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
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    module,
                    requestType,
                    senderClientId,
                    context.ActorNetworkId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            if (IsProtocolContextMismatch(in context))
            {
                ushort actorSignature = NetworkCorrelation.ExtractActorSegment(context.CorrelationId);
                ushort requestSegment = NetworkCorrelation.ExtractRequestId(context.CorrelationId);
                manager.RecordViolation(
                    senderClientId,
                    context.ActorNetworkId,
                    SecurityViolationType.ProtocolMismatch,
                    module,
                    $"Invalid protocol v2 context for {requestType}: " +
                    $"actor={context.ActorNetworkId}, correlation={context.CorrelationId}, " +
                    $"corrActorSignature={actorSignature}, corrRequestSegment={requestSegment}");
                return false;
            }

            if (!manager.CheckRateLimit(senderClientId, module))
            {
                return false;
            }

            uint sequenceKey = NetworkCorrelation.ExtractSequenceKey(context.CorrelationId);
            uint sequenceMask = NetworkCorrelation.GetSequenceMask();
            if (!manager.ValidateSequence(senderClientId, context.ActorNetworkId, sequenceKey, module, sequenceMask))
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
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    module,
                    "ValidateOwnership",
                    senderClientId,
                    actorNetworkId))
            {
                return false;
            }

            if (manager == null || !manager.IsServer) return true;

            if (!NetworkTransportBridge.IsValidClientId(senderClientId) || actorNetworkId == 0)
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
                if (!TryPrimeOwnershipFromTransport(senderClientId, actorNetworkId, out ownerClientId))
                {
                    manager.RecordViolation(
                        senderClientId,
                        actorNetworkId,
                        SecurityViolationType.InvalidTarget,
                        module,
                        $"Unresolved ownership for actor {actorNetworkId}. " +
                        "Register actor ownership through SecurityIntegration.RegisterActorOwnership " +
                        "from your transport bridge before gameplay requests are processed.");
                    return false;
                }
            }

            if (ownerClientId != senderClientId)
            {
                if (TryPrimeOwnershipFromTransport(senderClientId, actorNetworkId, out uint refreshedOwnerClientId))
                {
                    ownerClientId = refreshedOwnerClientId;
                }

                if (ownerClientId == senderClientId)
                {
                    return true;
                }

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

        /// <summary>
        /// Attempts to bootstrap actor ownership from the active transport bridge using
        /// strict sender+actor verification. This does not trust sender claims directly.
        /// </summary>
        public static bool TryPrimeOwnershipFromTransport(uint senderClientId, uint actorNetworkId, out uint ownerClientId)
        {
            ownerClientId = 0;
            if (!NetworkTransportBridge.IsValidClientId(senderClientId) || actorNetworkId == 0)
            {
                return false;
            }

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge == null)
            {
                return false;
            }

            if (!bridge.TryVerifyActorOwnership(senderClientId, actorNetworkId, out ownerClientId) ||
                !NetworkTransportBridge.IsValidClientId(ownerClientId))
            {
                return false;
            }

            RegisterActorOwnership(actorNetworkId, ownerClientId);
            return true;
        }

        public static bool ValidateOwnership(ulong senderClientId, uint actorNetworkId, string module)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(senderClientId, out uint convertedClientId))
            {
                var manager = NetworkSecurityManager.Instance;
                manager?.RecordViolation(
                    NetworkTransportBridge.InvalidClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidTarget,
                    module,
                    $"Sender id {senderClientId} exceeds uint range");
                return false;
            }

            return ValidateOwnership(convertedClientId, actorNetworkId, module);
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
            if (ShouldFailClosedNoSecurityManager(
                    manager,
                    module,
                    requestType,
                    senderClientId,
                    actorNetworkId))
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
                bool primedFromTransport = false;
                if (actorNetworkId != 0)
                {
                    bool targetMapsToActor = targetEntityNetworkId == actorNetworkId;
                    if (!targetMapsToActor &&
                        OwnershipResolver.TryResolveActorNetworkIdForEntity(targetEntityNetworkId, out uint mappedTargetActorNetworkId))
                    {
                        targetMapsToActor = mappedTargetActorNetworkId == actorNetworkId;
                    }

                    if (targetMapsToActor &&
                        TryPrimeOwnershipFromTransport(senderClientId, actorNetworkId, out ownerClientId))
                    {
                        OwnershipResolver.RegisterEntityActor(targetEntityNetworkId, actorNetworkId);
                        OwnershipResolver.RegisterEntityOwner(targetEntityNetworkId, ownerClientId);
                        primedFromTransport = true;
                    }
                }

                if (!primedFromTransport && !OwnershipResolver.TryResolveOwnerClientIdForEntity(targetEntityNetworkId, out ownerClientId))
                {
                    manager.RecordViolation(
                        senderClientId,
                        actorNetworkId,
                        SecurityViolationType.InvalidTarget,
                        module,
                        $"Unresolved target ownership entity={targetEntityNetworkId} for {requestType}");
                    return false;
                }
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

        private static float ResolveConfiguredMaxValue(
            NetworkSecurityManager manager,
            string module,
            float fallback)
        {
            if (manager == null) return fallback;

            NetworkSecurityConfig config = manager.GetConfigForModule(module);
            if (config != null && config.MaxValueChangePerRequest > 0f)
            {
                return config.MaxValueChangePerRequest;
            }

            return fallback;
        }

        private static int ResolveConfiguredMaxQuantity(
            NetworkSecurityManager manager,
            string module,
            int fallback)
        {
            float configured = ResolveConfiguredMaxValue(manager, module, fallback);
            if (float.IsNaN(configured) || float.IsInfinity(configured) || configured <= 0f)
            {
                return fallback;
            }

            return Mathf.Max(1, Mathf.RoundToInt(configured));
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
