using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;


namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkCharacter
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARACTER EVENT HANDLING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void SubscribeToCharacterEvents()
        {
            if (m_Character == null) return;
            
            m_Character.EventDie += OnLocalDeath;
            m_Character.EventRevive += OnLocalRevive;
        }
        
        private void UnsubscribeFromCharacterEvents()
        {
            if (m_Character == null) return;
            
            m_Character.EventDie -= OnLocalDeath;
            m_Character.EventRevive -= OnLocalRevive;
        }
        
        private void OnLocalDeath()
        {
            m_LastIsDead = true;
            OnNetworkDeathChanged?.Invoke(true);
        }
        
        private void OnLocalRevive()
        {
            m_LastIsDead = false;
            OnNetworkDeathChanged?.Invoke(false);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-AUTHORITATIVE STATE CHANGES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server-authoritative kill. Only callable on server.
        /// </summary>
        public void ServerKill()
        {
            if (!m_RuntimeIsServer)
            {
                Debug.LogWarning("[NetworkCharacter] ServerKill called on client");
                return;
            }
            
            m_Character.IsDead = true;
            m_LastIsDead = true;
            OnNetworkDeathChanged?.Invoke(true);
        }
        
        /// <summary>
        /// Server-authoritative revive. Only callable on server.
        /// </summary>
        public void ServerRevive()
        {
            if (!m_RuntimeIsServer)
            {
                Debug.LogWarning("[NetworkCharacter] ServerRevive called on client");
                return;
            }
            
            m_Character.IsDead = false;
            m_LastIsDead = false;
            OnNetworkDeathChanged?.Invoke(false);
        }
        
        /// <summary>
        /// Server-authoritative player designation. Only callable on server.
        /// </summary>
        public void ServerSetIsPlayer(bool isPlayer)
        {
            if (!m_RuntimeIsServer)
            {
                Debug.LogWarning("[NetworkCharacter] ServerSetIsPlayer called on client");
                return;
            }
            
            m_Character.IsPlayer = isPlayer;
            m_LastIsPlayer = isPlayer;
            OnNetworkPlayerChanged?.Invoke(isPlayer);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // FACING UNIT SUPPORT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called by UnitFacingNetworkPivot when it initializes.
        /// </summary>
        public void OnFacingUnitRegistered(UnitFacingNetworkPivot facingUnit)
        {
            m_NetworkFacingUnit = facingUnit;

            if (m_NetworkFacingUnit != null)
            {
                m_NetworkFacingUnit.OnServerYawReceived(transform.eulerAngles.y);
            }
        }
        
        /// <summary>
        /// Called by UnitFacingNetworkPivot when it is disposed.
        /// </summary>
        public void OnFacingUnitUnregistered()
        {
            m_NetworkFacingUnit = null;
        }
        
        /// <summary>
        /// Request a facing update from the server. Called by local client.
        /// </summary>
        public void RequestFacingUpdate(float desiredYaw)
        {
            if (m_NetworkFacingUnit == null) return;
            if (!m_RuntimeIsServer && !m_RuntimeIsOwner) return;

            float validatedYaw = m_NetworkFacingUnit.ValidateFacingRequest(desiredYaw);
            m_NetworkFacingUnit.OnServerYawReceived(validatedYaw);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ANIMIM UNIT SUPPORT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called by UnitAnimimNetworkKinematic when it initializes.
        /// </summary>
        public void OnAnimimUnitRegistered(UnitAnimimNetworkKinematic animimUnit)
        {
            m_NetworkAnimimUnit = animimUnit;

            if (m_NetworkAnimimUnit != null)
            {
                m_NetworkAnimimUnit.OnServerStateReceived(m_NetworkAnimimUnit.GetCurrentState());
            }
        }
        
        /// <summary>
        /// Called by UnitAnimimNetworkKinematic when it is disposed.
        /// </summary>
        public void OnAnimimUnitUnregistered()
        {
            m_NetworkAnimimUnit = null;
        }
        
        /// <summary>
        /// Request an animim state update from the server. Called by local client.
        /// </summary>
        public void RequestAnimimUpdate(NetworkAnimimState state)
        {
            if (m_NetworkAnimimUnit == null) return;
            if (!m_RuntimeIsServer && !m_RuntimeIsOwner) return;

            m_NetworkAnimimUnit.OnServerStateReceived(state);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UPDATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Update()
        {
            if (!m_IsInitialized) return;
            
            float deltaTime = m_Character != null ? m_Character.Time.DeltaTime : Time.deltaTime;
            if (m_RegisteredBridge == null && NetworkTransportBridge.HasActive)
            {
                ResolveSessionProfile();
                RegisterWithBridge();
                ApplySessionProfileToDrivers();
            }
            
            if (m_RuntimeIsServer)
            {
                ProcessServerSimulation(deltaTime);
                PublishHostLocalClientState();
            }

            if (m_RemoteDriver != null && NetworkTransportBridge.HasActive)
            {
                m_RemoteDriver.SetServerTime(NetworkTransportBridge.Active.ServerTime);
            }
            
            ApplyCurrentRelevanceTier();
            DetectStateChanges();
        }
        
        private void DetectStateChanges()
        {
            // Only relevant for server or solutions that need polling
            if (m_Character.IsDead != m_LastIsDead)
            {
                m_LastIsDead = m_Character.IsDead;
                OnNetworkDeathChanged?.Invoke(m_LastIsDead);
            }
            
            if (m_Character.IsPlayer != m_LastIsPlayer)
            {
                m_LastIsPlayer = m_Character.IsPlayer;
                OnNetworkPlayerChanged?.Invoke(m_LastIsPlayer);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnDestroy()
        {
            Cleanup();
        }
        
        private void Cleanup()
        {
            if (m_AnimimController != null)
            {
                NetworkAnimationManager.Instance?.UnregisterController(m_AnimimController);
            }

            if (m_MotionController != null)
            {
                NetworkMotionManager.Instance?.UnregisterController(m_MotionController);
            }

            UnregisterFromBridge();
            UnwireMovementEvents();
            UnsubscribeFromCharacterEvents();
            m_IsInitialized = false;
            m_CurrentRole = NetworkRole.None;
            m_RuntimeIsServer = false;
            m_RuntimeIsOwner = false;
            m_RuntimeIsHost = false;
            m_LastStateBroadcastPerClient.Clear();
        }

        public void ResetNetworkRole()
        {
            Cleanup();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MANUAL NETWORK SYNC (For non-provider solutions)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Apply state received from network. Call this when receiving state updates.
        /// For Photon, FishNet, Mirror, etc.
        /// </summary>
        public void ApplyNetworkState(NetworkCharacterState state)
        {
            if (m_CurrentRole != NetworkRole.RemoteClient && m_CurrentRole != NetworkRole.LocalClient)
            {
                return; // Server doesn't apply received state
            }
            
            if (m_Character.IsDead != state.isDead)
            {
                m_Character.IsDead = state.isDead;
            }
            
            // GC2's IsPlayer gates local input, so remote replicas must never inherit it.
            bool isLocalPlayer = m_CurrentRole != NetworkRole.RemoteClient && state.isPlayer;
            if (m_Character.IsPlayer != isLocalPlayer)
            {
                m_Character.IsPlayer = isLocalPlayer;
            }
        }
        
        /// <summary>
        /// Get current state for sending over network.
        /// </summary>
        public NetworkCharacterState GetNetworkState()
        {
            return new NetworkCharacterState
            {
                isDead = m_Character.IsDead,
                isPlayer = m_Character.IsPlayer
            };
        }
        
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (!m_UseAutomaticNetworkId && m_ManualNetworkId == 0)
            {
                m_ManualNetworkId = 1;
            }
            
            if (m_IsInitialized)
            {
                RefreshNetworkId();
            }
        }
#endif
    }
}
