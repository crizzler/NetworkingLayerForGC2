namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Shared contract for GC2 facing units that synchronize yaw through NetworkCharacter.
    /// </summary>
    public interface INetworkFacingUnit
    {
        float ServerYaw { get; }
        float ClientYaw { get; }
        bool IsNetworkInitialized { get; }

        void OnServerYawReceived(float yaw);
        float ValidateFacingRequest(float requestedYaw);
        void ForceServerYaw(float yaw);
    }
}
