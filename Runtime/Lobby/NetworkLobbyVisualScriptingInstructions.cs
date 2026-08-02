using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Lobby
{
    [Serializable]
    public abstract class NetworkLobbyInstruction : Instruction
    {
        [SerializeField]
        [Tooltip("Optional GameObject with the lobby service. Empty searches the active scene.")]
        private PropertyGetGameObject m_Service = new PropertyGetGameObject();

        protected INetworkLobbyService ResolveService(Args args)
        {
            return NetworkLobbyServiceUtility.Resolve(m_Service.Get(args));
        }

        protected async Task RunLobbyOperation(
            Args args,
            Func<INetworkLobbyService, Task<NetworkLobbyOperationResult>> operation)
        {
            INetworkLobbyService service = ResolveService(args);
            if (service == null)
            {
                Debug.LogWarning(
                    $"[{GetType().Name}] No component implementing INetworkLobbyService was found.");
                return;
            }

            try
            {
                NetworkLobbyOperationResult result = await operation(service);
                if (!result.Succeeded)
                {
                    Debug.LogWarning(
                        $"[{GetType().Name}] Lobby operation failed" +
                        (string.IsNullOrWhiteSpace(result.Code) ? ": " : $" ({result.Code}): ") +
                        result.Message,
                        service as UnityEngine.Object);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, service as UnityEngine.Object);
            }
        }

        protected static NetworkLobbyQuery Query(
            PropertyGetString region,
            NetworkLobbyTopology topology,
            PropertyGetBool includeIncompatible,
            Args args)
        {
            return new NetworkLobbyQuery(
                region.Get(args) ?? string.Empty,
                topology,
                includeIncompatible.Get(args));
        }

        protected static NetworkLobbyEntry FindSession(
            INetworkLobbyService service,
            string idOrName)
        {
            if (service?.Sessions == null || string.IsNullOrWhiteSpace(idOrName)) return null;

            string value = idOrName.Trim();
            for (int i = 0; i < service.Sessions.Count; i++)
            {
                NetworkLobbyEntry entry = service.Sessions[i];
                if (entry == null) continue;
                if (string.Equals(entry.Id, value, StringComparison.Ordinal) ||
                    string.Equals(entry.Name, value, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }
    }

    [Title("Initialize Network Lobby")]
    [Description("Initializes the active provider-neutral lobby service")]
    [Category("Network/Lobby/Initialize Lobby")]
    [Keywords("Network", "Lobby", "Initialize", "Connect", "Discovery")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkLobbyInitialize : NetworkLobbyInstruction
    {
        public override string Title => "Initialize Network Lobby";

        protected override Task Run(Args args)
        {
            return RunLobbyOperation(args, service => service.InitializeAsync());
        }
    }

    [Title("Refresh Network Lobby")]
    [Description("Refreshes the compatible sessions reported by the active lobby service")]
    [Category("Network/Lobby/Refresh Sessions")]
    [Parameter("Region", "Optional provider region; empty lets the provider choose")]
    [Parameter("Topology", "Requested client/server or shared topology")]
    [Parameter("Include Incompatible", "Also request sessions from incompatible builds when supported")]
    [Keywords("Network", "Lobby", "Refresh", "Browse", "Sessions")]
    [Image(typeof(IconRefresh), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkLobbyRefresh : NetworkLobbyInstruction
    {
        [SerializeField] private PropertyGetString m_Region = new PropertyGetString(string.Empty);
        [SerializeField] private NetworkLobbyTopology m_Topology;
        [SerializeField] private PropertyGetBool m_IncludeIncompatible = new PropertyGetBool(false);

        public override string Title => "Refresh Network Lobby";

        protected override Task Run(Args args)
        {
            NetworkLobbyQuery query = Query(
                m_Region,
                m_Topology,
                m_IncludeIncompatible,
                args);
            return RunLobbyOperation(args, service => service.RefreshAsync(query));
        }
    }

    [Title("Create Network Lobby Session")]
    [Description("Creates and hosts a session through the active lobby service")]
    [Category("Network/Lobby/Create Session")]
    [Parameter("Player Name", "Local player's display name")]
    [Parameter("Session Name", "Name shown in browsers and invitations")]
    [Parameter("Join Code", "Optional room or invitation code")]
    [Parameter("Region", "Optional provider region")]
    [Parameter("Topology", "Requested client/server or shared topology")]
    [Parameter("Max Players", "Maximum session capacity")]
    [Parameter("Visible", "Whether discovery providers should list the session")]
    [Keywords("Network", "Lobby", "Create", "Host", "Session", "Room")]
    [Image(typeof(IconPlusCircle), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkLobbyCreate : NetworkLobbyInstruction
    {
        [SerializeField] private PropertyGetString m_PlayerName = new PropertyGetString("Player");
        [SerializeField] private PropertyGetString m_SessionName = new PropertyGetString("My Game");
        [SerializeField] private PropertyGetString m_JoinCode = new PropertyGetString(string.Empty);
        [SerializeField] private PropertyGetString m_Region = new PropertyGetString(string.Empty);
        [SerializeField] private NetworkLobbyTopology m_Topology;
        [SerializeField] private PropertyGetInteger m_MaxPlayers = new PropertyGetInteger(8);
        [SerializeField] private PropertyGetBool m_Visible = new PropertyGetBool(true);
        [SerializeField] private PropertyGetString m_Address = new PropertyGetString(string.Empty);
        [SerializeField] private PropertyGetInteger m_Port = new PropertyGetInteger(7777);

        public override string Title => $"Create Lobby: {m_SessionName}";

        protected override Task Run(Args args)
        {
            var request = new NetworkLobbyCreateRequest(
                m_SessionName.Get(args),
                m_JoinCode.Get(args),
                m_Region.Get(args),
                m_Topology,
                Math.Max(1, (int)m_MaxPlayers.Get(args)),
                m_Visible.Get(args),
                m_Address.Get(args),
                (ushort)Mathf.Clamp((int)m_Port.Get(args), 0, ushort.MaxValue),
                m_PlayerName.Get(args));
            return RunLobbyOperation(args, service => service.CreateAsync(request));
        }
    }

    [Title("Quick Join Network Lobby")]
    [Description("Joins the best compatible session selected by the active lobby provider")]
    [Category("Network/Lobby/Quick Join")]
    [Parameter("Player Name", "Local player's display name")]
    [Parameter("Region", "Optional provider region")]
    [Parameter("Topology", "Requested client/server or shared topology")]
    [Keywords("Network", "Lobby", "Quick", "Join", "Matchmaking")]
    [Image(typeof(IconArrowCircleRight), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkLobbyQuickJoin : NetworkLobbyInstruction
    {
        [SerializeField] private PropertyGetString m_PlayerName = new PropertyGetString("Player");
        [SerializeField] private PropertyGetString m_Region = new PropertyGetString(string.Empty);
        [SerializeField] private NetworkLobbyTopology m_Topology;

        public override string Title => "Quick Join Network Lobby";

        protected override Task Run(Args args)
        {
            var query = new NetworkLobbyQuery(
                m_Region.Get(args),
                m_Topology,
                false,
                m_PlayerName.Get(args));
            return RunLobbyOperation(args, service => service.QuickJoinAsync(query));
        }
    }

    [Title("Join Network Lobby By Code")]
    [Description("Joins a session using a room or invitation code")]
    [Category("Network/Lobby/Join By Code")]
    [Parameter("Player Name", "Local player's display name")]
    [Parameter("Join Code", "Room, session, or invitation code")]
    [Parameter("Region", "Optional provider region")]
    [Keywords("Network", "Lobby", "Join", "Code", "Room", "Invite")]
    [Image(typeof(IconCode), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkLobbyJoinCode : NetworkLobbyInstruction
    {
        [SerializeField] private PropertyGetString m_PlayerName = new PropertyGetString("Player");
        [SerializeField] private PropertyGetString m_JoinCode = new PropertyGetString(string.Empty);
        [SerializeField] private PropertyGetString m_Region = new PropertyGetString(string.Empty);
        [SerializeField] private NetworkLobbyTopology m_Topology;

        public override string Title => $"Join Lobby Code: {m_JoinCode}";

        protected override Task Run(Args args)
        {
            var request = new NetworkLobbyJoinRequest(
                null,
                m_JoinCode.Get(args),
                string.Empty,
                0,
                m_Region.Get(args),
                m_Topology,
                m_PlayerName.Get(args));
            return RunLobbyOperation(args, service => service.JoinAsync(request));
        }
    }

    [Title("Join Network Lobby By Address")]
    [Description("Joins a direct or LAN session using its host address and port")]
    [Category("Network/Lobby/Join By Address")]
    [Parameter("Player Name", "Local player's display name")]
    [Parameter("Address", "Host name or IP address")]
    [Parameter("Port", "Host UDP port")]
    [Keywords("Network", "Lobby", "Join", "Address", "IP", "LAN", "Direct")]
    [Image(typeof(IconLocation), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkLobbyJoinAddress : NetworkLobbyInstruction
    {
        [SerializeField] private PropertyGetString m_PlayerName = new PropertyGetString("Player");
        [SerializeField] private PropertyGetString m_Address = new PropertyGetString("127.0.0.1");
        [SerializeField] private PropertyGetInteger m_Port = new PropertyGetInteger(7777);
        [SerializeField] private NetworkLobbyTopology m_Topology;

        public override string Title => $"Join Lobby Address: {m_Address}";

        protected override Task Run(Args args)
        {
            var request = new NetworkLobbyJoinRequest(
                null,
                string.Empty,
                m_Address.Get(args),
                (ushort)Mathf.Clamp((int)m_Port.Get(args), 0, ushort.MaxValue),
                string.Empty,
                m_Topology,
                m_PlayerName.Get(args));
            return RunLobbyOperation(args, service => service.JoinAsync(request));
        }
    }

    [Title("Join Listed Network Lobby Session")]
    [Description("Joins a browsed session by its exact identifier or display name")]
    [Category("Network/Lobby/Join Listed Session")]
    [Parameter("Player Name", "Local player's display name")]
    [Parameter("Session", "Exact session identifier or display name")]
    [Keywords("Network", "Lobby", "Join", "Session", "Browse", "List")]
    [Image(typeof(IconListIndex), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkLobbyJoinSession : NetworkLobbyInstruction
    {
        [SerializeField] private PropertyGetString m_PlayerName = new PropertyGetString("Player");
        [SerializeField] private PropertyGetString m_Session = new PropertyGetString(string.Empty);

        public override string Title => $"Join Listed Lobby: {m_Session}";

        protected override Task Run(Args args)
        {
            string idOrName = m_Session.Get(args);
            return RunLobbyOperation(
                args,
                service =>
                {
                    NetworkLobbyEntry entry = FindSession(service, idOrName);
                    if (entry == null)
                    {
                        return Task.FromResult(NetworkLobbyOperationResult.Failure(
                            "session-not-found",
                            $"No listed session matches '{idOrName}'."));
                    }

                    var request = new NetworkLobbyJoinRequest(
                        entry,
                        entry.JoinCode,
                        entry.Address,
                        entry.Port,
                        entry.Region,
                        entry.Topology,
                        m_PlayerName.Get(args));
                    return service.JoinAsync(request);
                });
        }
    }

    [Title("Leave Network Lobby Session")]
    [Description("Leaves the current session and returns the lobby service to its offline state")]
    [Category("Network/Lobby/Leave Session")]
    [Keywords("Network", "Lobby", "Leave", "Disconnect", "Stop")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionNetworkLobbyLeave : NetworkLobbyInstruction
    {
        public override string Title => "Leave Network Lobby Session";

        protected override Task Run(Args args)
        {
            return RunLobbyOperation(args, service => service.LeaveAsync());
        }
    }
}
