using UnityEngine;

namespace Arawn.NetworkingCore.LagCompensation
{
    /// <summary>
    /// MonoBehaviour that automatically initializes LagCompensationManager
    /// and records frames every FixedUpdate on the server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add this to a persistent GameObject in your scene (e.g., a NetworkManager
    /// or bootstrap object). It handles the two steps that are NOT automatic:
    /// 1. Calling LagCompensationManager.Initialize() on server startup
    /// 2. Calling RecordFrame() every server tick
    /// </para>
    /// <para>
    /// This component is network-agnostic. Tell it whether this instance is the
    /// server by calling <see cref="SetServerMode"/> from your networking solution's
    /// spawn/start callback, or enable <see cref="m_TreatAsServer"/> in the inspector
    /// for testing.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // From your networking solution's server start callback:
    /// GetComponent&lt;LagCompensationBootstrap&gt;().SetServerMode(true);
    ///
    /// // Or with Netcode for GameObjects:
    /// public override void OnNetworkSpawn()
    /// {
    ///     GetComponent&lt;LagCompensationBootstrap&gt;().SetServerMode(IsServer);
    /// }
    /// </code>
    /// </example>
    [AddComponentMenu("Networking/Lag Compensation Bootstrap")]
    public class LagCompensationBootstrap : MonoBehaviour
    {
        // INSPECTOR ──────────────────────────────────────────────────────────

        [Header("Server Detection")]
        [Tooltip("Manually mark this instance as the server.\n" +
                 "Useful for testing or when not using SetServerMode().")]
        [SerializeField] private bool m_TreatAsServer;

        [Tooltip("Automatically initialize when this component starts.\n" +
                 "Disable if you want to call SetServerMode() later.")]
        [SerializeField] private bool m_InitializeOnStart = true;

        [Header("Configuration")]
        [Tooltip("How many snapshots to store per entity.")]
        [Range(16, 256)]
        [SerializeField] private int m_HistorySize = 64;

        [Tooltip("How often to record snapshots (Hz).")]
        [Range(10, 120)]
        [SerializeField] private int m_SnapshotRate = 60;

        [Tooltip("Maximum age of snapshots to consider (seconds).")]
        [Range(0.1f, 2f)]
        [SerializeField] private float m_MaxHistoryAge = 1f;

        [Tooltip("Extra tolerance when validating hits (meters).")]
        [Range(0f, 1f)]
        [SerializeField] private float m_HitTolerance = 0.2f;

        [Tooltip("Maximum allowed rewind into the past (seconds).")]
        [Range(0.1f, 1f)]
        [SerializeField] private float m_MaxRewindTime = 0.5f;

        // FIELDS ─────────────────────────────────────────────────────────────

        private bool m_IsServer;
        private bool m_IsInitialized;

        // PROPERTIES ─────────────────────────────────────────────────────────

        /// <summary>
        /// Whether this instance is acting as the server.
        /// </summary>
        public bool IsServer => m_IsServer;

        /// <summary>
        /// Whether the manager has been initialized by this bootstrap.
        /// </summary>
        public bool IsInitialized => m_IsInitialized;

        // UNITY LIFECYCLE ────────────────────────────────────────────────────

        private void Start()
        {
            if (m_InitializeOnStart && m_TreatAsServer)
            {
                InitializeManager();
            }
        }

        private void FixedUpdate()
        {
            if (!m_IsServer || !m_IsInitialized) return;

            var timestamp = NetworkTimestamp.FromServerTime(Time.timeAsDouble);
            LagCompensationManager.Instance.RecordFrame(timestamp);
        }

        private void OnDestroy()
        {
            if (m_IsInitialized && LagCompensationManager.IsInitialized)
            {
                LagCompensationManager.Instance.Dispose();
                m_IsInitialized = false;
            }
        }

        // PUBLIC API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Tell the bootstrap whether this instance is the server.
        /// Call this from your networking solution's server/host start callback.
        /// Automatically initializes the manager if <paramref name="isServer"/> is true.
        /// </summary>
        /// <param name="isServer">True if this instance is the server or host.</param>
        public void SetServerMode(bool isServer)
        {
            m_IsServer = isServer;

            if (isServer && !m_IsInitialized)
            {
                InitializeManager();
            }
            else if (!isServer && m_IsInitialized)
            {
                // Switched away from server (unlikely, but handle gracefully)
                LagCompensationManager.Instance.Dispose();
                m_IsInitialized = false;
            }
        }

        /// <summary>
        /// Build a LagCompensationConfig from the current inspector values.
        /// </summary>
        public LagCompensationConfig BuildConfig()
        {
            return new LagCompensationConfig
            {
                historySize = m_HistorySize,
                snapshotRate = m_SnapshotRate,
                maxHistoryAge = m_MaxHistoryAge,
                hitTolerance = m_HitTolerance,
                maxRewindTime = m_MaxRewindTime
            };
        }

        // PRIVATE ────────────────────────────────────────────────────────────

        private void InitializeManager()
        {
            if (m_IsInitialized) return;

            var config = BuildConfig();
            LagCompensationManager.Initialize(config);

            m_IsServer = true;
            m_IsInitialized = true;

            Debug.Log("[LagCompensation] Manager initialized via bootstrap. " +
                      $"History={m_HistorySize}, Rate={m_SnapshotRate}Hz, " +
                      $"MaxRewind={m_MaxRewindTime}s");
        }
    }
}
