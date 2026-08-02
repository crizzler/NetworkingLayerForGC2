using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Arawn.GameCreator2.Networking.Lobby
{
    [Flags]
    public enum NetworkLobbyCapabilities
    {
        None = 0,
        Create = 1 << 0,
        QuickJoin = 1 << 1,
        JoinByCode = 1 << 2,
        Browse = 1 << 3,
        Refresh = 1 << 4,
        RegionSelection = 1 << 5,
        TopologySelection = 1 << 6,
        DirectAddress = 1 << 7,
        PlayerCapacity = 1 << 8,
        Visibility = 1 << 9
    }

    public enum NetworkLobbyState
    {
        Unavailable = 0,
        Offline = 1,
        Initializing = 2,
        Browsing = 3,
        Creating = 4,
        Joining = 5,
        Connected = 6,
        Leaving = 7,
        Error = 8
    }

    public enum NetworkLobbyTopology
    {
        ClientServer = 0,
        Shared = 1
    }

    public enum NetworkLobbyConnectionKind
    {
        Unknown = 0,
        Direct = 1,
        Lan = 2,
        Relay = 3,
        Cloud = 4
    }

    /// <summary>
    /// Provider-independent, immutable session description consumed by the shared UI.
    /// Provider SDK objects must not escape through this type.
    /// </summary>
    public sealed class NetworkLobbyEntry
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
            new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(StringComparer.Ordinal));

        public NetworkLobbyEntry(
            string id,
            string name,
            string joinCode,
            string region,
            NetworkLobbyTopology topology,
            NetworkLobbyConnectionKind connectionKind,
            int playerCount,
            int maxPlayers,
            bool isOpen,
            bool isVisible,
            bool isCompatible,
            string compatibilityMessage,
            string address = "",
            ushort port = 0,
            IReadOnlyDictionary<string, string> metadata = null)
        {
            Id = id ?? string.Empty;
            Name = name ?? string.Empty;
            JoinCode = joinCode ?? string.Empty;
            Region = region ?? string.Empty;
            Topology = topology;
            ConnectionKind = connectionKind;
            PlayerCount = Math.Max(0, playerCount);
            MaxPlayers = Math.Max(0, maxPlayers);
            IsOpen = isOpen;
            IsVisible = isVisible;
            IsCompatible = isCompatible;
            CompatibilityMessage = compatibilityMessage ?? string.Empty;
            Address = address ?? string.Empty;
            Port = port;
            Metadata = CopyMetadata(metadata);
        }

        public string Id { get; }
        public string Name { get; }
        public string JoinCode { get; }
        public string Region { get; }
        public NetworkLobbyTopology Topology { get; }
        public NetworkLobbyConnectionKind ConnectionKind { get; }
        public int PlayerCount { get; }
        public int MaxPlayers { get; }
        public bool IsOpen { get; }
        public bool IsVisible { get; }
        public bool IsCompatible { get; }
        public string CompatibilityMessage { get; }
        public string Address { get; }
        public ushort Port { get; }
        public IReadOnlyDictionary<string, string> Metadata { get; }

        public bool IsFull => MaxPlayers > 0 && PlayerCount >= MaxPlayers;
        public bool CanJoin => IsOpen && !IsFull && IsCompatible;

        private static IReadOnlyDictionary<string, string> CopyMetadata(
            IReadOnlyDictionary<string, string> source)
        {
            if (source == null || source.Count == 0) return EmptyMetadata;

            var copy = new Dictionary<string, string>(source.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in source)
            {
                if (string.IsNullOrEmpty(pair.Key)) continue;
                copy[pair.Key] = pair.Value ?? string.Empty;
            }

            return new ReadOnlyDictionary<string, string>(copy);
        }
    }

    public readonly struct NetworkLobbyQuery
    {
        public NetworkLobbyQuery(
            string region,
            NetworkLobbyTopology topology,
            bool includeIncompatible = false,
            string playerName = "")
        {
            Region = region ?? string.Empty;
            Topology = topology;
            IncludeIncompatible = includeIncompatible;
            PlayerName = playerName ?? string.Empty;
        }

        public string Region { get; }
        public NetworkLobbyTopology Topology { get; }
        public bool IncludeIncompatible { get; }
        public string PlayerName { get; }
    }

    public readonly struct NetworkLobbyCreateRequest
    {
        public NetworkLobbyCreateRequest(
            string sessionName,
            string joinCode,
            string region,
            NetworkLobbyTopology topology,
            int maxPlayers,
            bool isVisible,
            string address = "",
            ushort port = 0,
            string playerName = "")
        {
            SessionName = sessionName ?? string.Empty;
            JoinCode = joinCode ?? string.Empty;
            Region = region ?? string.Empty;
            Topology = topology;
            MaxPlayers = Math.Max(1, maxPlayers);
            IsVisible = isVisible;
            Address = address ?? string.Empty;
            Port = port;
            PlayerName = playerName ?? string.Empty;
        }

        public string SessionName { get; }
        public string JoinCode { get; }
        public string Region { get; }
        public NetworkLobbyTopology Topology { get; }
        public int MaxPlayers { get; }
        public bool IsVisible { get; }
        public string Address { get; }
        public ushort Port { get; }
        public string PlayerName { get; }
    }

    public readonly struct NetworkLobbyJoinRequest
    {
        public NetworkLobbyJoinRequest(
            NetworkLobbyEntry entry,
            string joinCode,
            string address,
            ushort port,
            string region,
            NetworkLobbyTopology topology,
            string playerName = "")
        {
            Entry = entry;
            JoinCode = joinCode ?? string.Empty;
            Address = address ?? string.Empty;
            Port = port;
            Region = region ?? string.Empty;
            Topology = topology;
            PlayerName = playerName ?? string.Empty;
        }

        public NetworkLobbyEntry Entry { get; }
        public string JoinCode { get; }
        public string Address { get; }
        public ushort Port { get; }
        public string Region { get; }
        public NetworkLobbyTopology Topology { get; }
        public string PlayerName { get; }
    }

    public readonly struct NetworkLobbyOperationResult
    {
        private NetworkLobbyOperationResult(bool succeeded, string code, string message)
        {
            Succeeded = succeeded;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public bool Succeeded { get; }
        public string Code { get; }
        public string Message { get; }

        public static NetworkLobbyOperationResult Success(string message = "")
        {
            return new NetworkLobbyOperationResult(true, string.Empty, message);
        }

        public static NetworkLobbyOperationResult Failure(string code, string message)
        {
            return new NetworkLobbyOperationResult(false, code, message);
        }
    }

    public interface INetworkLobbyService
    {
        string ServiceName { get; }
        NetworkLobbyCapabilities Capabilities { get; }
        NetworkLobbyState State { get; }
        string StatusMessage { get; }
        string LastError { get; }
        IReadOnlyList<NetworkLobbyEntry> Sessions { get; }
        string CurrentSessionId { get; }
        string CurrentSessionName { get; }

        event Action StateChanged;
        event Action SessionsChanged;

        Task<NetworkLobbyOperationResult> InitializeAsync(
            CancellationToken cancellationToken = default);

        Task<NetworkLobbyOperationResult> RefreshAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default);

        Task<NetworkLobbyOperationResult> CreateAsync(
            NetworkLobbyCreateRequest request,
            CancellationToken cancellationToken = default);

        Task<NetworkLobbyOperationResult> QuickJoinAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default);

        Task<NetworkLobbyOperationResult> JoinAsync(
            NetworkLobbyJoinRequest request,
            CancellationToken cancellationToken = default);

        Task<NetworkLobbyOperationResult> LeaveAsync(
            CancellationToken cancellationToken = default);
    }
}
