using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Serializable]
    public struct NetworkMotionCommandMessage
    {
        public uint CharacterNetworkId;
        public NetworkMotionCommand Command;
    }

    [Serializable]
    public struct NetworkMotionResultMessage
    {
        public uint CharacterNetworkId;
        public NetworkMotionResult Result;
    }

    /// <summary>
    /// Transport-agnostic router for semantic GC2 motion commands such as Dash,
    /// Teleport and server-validated transient movement.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Network Motion Manager")]
    public sealed class NetworkMotionManager : NetworkSingleton<NetworkMotionManager>
    {
        private sealed class ControllerBinding
        {
            public UnitMotionNetworkController Controller;
            public uint NetworkId;
            public Action<NetworkMotionCommand> SendHandler;
            public Action<NetworkMotionCommand> BroadcastHandler;
            public Action<NetworkMotionConfig> ConfigHandler;
        }

        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;

        public Action<NetworkMotionCommandMessage> SendCommandToServer;
        public Action<uint, NetworkMotionResultMessage> SendResultToClient;
        public Action<NetworkMotionCommandMessage> BroadcastCommandToClients;

        public Func<uint, NetworkCharacter> GetNetworkCharacterById;

        private readonly Dictionary<uint, UnitMotionNetworkController> m_ControllersById = new(128);
        private readonly Dictionary<UnitMotionNetworkController, ControllerBinding> m_BindingsByController = new(128);

        [SerializeField] private bool m_LogDashDiagnostics = false;
        [SerializeField] private bool m_LogNavigationDiagnostics = false;

        private bool m_IsServer;
        private bool m_IsClient;

        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            LogDash($"initialized server={m_IsServer} client={m_IsClient}");
            RefreshControllerRegistry();
        }

        public void RegisterController(UnitMotionNetworkController controller)
        {
            if (controller == null || controller.Character == null) return;
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

            binding.SendHandler = command => HandleLocalCommand(binding, command);
            binding.BroadcastHandler = command => HandleServerBroadcast(binding, command);
            binding.ConfigHandler = _ => { };

            controller.OnSendCommand += binding.SendHandler;
            controller.OnBroadcastCommand += binding.BroadcastHandler;
            controller.OnConfigChanged += binding.ConfigHandler;

            m_BindingsByController[controller] = binding;
            m_ControllersById[networkId] = controller;
            LogDash($"registered '{controller.Character.name}' netId={networkId}");
        }

        public void UnregisterController(UnitMotionNetworkController controller)
        {
            if (controller == null) return;
            if (!m_BindingsByController.TryGetValue(controller, out ControllerBinding binding)) return;

            controller.OnSendCommand -= binding.SendHandler;
            controller.OnBroadcastCommand -= binding.BroadcastHandler;
            controller.OnConfigChanged -= binding.ConfigHandler;

            m_BindingsByController.Remove(controller);

            if (m_ControllersById.TryGetValue(binding.NetworkId, out UnitMotionNetworkController registered) &&
                registered == controller)
            {
                m_ControllersById.Remove(binding.NetworkId);
            }
        }

        public void RefreshControllerRegistry()
        {
            var characters = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < characters.Length; i++)
            {
                UnitMotionNetworkController controller = characters[i]?.MotionController;
                if (controller != null) RegisterController(controller);
            }
        }

        public void ReceiveCommand(uint senderClientId, NetworkMotionCommandMessage message)
        {
            if (!m_IsServer)
            {
                LogDash(message.Command, $"dropped server command: manager is not server sender={senderClientId}");
                return;
            }

            if (!CanAcceptClientCommand(senderClientId, message.CharacterNetworkId))
            {
                LogDash(
                    message.Command,
                    $"dropped server command: ownership validation failed sender={senderClientId} " +
                    $"character={message.CharacterNetworkId}");
                return;
            }

            if (!TryGetController(message.CharacterNetworkId, out UnitMotionNetworkController controller))
            {
                LogDash(message.Command, $"dropped server command: controller not found character={message.CharacterNetworkId}");
                return;
            }

            NetworkMotionResult result = controller.ProcessClientCommand(message.Command, senderClientId);
            LogDash(
                message.Command,
                $"server received command character={message.CharacterNetworkId} sender={senderClientId} " +
                $"approved={result.approved} reason={result.rejectionReason}");

            if (SendResultToClient == null)
            {
                LogDash(message.Command, $"cannot send result: SendResultToClient has no listeners sender={senderClientId}");
            }

            SendResultToClient?.Invoke(senderClientId, new NetworkMotionResultMessage
            {
                CharacterNetworkId = message.CharacterNetworkId,
                Result = result
            });
        }

        public void ReceiveResult(NetworkMotionResultMessage message)
        {
            if (!m_IsClient)
            {
                LogDash($"dropped motion result: manager is not client character={message.CharacterNetworkId}");
                return;
            }

            if (!TryGetController(message.CharacterNetworkId, out UnitMotionNetworkController controller))
            {
                LogDash($"dropped motion result: controller not found character={message.CharacterNetworkId}");
                return;
            }

            controller.HandleCommandResult(message.Result);
        }

        public void ReceiveBroadcast(NetworkMotionCommandMessage message)
        {
            if (!m_IsClient)
            {
                LogDash(message.Command, $"dropped broadcast: manager is not client character={message.CharacterNetworkId}");
                return;
            }

            if (!TryGetController(message.CharacterNetworkId, out UnitMotionNetworkController controller))
            {
                LogDash(message.Command, $"dropped broadcast: controller not found character={message.CharacterNetworkId}");
                return;
            }

            NetworkCharacter networkCharacter = controller.Character != null
                ? controller.Character.GetComponent<NetworkCharacter>()
                : null;

            // Network-aware local instructions can predict a command immediately.
            // Skip only matching predicted sequence numbers; strict server-authority
            // commands still apply on the owner when the server broadcast arrives.
            if (networkCharacter != null &&
                networkCharacter.IsOwnerInstance &&
                controller.ConsumePredictedCommand(message.Command))
            {
                LogDash(
                    message.Command,
                    $"skipped owner predicted broadcast character={message.CharacterNetworkId} seq={message.Command.sequenceNumber}");
                return;
            }

            LogDash(message.Command, $"applying broadcast character={message.CharacterNetworkId}");
            controller.ApplyBroadcastCommand(message.Command);
        }

        private void HandleLocalCommand(ControllerBinding binding, NetworkMotionCommand command)
        {
            if (binding == null || binding.NetworkId == 0) return;

            if (SendCommandToServer == null)
            {
                LogDash(command, $"cannot send local command: SendCommandToServer has no listeners character={binding.NetworkId}");
            }

            LogDash(command, $"sending local command to server character={binding.NetworkId}");
            SendCommandToServer?.Invoke(new NetworkMotionCommandMessage
            {
                CharacterNetworkId = binding.NetworkId,
                Command = command
            });
        }

        private void HandleServerBroadcast(ControllerBinding binding, NetworkMotionCommand command)
        {
            if (binding == null || binding.NetworkId == 0) return;
            if (!m_IsServer) return;

            if (BroadcastCommandToClients == null)
            {
                LogDash(command, $"cannot broadcast command: BroadcastCommandToClients has no listeners character={binding.NetworkId}");
            }

            LogDash(command, $"broadcasting command character={binding.NetworkId}");
            BroadcastCommandToClients?.Invoke(new NetworkMotionCommandMessage
            {
                CharacterNetworkId = binding.NetworkId,
                Command = command
            });
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

        private void LogDash(NetworkMotionCommand command, string message)
        {
            if (command.commandType == NetworkMotionCommandType.Dash)
            {
                LogDash(
                    $"{message} seq={command.sequenceNumber} dir={FormatVector(command.GetDirection())} " +
                    $"speed={command.GetSpeed():F2} gravity={command.GetGravity():F2} duration={command.GetDuration():F2}");
                return;
            }

            if (!IsNavigationCommand(command.commandType)) return;
            LogNavigation($"{message} {FormatNavigationCommand(command)}");
        }

        private void LogDash(string message)
        {
            if (!m_LogDashDiagnostics) return;
            Debug.Log($"[NetworkDashDebug][MotionManager] {message}", this);
        }

        private void LogNavigation(string message)
        {
            if (!m_LogNavigationDiagnostics) return;
            Debug.Log($"[NetworkNavigationDebug][MotionManager] {message}", this);
        }

        private static bool IsNavigationCommand(NetworkMotionCommandType commandType)
        {
            return commandType == NetworkMotionCommandType.MoveToPosition ||
                   commandType == NetworkMotionCommandType.FollowTarget ||
                   commandType == NetworkMotionCommandType.StopFollow;
        }

        private static string FormatNavigationCommand(NetworkMotionCommand command)
        {
            return command.commandType switch
            {
                NetworkMotionCommandType.MoveToPosition =>
                    $"MoveTo seq={command.sequenceNumber} target={FormatVector(command.GetPosition())} " +
                    $"stop={command.GetStopDistance():F2} priority={command.priority}",
                NetworkMotionCommandType.FollowTarget =>
                    $"Follow seq={command.sequenceNumber} targetId={command.targetNetworkId} " +
                    $"fallback={FormatVector(command.GetPosition())} min={command.GetFollowMinRadius():F2} " +
                    $"max={command.GetFollowMaxRadius():F2} priority={command.priority}",
                NetworkMotionCommandType.StopFollow =>
                    $"StopFollow seq={command.sequenceNumber} priority={command.priority}",
                _ => $"{command.commandType} seq={command.sequenceNumber}"
            };
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private bool TryGetController(uint characterNetworkId, out UnitMotionNetworkController controller)
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

            controller = networkCharacter != null ? networkCharacter.MotionController : null;
            if (controller == null) return false;

            RegisterController(controller);
            return true;
        }

        private static bool TryResolveNetworkCharacter(
            UnitMotionNetworkController controller,
            out NetworkCharacter networkCharacter)
        {
            networkCharacter = controller?.Character != null
                ? controller.Character.GetComponent<NetworkCharacter>()
                : null;

            return networkCharacter != null;
        }
    }
}
