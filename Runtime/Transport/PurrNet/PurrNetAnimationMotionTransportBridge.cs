using GameCreator.Runtime.Characters;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Animation Motion Bridge")]
    [DefaultExecutionOrder(-395)]
    public sealed class PurrNetAnimationMotionTransportBridge : MonoBehaviour
    {
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Reliable channel used for one-shot animation and semantic motion commands.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Tooltip("Create NetworkAnimationManager and NetworkMotionManager if the scene does not already contain them.")]
        [SerializeField] private bool m_CreateManagersIfMissing = true;

        [SerializeField] private bool m_LogDashDiagnostics = false;

        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagersInitialized;
        private bool m_LastServer;
        private bool m_LastClient;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        public void Configure(NetworkManager networkManager)
        {
            if (networkManager != null) m_NetworkManager = networkManager;
            TryHookNetworkManager();
            WireManagers();
        }

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireManagers();
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireManagers();
        }

        private void Update()
        {
            if (ActiveManager == null) TryHookNetworkManager();
            WireManagers();
        }

        private void OnDisable()
        {
            var nm = ActiveManager;
            if (nm != null)
            {
                nm.onNetworkStarted -= HandleNetworkStarted;
                nm.onNetworkShutdown -= HandleNetworkShutdown;

                if (m_SubscribedServer)
                {
                    UnsubscribeServer(nm);
                    m_SubscribedServer = false;
                }

                if (m_SubscribedClient)
                {
                    UnsubscribeClient(nm);
                    m_SubscribedClient = false;
                }
            }

            UnwireManagers();
        }

        private void TryHookNetworkManager()
        {
            var nm = ActiveManager;
            if (nm == null) return;

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkStarted += HandleNetworkStarted;

            nm.onNetworkShutdown -= HandleNetworkShutdown;
            nm.onNetworkShutdown += HandleNetworkShutdown;

            if (nm.isServer) HandleNetworkStarted(nm, true);
            if (nm.isClient) HandleNetworkStarted(nm, false);
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                SubscribeServer(manager);
                m_SubscribedServer = true;
                LogDash($"subscribed server packets manager={manager.name}");
            }
            else if (!asServer && !m_SubscribedClient)
            {
                SubscribeClient(manager);
                m_SubscribedClient = true;
                LogDash($"subscribed client packets manager={manager.name}");
            }

            WireManagers();
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                UnsubscribeServer(manager);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                UnsubscribeClient(manager);
                m_SubscribedClient = false;
            }

            WireManagers();
        }

        private void SubscribeServer(NetworkManager manager)
        {
            manager.Subscribe<GC2AnimationStateCommandPacket>(HandleAnimationStateCommandServer, true);
            manager.Subscribe<GC2AnimationGestureCommandPacket>(HandleAnimationGestureCommandServer, true);
            manager.Subscribe<GC2AnimationStopStateCommandPacket>(HandleAnimationStopStateCommandServer, true);
            manager.Subscribe<GC2AnimationStopGestureCommandPacket>(HandleAnimationStopGestureCommandServer, true);
            manager.Subscribe<GC2MotionCommandPacket>(HandleMotionCommandServer, true);
        }

        private void UnsubscribeServer(NetworkManager manager)
        {
            manager.Unsubscribe<GC2AnimationStateCommandPacket>(HandleAnimationStateCommandServer, true);
            manager.Unsubscribe<GC2AnimationGestureCommandPacket>(HandleAnimationGestureCommandServer, true);
            manager.Unsubscribe<GC2AnimationStopStateCommandPacket>(HandleAnimationStopStateCommandServer, true);
            manager.Unsubscribe<GC2AnimationStopGestureCommandPacket>(HandleAnimationStopGestureCommandServer, true);
            manager.Unsubscribe<GC2MotionCommandPacket>(HandleMotionCommandServer, true);
        }

        private void SubscribeClient(NetworkManager manager)
        {
            manager.Subscribe<GC2AnimationStateCommandPacket>(HandleAnimationStateBroadcastClient, false);
            manager.Subscribe<GC2AnimationGestureCommandPacket>(HandleAnimationGestureBroadcastClient, false);
            manager.Subscribe<GC2AnimationStopStateCommandPacket>(HandleAnimationStopStateBroadcastClient, false);
            manager.Subscribe<GC2AnimationStopGestureCommandPacket>(HandleAnimationStopGestureBroadcastClient, false);
            manager.Subscribe<GC2MotionResultPacket>(HandleMotionResultClient, false);
            manager.Subscribe<GC2MotionCommandPacket>(HandleMotionBroadcastClient, false);
        }

        private void UnsubscribeClient(NetworkManager manager)
        {
            manager.Unsubscribe<GC2AnimationStateCommandPacket>(HandleAnimationStateBroadcastClient, false);
            manager.Unsubscribe<GC2AnimationGestureCommandPacket>(HandleAnimationGestureBroadcastClient, false);
            manager.Unsubscribe<GC2AnimationStopStateCommandPacket>(HandleAnimationStopStateBroadcastClient, false);
            manager.Unsubscribe<GC2AnimationStopGestureCommandPacket>(HandleAnimationStopGestureBroadcastClient, false);
            manager.Unsubscribe<GC2MotionResultPacket>(HandleMotionResultClient, false);
            manager.Unsubscribe<GC2MotionCommandPacket>(HandleMotionBroadcastClient, false);
        }

        private void WireManagers()
        {
            NetworkAnimationManager animationManager = GetAnimationManager();
            if (animationManager != null)
            {
                animationManager.SendStateCommandToServer -= SendAnimationStateCommandToServer;
                animationManager.SendStateCommandToServer += SendAnimationStateCommandToServer;
                animationManager.SendGestureCommandToServer -= SendAnimationGestureCommandToServer;
                animationManager.SendGestureCommandToServer += SendAnimationGestureCommandToServer;
                animationManager.SendStopStateCommandToServer -= SendAnimationStopStateCommandToServer;
                animationManager.SendStopStateCommandToServer += SendAnimationStopStateCommandToServer;
                animationManager.SendStopGestureCommandToServer -= SendAnimationStopGestureCommandToServer;
                animationManager.SendStopGestureCommandToServer += SendAnimationStopGestureCommandToServer;

                animationManager.BroadcastStateCommandToClients -= BroadcastAnimationStateCommand;
                animationManager.BroadcastStateCommandToClients += BroadcastAnimationStateCommand;
                animationManager.BroadcastGestureCommandToClients -= BroadcastAnimationGestureCommand;
                animationManager.BroadcastGestureCommandToClients += BroadcastAnimationGestureCommand;
                animationManager.BroadcastStopStateCommandToClients -= BroadcastAnimationStopStateCommand;
                animationManager.BroadcastStopStateCommandToClients += BroadcastAnimationStopStateCommand;
                animationManager.BroadcastStopGestureCommandToClients -= BroadcastAnimationStopGestureCommand;
                animationManager.BroadcastStopGestureCommandToClients += BroadcastAnimationStopGestureCommand;

                animationManager.GetNetworkCharacterById = ResolveNetworkCharacter;
            }

            NetworkMotionManager motionManager = GetMotionManager();
            if (motionManager != null)
            {
                motionManager.SendCommandToServer -= SendMotionCommandToServer;
                motionManager.SendCommandToServer += SendMotionCommandToServer;
                motionManager.SendResultToClient -= SendMotionResultToClient;
                motionManager.SendResultToClient += SendMotionResultToClient;
                motionManager.BroadcastCommandToClients -= BroadcastMotionCommand;
                motionManager.BroadcastCommandToClients += BroadcastMotionCommand;
                motionManager.GetNetworkCharacterById = ResolveNetworkCharacter;
            }

            var nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isClient = nm != null && nm.isClient;

            if (!m_ManagersInitialized || isServer != m_LastServer || isClient != m_LastClient)
            {
                animationManager?.Initialize(isServer, isClient);
                motionManager?.Initialize(isServer, isClient);
                LogDash(
                    $"wired managers server={isServer} client={isClient} " +
                    $"animation={(animationManager != null)} motion={(motionManager != null)}");
                m_ManagersInitialized = true;
                m_LastServer = isServer;
                m_LastClient = isClient;
            }
            else
            {
                animationManager?.RefreshControllerRegistry();
                motionManager?.RefreshControllerRegistry();
            }
        }

        private void UnwireManagers()
        {
            NetworkAnimationManager animationManager = NetworkAnimationManager.Instance;
            if (animationManager != null)
            {
                animationManager.SendStateCommandToServer -= SendAnimationStateCommandToServer;
                animationManager.SendGestureCommandToServer -= SendAnimationGestureCommandToServer;
                animationManager.SendStopStateCommandToServer -= SendAnimationStopStateCommandToServer;
                animationManager.SendStopGestureCommandToServer -= SendAnimationStopGestureCommandToServer;
                animationManager.BroadcastStateCommandToClients -= BroadcastAnimationStateCommand;
                animationManager.BroadcastGestureCommandToClients -= BroadcastAnimationGestureCommand;
                animationManager.BroadcastStopStateCommandToClients -= BroadcastAnimationStopStateCommand;
                animationManager.BroadcastStopGestureCommandToClients -= BroadcastAnimationStopGestureCommand;

                if (ReferenceEquals(animationManager.GetNetworkCharacterById?.Target, this))
                {
                    animationManager.GetNetworkCharacterById = null;
                }
            }

            NetworkMotionManager motionManager = NetworkMotionManager.Instance;
            if (motionManager != null)
            {
                motionManager.SendCommandToServer -= SendMotionCommandToServer;
                motionManager.SendResultToClient -= SendMotionResultToClient;
                motionManager.BroadcastCommandToClients -= BroadcastMotionCommand;

                if (ReferenceEquals(motionManager.GetNetworkCharacterById?.Target, this))
                {
                    motionManager.GetNetworkCharacterById = null;
                }
            }

            m_ManagersInitialized = false;
        }

        private NetworkAnimationManager GetAnimationManager()
        {
            NetworkAnimationManager manager = NetworkAnimationManager.Instance;
            if (manager != null || !m_CreateManagersIfMissing) return manager;

            var go = new GameObject("Network Animation Manager");
            manager = go.AddComponent<NetworkAnimationManager>();
            return manager;
        }

        private NetworkMotionManager GetMotionManager()
        {
            NetworkMotionManager manager = NetworkMotionManager.Instance;
            if (manager != null || !m_CreateManagersIfMissing) return manager;

            var go = new GameObject("Network Motion Manager");
            manager = go.AddComponent<NetworkMotionManager>();
            return manager;
        }

        private NetworkCharacter ResolveNetworkCharacter(uint networkId)
        {
            Character character = NetworkTransportBridge.Active?.ResolveCharacter(networkId);
            return character != null ? character.GetComponent<NetworkCharacter>() : null;
        }

        private void SendAnimationStateCommandToServer(NetworkAnimationStateCommandMessage message)
        {
            SendToServerOrLoopback(new GC2AnimationStateCommandPacket { message = message }, DispatchAnimationStateCommandOnServer);
        }

        private void SendAnimationGestureCommandToServer(NetworkAnimationGestureCommandMessage message)
        {
            LogDash(message, "send gesture to server");
            SendToServerOrLoopback(new GC2AnimationGestureCommandPacket { message = message }, DispatchAnimationGestureCommandOnServer);
        }

        private void SendAnimationStopStateCommandToServer(NetworkAnimationStopStateCommandMessage message)
        {
            SendToServerOrLoopback(new GC2AnimationStopStateCommandPacket { message = message }, DispatchAnimationStopStateCommandOnServer);
        }

        private void SendAnimationStopGestureCommandToServer(NetworkAnimationStopGestureCommandMessage message)
        {
            SendToServerOrLoopback(new GC2AnimationStopGestureCommandPacket { message = message }, DispatchAnimationStopGestureCommandOnServer);
        }

        private void SendMotionCommandToServer(NetworkMotionCommandMessage message)
        {
            LogDash(message, "send motion to server");
            SendToServerOrLoopback(new GC2MotionCommandPacket { message = message }, DispatchMotionCommandOnServer);
        }

        private void SendToServerOrLoopback<T>(T packet, System.Action<PlayerID, T> hostLoopback)
        {
            var nm = ActiveManager;
            if (nm == null)
            {
                LogDash($"send dropped packet={typeof(T).Name}: NetworkManager is null");
                return;
            }

            if (!nm.isClient)
            {
                LogDash($"send dropped packet={typeof(T).Name}: NetworkManager is not client");
                return;
            }

            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady)
                {
                    LogDash($"host loopback packet={typeof(T).Name} localPlayer={nm.localPlayer.id}");
                    hostLoopback(nm.localPlayer, packet);
                }
                else
                {
                    LogDash($"host loopback dropped packet={typeof(T).Name}: local player not ready");
                }
                return;
            }

            LogDash($"SendToServer packet={typeof(T).Name}");
            nm.SendToServer(packet, m_Channel);
        }

        private void SendMotionResultToClient(uint clientNetworkId, NetworkMotionResultMessage message)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDash($"motion result dropped client={clientNetworkId}: server not active");
                return;
            }

            if (!TryGetPlayerId(nm, clientNetworkId, out PlayerID playerId))
            {
                LogDash($"motion result dropped client={clientNetworkId}: PlayerID not found");
                return;
            }

            LogDash($"send motion result client={clientNetworkId} character={message.CharacterNetworkId} approved={message.Result.approved} reason={message.Result.rejectionReason}");
            nm.Send(playerId, new GC2MotionResultPacket { message = message }, m_Channel);
        }

        private void BroadcastAnimationStateCommand(NetworkAnimationStateCommandMessage message)
        {
            LogState(message, "broadcast state");
            ActiveManager?.SendToAll(new GC2AnimationStateCommandPacket { message = message }, m_Channel);
        }

        private void BroadcastAnimationGestureCommand(NetworkAnimationGestureCommandMessage message)
        {
            LogDash(message, "broadcast gesture");
            ActiveManager?.SendToAll(new GC2AnimationGestureCommandPacket { message = message }, m_Channel);
        }

        private void BroadcastAnimationStopStateCommand(NetworkAnimationStopStateCommandMessage message)
        {
            LogStopState(message, "broadcast stop-state");
            ActiveManager?.SendToAll(new GC2AnimationStopStateCommandPacket { message = message }, m_Channel);
        }

        private void BroadcastAnimationStopGestureCommand(NetworkAnimationStopGestureCommandMessage message)
        {
            ActiveManager?.SendToAll(new GC2AnimationStopGestureCommandPacket { message = message }, m_Channel);
        }

        private void BroadcastMotionCommand(NetworkMotionCommandMessage message)
        {
            LogDash(message, "broadcast motion");
            ActiveManager?.SendToAll(new GC2MotionCommandPacket { message = message }, m_Channel);
        }

        private void HandleAnimationStateCommandServer(PlayerID senderPlayer, GC2AnimationStateCommandPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchAnimationStateCommandOnServer(senderPlayer, data);
        }

        private void HandleAnimationGestureCommandServer(PlayerID senderPlayer, GC2AnimationGestureCommandPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchAnimationGestureCommandOnServer(senderPlayer, data);
        }

        private void HandleAnimationStopStateCommandServer(PlayerID senderPlayer, GC2AnimationStopStateCommandPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchAnimationStopStateCommandOnServer(senderPlayer, data);
        }

        private void HandleAnimationStopGestureCommandServer(PlayerID senderPlayer, GC2AnimationStopGestureCommandPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchAnimationStopGestureCommandOnServer(senderPlayer, data);
        }

        private void HandleMotionCommandServer(PlayerID senderPlayer, GC2MotionCommandPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchMotionCommandOnServer(senderPlayer, data);
        }

        private void DispatchAnimationStateCommandOnServer(PlayerID senderPlayer, GC2AnimationStateCommandPacket data)
        {
            if (!TryConvertSenderClientId(senderPlayer, out uint senderClientId)) return;
            LogState(data.message, $"server dispatch state sender={senderClientId}");
            GetAnimationManager()?.ReceiveStateCommand(senderClientId, data.message);
        }

        private void DispatchAnimationGestureCommandOnServer(PlayerID senderPlayer, GC2AnimationGestureCommandPacket data)
        {
            if (!TryConvertSenderClientId(senderPlayer, out uint senderClientId))
            {
                LogDash(data.message, $"server gesture dispatch dropped: could not convert sender={senderPlayer.id}");
                return;
            }

            LogDash(data.message, $"server dispatch gesture sender={senderClientId}");
            GetAnimationManager()?.ReceiveGestureCommand(senderClientId, data.message);
        }

        private void DispatchAnimationStopStateCommandOnServer(PlayerID senderPlayer, GC2AnimationStopStateCommandPacket data)
        {
            if (!TryConvertSenderClientId(senderPlayer, out uint senderClientId)) return;
            LogStopState(data.message, $"server dispatch stop-state sender={senderClientId}");
            GetAnimationManager()?.ReceiveStopStateCommand(senderClientId, data.message);
        }

        private void DispatchAnimationStopGestureCommandOnServer(PlayerID senderPlayer, GC2AnimationStopGestureCommandPacket data)
        {
            if (!TryConvertSenderClientId(senderPlayer, out uint senderClientId)) return;
            GetAnimationManager()?.ReceiveStopGestureCommand(senderClientId, data.message);
        }

        private void DispatchMotionCommandOnServer(PlayerID senderPlayer, GC2MotionCommandPacket data)
        {
            if (!TryConvertSenderClientId(senderPlayer, out uint senderClientId))
            {
                LogDash(data.message, $"server motion dispatch dropped: could not convert sender={senderPlayer.id}");
                return;
            }

            LogDash(data.message, $"server dispatch motion sender={senderClientId}");
            GetMotionManager()?.ReceiveCommand(senderClientId, data.message);
        }

        private void HandleAnimationStateBroadcastClient(PlayerID senderPlayer, GC2AnimationStateCommandPacket data, bool asServer)
        {
            if (asServer) return;
            LogState(data.message, $"client received state broadcast sender={senderPlayer.id}");
            GetAnimationManager()?.ReceiveStateBroadcast(data.message);
        }

        private void HandleAnimationGestureBroadcastClient(PlayerID senderPlayer, GC2AnimationGestureCommandPacket data, bool asServer)
        {
            if (asServer) return;
            LogDash(data.message, $"client received gesture broadcast sender={senderPlayer.id}");
            GetAnimationManager()?.ReceiveGestureBroadcast(data.message);
        }

        private void HandleAnimationStopStateBroadcastClient(PlayerID senderPlayer, GC2AnimationStopStateCommandPacket data, bool asServer)
        {
            if (asServer) return;
            LogStopState(data.message, $"client received stop-state broadcast sender={senderPlayer.id}");
            GetAnimationManager()?.ReceiveStopStateBroadcast(data.message);
        }

        private void HandleAnimationStopGestureBroadcastClient(PlayerID senderPlayer, GC2AnimationStopGestureCommandPacket data, bool asServer)
        {
            if (asServer) return;
            GetAnimationManager()?.ReceiveStopGestureBroadcast(data.message);
        }

        private void HandleMotionResultClient(PlayerID senderPlayer, GC2MotionResultPacket data, bool asServer)
        {
            if (asServer) return;
            LogDash($"client received motion result sender={senderPlayer.id} character={data.message.CharacterNetworkId} approved={data.message.Result.approved} reason={data.message.Result.rejectionReason}");
            GetMotionManager()?.ReceiveResult(data.message);
        }

        private void HandleMotionBroadcastClient(PlayerID senderPlayer, GC2MotionCommandPacket data, bool asServer)
        {
            if (asServer) return;
            LogDash(data.message, $"client received motion broadcast sender={senderPlayer.id}");
            GetMotionManager()?.ReceiveBroadcast(data.message);
        }

        private void LogDash(NetworkMotionCommandMessage message, string prefix)
        {
            if (message.Command.commandType != NetworkMotionCommandType.Dash) return;
            LogDash(
                $"{prefix} character={message.CharacterNetworkId} seq={message.Command.sequenceNumber} " +
                $"dir={FormatVector(message.Command.GetDirection())} speed={message.Command.GetSpeed():F2} " +
                $"gravity={message.Command.GetGravity():F2} duration={message.Command.GetDuration():F2}");
        }

        private void LogDash(NetworkAnimationGestureCommandMessage message, string prefix)
        {
            ConfigGesture config = message.Command.ToConfigGesture();
            LogDash(
                $"{prefix} character={message.CharacterNetworkId} clipHash={message.Command.ClipHash} " +
                $"flags=0x{message.Command.Flags:X2} blend={message.Command.BlendMode} " +
                $"stopPrevious={message.Command.StopPreviousGestures} delay={config.DelayIn:F3} " +
                $"duration={config.Duration:F3} speed={config.Speed:F3} " +
                $"transitionIn={config.TransitionIn:F3} transitionOut={config.TransitionOut:F3} " +
                $"rawDuration={message.Command.Duration} rawSpeed={message.Command.Speed}");
        }

        private void LogState(NetworkAnimationStateCommandMessage message, string prefix)
        {
            ConfigState config = message.Command.ToConfigState();
            LogState(
                $"{prefix} character={message.CharacterNetworkId} animationId={message.Command.AnimationId} " +
                $"type={message.Command.StateType} layer={message.Command.Layer} " +
                $"flags=0x{message.Command.Flags:X2} blend={message.Command.BlendMode} " +
                $"rootMotion={message.Command.RootMotion} delay={config.DelayIn:F3} " +
                $"duration={config.Duration:F3} speed={config.Speed:F3} weight={config.Weight:F3} " +
                $"transitionIn={config.TransitionIn:F3} transitionOut={config.TransitionOut:F3} " +
                $"rawDuration={message.Command.Duration} rawSpeed={message.Command.Speed} " +
                $"rawWeight={message.Command.Weight}");
        }

        private void LogStopState(NetworkAnimationStopStateCommandMessage message, string prefix)
        {
            LogState(
                $"{prefix} character={message.CharacterNetworkId} layer={message.Command.Layer} " +
                $"delay={message.Command.GetDelay():F3} transitionOut={message.Command.GetTransitionOut():F3} " +
                $"rawDelay={message.Command.Delay} rawTransitionOut={message.Command.TransitionOut}");
        }

        private void LogState(string message)
        {
            if (!m_LogDashDiagnostics) return;
            Debug.Log($"[NetworkStateDebug][PurrNetAnimationMotionBridge] {message}", this);
        }

        private void LogDash(string message)
        {
            if (!m_LogDashDiagnostics) return;
            Debug.Log($"[NetworkDashDebug][PurrNetAnimationMotionBridge] {message}", this);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private static bool TryConvertSenderClientId(PlayerID playerId, out uint senderClientId)
        {
            ulong raw = playerId.id;
            return NetworkTransportBridge.TryConvertSenderClientId(raw, out senderClientId);
        }

        private static bool TryGetPlayerId(NetworkManager manager, uint clientId, out PlayerID playerId)
        {
            playerId = default;
            if (manager == null) return false;

            var players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerID pid = players[i];
                if (pid.id == clientId)
                {
                    playerId = pid;
                    return true;
                }
            }

            return false;
        }
    }
}
