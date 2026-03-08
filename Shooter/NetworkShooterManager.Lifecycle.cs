#if GC2_SHOOTER
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Shooter
{
    public partial class NetworkShooterManager
    {
        private void OnDisable()
        {
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

            Debug.Log($"[NetworkShooterManager] Initialized - Server: {isServer}, Client: {isClient}");
        }

        private void SyncPatchHooks()
        {
            if (!m_IsServer)
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

            m_Controllers[networkId] = controller;

            controller.OnShotRequestSent += OnControllerShotRequestSent;
            controller.OnHitDetected += OnControllerHitDetected;
        }

        /// <summary>
        /// Unregister a controller.
        /// </summary>
        public void UnregisterController(uint networkId)
        {
            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                controller.OnShotRequestSent -= OnControllerShotRequestSent;
                controller.OnHitDetected -= OnControllerHitDetected;
                m_Controllers.Remove(networkId);
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
            if (!m_IsClient) return;

            if (m_LogShotRequests)
            {
                Debug.Log($"[NetworkShooterManager] Shot request: Shooter={request.ShooterNetworkId}, " +
                         $"Pos={request.MuzzlePosition}, Dir={request.ShotDirection}");
            }

            SendShotRequestToServer?.Invoke(request);
            m_Stats.ShotRequestsSent++;

            OnShotRequestSent?.Invoke(request);
        }

        private void OnControllerHitDetected(NetworkShooterHitRequest request)
        {
            if (!m_IsClient) return;

            if (m_LogHitRequests)
            {
                Debug.Log($"[NetworkShooterManager] Hit request: Target={request.TargetNetworkId}, " +
                         $"Point={request.HitPoint}");
            }

            SendHitRequestToServer?.Invoke(request);
        }
    }
}
#endif
