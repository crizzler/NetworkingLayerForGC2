using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

#if GC2_MELEE
using GameCreator.Runtime.Melee;
#endif

#if GC2_SHOOTER
using GameCreator.Runtime.Shooter;
#endif

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Intercepts GC2 combat events and routes them through server validation.
    /// Attach to any character that should use server-authoritative combat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component hooks into GC2's combat system at the point where damage
    /// would normally be applied, and instead routes through NetworkCombatController.
    /// </para>
    /// <para>
    /// On the server, it applies damage directly. On clients, it sends hit requests.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Character))]
    [AddComponentMenu("Game Creator/Network/Network Combat Interceptor")]
    public class NetworkCombatInterceptor : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Interception Settings")]
        [Tooltip("Intercept melee strikes from this character.")]
        [SerializeField] private bool m_InterceptMelee = true;
        
        [Tooltip("Intercept projectile hits from this character.")]
        [SerializeField] private bool m_InterceptShooter = true;
        
        [Tooltip("Allow local hit detection (for effects) but don't apply damage.")]
        [SerializeField] private bool m_AllowLocalDetection = true;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogInterceptions = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Character m_Character;
        private NetworkCharacter m_NetworkCharacter;
        private NetworkCombatController m_CombatController;
        
        private bool m_IsServer;
        private bool m_IsLocalPlayer;
        
        // Weapon tracking for hash IDs
        private int m_CurrentWeaponHash;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when a hit is intercepted (before sending to server).
        /// </summary>
        public event Action<Character, Vector3, Vector3> OnHitIntercepted;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Character.</summary>
        public Character Character => m_Character;
        
        /// <summary>Whether melee interception is enabled.</summary>
        public bool InterceptMelee
        {
            get => m_InterceptMelee;
            set => m_InterceptMelee = value;
        }
        
        /// <summary>Whether shooter interception is enabled.</summary>
        public bool InterceptShooter
        {
            get => m_InterceptShooter;
            set => m_InterceptShooter = value;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }
        
        private void Start()
        {
            // Find combat controller
            m_CombatController = NetworkCombatController.Instance;
            
            if (m_CombatController == null)
            {
                m_CombatController = FindAnyObjectByType<NetworkCombatController>();
            }
            
            if (m_CombatController == null)
            {
                Debug.LogWarning($"[NetworkCombatInterceptor] No NetworkCombatController found. Combat will not be server-authoritative.");
            }
            
            // Subscribe to weapon equip events for tracking
            m_Character.Combat.EventEquip += OnWeaponEquipped;
            m_Character.Combat.EventUnequip += OnWeaponUnequipped;
        }
        
        private void OnDestroy()
        {
            if (m_Character != null)
            {
                m_Character.Combat.EventEquip -= OnWeaponEquipped;
                m_Character.Combat.EventUnequip -= OnWeaponUnequipped;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize with network role.
        /// </summary>
        public void Initialize(bool isServer, bool isLocalPlayer)
        {
            m_IsServer = isServer;
            m_IsLocalPlayer = isLocalPlayer;
            
            // On server, we process hits directly
            // On local client, we intercept and send to server
            // On remote clients, we do nothing (just receive broadcasts)
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // WEAPON TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnWeaponEquipped(IWeapon weapon, GameObject instance)
        {
            if (weapon != null)
            {
                m_CurrentWeaponHash = weapon.Id.Hash;
            }
        }
        
        private void OnWeaponUnequipped(IWeapon weapon, GameObject instance)
        {
            // Reset or update weapon hash
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MELEE INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if GC2_MELEE
        /// <summary>
        /// Intercept a melee strike hit. Call this from your striker component.
        /// </summary>
        /// <param name="target">The character that was hit.</param>
        /// <param name="hitPoint">World position of the hit.</param>
        /// <param name="strikeDirection">Direction of the strike.</param>
        /// <param name="skill">The melee skill being used.</param>
        /// <returns>True if the hit should be processed locally, false if intercepted.</returns>
        public bool InterceptMeleeStrike(Character target, Vector3 hitPoint, Vector3 strikeDirection, Skill skill)
        {
            if (!m_InterceptMelee) return true;
            if (m_CombatController == null) return true;
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits
            if (!m_IsLocalPlayer)
            {
                return false;
            }
            
            // Local client - send to server
            int weaponHash = skill?.Weapon?.Id.Hash ?? m_CurrentWeaponHash;
            
            m_CombatController.RequestMeleeHit(
                target,
                hitPoint,
                strikeDirection,
                weaponHash
            );
            
            if (m_LogInterceptions)
            {
                Debug.Log($"[NetworkCombatInterceptor] Intercepted melee hit on {target.name}");
            }
            
            OnHitIntercepted?.Invoke(target, hitPoint, strikeDirection);
            
            // Return false to prevent local damage application
            // Return true if AllowLocalDetection is on (for effects only)
            return m_AllowLocalDetection;
        }
        
        /// <summary>
        /// Intercept a melee strike output from the striker system.
        /// </summary>
        public bool InterceptStrikeOutput(StrikeOutput output, Skill skill)
        {
            if (!m_InterceptMelee) return true;
            if (output.Target == null) return true;
            
            var targetCharacter = output.Target.GetComponent<Character>();
            if (targetCharacter == null) return true;
            
            return InterceptMeleeStrike(
                targetCharacter,
                output.Point,
                output.Direction,
                skill
            );
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOOTER INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if GC2_SHOOTER
        /// <summary>
        /// Intercept a projectile/raycast hit. Call this from your shooter weapon.
        /// </summary>
        /// <param name="target">The character that was hit.</param>
        /// <param name="hitPoint">World position of the hit.</param>
        /// <param name="shootDirection">Direction of the shot.</param>
        /// <param name="weapon">The shooter weapon used.</param>
        /// <returns>True if the hit should be processed locally, false if intercepted.</returns>
        public bool InterceptProjectileHit(Character target, Vector3 hitPoint, Vector3 shootDirection, ShooterWeapon weapon)
        {
            if (!m_InterceptShooter) return true;
            if (m_CombatController == null) return true;
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits
            if (!m_IsLocalPlayer)
            {
                return false;
            }
            
            // Local client - send to server
            int weaponHash = weapon?.Id.Hash ?? m_CurrentWeaponHash;
            
            m_CombatController.RequestProjectileHit(
                target,
                hitPoint,
                shootDirection,
                weaponHash
            );
            
            if (m_LogInterceptions)
            {
                Debug.Log($"[NetworkCombatInterceptor] Intercepted projectile hit on {target.name}");
            }
            
            OnHitIntercepted?.Invoke(target, hitPoint, shootDirection);
            
            // Return false to prevent local damage application
            return m_AllowLocalDetection;
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // GENERIC INTERCEPTION (For custom combat systems)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept any hit type. Use for custom combat systems.
        /// </summary>
        public bool InterceptHit(Character target, Vector3 hitPoint, Vector3 hitDirection, 
            int weaponHash, HitType hitType)
        {
            if (m_CombatController == null) return true;
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits
            if (!m_IsLocalPlayer)
            {
                return false;
            }
            
            // Send appropriate request based on hit type
            switch (hitType)
            {
                case HitType.MeleeStrike:
                    m_CombatController.RequestMeleeHit(target, hitPoint, hitDirection, weaponHash);
                    break;
                    
                case HitType.Projectile:
                    m_CombatController.RequestProjectileHit(target, hitPoint, hitDirection, weaponHash);
                    break;
                    
                case HitType.AreaOfEffect:
                    m_CombatController.RequestAOEHit(target, hitPoint, hitDirection, weaponHash);
                    break;
                    
                default:
                    m_CombatController.RequestMeleeHit(target, hitPoint, hitDirection, weaponHash);
                    break;
            }
            
            OnHitIntercepted?.Invoke(target, hitPoint, hitDirection);
            
            return m_AllowLocalDetection;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DAMAGE APPLICATION (Server-side)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Apply validated damage to this character.
        /// Called by NetworkCombatController when a hit is validated.
        /// </summary>
        public void ApplyValidatedDamage(ValidatedDamage damage, Character attacker)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCombatInterceptor] ApplyValidatedDamage called on non-server");
                return;
            }
            
            // Create reaction input
            ReactionInput reactionInput = damage.ToReactionInput();
            Args args = new Args(attacker?.gameObject, gameObject);
            
            // Apply through GC2's combat system
            _ = m_Character.Combat.GetHitReaction(reactionInput, args, null);
            
            if (m_LogInterceptions)
            {
                Debug.Log($"[NetworkCombatInterceptor] Applied {damage.finalDamage} damage to {m_Character.name}");
            }
        }
    }
}
