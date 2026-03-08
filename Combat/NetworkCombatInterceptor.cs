using System;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

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
        
        /// <summary>
        /// Intercept a melee strike hit. Works with any skill object that exposes
        /// either <c>Id.Hash</c> or <c>Weapon.Id.Hash</c>.
        /// </summary>
        /// <param name="target">The character that was hit.</param>
        /// <param name="hitPoint">World position of the hit.</param>
        /// <param name="strikeDirection">Direction of the strike.</param>
        /// <param name="skill">The melee skill object (optional, reflection-based).</param>
        /// <returns>True if the hit should be processed locally, false if intercepted.</returns>
        public bool InterceptMeleeStrike(Character target, Vector3 hitPoint, Vector3 strikeDirection, object skill = null)
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
            int weaponHash = ResolveWeaponHash(skill, m_CurrentWeaponHash);
            
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
        /// Intercept a melee strike output object from striker systems.
        /// Supports payloads with members <c>Target</c>, <c>Point</c>, and <c>Direction</c>.
        /// </summary>
        public bool InterceptStrikeOutput(object output, object skill = null)
        {
            if (!m_InterceptMelee) return true;
            if (output == null) return true;
            
            if (!TryResolveStrikeOutput(output, out Character targetCharacter, out Vector3 point, out Vector3 direction))
            {
                return true;
            }
            
            return InterceptMeleeStrike(
                targetCharacter,
                point,
                direction,
                skill
            );
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOOTER INTERCEPTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a projectile/raycast hit. Works with any weapon object exposing
        /// <c>Id.Hash</c> via reflection.
        /// </summary>
        /// <param name="target">The character that was hit.</param>
        /// <param name="hitPoint">World position of the hit.</param>
        /// <param name="shootDirection">Direction of the shot.</param>
        /// <param name="weapon">The shooter weapon object (optional, reflection-based).</param>
        /// <returns>True if the hit should be processed locally, false if intercepted.</returns>
        public bool InterceptProjectileHit(Character target, Vector3 hitPoint, Vector3 shootDirection, object weapon = null)
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
            int weaponHash = ResolveWeaponHash(weapon, m_CurrentWeaponHash);
            
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

        private static int ResolveWeaponHash(object source, int fallback)
        {
            if (source == null) return fallback;

            if (TryResolveNestedInt(source, "Id", "Hash", out int directHash) && directHash != 0)
            {
                return directHash;
            }

            if (TryResolveNestedInt(source, "Weapon", "Id", "Hash", out int weaponHash) && weaponHash != 0)
            {
                return weaponHash;
            }

            return fallback;
        }

        private static bool TryResolveStrikeOutput(
            object output,
            out Character targetCharacter,
            out Vector3 point,
            out Vector3 direction)
        {
            targetCharacter = null;
            point = Vector3.zero;
            direction = Vector3.forward;

            if (output == null) return false;

            if (!TryGetMemberValue(output, "Target", out object targetObj) || targetObj == null)
            {
                return false;
            }

            if (targetObj is Character directCharacter)
            {
                targetCharacter = directCharacter;
            }
            else if (targetObj is Component component)
            {
                targetCharacter = component.GetComponent<Character>();
            }
            else if (targetObj is GameObject gameObject)
            {
                targetCharacter = gameObject.GetComponent<Character>();
            }

            if (targetCharacter == null) return false;
            if (!TryGetMemberValue(output, "Point", out point)) return false;
            if (!TryGetMemberValue(output, "Direction", out direction)) return false;

            return true;
        }

        private static bool TryResolveNestedInt(
            object source,
            string first,
            string second,
            out int value)
        {
            value = 0;
            if (source == null) return false;

            if (!TryGetMemberValue(source, first, out object nested) || nested == null) return false;
            if (!TryGetMemberValue(nested, second, out value)) return false;
            return true;
        }

        private static bool TryResolveNestedInt(
            object source,
            string first,
            string second,
            string third,
            out int value)
        {
            value = 0;
            if (source == null) return false;

            if (!TryGetMemberValue(source, first, out object nested) || nested == null) return false;
            if (!TryResolveNestedInt(nested, second, third, out value)) return false;
            return true;
        }

        private static bool TryGetMemberValue<T>(object source, string memberName, out T value)
        {
            value = default;
            if (source == null || string.IsNullOrEmpty(memberName)) return false;

            Type type = source.GetType();
            const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo property = type.GetProperty(memberName, FLAGS);
            if (property != null)
            {
                object raw = property.GetValue(source);
                if (TryConvertValue(raw, out value))
                {
                    return true;
                }
            }

            FieldInfo field = type.GetField(memberName, FLAGS);
            if (field != null)
            {
                object raw = field.GetValue(source);
                if (TryConvertValue(raw, out value))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryConvertValue<T>(object raw, out T value)
        {
            value = default;
            if (raw == null) return false;

            if (raw is T typed)
            {
                value = typed;
                return true;
            }

            try
            {
                object converted = Convert.ChangeType(raw, typeof(T));
                if (converted is T changeTypeValue)
                {
                    value = changeTypeValue;
                    return true;
                }
            }
            catch
            {
                // Ignore conversion failures and return false.
            }

            return false;
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
