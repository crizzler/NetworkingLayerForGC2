namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Implemented by active Fusion module bridges whose local registrations must exist before
    /// a client can acknowledge GameplayReady. The identity passed to the participant is the
    /// client's transport-managed player identity.
    /// </summary>
    public interface IFusionGameplayReadinessParticipant
    {
        /// <summary>A stable, human-readable name used by readiness diagnostics.</summary>
        string GameplayReadinessName { get; }

        /// <summary>The exact transport instance whose local registrations are being reported.</summary>
        FusionTransportBridge GameplayReadinessTransport { get; }

        /// <summary>The protocol module represented by this readiness participant.</summary>
        ushort GameplayReadinessModuleId { get; }

        /// <summary>
        /// Returns true only after this bridge is bound and all module state relevant to the
        /// supplied identity has been initialized and registered locally.
        /// </summary>
        bool IsGameplayReady(FusionNetworkIdentity identity);
    }
}
