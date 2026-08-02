using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Animation Motion Bridge")]
    [DefaultExecutionOrder(-395)]
    public sealed class FusionAnimationMotionTransportBridge : FusionModuleTransportBridgeBase,
        IFusionGameplayReadinessParticipant
    {
        private enum MessageType : ushort
        {
            AnimationState = 1,
            AnimationGesture = 2,
            AnimationStopState = 3,
            AnimationStopGesture = 4,
            MotionCommand = 5,
            MotionResult = 6
        }

        [SerializeField] private bool m_CreateManagersIfMissing = true;
        [SerializeField] private bool m_LogDiagnostics;

        private NetworkAnimationManager m_WiredAnimationManager;
        private NetworkMotionManager m_WiredMotionManager;
        private bool m_ManagersInitialized;
        private bool m_LastServer;
        private bool m_LastClient;

        protected override ushort ModuleId => FusionModuleIds.AnimationMotion;

        public string GameplayReadinessName => "Animation/Motion";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || TransportBridge == null ||
                !TransportBridge.IsClient)
            {
                return false;
            }

            WireManagers();
            if (!m_ManagersInitialized || m_WiredAnimationManager == null ||
                m_WiredMotionManager == null)
            {
                return false;
            }

            Character character = TransportBridge.ResolveCharacter(identity.NetworkId);
            NetworkCharacter networkCharacter = character != null
                ? character.GetComponent<NetworkCharacter>()
                : null;
            if (networkCharacter == null ||
                networkCharacter.NetworkId != identity.NetworkId ||
                networkCharacter.Role == NetworkCharacter.NetworkRole.None)
            {
                return false;
            }

            // Refresh is synchronous. Once these calls return, every initialized controller
            // present on the character has been offered to its manager's registry.
            m_WiredAnimationManager.RefreshControllerRegistry();
            m_WiredMotionManager.RefreshControllerRegistry();

            UnitAnimimNetworkController animation = networkCharacter.AnimimController;
            return animation == null || animation.IsInitialized;
        }

        protected override void OnModuleEnabled()
        {
            WireManagers();
        }

        protected override void OnModuleStarted()
        {
            WireManagers();
        }

        protected override void OnModuleUpdate()
        {
            WireManagers();
        }

        protected override void OnModuleDisabled()
        {
            UnwireManagers();
        }

        protected override void OnAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            m_ManagersInitialized = false;
            WireManagers();
        }

        public override string FullSnapshotProducerName => "Animation/Motion";

        protected override FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context)
        {
            WireManagers();
            if (!m_ManagersInitialized || m_WiredAnimationManager == null ||
                m_WiredMotionManager == null)
            {
                return context.Fail(
                    "Animation and motion managers are unavailable or not initialized.");
            }

            // These managers replicate semantic commands and have no persistent packet state.
            // Reporting an explicit zero-packet completion keeps the readiness barrier honest.
            return context.Complete();
        }

        protected override void HandleModuleMessage(FusionModuleMessage message)
        {
            bool request = TransportBridge != null &&
                           TransportBridge.IsServer &&
                           !message.FromAuthority;
            bool authorityMessage = TransportBridge != null &&
                                    TransportBridge.IsClient &&
                                    message.FromAuthority;

            switch ((MessageType)message.MessageType)
            {
                case MessageType.AnimationState:
                    if (request &&
                        TryRead(message, out NetworkAnimationStateCommandMessage stateRequest))
                    {
                        Log($"Receive state request sender={message.SenderClientId}");
                        GetAnimationManager()?.ReceiveStateCommand(
                            message.SenderClientId, stateRequest);
                    }
                    else if (authorityMessage &&
                             TryRead(message, out NetworkAnimationStateCommandMessage stateBroadcast))
                    {
                        GetAnimationManager()?.ReceiveStateBroadcast(stateBroadcast);
                    }
                    break;
                case MessageType.AnimationGesture:
                    if (request &&
                        TryRead(message, out NetworkAnimationGestureCommandMessage gestureRequest))
                    {
                        Log($"Receive gesture request sender={message.SenderClientId}");
                        GetAnimationManager()?.ReceiveGestureCommand(
                            message.SenderClientId, gestureRequest);
                    }
                    else if (authorityMessage &&
                             TryRead(message, out NetworkAnimationGestureCommandMessage gestureBroadcast))
                    {
                        GetAnimationManager()?.ReceiveGestureBroadcast(gestureBroadcast);
                    }
                    break;
                case MessageType.AnimationStopState:
                    if (request &&
                        TryRead(message, out NetworkAnimationStopStateCommandMessage stopStateRequest))
                    {
                        GetAnimationManager()?.ReceiveStopStateCommand(
                            message.SenderClientId, stopStateRequest);
                    }
                    else if (authorityMessage &&
                             TryRead(message, out NetworkAnimationStopStateCommandMessage stopStateBroadcast))
                    {
                        GetAnimationManager()?.ReceiveStopStateBroadcast(stopStateBroadcast);
                    }
                    break;
                case MessageType.AnimationStopGesture:
                    if (request &&
                        TryRead(message, out NetworkAnimationStopGestureCommandMessage stopGestureRequest))
                    {
                        GetAnimationManager()?.ReceiveStopGestureCommand(
                            message.SenderClientId, stopGestureRequest);
                    }
                    else if (authorityMessage &&
                             TryRead(message, out NetworkAnimationStopGestureCommandMessage stopGestureBroadcast))
                    {
                        GetAnimationManager()?.ReceiveStopGestureBroadcast(stopGestureBroadcast);
                    }
                    break;
                case MessageType.MotionCommand:
                    if (request &&
                        TryRead(message, out NetworkMotionCommandMessage motionRequest))
                    {
                        Log($"Receive motion request sender={message.SenderClientId}");
                        GetMotionManager()?.ReceiveCommand(message.SenderClientId, motionRequest);
                    }
                    else if (authorityMessage &&
                             TryRead(message, out NetworkMotionCommandMessage motionBroadcast))
                    {
                        GetMotionManager()?.ReceiveBroadcast(motionBroadcast);
                    }
                    break;
                case MessageType.MotionResult:
                    if (authorityMessage &&
                        TryRead(message, out NetworkMotionResultMessage motionResult))
                        GetMotionManager()?.ReceiveResult(motionResult);
                    break;
            }
        }

        private void WireManagers()
        {
            NetworkAnimationManager animationManager = GetAnimationManager();
            if (m_WiredAnimationManager != null &&
                m_WiredAnimationManager != animationManager) UnwireAnimationManager();
            if (animationManager != null)
            {
                m_WiredAnimationManager = animationManager;
                animationManager.SendStateCommandToServer -= SendStateCommand;
                animationManager.SendStateCommandToServer += SendStateCommand;
                animationManager.SendGestureCommandToServer -= SendGestureCommand;
                animationManager.SendGestureCommandToServer += SendGestureCommand;
                animationManager.SendStopStateCommandToServer -= SendStopStateCommand;
                animationManager.SendStopStateCommandToServer += SendStopStateCommand;
                animationManager.SendStopGestureCommandToServer -= SendStopGestureCommand;
                animationManager.SendStopGestureCommandToServer += SendStopGestureCommand;
                animationManager.BroadcastStateCommandToClients -= BroadcastStateCommand;
                animationManager.BroadcastStateCommandToClients += BroadcastStateCommand;
                animationManager.BroadcastGestureCommandToClients -= BroadcastGestureCommand;
                animationManager.BroadcastGestureCommandToClients += BroadcastGestureCommand;
                animationManager.BroadcastStopStateCommandToClients -= BroadcastStopStateCommand;
                animationManager.BroadcastStopStateCommandToClients += BroadcastStopStateCommand;
                animationManager.BroadcastStopGestureCommandToClients -= BroadcastStopGestureCommand;
                animationManager.BroadcastStopGestureCommandToClients += BroadcastStopGestureCommand;
                animationManager.GetNetworkCharacterById = ResolveNetworkCharacter;
            }

            NetworkMotionManager motionManager = GetMotionManager();
            if (m_WiredMotionManager != null &&
                m_WiredMotionManager != motionManager) UnwireMotionManager();
            if (motionManager != null)
            {
                m_WiredMotionManager = motionManager;
                motionManager.SendCommandToServer -= SendMotionCommand;
                motionManager.SendCommandToServer += SendMotionCommand;
                motionManager.SendResultToClient -= SendMotionResult;
                motionManager.SendResultToClient += SendMotionResult;
                motionManager.BroadcastCommandToClients -= BroadcastMotionCommand;
                motionManager.BroadcastCommandToClients += BroadcastMotionCommand;
                motionManager.GetNetworkCharacterById = ResolveNetworkCharacter;
            }

            bool isServer = TransportBridge != null && TransportBridge.IsServer;
            bool isClient = TransportBridge != null && TransportBridge.IsClient;
            if (!m_ManagersInitialized || isServer != m_LastServer || isClient != m_LastClient)
            {
                animationManager?.Initialize(isServer, isClient);
                motionManager?.Initialize(isServer, isClient);
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
            UnwireAnimationManager();
            UnwireMotionManager();
            m_ManagersInitialized = false;
        }

        private void UnwireAnimationManager()
        {
            NetworkAnimationManager manager = m_WiredAnimationManager;
            if (manager == null) return;
            manager.SendStateCommandToServer -= SendStateCommand;
            manager.SendGestureCommandToServer -= SendGestureCommand;
            manager.SendStopStateCommandToServer -= SendStopStateCommand;
            manager.SendStopGestureCommandToServer -= SendStopGestureCommand;
            manager.BroadcastStateCommandToClients -= BroadcastStateCommand;
            manager.BroadcastGestureCommandToClients -= BroadcastGestureCommand;
            manager.BroadcastStopStateCommandToClients -= BroadcastStopStateCommand;
            manager.BroadcastStopGestureCommandToClients -= BroadcastStopGestureCommand;
            if (ReferenceEquals(manager.GetNetworkCharacterById?.Target, this))
                manager.GetNetworkCharacterById = null;
            m_WiredAnimationManager = null;
        }

        private void UnwireMotionManager()
        {
            NetworkMotionManager manager = m_WiredMotionManager;
            if (manager == null) return;
            manager.SendCommandToServer -= SendMotionCommand;
            manager.SendResultToClient -= SendMotionResult;
            manager.BroadcastCommandToClients -= BroadcastMotionCommand;
            if (ReferenceEquals(manager.GetNetworkCharacterById?.Target, this))
                manager.GetNetworkCharacterById = null;
            m_WiredMotionManager = null;
        }

        private NetworkAnimationManager GetAnimationManager()
        {
            NetworkAnimationManager manager = NetworkAnimationManager.Instance;
            if (manager == null)
                manager = FindFirstObjectByType<NetworkAnimationManager>(FindObjectsInactive.Include);
            if (manager != null || !m_CreateManagersIfMissing) return manager;
            return new GameObject("Network Animation Manager")
                .AddComponent<NetworkAnimationManager>();
        }

        private NetworkMotionManager GetMotionManager()
        {
            NetworkMotionManager manager = NetworkMotionManager.Instance;
            if (manager == null)
                manager = FindFirstObjectByType<NetworkMotionManager>(FindObjectsInactive.Include);
            if (manager != null || !m_CreateManagersIfMissing) return manager;
            return new GameObject("Network Motion Manager").AddComponent<NetworkMotionManager>();
        }

        private NetworkCharacter ResolveNetworkCharacter(uint networkId)
        {
            Character character =
                TransportBridge != null ? TransportBridge.ResolveCharacter(networkId) : null;
            return character != null ? character.GetComponent<NetworkCharacter>() : null;
        }

        private void SendStateCommand(NetworkAnimationStateCommandMessage message) =>
            SendToAuthority((ushort)MessageType.AnimationState, message);
        private void SendGestureCommand(NetworkAnimationGestureCommandMessage message) =>
            SendToAuthority((ushort)MessageType.AnimationGesture, message);
        private void SendStopStateCommand(NetworkAnimationStopStateCommandMessage message) =>
            SendToAuthority((ushort)MessageType.AnimationStopState, message);
        private void SendStopGestureCommand(NetworkAnimationStopGestureCommandMessage message) =>
            SendToAuthority((ushort)MessageType.AnimationStopGesture, message);
        private void SendMotionCommand(NetworkMotionCommandMessage message) =>
            SendToAuthority((ushort)MessageType.MotionCommand, message);

        private void SendMotionResult(uint clientId, NetworkMotionResultMessage message) =>
            SendToClient(clientId, (ushort)MessageType.MotionResult, message);

        private void BroadcastStateCommand(NetworkAnimationStateCommandMessage message) =>
            Broadcast((ushort)MessageType.AnimationState, message);
        private void BroadcastGestureCommand(NetworkAnimationGestureCommandMessage message) =>
            Broadcast((ushort)MessageType.AnimationGesture, message);
        private void BroadcastStopStateCommand(NetworkAnimationStopStateCommandMessage message) =>
            Broadcast((ushort)MessageType.AnimationStopState, message);
        private void BroadcastStopGestureCommand(NetworkAnimationStopGestureCommandMessage message) =>
            Broadcast((ushort)MessageType.AnimationStopGesture, message);
        private void BroadcastMotionCommand(NetworkMotionCommandMessage message) =>
            Broadcast((ushort)MessageType.MotionCommand, message);

        private void Log(string message)
        {
            if (m_LogDiagnostics)
                Debug.Log($"[FusionAnimationMotionTransportBridge] {message}", this);
        }
    }
}
