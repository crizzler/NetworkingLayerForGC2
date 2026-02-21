using System;
using System.Collections.Generic;
using UnityEngine;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network message routing manager for the Abilities system.
    /// Provides message type IDs and convenience methods for network integration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This class handles:
    /// - Message type ID allocation (230-259 range reserved for Abilities)
    /// - Ability registry for hash-to-asset lookup
    /// - Projectile and Impact registries
    /// - Convenience methods for common operations
    /// </para>
    /// <para>
    /// Integration with your network layer:
    /// 1. Register your network message handlers using the MessageTypes constants
    /// 2. Wire up the controller delegates to your network send functions
    /// 3. Call the Receive* methods when messages arrive from network
    /// </para>
    /// </remarks>
    public static class NetworkAbilitiesManager
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MESSAGE TYPE IDS (230-259 reserved for Abilities)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public static class MessageTypes
        {
            // Cast Messages (230-234)
            public const ushort AbilityCastRequest = 230;
            public const ushort AbilityCastResponse = 231;
            public const ushort AbilityCastBroadcast = 232;
            public const ushort AbilityEffectBroadcast = 233;
            public const ushort CastCancelRequest = 234;
            
            // Projectile Messages (235-237)
            public const ushort ProjectileSpawnBroadcast = 235;
            public const ushort ProjectileEventBroadcast = 236;
            
            // Impact Messages (238-239)
            public const ushort ImpactSpawnBroadcast = 238;
            public const ushort ImpactHitBroadcast = 239;
            
            // Cooldown Messages (240-242)
            public const ushort CooldownRequest = 240;
            public const ushort CooldownResponse = 241;
            public const ushort CooldownBroadcast = 242;
            
            // Learning Messages (243-245)
            public const ushort AbilityLearnRequest = 243;
            public const ushort AbilityLearnResponse = 244;
            public const ushort AbilityLearnBroadcast = 245;
            
            // State Sync Messages (246-249)
            public const ushort AbilityStateRequest = 246;
            public const ushort AbilityStateResponse = 247;
            public const ushort AbilitySlotEntry = 248;
            public const ushort CooldownEntry = 249;
            
            // Cancel Messages (250-251)
            public const ushort CastCancelResponse = 250;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // REGISTRIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Registry entry for an Ability asset.
        /// </summary>
        public struct AbilityRegistryEntry
        {
            public int Hash;
            public Ability Ability;
            public string Name;
        }
        
        /// <summary>
        /// Registry entry for a Projectile asset.
        /// </summary>
        public struct ProjectileRegistryEntry
        {
            public int Hash;
            public Projectile Projectile;
            public string Name;
        }
        
        /// <summary>
        /// Registry entry for an Impact asset.
        /// </summary>
        public struct ImpactRegistryEntry
        {
            public int Hash;
            public Impact Impact;
            public string Name;
        }
        
        private static readonly Dictionary<int, AbilityRegistryEntry> s_AbilityRegistry = new(128);
        private static readonly Dictionary<int, ProjectileRegistryEntry> s_ProjectileRegistry = new(64);
        private static readonly Dictionary<int, ImpactRegistryEntry> s_ImpactRegistry = new(32);
        
        // Pawn tracking for network lookup
        private static readonly Dictionary<uint, Pawn> s_NetworkIdToPawn = new(64);
        private static readonly Dictionary<Pawn, uint> s_PawnToNetworkId = new(64);
        
        private static Func<float> s_GetServerTime;
        private static Func<uint> s_GetLocalPlayerNetworkId;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the manager with time and player ID providers.
        /// </summary>
        public static void Initialize(Func<float> getServerTime, Func<uint> getLocalPlayerNetworkId)
        {
            s_GetServerTime = getServerTime;
            s_GetLocalPlayerNetworkId = getLocalPlayerNetworkId;
            
            // Wire up the controller if it exists
            if (NetworkAbilitiesController.HasInstance)
            {
                WireUpController(NetworkAbilitiesController.Instance);
            }
        }
        
        /// <summary>
        /// Wire up a controller instance with the registry lookups.
        /// </summary>
        public static void WireUpController(NetworkAbilitiesController controller)
        {
            controller.GetServerTime = () => s_GetServerTime?.Invoke() ?? Time.time;
            controller.GetLocalPlayerNetworkId = () => s_GetLocalPlayerNetworkId?.Invoke() ?? 0;
            controller.GetPawnByNetworkId = GetPawnByNetworkId;
            controller.GetCharacterByNetworkId = GetCharacterByNetworkId;
            controller.GetAbilityByHash = GetAbilityByHash;
            controller.GetProjectileByHash = GetProjectileByHash;
            controller.GetImpactByHash = GetImpactByHash;
            controller.GetNetworkIdForPawn = GetNetworkIdForPawn;
        }
        
        /// <summary>
        /// Clear all registries and tracking data.
        /// </summary>
        public static void Clear()
        {
            s_AbilityRegistry.Clear();
            s_ProjectileRegistry.Clear();
            s_ImpactRegistry.Clear();
            s_NetworkIdToPawn.Clear();
            s_PawnToNetworkId.Clear();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABILITY REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Register an Ability for network lookup.
        /// </summary>
        public static void RegisterAbility(Ability ability)
        {
            if (ability == null) return;
            
            int hash = ability.ID.Hash;
            s_AbilityRegistry[hash] = new AbilityRegistryEntry
            {
                Hash = hash,
                Ability = ability,
                Name = ability.name
            };
        }
        
        /// <summary>
        /// Register multiple abilities at once.
        /// </summary>
        public static void RegisterAbilities(IEnumerable<Ability> abilities)
        {
            foreach (var ability in abilities)
            {
                RegisterAbility(ability);
            }
        }
        
        /// <summary>
        /// Unregister an Ability.
        /// </summary>
        public static void UnregisterAbility(Ability ability)
        {
            if (ability == null) return;
            s_AbilityRegistry.Remove(ability.ID.Hash);
        }
        
        /// <summary>
        /// Get an Ability by its ID hash.
        /// </summary>
        public static Ability GetAbilityByHash(int hash)
        {
            return s_AbilityRegistry.TryGetValue(hash, out var entry) ? entry.Ability : null;
        }
        
        /// <summary>
        /// Check if an ability is registered.
        /// </summary>
        public static bool IsAbilityRegistered(Ability ability)
        {
            return ability != null && s_AbilityRegistry.ContainsKey(ability.ID.Hash);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROJECTILE REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Register a Projectile for network lookup.
        /// </summary>
        public static void RegisterProjectile(Projectile projectile)
        {
            if (projectile == null) return;
            
            int hash = projectile.GetHashCode();
            s_ProjectileRegistry[hash] = new ProjectileRegistryEntry
            {
                Hash = hash,
                Projectile = projectile,
                Name = projectile.name
            };
        }
        
        /// <summary>
        /// Register multiple projectiles at once.
        /// </summary>
        public static void RegisterProjectiles(IEnumerable<Projectile> projectiles)
        {
            foreach (var projectile in projectiles)
            {
                RegisterProjectile(projectile);
            }
        }
        
        /// <summary>
        /// Unregister a Projectile.
        /// </summary>
        public static void UnregisterProjectile(Projectile projectile)
        {
            if (projectile == null) return;
            s_ProjectileRegistry.Remove(projectile.GetHashCode());
        }
        
        /// <summary>
        /// Get a Projectile by its hash.
        /// </summary>
        public static Projectile GetProjectileByHash(int hash)
        {
            return s_ProjectileRegistry.TryGetValue(hash, out var entry) ? entry.Projectile : null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // IMPACT REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Register an Impact for network lookup.
        /// </summary>
        public static void RegisterImpact(Impact impact)
        {
            if (impact == null) return;
            
            int hash = impact.GetHashCode();
            s_ImpactRegistry[hash] = new ImpactRegistryEntry
            {
                Hash = hash,
                Impact = impact,
                Name = impact.name
            };
        }
        
        /// <summary>
        /// Register multiple impacts at once.
        /// </summary>
        public static void RegisterImpacts(IEnumerable<Impact> impacts)
        {
            foreach (var impact in impacts)
            {
                RegisterImpact(impact);
            }
        }
        
        /// <summary>
        /// Unregister an Impact.
        /// </summary>
        public static void UnregisterImpact(Impact impact)
        {
            if (impact == null) return;
            s_ImpactRegistry.Remove(impact.GetHashCode());
        }
        
        /// <summary>
        /// Get an Impact by its hash.
        /// </summary>
        public static Impact GetImpactByHash(int hash)
        {
            return s_ImpactRegistry.TryGetValue(hash, out var entry) ? entry.Impact : null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PAWN TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Register a Pawn with its network ID.
        /// </summary>
        public static void RegisterPawn(Pawn pawn, uint networkId)
        {
            if (pawn == null) return;
            
            s_NetworkIdToPawn[networkId] = pawn;
            s_PawnToNetworkId[pawn] = networkId;
        }
        
        /// <summary>
        /// Unregister a Pawn.
        /// </summary>
        public static void UnregisterPawn(Pawn pawn)
        {
            if (pawn == null) return;
            
            if (s_PawnToNetworkId.TryGetValue(pawn, out var networkId))
            {
                s_NetworkIdToPawn.Remove(networkId);
                s_PawnToNetworkId.Remove(pawn);
            }
        }
        
        /// <summary>
        /// Unregister a Pawn by network ID.
        /// </summary>
        public static void UnregisterPawn(uint networkId)
        {
            if (s_NetworkIdToPawn.TryGetValue(networkId, out var pawn))
            {
                s_PawnToNetworkId.Remove(pawn);
                s_NetworkIdToPawn.Remove(networkId);
            }
        }
        
        /// <summary>
        /// Get a Pawn by its network ID.
        /// </summary>
        public static Pawn GetPawnByNetworkId(uint networkId)
        {
            return s_NetworkIdToPawn.TryGetValue(networkId, out var pawn) ? pawn : null;
        }
        
        /// <summary>
        /// Get a Character by network ID (via Pawn lookup).
        /// </summary>
        public static Character GetCharacterByNetworkId(uint networkId)
        {
            Pawn pawn = GetPawnByNetworkId(networkId);
            return pawn != null ? pawn.GetComponent<Character>() : null;
        }
        
        /// <summary>
        /// Get the network ID for a Pawn.
        /// </summary>
        public static uint GetNetworkIdForPawn(Pawn pawn)
        {
            return pawn != null && s_PawnToNetworkId.TryGetValue(pawn, out var networkId) ? networkId : 0;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVE HANDLERS (Call from your network layer)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Route an incoming message to the appropriate handler based on message type.
        /// Call this from your network layer's message receive callback.
        /// </summary>
        public static void RouteIncomingMessage(ushort messageType, object data, uint senderId = 0)
        {
            if (!NetworkAbilitiesController.HasInstance) return;
            var controller = NetworkAbilitiesController.Instance;
            
            switch (messageType)
            {
                // Cast
                case MessageTypes.AbilityCastRequest:
                    if (data is NetworkAbilityCastRequest castReq)
                        controller.ProcessCastRequest(senderId, castReq);
                    break;
                    
                case MessageTypes.AbilityCastResponse:
                    if (data is NetworkAbilityCastResponse castResp)
                        controller.ReceiveCastResponse(castResp);
                    break;
                    
                case MessageTypes.AbilityCastBroadcast:
                    if (data is NetworkAbilityCastBroadcast castBroadcast)
                        controller.ReceiveCastBroadcast(castBroadcast);
                    break;
                    
                // Cooldowns
                case MessageTypes.CooldownBroadcast:
                    if (data is NetworkCooldownBroadcast cooldownBroadcast)
                        controller.ReceiveCooldownBroadcast(cooldownBroadcast);
                    break;
                    
                // Learning
                case MessageTypes.AbilityLearnRequest:
                    if (data is NetworkAbilityLearnRequest learnReq)
                        controller.ProcessLearnRequest(senderId, learnReq);
                    break;
                    
                case MessageTypes.AbilityLearnResponse:
                    if (data is NetworkAbilityLearnResponse learnResp)
                        controller.ReceiveLearnResponse(learnResp);
                    break;
                    
                case MessageTypes.AbilityLearnBroadcast:
                    if (data is NetworkAbilityLearnBroadcast learnBroadcast)
                        controller.ReceiveLearnBroadcast(learnBroadcast);
                    break;
                    
                // Projectiles
                case MessageTypes.ProjectileSpawnBroadcast:
                    if (data is NetworkProjectileSpawnBroadcast projSpawn)
                        controller.ReceiveProjectileSpawnBroadcast(projSpawn);
                    break;
                    
                // Impacts
                case MessageTypes.ImpactSpawnBroadcast:
                    if (data is NetworkImpactSpawnBroadcast impactSpawn)
                        controller.ReceiveImpactSpawnBroadcast(impactSpawn);
                    break;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONVENIENCE METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request to cast an ability (convenience wrapper).
        /// </summary>
        public static void RequestCast(
            Pawn caster,
            Ability ability,
            DaimahouGames.Runtime.Core.Common.Target target = default,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!NetworkAbilitiesController.HasInstance)
            {
                Debug.LogWarning("[NetworkAbilitiesManager] Controller not available.");
                return;
            }
            
            uint networkId = GetNetworkIdForPawn(caster);
            if (networkId == 0)
            {
                Debug.LogWarning("[NetworkAbilitiesManager] Caster not registered.");
                return;
            }
            
            NetworkAbilitiesController.Instance.RequestCastAbility(networkId, ability, target, callback);
        }
        
        /// <summary>
        /// Request to cast ability at a position (convenience wrapper).
        /// </summary>
        public static void RequestCastAtPosition(
            Pawn caster,
            Ability ability,
            Vector3 targetPosition,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!NetworkAbilitiesController.HasInstance)
            {
                Debug.LogWarning("[NetworkAbilitiesManager] Controller not available.");
                return;
            }
            
            uint networkId = GetNetworkIdForPawn(caster);
            if (networkId == 0)
            {
                Debug.LogWarning("[NetworkAbilitiesManager] Caster not registered.");
                return;
            }
            
            NetworkAbilitiesController.Instance.RequestCastAbilityAutoConfirm(
                networkId, ability, targetPosition, 0, callback);
        }
        
        /// <summary>
        /// Request to cast ability at a target (convenience wrapper).
        /// </summary>
        public static void RequestCastAtTarget(
            Pawn caster,
            Ability ability,
            Pawn target,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!NetworkAbilitiesController.HasInstance)
            {
                Debug.LogWarning("[NetworkAbilitiesManager] Controller not available.");
                return;
            }
            
            uint casterNetworkId = GetNetworkIdForPawn(caster);
            uint targetNetworkId = GetNetworkIdForPawn(target);
            
            if (casterNetworkId == 0)
            {
                Debug.LogWarning("[NetworkAbilitiesManager] Caster not registered.");
                return;
            }
            
            Vector3 targetPos = target != null ? target.Position : Vector3.zero;
            
            NetworkAbilitiesController.Instance.RequestCastAbilityAutoConfirm(
                casterNetworkId, ability, targetPos, targetNetworkId, callback);
        }
        
        /// <summary>
        /// Request to learn an ability (convenience wrapper).
        /// </summary>
        public static void RequestLearn(
            Pawn pawn,
            Ability ability,
            int slot,
            Action<NetworkAbilityLearnResponse> callback = null)
        {
            if (!NetworkAbilitiesController.HasInstance) return;
            
            uint networkId = GetNetworkIdForPawn(pawn);
            if (networkId == 0) return;
            
            NetworkAbilitiesController.Instance.RequestLearnAbility(networkId, ability, slot, callback);
        }
        
        /// <summary>
        /// Request to unlearn an ability (convenience wrapper).
        /// </summary>
        public static void RequestUnlearn(
            Pawn pawn,
            int slot,
            Action<NetworkAbilityLearnResponse> callback = null)
        {
            if (!NetworkAbilitiesController.HasInstance) return;
            
            uint networkId = GetNetworkIdForPawn(pawn);
            if (networkId == 0) return;
            
            NetworkAbilitiesController.Instance.RequestUnlearnAbility(networkId, slot, callback);
        }
        
        /// <summary>
        /// Check if an ability is on cooldown for a pawn.
        /// </summary>
        public static bool IsOnCooldown(Pawn pawn, Ability ability)
        {
            if (!NetworkAbilitiesController.HasInstance) return false;
            
            uint networkId = GetNetworkIdForPawn(pawn);
            if (networkId == 0) return false;
            
            return NetworkAbilitiesController.Instance.IsOnCooldown(networkId, ability.ID.Hash);
        }
        
        /// <summary>
        /// Get remaining cooldown time for an ability.
        /// </summary>
        public static float GetCooldownRemaining(Pawn pawn, Ability ability)
        {
            if (!NetworkAbilitiesController.HasInstance) return 0f;
            
            uint networkId = GetNetworkIdForPawn(pawn);
            if (networkId == 0) return 0f;
            
            return NetworkAbilitiesController.Instance.GetCooldownRemaining(networkId, ability.ID.Hash);
        }
        
        /// <summary>
        /// Server resets cooldown for a pawn's ability.
        /// </summary>
        public static void ServerResetCooldown(Pawn pawn, Ability ability)
        {
            if (!NetworkAbilitiesController.HasInstance) return;
            if (!NetworkAbilitiesController.Instance.IsServer) return;
            
            uint networkId = GetNetworkIdForPawn(pawn);
            if (networkId == 0) return;
            
            NetworkAbilitiesController.Instance.ServerResetCooldown(networkId, ability.ID.Hash);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER PROJECTILE/IMPACT HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server spawns a projectile through the network system.
        /// Use this instead of direct Projectile.Get() for networked games.
        /// </summary>
        public static RuntimeProjectile ServerSpawnProjectile(
            uint castInstanceId,
            Projectile projectile,
            Vector3 spawnPosition,
            Vector3 direction,
            Vector3 targetPosition,
            Pawn targetPawn = null,
            DaimahouGames.Runtime.Core.Common.ExtendedArgs args = null)
        {
            if (!NetworkAbilitiesController.HasInstance) return null;
            if (!NetworkAbilitiesController.Instance.IsServer) return null;
            
            uint targetNetworkId = targetPawn != null ? GetNetworkIdForPawn(targetPawn) : 0;
            
            return NetworkAbilitiesController.Instance.ServerSpawnProjectile(
                castInstanceId, projectile, spawnPosition, direction, targetPosition, targetNetworkId, args);
        }
        
        /// <summary>
        /// Server spawns an impact through the network system.
        /// Use this instead of direct Impact.Get() for networked games.
        /// </summary>
        public static RuntimeImpact ServerSpawnImpact(
            uint castInstanceId,
            Impact impact,
            Vector3 position,
            Quaternion rotation,
            DaimahouGames.Runtime.Core.Common.ExtendedArgs args = null)
        {
            if (!NetworkAbilitiesController.HasInstance) return null;
            if (!NetworkAbilitiesController.Instance.IsServer) return null;
            
            return NetworkAbilitiesController.Instance.ServerSpawnImpact(
                castInstanceId, impact, position, rotation, args);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATISTICS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Get registry statistics.
        /// </summary>
        public static (int abilities, int projectiles, int impacts, int pawns) GetRegistryStats()
        {
            return (s_AbilityRegistry.Count, s_ProjectileRegistry.Count, s_ImpactRegistry.Count, s_NetworkIdToPawn.Count);
        }
        
        /// <summary>
        /// Get controller statistics.
        /// </summary>
        public static NetworkAbilitiesStats GetControllerStats()
        {
            return NetworkAbilitiesController.HasInstance 
                ? NetworkAbilitiesController.Instance.Stats 
                : default;
        }
        
        /// <summary>
        /// Get current system state.
        /// </summary>
        public static NetworkAbilitiesState GetSystemState()
        {
            return NetworkAbilitiesController.HasInstance
                ? NetworkAbilitiesController.Instance.GetState()
                : default;
        }
    }
}
