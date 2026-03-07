using System;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Common request envelope data used by gameplay modules.
    /// </summary>
    [Serializable]
    public struct NetworkRequestContext
    {
        public uint ActorNetworkId;
        public uint CorrelationId;

        public bool IsValid => ActorNetworkId != 0 && CorrelationId != 0;

        public static NetworkRequestContext Create(uint actorNetworkId, uint correlationId)
        {
            return new NetworkRequestContext
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId
            };
        }
    }

    /// <summary>
    /// Correlation helpers for protocol v2 request/response matching.
    /// </summary>
    public static class NetworkCorrelation
    {
        public static uint Compose(uint actorNetworkId, ushort localRequestId)
        {
            uint actorPart = (actorNetworkId & 0xFFFFu) << 16;
            uint requestPart = localRequestId == 0 ? 1u : localRequestId;
            return actorPart | requestPart;
        }

        public static uint Compose(uint actorNetworkId, uint localCounter)
        {
            uint actorPart = (actorNetworkId & 0xFFFFu) << 16;
            uint requestPart = localCounter & 0xFFFFu;
            if (requestPart == 0) requestPart = 1;
            return actorPart | requestPart;
        }

        public static uint Next(uint actorNetworkId, ref ushort localCounter)
        {
            localCounter++;
            if (localCounter == 0)
            {
                localCounter = 1;
            }

            return Compose(actorNetworkId, localCounter);
        }

        public static uint Next(uint actorNetworkId, ref uint localCounter)
        {
            localCounter++;
            uint low = localCounter & 0xFFFFu;
            if (low == 0)
            {
                localCounter++;
            }

            return Compose(actorNetworkId, localCounter);
        }

        public static ushort ExtractRequestId(uint correlationId)
        {
            return (ushort)(correlationId & 0xFFFFu);
        }

        public static ushort ExtractActorSegment(uint correlationId)
        {
            return (ushort)((correlationId >> 16) & 0xFFFFu);
        }

        public static bool MatchesActor(uint correlationId, uint actorNetworkId)
        {
            return ExtractActorSegment(correlationId) == (ushort)(actorNetworkId & 0xFFFFu);
        }
    }
}
