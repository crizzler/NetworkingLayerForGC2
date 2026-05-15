#if GC2_SHOOTER
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterManager
    {
        private void LogDiagnostics(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.Log($"[NetworkShooterManager] {message}", this);
        }

        private void LogDiagnosticsWarning(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.LogWarning($"[NetworkShooterManager] {message}", this);
        }

        private void Start()
        {
            LogDiagnostics(
                $"started server={m_IsServer} client={m_IsClient} controllers={m_Controllers.Count} " +
                $"delegates shotReq={(SendShotRequestToServer != null)} hitReq={(SendHitRequestToServer != null)} " +
                $"reloadReq={(SendReloadRequestToServer != null)} shotBroadcast={(BroadcastShotToAllClients != null)} " +
                $"hitBroadcast={(BroadcastHitToAllClients != null)} reloadBroadcast={(BroadcastReloadToAllClients != null)}");

            if (!m_IsServer && !m_IsClient)
            {
                LogDiagnosticsWarning(
                    "manager has not been initialized by a transport bridge yet. " +
                    "If this remains true after the network session starts, Shooter sync has no active transport wiring.");
            }
        }

        private void OnDisable()
        {
            LogDiagnostics($"disabled; registeredControllers={m_Controllers.Count}");
            SecurityIntegration.SetModuleServerContext("Shooter", false);
            m_ValidatedShotReferences.Clear();

            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Initialize the manager with network role.
        /// </summary>
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            SecurityIntegration.SetModuleServerContext("Shooter", isServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(isServer, () => GetNetworkTimeFunc?.Invoke() ?? Time.time);
            SyncPatchHooks();

            LogDiagnostics(
                $"initialized server={isServer} client={isClient} controllers={m_Controllers.Count} " +
                $"delegates shotReq={(SendShotRequestToServer != null)} hitReq={(SendHitRequestToServer != null)} " +
                $"reloadReq={(SendReloadRequestToServer != null)} shotResp={(SendShotResponseToClient != null)} " +
                $"hitResp={(SendHitResponseToClient != null)} reloadResp={(SendReloadResponseToClient != null)} " +
                $"shotBroadcast={(BroadcastShotToAllClients != null)} hitBroadcast={(BroadcastHitToAllClients != null)} " +
                $"reloadBroadcast={(BroadcastReloadToAllClients != null)} lookup={(GetCharacterByNetworkIdFunc != null)}");
        }

        private void SyncPatchHooks()
        {
            if (!m_IsServer && !m_IsClient)
            {
                if (m_PatchHooks != null) m_PatchHooks.Initialize(false);
                return;
            }

            if (m_PatchHooks == null)
            {
                m_PatchHooks = GetComponent<NetworkShooterPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkShooterPatchHooks>();
                }
            }

            m_PatchHooks.Initialize(true);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONTROLLER REGISTRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a NetworkShooterController for a character.
        /// </summary>
        public void RegisterController(uint networkId, NetworkShooterController controller)
        {
            if (controller == null) return;

            if (m_Controllers.TryGetValue(networkId, out var previous) && previous != null && previous != controller)
            {
                previous.OnShotRequestSent -= OnControllerShotRequestSent;
                previous.OnHitDetected -= OnControllerHitDetected;
                previous.OnReloadRequestSent -= OnControllerReloadRequestSent;
            }

            m_Controllers[networkId] = controller;

            controller.OnShotRequestSent -= OnControllerShotRequestSent;
            controller.OnHitDetected -= OnControllerHitDetected;
            controller.OnReloadRequestSent -= OnControllerReloadRequestSent;

            controller.OnShotRequestSent += OnControllerShotRequestSent;
            controller.OnHitDetected += OnControllerHitDetected;
            controller.OnReloadRequestSent += OnControllerReloadRequestSent;

            var networkCharacter = controller.GetComponent<NetworkCharacter>();
            LogDiagnostics(
                $"registered controller netId={networkId} name={controller.name} " +
                $"role={(networkCharacter != null ? networkCharacter.Role.ToString() : "no NetworkCharacter")} " +
                $"server={controller.IsServer} localClient={controller.IsLocalClient} " +
                $"transportDelegates shot={(SendShotRequestToServer != null)} hit={(SendHitRequestToServer != null)} " +
                $"reload={(SendReloadRequestToServer != null)}");
        }

        /// <summary>
        /// Unregister a controller.
        /// </summary>
        public void UnregisterController(uint networkId)
        {
            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                string controllerName = controller != null ? controller.name : "<destroyed>";
                if (controller != null)
                {
                    controller.OnShotRequestSent -= OnControllerShotRequestSent;
                    controller.OnHitDetected -= OnControllerHitDetected;
                    controller.OnReloadRequestSent -= OnControllerReloadRequestSent;
                }

                m_Controllers.Remove(networkId);
                LogDiagnostics($"unregistered controller netId={networkId} name={controllerName}");
            }
        }

        /// <summary>
        /// Get a NetworkCharacter by network ID.
        /// </summary>
        public NetworkCharacter GetCharacterByNetworkId(uint networkId)
        {
            if (GetCharacterByNetworkIdFunc != null)
            {
                return GetCharacterByNetworkIdFunc(networkId);
            }

            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                return controller.GetComponent<NetworkCharacter>();
            }

            return null;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: SENDING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void OnControllerShotRequestSent(NetworkShotRequest request)
        {
            if (!m_IsClient)
            {
                LogDiagnosticsWarning(
                    $"dropped shot request because manager is not client actor={request.ActorNetworkId} req={request.RequestId}");
                return;
            }

            if (m_LogShotRequests || m_LogDiagnostics || NetworkShooterDebug.ForceDiagnostics)
            {
                Debug.Log($"[NetworkShooterManager] Shot request: Shooter={request.ShooterNetworkId}, " +
                         $"Pos={request.MuzzlePosition}, Dir={request.ShotDirection}");
            }

            if (SendShotRequestToServer == null)
            {
                LogDiagnosticsWarning(
                    $"shot request has no transport delegate actor={request.ActorNetworkId} req={request.RequestId}. " +
                    "Expected a Shooter transport bridge to assign SendShotRequestToServer.");
            }

            SendShotRequestToServer?.Invoke(request);
            m_Stats.ShotRequestsSent++;

            OnShotRequestSent?.Invoke(request);
        }

        private void OnControllerHitDetected(NetworkShooterHitRequest request)
        {
            if (!m_IsClient)
            {
                LogDiagnosticsWarning(
                    $"dropped hit request because manager is not client actor={request.ActorNetworkId} req={request.RequestId}");
                return;
            }

            if (m_LogHitRequests || m_LogDiagnostics || NetworkShooterDebug.ForceDiagnostics)
            {
                Debug.Log($"[NetworkShooterManager] Hit request: Target={request.TargetNetworkId}, " +
                         $"Point={request.HitPoint}");
            }

            if (SendHitRequestToServer == null)
            {
                LogDiagnosticsWarning(
                    $"hit request has no transport delegate actor={request.ActorNetworkId} req={request.RequestId}. " +
                    "Expected a Shooter transport bridge to assign SendHitRequestToServer.");
            }

            SendHitRequestToServer?.Invoke(request);
        }

        private void OnControllerReloadRequestSent(NetworkReloadRequest request)
        {
            if (!m_IsClient)
            {
                LogDiagnosticsWarning(
                    $"dropped reload request because manager is not client actor={request.ActorNetworkId} req={request.RequestId}");
                return;
            }

            LogDiagnostics(
                $"reload request actor={request.ActorNetworkId} req={request.RequestId} weaponHash={request.WeaponHash}");

            if (SendReloadRequestToServer == null)
            {
                LogDiagnosticsWarning(
                    $"reload request has no transport delegate actor={request.ActorNetworkId} req={request.RequestId}. " +
                    "Expected a Shooter transport bridge to assign SendReloadRequestToServer.");
            }

            SendReloadRequestToServer?.Invoke(request);
        }
    }
}
#endif
