using System;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Steam lobby visibility without exposing an optional Steamworks.NET type from
    /// the always-compiled GC2 transport assembly.
    /// </summary>
    public enum PurrNetSteamLobbyVisibility
    {
        Private = 0,
        FriendsOnly = 1,
        Public = 2,
        Invisible = 3
    }

    public enum PurrNetSteamLobbyProviderState
    {
        Unavailable,
        Initializing,
        Ready,
        CreatingLobby,
        JoiningLobby,
        InLobby,
        Error
    }

    public enum PurrNetSteamLobbySessionState
    {
        Unavailable,
        InitializingSteam,
        Ready,
        CreatingLobby,
        JoiningLobby,
        StartingHost,
        StartingClient,
        Hosting,
        Connected,
        Leaving,
        Error
    }

    [Serializable]
    public struct PurrNetSteamLobbyCreateRequest
    {
        public PurrNetSteamLobbyVisibility visibility;
        public int maxMembers;
        public string product;
        public string buildVersion;
        public string protocolVersion;
    }

    [Serializable]
    public struct PurrNetSteamLobbyJoinRequest
    {
        public string lobbyId;
        public string expectedProduct;
        public string expectedBuildVersion;
        public string expectedProtocolVersion;
        public bool requireMatchingBuild;
    }

    public readonly struct PurrNetSteamLobbyCreated
    {
        public readonly string lobbyId;
        public readonly string hostSteamId;

        public PurrNetSteamLobbyCreated(string lobbyId, string hostSteamId)
        {
            this.lobbyId = lobbyId;
            this.hostSteamId = hostSteamId;
        }
    }

    public readonly struct PurrNetSteamLobbyJoined
    {
        public readonly string lobbyId;
        public readonly string hostSteamId;

        public PurrNetSteamLobbyJoined(string lobbyId, string hostSteamId)
        {
            this.lobbyId = lobbyId;
            this.hostSteamId = hostSteamId;
        }
    }

    /// <summary>
    /// Boundary between the PurrNet session coordinator and an optional Steam SDK.
    /// Implementations must not expose wrapper-specific types through this API.
    /// </summary>
    public interface IPurrNetSteamLobbyProvider
    {
        bool IsAvailable { get; }
        bool IsInitialized { get; }
        PurrNetSteamLobbyProviderState State { get; }
        string StatusMessage { get; }
        string LocalSteamId { get; }
        string CurrentLobbyId { get; }

        event Action Ready;
        event Action<PurrNetSteamLobbyCreated> LobbyCreated;
        event Action<PurrNetSteamLobbyJoined> LobbyJoined;
        event Action<string> JoinRequested;
        event Action LeftLobby;
        event Action<string> Failed;

        void Initialize();
        void CreateLobby(PurrNetSteamLobbyCreateRequest request);
        void JoinLobby(PurrNetSteamLobbyJoinRequest request);
        void OpenInviteOverlay();
        void LeaveLobby();
        void Shutdown();
    }
}
