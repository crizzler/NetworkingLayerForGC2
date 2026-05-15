#if GC2_SHOOTER
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// Lightweight deterministic mover for environment props hit by networked shooter weapons.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Shooter/Network Shooter Impact Prop")]
    public sealed class NetworkShooterImpactProp : MonoBehaviour
    {
        private static readonly Dictionary<uint, NetworkShooterImpactProp> Registry = new(128);

        [Header("Network Id")]
        [SerializeField] private bool m_UseAutomaticNetworkId = true;
        [SerializeField] private uint m_ManualNetworkId;
        [SerializeField] private string m_NetworkIdSalt = string.Empty;

        [Header("Motion")]
        [SerializeField] private bool m_ProjectDirectionOnGround = true;
        [SerializeField] private float m_DistancePerForce = 0.08f;
        [SerializeField] private float m_MinDisplacement = 0.05f;
        [SerializeField] private float m_MaxDisplacement = 2.5f;
        [SerializeField] private float m_MotionSpeed = 4.5f;
        [SerializeField] private float m_MinDuration = 0.08f;
        [SerializeField] private float m_MaxDuration = 0.65f;
        [SerializeField] private float m_RotationDegreesPerMeter = 180f;
        [SerializeField] private float m_MaxHitPointDistance = 1.5f;

        [Header("Rigidbody")]
        [SerializeField] private bool m_MakeRigidbodyKinematic = true;
        [SerializeField] private bool m_RestoreRigidbodyStateOnDisable;

        [Header("Debug")]
        [SerializeField] private bool m_LogDiagnostics;

        private Rigidbody m_Rigidbody;
        private bool m_HadRigidbody;
        private bool m_OriginalKinematic;
        private uint m_RegisteredNetworkId;

        private bool m_HasActiveMotion;
        private NetworkShooterImpactMotion m_ActiveMotion;

        public uint NetworkId => ResolveNetworkId();

        private void Awake()
        {
            CacheRigidbody();
            ConfigureRigidbodyForKinematicMotion();
        }

        private void OnEnable()
        {
            CacheRigidbody();
            ConfigureRigidbodyForKinematicMotion();
            Register();
        }

        private void OnDisable()
        {
            Unregister();

            if (m_RestoreRigidbodyStateOnDisable && m_HadRigidbody && m_Rigidbody != null)
            {
                m_Rigidbody.isKinematic = m_OriginalKinematic;
            }
        }

        private void Update()
        {
            if (!m_HasActiveMotion) return;

            float now = GetNetworkTime();
            float duration = Mathf.Max(0.0001f, m_ActiveMotion.Duration);
            float t = Mathf.Clamp01((now - m_ActiveMotion.StartTime) / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);

            Vector3 position = Vector3.LerpUnclamped(
                m_ActiveMotion.StartPosition,
                m_ActiveMotion.TargetPosition,
                eased);
            Quaternion rotation = Quaternion.SlerpUnclamped(
                m_ActiveMotion.StartRotation,
                m_ActiveMotion.TargetRotation,
                eased);

            ApplyPose(position, rotation);

            if (t >= 1f)
            {
                ApplyPose(m_ActiveMotion.TargetPosition, m_ActiveMotion.TargetRotation);
                m_HasActiveMotion = false;
            }
        }

        public bool TryBuildImpactMotion(
            Vector3 hitPoint,
            Vector3 hitNormal,
            float impactStrength,
            float serverTime,
            out NetworkShooterImpactMotion motion)
        {
            motion = default;

            uint networkId = NetworkId;
            if (networkId == 0 || impactStrength <= 0f) return false;
            if (!IsHitPointCloseEnough(hitPoint)) return false;

            Vector3 direction = hitNormal.sqrMagnitude > 0.0001f
                ? -hitNormal.normalized
                : transform.forward;

            if (m_ProjectDirectionOnGround)
            {
                Vector3 planar = Vector3.ProjectOnPlane(direction, Vector3.up);
                if (planar.sqrMagnitude > 0.0001f)
                {
                    direction = planar.normalized;
                }
            }

            if (direction.sqrMagnitude <= 0.0001f)
            {
                direction = transform.forward.sqrMagnitude > 0.0001f
                    ? transform.forward.normalized
                    : Vector3.forward;
            }

            float mass = m_Rigidbody != null ? Mathf.Max(0.001f, m_Rigidbody.mass) : 1f;
            float distance = Mathf.Clamp(
                impactStrength * m_DistancePerForce / mass,
                m_MinDisplacement,
                m_MaxDisplacement);

            float duration = Mathf.Clamp(
                distance / Mathf.Max(0.01f, m_MotionSpeed),
                m_MinDuration,
                m_MaxDuration);

            Vector3 startPosition = transform.position;
            Quaternion startRotation = transform.rotation;
            Vector3 targetPosition = startPosition + direction * distance;
            Quaternion targetRotation = BuildTargetRotation(startRotation, direction, distance);

            motion = new NetworkShooterImpactMotion
            {
                PropNetworkId = networkId,
                StartPosition = startPosition,
                StartRotation = startRotation,
                TargetPosition = targetPosition,
                TargetRotation = targetRotation,
                HitPoint = hitPoint,
                ImpactDirection = direction,
                StartTime = serverTime,
                Duration = duration,
                ImpactStrength = impactStrength
            };

            return true;
        }

        public void ApplyImpactMotion(NetworkShooterImpactMotion motion)
        {
            if (motion.PropNetworkId == 0 || motion.PropNetworkId != NetworkId) return;
            if (m_HasActiveMotion && motion.StartTime < m_ActiveMotion.StartTime) return;

            CacheRigidbody();
            ConfigureRigidbodyForKinematicMotion();

            m_ActiveMotion = motion;
            m_HasActiveMotion = true;

            float now = GetNetworkTime();
            float duration = Mathf.Max(0.0001f, motion.Duration);
            float t = Mathf.Clamp01((now - motion.StartTime) / duration);
            float eased = Mathf.SmoothStep(0f, 1f, t);
            ApplyPose(
                Vector3.LerpUnclamped(motion.StartPosition, motion.TargetPosition, eased),
                Quaternion.SlerpUnclamped(motion.StartRotation, motion.TargetRotation, eased));

            Log($"applied impact motion start={motion.StartPosition} target={motion.TargetPosition} " +
                $"duration={motion.Duration:F3} strength={motion.ImpactStrength:F2}");
        }

        public static bool TryGet(uint networkId, out NetworkShooterImpactProp prop)
        {
            if (networkId != 0 && Registry.TryGetValue(networkId, out prop) && prop != null)
            {
                return true;
            }

            prop = null;
            return false;
        }

        public static bool TryGetExisting(GameObject hitObject, out NetworkShooterImpactProp prop)
        {
            prop = null;
            if (hitObject == null) return false;
            if (hitObject.GetComponentInParent<Character>() != null) return false;

            prop = hitObject.GetComponentInParent<NetworkShooterImpactProp>();
            if (prop != null)
            {
                prop.CacheRigidbody();
                prop.ConfigureRigidbodyForKinematicMotion();
                prop.Register();
                return prop.NetworkId != 0;
            }

            Rigidbody rigidbody = ResolveRigidbody(hitObject);
            if (rigidbody == null || rigidbody.GetComponentInParent<Character>() != null) return false;

            prop = rigidbody.GetComponent<NetworkShooterImpactProp>();
            if (prop == null) return false;

            prop.CacheRigidbody();
            prop.ConfigureRigidbodyForKinematicMotion();
            prop.Register();
            return prop.NetworkId != 0;
        }

        public static bool TryFindExisting(uint networkId, out NetworkShooterImpactProp prop)
        {
            if (TryGet(networkId, out prop)) return true;

            NetworkShooterImpactProp[] props = FindObjectsByType<NetworkShooterImpactProp>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < props.Length; i++)
            {
                NetworkShooterImpactProp candidate = props[i];
                if (candidate == null || candidate.GetComponentInParent<Character>() != null) continue;

                if (candidate.NetworkId != networkId) continue;

                candidate.CacheRigidbody();
                candidate.ConfigureRigidbodyForKinematicMotion();
                candidate.Register();
                prop = candidate;
                return true;
            }

            prop = null;
            return false;
        }

        public static bool TryApplyImpactMotion(NetworkShooterImpactMotion motion)
        {
            if (!TryFindExisting(motion.PropNetworkId, out NetworkShooterImpactProp prop))
            {
                return false;
            }

            prop.ApplyImpactMotion(motion);
            return true;
        }

        private void Register()
        {
            uint networkId = NetworkId;
            if (networkId == 0) return;
            if (m_RegisteredNetworkId == networkId && Registry.TryGetValue(networkId, out var current) && current == this)
            {
                return;
            }

            if (m_RegisteredNetworkId != 0 && m_RegisteredNetworkId != networkId)
            {
                Unregister();
            }

            if (Registry.TryGetValue(networkId, out NetworkShooterImpactProp existing) && existing != null && existing != this)
            {
                Debug.LogWarning(
                    $"[NetworkShooterImpactProp] Duplicate prop network id {networkId}. " +
                    $"Objects: {existing.name}, {name}",
                    this);
            }

            Registry[networkId] = this;
            m_RegisteredNetworkId = networkId;
        }

        private void Unregister()
        {
            if (m_RegisteredNetworkId == 0) return;
            if (Registry.TryGetValue(m_RegisteredNetworkId, out NetworkShooterImpactProp existing) && existing == this)
            {
                Registry.Remove(m_RegisteredNetworkId);
            }

            m_RegisteredNetworkId = 0;
        }

        private uint ResolveNetworkId()
        {
            if (!m_UseAutomaticNetworkId)
            {
                return m_ManualNetworkId == 0 ? 1u : m_ManualNetworkId;
            }

            return BuildAutomaticNetworkId(gameObject, m_NetworkIdSalt);
        }

        private static uint BuildAutomaticNetworkId(GameObject gameObject, string salt)
        {
            if (gameObject == null) return 0;

            string sceneKey = !string.IsNullOrEmpty(gameObject.scene.path)
                ? gameObject.scene.path
                : gameObject.scene.name;
            string key = $"{sceneKey}|{BuildHierarchyPath(gameObject.transform)}|ShooterImpactProp|{salt}";
            uint hash = Fnv1A32(key);
            return hash == 0 ? 1u : hash;
        }

        private static string BuildHierarchyPath(Transform transform)
        {
            if (transform == null) return string.Empty;

            string path = $"{transform.GetSiblingIndex()}:{transform.name}";
            Transform current = transform.parent;
            while (current != null)
            {
                path = $"{current.GetSiblingIndex()}:{current.name}/{path}";
                current = current.parent;
            }

            return path;
        }

        private static uint Fnv1A32(string value)
        {
            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;

                uint hash = offset;
                for (int i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= prime;
                }

                return hash;
            }
        }

        private static Rigidbody ResolveRigidbody(GameObject hitObject)
        {
            if (hitObject == null) return null;
            if (hitObject.TryGetComponent(out Rigidbody ownRigidbody)) return ownRigidbody;
            if (hitObject.TryGetComponent(out Collider collider) && collider.attachedRigidbody != null)
            {
                return collider.attachedRigidbody;
            }

            return hitObject.GetComponentInParent<Rigidbody>();
        }

        private void CacheRigidbody()
        {
            if (m_Rigidbody != null) return;

            m_Rigidbody = GetComponent<Rigidbody>();
            m_HadRigidbody = m_Rigidbody != null;
            if (m_HadRigidbody)
            {
                m_OriginalKinematic = m_Rigidbody.isKinematic;
            }
        }

        private void ConfigureRigidbodyForKinematicMotion()
        {
            if (!m_MakeRigidbodyKinematic || m_Rigidbody == null) return;

            m_Rigidbody.isKinematic = true;
            m_Rigidbody.linearVelocity = Vector3.zero;
            m_Rigidbody.angularVelocity = Vector3.zero;
        }

        private bool IsHitPointCloseEnough(Vector3 hitPoint)
        {
            if (m_MaxHitPointDistance <= 0f) return true;

            Collider[] colliders = GetComponentsInChildren<Collider>();
            if (colliders == null || colliders.Length == 0)
            {
                return Vector3.Distance(transform.position, hitPoint) <= m_MaxHitPointDistance;
            }

            float maxDistanceSqr = m_MaxHitPointDistance * m_MaxHitPointDistance;
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger) continue;

                Vector3 closest = collider.ClosestPoint(hitPoint);
                if ((closest - hitPoint).sqrMagnitude <= maxDistanceSqr)
                {
                    return true;
                }
            }

            return false;
        }

        private Quaternion BuildTargetRotation(Quaternion startRotation, Vector3 direction, float distance)
        {
            if (m_RotationDegreesPerMeter <= 0f) return startRotation;

            Vector3 axis = Vector3.Cross(Vector3.up, direction);
            if (axis.sqrMagnitude <= 0.0001f) return startRotation;

            float angle = Mathf.Clamp(distance * m_RotationDegreesPerMeter, -360f, 360f);
            return Quaternion.AngleAxis(angle, axis.normalized) * startRotation;
        }

        private void ApplyPose(Vector3 position, Quaternion rotation)
        {
            if (m_Rigidbody != null)
            {
                m_Rigidbody.position = position;
                m_Rigidbody.rotation = rotation;
                m_Rigidbody.linearVelocity = Vector3.zero;
                m_Rigidbody.angularVelocity = Vector3.zero;
                return;
            }

            transform.SetPositionAndRotation(position, rotation);
        }

        private static float GetNetworkTime()
        {
            NetworkShooterManager manager = NetworkShooterManager.Instance;
            return manager != null ? manager.NetworkTime : Time.time;
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkShooterImpactProp] {name} id={NetworkId} {message}", this);
        }
    }
}
#endif
