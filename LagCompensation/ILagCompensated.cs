using UnityEngine;

namespace Arawn.NetworkingCore.LagCompensation
{
    /// <summary>
    /// Interface for any networked entity that supports lag compensation.
    /// Implement this on GC2 characters, Enemy Masses agents, or any networked object
    /// that needs server-side hit validation with historical position rewinding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lag compensation allows the server to validate hits based on where targets
    /// were at the time the attacker fired, accounting for network latency.
    /// </para>
    /// <para>
    /// This interface is network-agnostic and works with:
    /// - Unity Netcode for GameObjects
    /// - Photon Fusion / PUN
    /// - FishNet
    /// - Mirror
    /// - Custom solutions
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public class MyNetworkedEntity : MonoBehaviour, ILagCompensated
    /// {
    ///     public uint NetworkId => netId;
    ///     public Vector3 Position => transform.position;
    ///     public Quaternion Rotation => transform.rotation;
    ///     public Bounds Bounds => collider.bounds;
    ///     public bool IsActive => gameObject.activeInHierarchy;
    ///     public float Radius => 0.5f;
    ///     public float Height => 2f;
    /// }
    /// </code>
    /// </example>
    public interface ILagCompensated
    {
        /// <summary>
        /// Unique network identifier for this entity.
        /// Must be consistent across all clients and server.
        /// </summary>
        uint NetworkId { get; }
        
        /// <summary>
        /// Current world-space position of the entity.
        /// </summary>
        Vector3 Position { get; }
        
        /// <summary>
        /// Current world-space rotation of the entity.
        /// </summary>
        Quaternion Rotation { get; }
        
        /// <summary>
        /// Axis-aligned bounding box for broad-phase hit detection.
        /// </summary>
        Bounds Bounds { get; }
        
        /// <summary>
        /// Whether this entity is currently active and can be hit.
        /// Return false for dead, despawned, or inactive entities.
        /// </summary>
        bool IsActive { get; }
        
        /// <summary>
        /// Collision radius for cylindrical/spherical hit detection.
        /// </summary>
        float Radius { get; }
        
        /// <summary>
        /// Height for cylindrical hit detection (from position upward).
        /// </summary>
        float Height { get; }
    }
    
    /// <summary>
    /// Extended interface for entities with hit zones (head, torso, legs, etc.)
    /// </summary>
    public interface ILagCompensatedWithHitZones : ILagCompensated
    {
        /// <summary>
        /// Get all hit zones for this entity.
        /// </summary>
        LagCompensatedHitZone[] GetHitZones();
        
        /// <summary>
        /// Get a specific hit zone by name.
        /// </summary>
        bool TryGetHitZone(string zoneName, out LagCompensatedHitZone hitZone);
    }
    
    /// <summary>
    /// Represents a hit zone on an entity (head, torso, etc.)
    /// </summary>
    [System.Serializable]
    public struct LagCompensatedHitZone
    {
        /// <summary>Zone identifier (e.g., "head", "torso", "legs")</summary>
        public string name;
        
        /// <summary>Local offset from entity position</summary>
        public Vector3 localOffset;
        
        /// <summary>Collision radius for this zone</summary>
        public float radius;
        
        /// <summary>Min height (local Y) of this zone</summary>
        public float minHeight;
        
        /// <summary>Max height (local Y) of this zone</summary>
        public float maxHeight;
        
        /// <summary>Damage multiplier when this zone is hit</summary>
        public float damageMultiplier;
        
        /// <summary>Whether this zone is a critical hit zone</summary>
        public bool isCritical;
        
        /// <summary>
        /// Get the world-space center of this hit zone.
        /// </summary>
        public Vector3 GetWorldCenter(Vector3 entityPosition, Quaternion entityRotation)
        {
            return entityPosition + entityRotation * localOffset + 
                   Vector3.up * ((minHeight + maxHeight) * 0.5f);
        }
    }
}
