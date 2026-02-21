using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using Arawn.NetworkingCore;
using Arawn.NetworkingCore.LagCompensation;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Lag compensation adapter for Game Creator 2 characters.
    /// Implements ILagCompensated to enable server-side hit validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attach this component to any networked GC2 character that needs lag compensation.
    /// The component automatically registers with the LagCompensationManager.
    /// </para>
    /// <para>
    /// This component is network-agnostic. Works with NGO, Photon, FishNet, Mirror, etc.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Character))]
    [AddComponentMenu("Game Creator/Network/Character Lag Compensation")]
    public class CharacterLagCompensation : MonoBehaviour, ILagCompensatedWithHitZones
    {
        // INSPECTOR ──────────────────────────────────────────────────────────
        
        [Header("Network Identity")]
        [Tooltip("Network ID for this character. Set by your networking solution.")]
        [SerializeField] private uint m_NetworkId;
        
        [Header("Hit Detection")]
        [Tooltip("Collision radius for cylindrical hit detection.")]
        [SerializeField] private float m_Radius = 0.4f;
        
        [Tooltip("Height for cylindrical hit detection.")]
        [SerializeField] private float m_Height = 1.8f;
        
        [Tooltip("Use Character's capsule dimensions instead of manual values.")]
        [SerializeField] private bool m_UseCharacterDimensions = true;
        
        [Header("Hit Zones (Optional)")]
        [Tooltip("Define hit zones for damage multipliers (head, torso, etc.)")]
        [SerializeField] private CharacterHitZone[] m_HitZones = new CharacterHitZone[]
        {
            new CharacterHitZone 
            { 
                name = "Head", 
                heightMin = 0.75f, 
                heightMax = 1f, 
                radius = 0.15f,
                damageMultiplier = 2f, 
                isCritical = true 
            },
            new CharacterHitZone 
            { 
                name = "Torso", 
                heightMin = 0.4f, 
                heightMax = 0.75f, 
                radius = 0.3f,
                damageMultiplier = 1f, 
                isCritical = false 
            },
            new CharacterHitZone 
            { 
                name = "Legs", 
                heightMin = 0f, 
                heightMax = 0.4f, 
                radius = 0.2f,
                damageMultiplier = 0.75f, 
                isCritical = false 
            }
        };
        
        [Header("Registration")]
        [Tooltip("Auto-register with LagCompensationManager on Start.")]
        [SerializeField] private bool m_AutoRegister = true;
        
        [Tooltip("Only register on server (disable on clients).")]
        [SerializeField] private bool m_ServerOnly = true;
        
        // COMPONENTS ─────────────────────────────────────────────────────────
        
        private Character m_Character;
        private CharacterController m_Controller;
        private bool m_IsRegistered;
        
        // EVENTS ─────────────────────────────────────────────────────────────
        
        /// <summary>
        /// Called when this character is hit via lag compensation validation.
        /// </summary>
        public event Action<HitValidationResult> OnHitValidated;
        
        /// <summary>
        /// Called to determine if this character is currently alive/hittable.
        /// Override default behavior by subscribing to this.
        /// </summary>
        public Func<bool> IsAliveCheck;
        
        // ILagCompensated IMPLEMENTATION ─────────────────────────────────────
        
        public uint NetworkId
        {
            get => m_NetworkId;
            set => m_NetworkId = value;
        }
        
        public Vector3 Position => transform.position;
        
        public Quaternion Rotation => transform.rotation;
        
        public Bounds Bounds
        {
            get
            {
                float radius = GetRadius();
                float height = GetHeight();
                
                Vector3 center = transform.position + Vector3.up * (height * 0.5f);
                Vector3 size = new Vector3(radius * 2f, height, radius * 2f);
                
                return new Bounds(center, size);
            }
        }
        
        public bool IsActive
        {
            get
            {
                // Allow custom override
                if (IsAliveCheck != null)
                    return IsAliveCheck();
                
                // Default: check if gameobject active and character alive
                if (!gameObject.activeInHierarchy)
                    return false;
                
                if (m_Character != null)
                    return !m_Character.IsDead;
                
                return true;
            }
        }
        
        public float Radius => GetRadius();
        
        public float Height => GetHeight();
        
        // ILagCompensatedWithHitZones IMPLEMENTATION ─────────────────────────
        
        public LagCompensatedHitZone[] GetHitZones()
        {
            if (m_HitZones == null || m_HitZones.Length == 0)
                return Array.Empty<LagCompensatedHitZone>();
            
            float height = GetHeight();
            var zones = new LagCompensatedHitZone[m_HitZones.Length];
            
            for (int i = 0; i < m_HitZones.Length; i++)
            {
                var src = m_HitZones[i];
                zones[i] = new LagCompensatedHitZone
                {
                    name = src.name,
                    localOffset = Vector3.zero,
                    radius = src.radius,
                    minHeight = src.heightMin * height,
                    maxHeight = src.heightMax * height,
                    damageMultiplier = src.damageMultiplier,
                    isCritical = src.isCritical
                };
            }
            
            return zones;
        }
        
        public bool TryGetHitZone(string zoneName, out LagCompensatedHitZone hitZone)
        {
            hitZone = default;
            
            if (m_HitZones == null)
                return false;
            
            float height = GetHeight();
            
            foreach (var src in m_HitZones)
            {
                if (src.name == zoneName)
                {
                    hitZone = new LagCompensatedHitZone
                    {
                        name = src.name,
                        localOffset = Vector3.zero,
                        radius = src.radius,
                        minHeight = src.heightMin * height,
                        maxHeight = src.heightMax * height,
                        damageMultiplier = src.damageMultiplier,
                        isCritical = src.isCritical
                    };
                    return true;
                }
            }
            
            return false;
        }
        
        // UNITY LIFECYCLE ────────────────────────────────────────────────────
        
        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_Controller = GetComponent<CharacterController>();
        }
        
        private void Start()
        {
            if (m_AutoRegister)
            {
                Register();
            }
        }
        
        private void OnDestroy()
        {
            Unregister();
        }
        
        private void OnDisable()
        {
            // Optionally unregister when disabled
            // Unregister();
        }
        
        // PUBLIC METHODS ─────────────────────────────────────────────────────
        
        /// <summary>
        /// Register this character with the LagCompensationManager.
        /// Call this when the character spawns on the server.
        /// </summary>
        public void Register()
        {
            if (m_IsRegistered)
                return;
            
            if (LagCompensationManager.IsInitialized || !m_ServerOnly)
            {
                LagCompensationManager.Instance.Register(this);
                m_IsRegistered = true;
            }
        }
        
        /// <summary>
        /// Unregister this character from the LagCompensationManager.
        /// Call this when the character despawns.
        /// </summary>
        public void Unregister()
        {
            if (!m_IsRegistered)
                return;
            
            if (LagCompensationManager.IsInitialized)
            {
                LagCompensationManager.Instance.Unregister(this);
            }
            
            m_IsRegistered = false;
        }
        
        /// <summary>
        /// Set the network ID (call from your networking solution).
        /// </summary>
        public void SetNetworkId(uint networkId)
        {
            m_NetworkId = networkId;
        }
        
        /// <summary>
        /// Validate a hit against this character at a historical timestamp.
        /// </summary>
        public HitValidationResult ValidateHit(Vector3 hitPoint, NetworkTimestamp timestamp)
        {
            var result = LagCompensationManager.Instance.ValidateHit(
                m_NetworkId, hitPoint, timestamp
            );
            
            // Determine hit zone if valid
            if (result.isValid && m_HitZones != null)
            {
                DetermineHitZone(ref result);
            }
            
            OnHitValidated?.Invoke(result);
            return result;
        }
        
        /// <summary>
        /// Get the hit zone at a specific local height (0-1 normalized).
        /// </summary>
        public CharacterHitZone? GetHitZoneAtHeight(float normalizedHeight)
        {
            if (m_HitZones == null)
                return null;
            
            foreach (var zone in m_HitZones)
            {
                if (normalizedHeight >= zone.heightMin && normalizedHeight < zone.heightMax)
                    return zone;
            }
            
            return null;
        }
        
        // PRIVATE HELPERS ────────────────────────────────────────────────────
        
        private float GetRadius()
        {
            if (m_UseCharacterDimensions)
            {
                if (m_Controller != null)
                    return m_Controller.radius;
                if (m_Character != null)
                    return m_Character.Motion.Radius;
            }
            return m_Radius;
        }
        
        private float GetHeight()
        {
            if (m_UseCharacterDimensions)
            {
                if (m_Controller != null)
                    return m_Controller.height;
                if (m_Character != null)
                    return m_Character.Motion.Height;
            }
            return m_Height;
        }
        
        private void DetermineHitZone(ref HitValidationResult result)
        {
            // Calculate local height of hit
            float hitLocalY = result.hitPoint.y - transform.position.y;
            float height = GetHeight();
            float normalizedHeight = hitLocalY / height;
            
            var zone = GetHitZoneAtHeight(normalizedHeight);
            if (zone.HasValue)
            {
                result.hitZoneName = zone.Value.name;
                result.damageMultiplier = zone.Value.damageMultiplier;
            }
            else
            {
                result.hitZoneName = "Body";
                result.damageMultiplier = 1f;
            }
        }
        
        // DEBUG ──────────────────────────────────────────────────────────────
        
        #if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            float radius = m_UseCharacterDimensions && m_Character != null 
                ? m_Character.Motion.Radius 
                : m_Radius;
            float height = m_UseCharacterDimensions && m_Character != null 
                ? m_Character.Motion.Height 
                : m_Height;
            
            // Draw capsule
            Vector3 bottom = transform.position + Vector3.up * radius;
            Vector3 top = transform.position + Vector3.up * (height - radius);
            
            Gizmos.color = IsActive ? Color.green : Color.red;
            Gizmos.DrawWireSphere(bottom, radius);
            Gizmos.DrawWireSphere(top, radius);
            Gizmos.DrawLine(bottom + Vector3.forward * radius, top + Vector3.forward * radius);
            Gizmos.DrawLine(bottom - Vector3.forward * radius, top - Vector3.forward * radius);
            Gizmos.DrawLine(bottom + Vector3.right * radius, top + Vector3.right * radius);
            Gizmos.DrawLine(bottom - Vector3.right * radius, top - Vector3.right * radius);
            
            // Draw hit zones
            if (m_HitZones != null)
            {
                var colors = new[] { Color.red, Color.yellow, Color.blue, Color.cyan, Color.magenta };
                for (int i = 0; i < m_HitZones.Length; i++)
                {
                    var zone = m_HitZones[i];
                    Gizmos.color = colors[i % colors.Length];
                    
                    float zoneMin = zone.heightMin * height;
                    float zoneMax = zone.heightMax * height;
                    float zoneCenter = (zoneMin + zoneMax) * 0.5f;
                    
                    Vector3 center = transform.position + Vector3.up * zoneCenter;
                    Gizmos.DrawWireSphere(center, zone.radius);
                }
            }
        }
        #endif
    }
    
    /// <summary>
    /// Configuration for a character hit zone.
    /// </summary>
    [Serializable]
    public struct CharacterHitZone
    {
        [Tooltip("Zone name (e.g., Head, Torso, Legs)")]
        public string name;
        
        [Tooltip("Minimum height (0-1 normalized)")]
        [Range(0f, 1f)]
        public float heightMin;
        
        [Tooltip("Maximum height (0-1 normalized)")]
        [Range(0f, 1f)]
        public float heightMax;
        
        [Tooltip("Collision radius for this zone")]
        public float radius;
        
        [Tooltip("Damage multiplier when this zone is hit")]
        public float damageMultiplier;
        
        [Tooltip("Whether this is a critical hit zone")]
        public bool isCritical;
    }
}
