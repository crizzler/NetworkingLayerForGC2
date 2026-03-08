using System;
using System.Collections.Generic;

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
        // Correlation layout (v2):
        // bits 00..15 = request id
        // bits 16..27 = actor signature (12-bit checksum)
        // bits 28..31 = generation
        //
        // Effective replay/ordering sequence key:
        // sequence = (generation << 16) | requestId  => 20-bit
        public const uint SequenceMask = 0x000FFFFFu;

        private const int RequestBits = 16;
        private const int SignatureBits = 12;
        private const int GenerationBits = 4;
        private const int SignatureShift = RequestBits;
        private const int GenerationShift = RequestBits + SignatureBits;

        private const ushort RequestMask = 0xFFFF;
        private const uint SignatureMask = (1u << SignatureBits) - 1u;
        private const uint GenerationMask = (1u << GenerationBits) - 1u;

        private struct ActorCorrelationState
        {
            public ushort LastRequestId;
            public ushort Generation;
            public bool Initialized;
        }

        private static readonly Dictionary<uint, ActorCorrelationState> s_ActorCorrelationStates =
            new Dictionary<uint, ActorCorrelationState>(64);
        private static readonly object s_StateLock = new object();

        private static ushort ComputeContextSignature(uint actorNetworkId, ushort requestId, ushort generation)
        {
            // Mix actor + request + generation so actor validation can detect malformed contexts.
            uint mixed = actorNetworkId;
            mixed ^= (uint)requestId * 0x9E3779B9u;
            mixed ^= (uint)generation * 0x7F4A7C15u;
            mixed ^= mixed >> 16;
            mixed *= 0x85EBCA6Bu;
            mixed ^= mixed >> 15;
            mixed *= 0xC2B2AE35u;
            mixed ^= mixed >> 16;
            return (ushort)(mixed & SignatureMask);
        }

        private static uint ComposeInternal(uint actorNetworkId, ushort requestPart, ushort generation)
        {
            ushort signature = ComputeContextSignature(actorNetworkId, requestPart, generation);
            uint packedGeneration = ((uint)generation & GenerationMask) << GenerationShift;
            uint packedSignature = ((uint)signature & SignatureMask) << SignatureShift;
            return packedGeneration | packedSignature | requestPart;
        }

        private static ushort ResolveGenerationForRequest(uint actorNetworkId, ushort requestPart)
        {
            lock (s_StateLock)
            {
                if (!s_ActorCorrelationStates.TryGetValue(actorNetworkId, out ActorCorrelationState state) ||
                    !state.Initialized)
                {
                    state = new ActorCorrelationState
                    {
                        LastRequestId = requestPart,
                        Generation = 0,
                        Initialized = true
                    };
                    s_ActorCorrelationStates[actorNetworkId] = state;
                    return state.Generation;
                }

                if (requestPart <= state.LastRequestId)
                {
                    state.Generation = (ushort)((state.Generation + 1u) & GenerationMask);
                }

                state.LastRequestId = requestPart;
                s_ActorCorrelationStates[actorNetworkId] = state;
                return state.Generation;
            }
        }

        public static uint Compose(uint actorNetworkId, ushort localRequestId)
        {
            ushort requestPart = localRequestId == 0 ? (ushort)1 : localRequestId;
            ushort generation = ResolveGenerationForRequest(actorNetworkId, requestPart);
            return ComposeInternal(actorNetworkId, requestPart, generation);
        }

        public static uint Compose(uint actorNetworkId, uint localCounter)
        {
            ushort requestPart = (ushort)(localCounter & RequestMask);
            if (requestPart == 0) requestPart = 1;
            ushort generation = (ushort)((localCounter >> RequestBits) & GenerationMask);
            return ComposeInternal(actorNetworkId, requestPart, generation);
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
            uint low = localCounter & RequestMask;
            if (low == 0)
            {
                localCounter++;
            }

            return Compose(actorNetworkId, localCounter);
        }

        public static ushort ExtractRequestId(uint correlationId)
        {
            return (ushort)(correlationId & RequestMask);
        }

        public static ushort ExtractActorSegment(uint correlationId)
        {
            return (ushort)((correlationId >> SignatureShift) & SignatureMask);
        }

        public static byte ExtractGeneration(uint correlationId)
        {
            return (byte)(ExtractGenerationWide(correlationId) & GenerationMask);
        }

        public static ushort ExtractGenerationWide(uint correlationId)
        {
            return (ushort)((correlationId >> GenerationShift) & GenerationMask);
        }

        public static uint ExtractSequenceKey(uint correlationId)
        {
            uint raw = ((uint)ExtractGenerationWide(correlationId) << RequestBits) | ExtractRequestId(correlationId);
            return raw & SequenceMask;
        }

        public static uint GetSequenceMask()
        {
            return SequenceMask;
        }

        public static bool MatchesActor(uint correlationId, uint actorNetworkId)
        {
            ushort requestId = ExtractRequestId(correlationId);
            if (requestId == 0) return false;
            ushort generation = ExtractGenerationWide(correlationId);
            ushort signature = (ushort)((correlationId >> SignatureShift) & SignatureMask);
            return signature == ComputeContextSignature(actorNetworkId, requestId, generation);
        }

        public static void ResetComposeState()
        {
            lock (s_StateLock)
            {
                s_ActorCorrelationStates.Clear();
            }
        }

        public static void ClearComposeState(uint actorNetworkId)
        {
            if (actorNetworkId == 0) return;

            lock (s_StateLock)
            {
                s_ActorCorrelationStates.Remove(actorNetworkId);
            }
        }
    }
}
