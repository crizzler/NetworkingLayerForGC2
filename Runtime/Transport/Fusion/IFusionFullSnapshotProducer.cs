using System;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Result returned by a Fusion module after it has synchronously enqueued its complete
    /// authoritative state for one client.
    /// </summary>
    public readonly struct FusionFullSnapshotResult
    {
        internal FusionFullSnapshotResult(bool isComplete, int packetsEnqueued, string failureReason)
        {
            IsComplete = isComplete;
            PacketsEnqueued = packetsEnqueued;
            FailureReason = failureReason ?? string.Empty;
        }

        public bool IsComplete { get; }
        public int PacketsEnqueued { get; }
        public string FailureReason { get; }
    }

    /// <summary>
    /// Per-producer delivery scope. The transport records every module packet enqueued while
    /// the producer runs, so a producer cannot report success after a rejected send.
    /// </summary>
    public sealed class FusionFullSnapshotContext
    {
        private readonly FusionTransportBridge m_TransportBridge;
        private readonly IFusionFullSnapshotProducer m_Producer;
        private bool m_DeliveryFailed;
        private string m_DeliveryFailureReason = string.Empty;
        private int m_PacketsEnqueued;

        internal FusionFullSnapshotContext(
            FusionTransportBridge transportBridge,
            IFusionFullSnapshotProducer producer,
            ushort moduleId,
            uint clientId)
        {
            m_TransportBridge = transportBridge;
            m_Producer = producer;
            ModuleId = moduleId;
            ClientId = clientId;
        }

        public FusionTransportBridge TransportBridge => m_TransportBridge;
        public ushort ModuleId { get; }
        public uint ClientId { get; }
        public int PacketsEnqueued => m_PacketsEnqueued;

        /// <summary>Reports successful completion, including modules with no persistent state.</summary>
        public FusionFullSnapshotResult Complete()
        {
            return m_DeliveryFailed
                ? Fail(m_DeliveryFailureReason)
                : new FusionFullSnapshotResult(true, m_PacketsEnqueued, string.Empty);
        }

        public FusionFullSnapshotResult Fail(string reason)
        {
            string detail = string.IsNullOrWhiteSpace(reason)
                ? "The producer did not provide a complete snapshot."
                : reason.Trim();
            return new FusionFullSnapshotResult(false, m_PacketsEnqueued, detail);
        }

        internal bool BelongsTo(IFusionFullSnapshotProducer producer)
        {
            return ReferenceEquals(m_Producer, producer);
        }

        internal void RecordDelivery(ushort moduleId, uint clientId, bool delivered)
        {
            if (moduleId != ModuleId || clientId != ClientId)
            {
                m_DeliveryFailed = true;
                m_DeliveryFailureReason =
                    $"Snapshot producer attempted module {moduleId} for client {clientId}; " +
                    $"expected module {ModuleId} for client {ClientId}.";
                return;
            }

            if (!delivered)
            {
                m_DeliveryFailed = true;
                m_DeliveryFailureReason =
                    $"Fusion rejected a module {ModuleId} snapshot packet for client {ClientId}.";
                return;
            }

            m_PacketsEnqueued++;
        }
    }

    /// <summary>
    /// Implemented by every enabled Fusion module bridge. Production is synchronous: returning
    /// complete means every packet comprising the module's current state has been accepted by
    /// <see cref="FusionTransportBridge.SendModuleToClient"/>.
    /// </summary>
    public interface IFusionFullSnapshotProducer
    {
        ushort FullSnapshotModuleId { get; }
        string FullSnapshotProducerName { get; }
        FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context);
    }
}
