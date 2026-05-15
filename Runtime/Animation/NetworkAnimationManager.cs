using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Serializable]
    public struct NetworkAnimationStateCommandMessage
    {
        public uint CharacterNetworkId;
        public NetworkStateCommand Command;
    }

    [Serializable]
    public struct NetworkAnimationGestureCommandMessage
    {
        public uint CharacterNetworkId;
        public NetworkGestureCommand Command;
    }

    [Serializable]
    public struct NetworkAnimationStopStateCommandMessage
    {
        public uint CharacterNetworkId;
        public NetworkStopStateCommand Command;
    }

    [Serializable]
    public struct NetworkAnimationStopGestureCommandMessage
    {
        public uint CharacterNetworkId;
        public NetworkStopGestureCommand Command;
    }

    /// <summary>
    /// Transport-agnostic router for GC2 Animim state and gesture commands.
    /// Transports subscribe to the delegates and feed received packets back through
    /// the receive methods. The commands remain cosmetic; ownership is only used to
    /// prevent clients from driving other actors' animations.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Network Animation Manager")]
    public sealed class NetworkAnimationManager : NetworkSingleton<NetworkAnimationManager>
    {
        private sealed class ControllerBinding
        {
            public UnitAnimimNetworkController Controller;
            public uint NetworkId;
            public Action<NetworkStateCommand> StateHandler;
            public Action<NetworkGestureCommand> GestureHandler;
            public Action<NetworkStopStateCommand> StopStateHandler;
            public Action<NetworkStopGestureCommand> StopGestureHandler;
        }

        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;

        public Action<NetworkAnimationStateCommandMessage> SendStateCommandToServer;
        public Action<NetworkAnimationGestureCommandMessage> SendGestureCommandToServer;
        public Action<NetworkAnimationStopStateCommandMessage> SendStopStateCommandToServer;
        public Action<NetworkAnimationStopGestureCommandMessage> SendStopGestureCommandToServer;

        public Action<NetworkAnimationStateCommandMessage> BroadcastStateCommandToClients;
        public Action<NetworkAnimationGestureCommandMessage> BroadcastGestureCommandToClients;
        public Action<NetworkAnimationStopStateCommandMessage> BroadcastStopStateCommandToClients;
        public Action<NetworkAnimationStopGestureCommandMessage> BroadcastStopGestureCommandToClients;

        public Func<uint, NetworkCharacter> GetNetworkCharacterById;

        private readonly Dictionary<uint, UnitAnimimNetworkController> m_ControllersById = new(128);
        private readonly Dictionary<UnitAnimimNetworkController, ControllerBinding> m_BindingsByController = new(128);

        [SerializeField] private bool m_LogDashDiagnostics = false;

        private bool m_IsServer;
        private bool m_IsClient;

        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            LogGesture($"initialized server={m_IsServer} client={m_IsClient}");
            RefreshControllerRegistry();
        }

        public void RegisterController(UnitAnimimNetworkController controller)
        {
            if (controller == null || !controller.IsInitialized) return;
            if (!TryResolveNetworkCharacter(controller, out NetworkCharacter networkCharacter)) return;

            uint networkId = networkCharacter.NetworkId;
            if (networkId == 0) return;

            if (m_BindingsByController.TryGetValue(controller, out ControllerBinding existing))
            {
                if (existing.NetworkId == networkId)
                {
                    m_ControllersById[networkId] = controller;
                    return;
                }

                UnregisterController(controller);
            }

            var binding = new ControllerBinding
            {
                Controller = controller,
                NetworkId = networkId
            };

            binding.StateHandler = command => HandleLocalStateCommand(binding, command);
            binding.GestureHandler = command => HandleLocalGestureCommand(binding, command);
            binding.StopStateHandler = command => HandleLocalStopStateCommand(binding, command);
            binding.StopGestureHandler = command => HandleLocalStopGestureCommand(binding, command);

            controller.OnStateCommandReady += binding.StateHandler;
            controller.OnGestureCommandReady += binding.GestureHandler;
            controller.OnStopStateCommandReady += binding.StopStateHandler;
            controller.OnStopGestureCommandReady += binding.StopGestureHandler;

            m_BindingsByController[controller] = binding;
            m_ControllersById[networkId] = controller;
            LogGesture($"registered '{controller.name}' netId={networkId} local={controller.IsLocalPlayer}");
        }

        public void UnregisterController(UnitAnimimNetworkController controller)
        {
            if (controller == null) return;
            if (!m_BindingsByController.TryGetValue(controller, out ControllerBinding binding)) return;

            controller.OnStateCommandReady -= binding.StateHandler;
            controller.OnGestureCommandReady -= binding.GestureHandler;
            controller.OnStopStateCommandReady -= binding.StopStateHandler;
            controller.OnStopGestureCommandReady -= binding.StopGestureHandler;

            m_BindingsByController.Remove(controller);

            if (m_ControllersById.TryGetValue(binding.NetworkId, out UnitAnimimNetworkController registered) &&
                registered == controller)
            {
                m_ControllersById.Remove(binding.NetworkId);
            }
        }

        public void RefreshControllerRegistry()
        {
            var controllers = FindObjectsByType<UnitAnimimNetworkController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(controllers[i]);
            }
        }

        public void ReceiveStateCommand(uint senderClientId, NetworkAnimationStateCommandMessage message)
        {
            if (!m_IsServer)
            {
                LogState(message.Command, $"dropped server state: manager is not server sender={senderClientId}");
                return;
            }

            if (!CanAcceptClientCommand(senderClientId, message.CharacterNetworkId))
            {
                LogState(
                    message.Command,
                    $"dropped server state: ownership validation failed sender={senderClientId} " +
                    $"character={message.CharacterNetworkId}");
                return;
            }

            LogState(
                message.Command,
                $"server accepted state character={message.CharacterNetworkId} sender={senderClientId}");

            BroadcastStateCommandToClients?.Invoke(message);
        }

        public void ReceiveGestureCommand(uint senderClientId, NetworkAnimationGestureCommandMessage message)
        {
            if (!m_IsServer)
            {
                LogGesture(message.Command, $"dropped server gesture: manager is not server sender={senderClientId}");
                return;
            }

            if (!CanAcceptClientCommand(senderClientId, message.CharacterNetworkId))
            {
                LogGesture(
                    message.Command,
                    $"dropped server gesture: ownership validation failed sender={senderClientId} " +
                    $"character={message.CharacterNetworkId}");
                return;
            }

            LogGesture(
                message.Command,
                $"server accepted gesture character={message.CharacterNetworkId} sender={senderClientId}");

            if (BroadcastGestureCommandToClients == null)
            {
                LogGesture(message.Command, "cannot broadcast gesture: BroadcastGestureCommandToClients has no listeners");
            }
            BroadcastGestureCommandToClients?.Invoke(message);
        }

        public void ReceiveStopStateCommand(uint senderClientId, NetworkAnimationStopStateCommandMessage message)
        {
            if (!m_IsServer)
            {
                LogStopState(message.Command, $"dropped server stop-state: manager is not server sender={senderClientId}");
                return;
            }

            if (!CanAcceptClientCommand(senderClientId, message.CharacterNetworkId))
            {
                LogStopState(
                    message.Command,
                    $"dropped server stop-state: ownership validation failed sender={senderClientId} " +
                    $"character={message.CharacterNetworkId}");
                return;
            }

            LogStopState(
                message.Command,
                $"server accepted stop-state character={message.CharacterNetworkId} sender={senderClientId}");

            BroadcastStopStateCommandToClients?.Invoke(message);
        }

        public void ReceiveStopGestureCommand(uint senderClientId, NetworkAnimationStopGestureCommandMessage message)
        {
            if (!m_IsServer) return;
            if (!CanAcceptClientCommand(senderClientId, message.CharacterNetworkId)) return;

            BroadcastStopGestureCommandToClients?.Invoke(message);
        }

        public void ReceiveStateBroadcast(NetworkAnimationStateCommandMessage message)
        {
            if (!m_IsClient)
            {
                LogState(message.Command, $"dropped state broadcast: manager is not client character={message.CharacterNetworkId}");
                return;
            }

            if (!TryGetController(message.CharacterNetworkId, out UnitAnimimNetworkController controller))
            {
                LogState(message.Command, $"dropped state broadcast: controller not found character={message.CharacterNetworkId}");
                return;
            }

            LogState(
                message.Command,
                $"applying state broadcast character={message.CharacterNetworkId} controller={controller.name} " +
                $"local={controller.IsLocalPlayer}");
            _ = controller.ApplyStateCommand(message.Command);
        }

        public void ReceiveGestureBroadcast(NetworkAnimationGestureCommandMessage message)
        {
            if (!m_IsClient)
            {
                LogGesture(message.Command, $"dropped gesture broadcast: manager is not client character={message.CharacterNetworkId}");
                return;
            }

            if (!TryGetController(message.CharacterNetworkId, out UnitAnimimNetworkController controller))
            {
                LogGesture(message.Command, $"dropped gesture broadcast: controller not found character={message.CharacterNetworkId}");
                return;
            }

            LogGesture(
                message.Command,
                $"applying gesture broadcast character={message.CharacterNetworkId} controller={controller.name} " +
                $"local={controller.IsLocalPlayer}");
            _ = controller.ApplyGestureCommand(message.Command);
        }

        public void ReceiveStopStateBroadcast(NetworkAnimationStopStateCommandMessage message)
        {
            if (!m_IsClient)
            {
                LogStopState(message.Command, $"dropped stop-state broadcast: manager is not client character={message.CharacterNetworkId}");
                return;
            }

            if (!TryGetController(message.CharacterNetworkId, out UnitAnimimNetworkController controller))
            {
                LogStopState(message.Command, $"dropped stop-state broadcast: controller not found character={message.CharacterNetworkId}");
                return;
            }

            LogStopState(
                message.Command,
                $"applying stop-state broadcast character={message.CharacterNetworkId} controller={controller.name} " +
                $"local={controller.IsLocalPlayer}");
            controller.ApplyStopStateCommand(message.Command);
        }

        public void ReceiveStopGestureBroadcast(NetworkAnimationStopGestureCommandMessage message)
        {
            if (!m_IsClient) return;
            if (!TryGetController(message.CharacterNetworkId, out UnitAnimimNetworkController controller)) return;

            controller.ApplyStopGestureCommand(message.Command);
        }

        private void HandleLocalStateCommand(ControllerBinding binding, NetworkStateCommand command)
        {
            if (binding == null || binding.NetworkId == 0) return;

            var message = new NetworkAnimationStateCommandMessage
            {
                CharacterNetworkId = binding.NetworkId,
                Command = command
            };

            LogState(command, $"routing local state character={binding.NetworkId}");
            RouteLocalCommand(message, SendStateCommandToServer, BroadcastStateCommandToClients);
        }

        private void HandleLocalGestureCommand(ControllerBinding binding, NetworkGestureCommand command)
        {
            if (binding == null || binding.NetworkId == 0) return;

            var message = new NetworkAnimationGestureCommandMessage
            {
                CharacterNetworkId = binding.NetworkId,
                Command = command
            };

            LogGesture(command, $"routing local gesture character={binding.NetworkId}");
            RouteLocalCommand(message, SendGestureCommandToServer, BroadcastGestureCommandToClients);
        }

        private void HandleLocalStopStateCommand(ControllerBinding binding, NetworkStopStateCommand command)
        {
            if (binding == null || binding.NetworkId == 0) return;

            var message = new NetworkAnimationStopStateCommandMessage
            {
                CharacterNetworkId = binding.NetworkId,
                Command = command
            };

            LogStopState(command, $"routing local stop-state character={binding.NetworkId}");
            RouteLocalCommand(message, SendStopStateCommandToServer, BroadcastStopStateCommandToClients);
        }

        private void HandleLocalStopGestureCommand(ControllerBinding binding, NetworkStopGestureCommand command)
        {
            if (binding == null || binding.NetworkId == 0) return;

            var message = new NetworkAnimationStopGestureCommandMessage
            {
                CharacterNetworkId = binding.NetworkId,
                Command = command
            };

            RouteLocalCommand(message, SendStopGestureCommandToServer, BroadcastStopGestureCommandToClients);
        }

        private void RouteLocalCommand<T>(T message, Action<T> sendToServer, Action<T> broadcastToClients)
        {
            if (m_IsServer)
            {
                if (broadcastToClients == null)
                {
                    LogGesture("cannot broadcast local animation command: broadcast delegate has no listeners");
                }
                broadcastToClients?.Invoke(message);
                return;
            }

            if (sendToServer == null)
            {
                LogGesture("cannot send local animation command: server delegate has no listeners");
            }
            sendToServer?.Invoke(message);
        }

        private bool CanAcceptClientCommand(uint senderClientId, uint characterNetworkId)
        {
            if (!NetworkTransportBridge.IsValidClientId(senderClientId) || characterNetworkId == 0)
            {
                return false;
            }

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge == null) return false;

            return bridge.TryVerifyActorOwnership(senderClientId, characterNetworkId, out _);
        }

        private void LogState(NetworkStateCommand command, string message)
        {
            ConfigState config = command.ToConfigState();
            LogState(
                $"{message} animationId={command.AnimationId} type={command.StateType} " +
                $"layer={command.Layer} flags=0x{command.Flags:X2} blend={command.BlendMode} " +
                $"rootMotion={command.RootMotion} delay={config.DelayIn:F3} " +
                $"duration={config.Duration:F3} speed={config.Speed:F3} weight={config.Weight:F3} " +
                $"transitionIn={config.TransitionIn:F3} transitionOut={config.TransitionOut:F3} " +
                $"rawDelay={command.DelayIn} rawDuration={command.Duration} rawSpeed={command.Speed} " +
                $"rawWeight={command.Weight} rawTransitionIn={command.TransitionIn} " +
                $"rawTransitionOut={command.TransitionOut}");
        }

        private void LogStopState(NetworkStopStateCommand command, string message)
        {
            LogState(
                $"{message} layer={command.Layer} delay={command.GetDelay():F3} " +
                $"transitionOut={command.GetTransitionOut():F3} rawDelay={command.Delay} " +
                $"rawTransitionOut={command.TransitionOut}");
        }

        private void LogState(string message)
        {
            if (!m_LogDashDiagnostics) return;
            Debug.Log($"[NetworkStateDebug][AnimationManager] {message}", this);
        }

        private void LogGesture(NetworkGestureCommand command, string message)
        {
            ConfigGesture config = command.ToConfigGesture();
            LogGesture(
                $"{message} clipHash={command.ClipHash} flags=0x{command.Flags:X2} " +
                $"blend={command.BlendMode} rootMotion={command.RootMotion} " +
                $"stopPrevious={command.StopPreviousGestures} delay={config.DelayIn:F3} " +
                $"duration={config.Duration:F3} speed={config.Speed:F3} " +
                $"transitionIn={config.TransitionIn:F3} transitionOut={config.TransitionOut:F3} " +
                $"rawDelay={command.DelayIn} rawDuration={command.Duration} rawSpeed={command.Speed} " +
                $"rawTransitionIn={command.TransitionIn} rawTransitionOut={command.TransitionOut}");
        }

        private void LogGesture(string message)
        {
            if (!m_LogDashDiagnostics) return;
            Debug.Log($"[NetworkDashDebug][AnimationManager] {message}", this);
        }

        private bool TryGetController(uint characterNetworkId, out UnitAnimimNetworkController controller)
        {
            controller = null;
            if (characterNetworkId == 0) return false;

            if (m_ControllersById.TryGetValue(characterNetworkId, out controller) && controller != null)
            {
                return true;
            }

            NetworkCharacter networkCharacter = GetNetworkCharacterById?.Invoke(characterNetworkId);
            if (networkCharacter == null)
            {
                Character character = NetworkTransportBridge.Active?.ResolveCharacter(characterNetworkId);
                networkCharacter = character != null ? character.GetComponent<NetworkCharacter>() : null;
            }

            controller = networkCharacter != null
                ? networkCharacter.GetComponent<UnitAnimimNetworkController>()
                : null;

            if (controller == null || !controller.IsInitialized) return false;

            RegisterController(controller);
            return true;
        }

        private static bool TryResolveNetworkCharacter(
            UnitAnimimNetworkController controller,
            out NetworkCharacter networkCharacter)
        {
            networkCharacter = controller != null ? controller.GetComponent<NetworkCharacter>() : null;
            return networkCharacter != null;
        }
    }
}
