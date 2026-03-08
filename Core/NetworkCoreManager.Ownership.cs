using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkCoreManager
    {
        private static NetworkRequestContext BuildContext(uint actorNetworkId, uint correlationId)
        {
            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private static bool IsProtocolMismatch(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId);
        }

        private static void RegisterOwnedEntityMapping(uint entityNetworkId, uint actorNetworkId)
        {
            if (entityNetworkId == 0 || actorNetworkId == 0) return;

            SecurityIntegration.RegisterEntityActor(entityNetworkId, actorNetworkId);

            var bridge = NetworkTransportBridge.Active;
            if (bridge == null) return;

            if (!bridge.TryGetCharacterOwner(actorNetworkId, out uint ownerClientId) ||
                !NetworkTransportBridge.IsValidClientId(ownerClientId))
            {
                if (!bridge.TryGetCharacterOwner(entityNetworkId, out ownerClientId) ||
                    !NetworkTransportBridge.IsValidClientId(ownerClientId))
                {
                    return;
                }
            }

            SecurityIntegration.RegisterEntityOwner(entityNetworkId, ownerClientId);
        }

        private static void BootstrapCoreOwnershipMappings(uint actorNetworkId, uint characterNetworkId)
        {
            RegisterOwnedEntityMapping(actorNetworkId, actorNetworkId);

            if (characterNetworkId != actorNetworkId)
            {
                RegisterOwnedEntityMapping(characterNetworkId, actorNetworkId);
            }
        }

        private static void PrimeCoreOwnershipForInboundRequest(uint actorNetworkId, uint characterNetworkId)
        {
            // Only bootstrap when actor/character binding is structurally valid for Core requests.
            // This avoids persisting mappings from malformed/malicious actor-target pairs.
            if (actorNetworkId == 0 || characterNetworkId == 0 || actorNetworkId != characterNetworkId)
            {
                return;
            }

            BootstrapCoreOwnershipMappings(actorNetworkId, characterNetworkId);
        }

        private static bool TryValidateCoreActorBinding(
            uint senderClientId,
            uint actorNetworkId,
            uint characterNetworkId,
            string requestType,
            out bool protocolMismatch)
        {
            protocolMismatch = false;

            if (actorNetworkId == 0 || characterNetworkId == 0 || actorNetworkId != characterNetworkId)
            {
                protocolMismatch = true;
                SecurityIntegration.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.ProtocolMismatch,
                    "Core",
                    $"{requestType} actor mismatch actor={actorNetworkId}, character={characterNetworkId}");
                return false;
            }

            // If transport ownership maps are populated lazily, bootstrap actor ownership
            // via strict sender->actor verification before entity ownership checks.
            SecurityIntegration.TryPrimeOwnershipFromTransport(senderClientId, actorNetworkId, out _);

            BootstrapCoreOwnershipMappings(actorNetworkId, characterNetworkId);

            if (!SecurityIntegration.ValidateTargetEntityOwnership(
                    senderClientId,
                    actorNetworkId,
                    characterNetworkId,
                    "Core",
                    requestType))
            {
                return false;
            }

            return true;
        }
    }
}
