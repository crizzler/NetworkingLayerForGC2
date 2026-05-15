#if GC2_SHOOTER
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Shooter;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter
{
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class NetworkShooterProjectileVfx : MonoBehaviour
    {
        private static readonly RaycastHit[] HITS = new RaycastHit[16];

        private Character m_Source;
        private ShooterWeapon m_Weapon;
        private Vector3 m_Velocity;
        private LayerMask m_LayerMask;
        private float m_MaxDistance;
        private float m_Lifetime;
        private float m_SpawnTime;
        private float m_Distance;
        private float m_AirResistance;
        private float m_WindInfluence;
        private float m_Gravity;
        private bool m_UseRigidbody;
        private Vector3 m_LastPosition;
        private Bullet m_Bullet;
        private bool m_BulletWasEnabled;

        public void Configure(
            Character source,
            ShooterWeapon weapon,
            Vector3 velocity,
            LayerMask layerMask,
            float maxDistance,
            float lifetime,
            float airResistance,
            float windInfluence,
            float gravity,
            bool useRigidbody)
        {
            m_Source = source;
            m_Weapon = weapon;
            m_Velocity = velocity;
            m_LayerMask = layerMask;
            m_MaxDistance = Mathf.Max(0.1f, maxDistance);
            m_Lifetime = Mathf.Max(0.1f, lifetime);
            m_AirResistance = Mathf.Max(0f, airResistance);
            m_WindInfluence = Mathf.Max(0f, windInfluence);
            m_Gravity = Mathf.Max(0f, gravity);
            m_UseRigidbody = useRigidbody;
            m_SpawnTime = Time.time;
            m_Distance = 0f;
            m_LastPosition = transform.position;

            if (m_Bullet != null)
            {
                m_Bullet.enabled = m_BulletWasEnabled;
            }

            m_Bullet = GetComponent<Bullet>();
            if (m_Bullet != null)
            {
                m_BulletWasEnabled = m_Bullet.enabled;
                m_Bullet.enabled = false;
            }
        }

        private void OnEnable()
        {
            m_SpawnTime = Time.time;
            m_Distance = 0f;
            m_LastPosition = transform.position;
        }

        private void OnDisable()
        {
            if (m_Bullet != null)
            {
                m_Bullet.enabled = m_BulletWasEnabled;
                m_Bullet = null;
            }
        }

        private void Update()
        {
            if (Time.time - m_SpawnTime > m_Lifetime)
            {
                gameObject.SetActive(false);
                return;
            }

            Vector3 previous = transform.position;
            Vector3 next = previous;

            if (!m_UseRigidbody)
            {
                Vector3 drag = -m_AirResistance * m_Velocity;
                Vector3 wind = WindManager.Instance != null
                    ? WindManager.Instance.Wind * m_WindInfluence
                    : Vector3.zero;
                Vector3 gravity = Vector3.down * m_Gravity;

                m_Velocity += (drag + wind + gravity) * Time.deltaTime;
                next = previous + m_Velocity * Time.deltaTime;
                transform.position = next;

                if (m_Velocity.sqrMagnitude > 0.0001f)
                {
                    transform.rotation = Quaternion.LookRotation(m_Velocity.normalized);
                }
            }
            else
            {
                previous = m_LastPosition;
                next = transform.position;
            }

            Vector3 delta = next - previous;
            float stepDistance = delta.magnitude;
            if (stepDistance > 0.0001f && TryFindHit(previous, delta / stepDistance, stepDistance, out RaycastHit hit))
            {
                transform.position = hit.point;
                gameObject.SetActive(false);
                return;
            }

            m_Distance += stepDistance;
            m_LastPosition = transform.position;
            if (m_Distance >= m_MaxDistance)
            {
                gameObject.SetActive(false);
            }
        }

        private bool TryFindHit(Vector3 origin, Vector3 direction, float distance, out RaycastHit hit)
        {
            hit = default;
            float closestDistance = float.MaxValue;
            int count = Physics.RaycastNonAlloc(
                origin,
                direction,
                HITS,
                distance,
                m_LayerMask,
                QueryTriggerInteraction.Ignore
            );

            for (int i = 0; i < count; i++)
            {
                RaycastHit candidate = HITS[i];
                if (candidate.collider == null) continue;
                if (candidate.collider.transform == transform ||
                    candidate.collider.transform.IsChildOf(transform))
                {
                    continue;
                }

                if (m_Source != null &&
                    candidate.collider.GetComponentInParent<Character>() == m_Source)
                {
                    continue;
                }

                if (candidate.distance >= closestDistance) continue;

                closestDistance = candidate.distance;
                hit = candidate;
            }

            return hit.collider != null;
        }

        private void ApplyPhysicalImpact(Collider collider, Vector3 point, Vector3 direction)
        {
            if (collider == null || m_Weapon == null) return;
            if (!m_Weapon.Fire.ForceEnabled) return;
            if (collider.GetComponentInParent<Character>() != null) return;

            Rigidbody rigidbody = collider.attachedRigidbody != null
                ? collider.attachedRigidbody
                : collider.GetComponentInParent<Rigidbody>();

            if (rigidbody == null) return;

            Vector3 force = direction.sqrMagnitude > 0.0001f
                ? direction.normalized * m_Weapon.Fire.Force
                : transform.forward * m_Weapon.Fire.Force;

            rigidbody.AddForceAtPosition(force, point, ForceMode.Impulse);
        }
    }
}
#endif
