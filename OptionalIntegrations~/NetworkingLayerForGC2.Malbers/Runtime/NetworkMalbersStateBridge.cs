#if ARAWN_GC2_MALBERS
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Characters;
using MalbersAnimations.Controller;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Malbers
{
    /// <summary>
    /// Replicates the native Malbers Animal Controller layer that GC2 does not observe:
    /// active state, active mode/ability, stance and grounded. The GC2 Animim state and
    /// gesture layer remains owned by <see cref="NetworkAnimationManager"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Authority rules mirror the motor bridge:
    /// </para>
    /// <list type="bullet">
    /// <item><description>the server instance of a server-authoritative NPC broadcasts;</description></item>
    /// <item><description>the owning client of a player-owned character sends to the server,
    /// which validates ownership and broadcasts;</description></item>
    /// <item><description>every other role (remote player or NPC proxy) applies the last
    /// accepted snapshot.</description></item>
    /// </list>
    /// <para>
    /// States and modes are identified by their Malbers asset IDs. Every peer must use the
    /// same Malbers Animal Controller package and the same state/mode/stance catalog. A peer
    /// with a different catalog logs a rejected force and keeps its current state.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10001)]
    [AddComponentMenu("Game Creator/Network/Malbers State Bridge")]
    public sealed class NetworkMalbersStateBridge : MonoBehaviour
    {
        [Header("Replication")]
        [Tooltip(
            "Seconds between authority samples. Only changed values are sent. Set to zero " +
            "to send every frame (not recommended).")]
        [SerializeField] private float m_SendInterval = 0.1f;

        [Tooltip(
            "Seconds after which an unchanged snapshot is resent. Late joiners and recovering " +
            "proxies would otherwise wait for the next state change to converge. Set to zero to " +
            "disable the heartbeat.")]
        [SerializeField] private float m_HeartbeatInterval = 1.5f;

        [Tooltip(
            "Creates a NetworkMalbersStateManager when the scene has none. Leave enabled for " +
            "standalone demos; disable when a bootstrap owns manager creation.")]
        [SerializeField] private bool m_CreateManagerIfMissing = true;

        [Tooltip(
            "Also applies the replicated grounded flag to MAnimal when no motor bridge owns " +
            "that decision.")]
        [SerializeField] private bool m_ApplyGroundedWithoutMotorBridge = true;

        [Header("Diagnostics")]
        [SerializeField] private bool m_LogDiagnostics;

        private MAnimal m_Animal;
        private NetworkCharacter m_NetworkCharacter;
        private NetworkMalbersMotorBridge m_MotorBridge;
        private NetworkMalbersStateManager m_Manager;
        private NetworkMalbersStateManager m_SubscribedManager;

        private NetworkMalbersStateMessage m_Pending;
        private NetworkMalbersStateMessage m_LastApplied;
        private bool m_HasPending;
        private bool m_HasAppliedSnapshot;
        private float m_NextReconcileTime;
        private bool m_Subscribed;
        private bool m_SubscribedToNetworkCharacter;
        private float m_NextSampleTime;
        private float m_NextHeartbeatTime;
        private bool m_HasLastSent;

        private int m_LastStateId;
        private int m_LastModeId;
        private int m_LastAbilityIndex;
        private int m_LastStanceId;
        private bool m_LastGrounded;

        /// <summary>
        /// Diagnostics for multi-peer verification: number of authoritative snapshots applied to
        /// this proxy instance.
        /// </summary>
        public int AppliedSnapshotCount { get; private set; }

        /// <summary>Diagnostics: character network id carried by the last applied snapshot.</summary>
        public int LastAppliedCharacterId =>
            m_HasAppliedSnapshot ? unchecked((int)m_LastApplied.CharacterNetworkId) : 0;

        /// <summary>Diagnostics: state id carried by the last applied snapshot.</summary>
        public int LastAppliedStateId => m_HasAppliedSnapshot ? m_LastApplied.StateId : 0;

        private void Awake()
        {
            m_Animal = GetComponent<MAnimal>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            m_MotorBridge = GetComponent<NetworkMalbersMotorBridge>();
        }

        private void OnEnable()
        {
            SubscribeNetworkCharacter();
            EnsureManager();
        }

        private void Start()
        {
            EnsureManager();
        }

        private void OnDisable()
        {
            UnsubscribeNetworkCharacter();
            UnsubscribeManager();
            m_HasPending = false;
            m_HasLastSent = false;
            m_HasAppliedSnapshot = false;
        }

        private void Update()
        {
            EnsureManager();
            if (m_Manager == null || m_NetworkCharacter == null) return;

            if (CanSend())
            {
                TrySendSnapshot();
            }
        }

        private void LateUpdate()
        {
            if (m_HasPending)
            {
                m_HasPending = false;
                ApplySnapshot(m_Pending);
                return;
            }

            if (!m_HasAppliedSnapshot || m_Animal == null ||
                Time.unscaledTime < m_NextReconcileTime)
            {
                return;
            }

            m_NextReconcileTime = Time.unscaledTime + 0.5f;
            if (NeedsReconcile(m_LastApplied))
            {
                Log($"reconcile character={m_LastApplied.CharacterNetworkId}");
                ApplySnapshot(m_LastApplied);
            }
        }

        private bool NeedsReconcile(NetworkMalbersStateMessage message)
        {
            if (message.Has(NetworkMalbersStateFlags.State) && message.StateId > 0)
            {
                int current = m_Animal.ActiveStateID != null ? m_Animal.ActiveStateID.ID : 0;
                if (current != message.StateId) return true;
            }

            if (message.Has(NetworkMalbersStateFlags.Mode))
            {
                if (message.ModeId > 0 &&
                    (m_Animal.ActiveModeID != message.ModeId ||
                     m_Animal.ModeAbility != message.AbilityIndex))
                {
                    return true;
                }

                if (message.ModeId <= 0 && m_Animal.IsPlayingMode) return true;
            }

            if (message.Has(NetworkMalbersStateFlags.Stance))
            {
                int currentStance = m_Animal.Stance != null ? m_Animal.Stance.ID : 0;
                if (currentStance != message.StanceId) return true;
            }

            return false;
        }

        private void SubscribeNetworkCharacter()
        {
            if (m_SubscribedToNetworkCharacter || m_NetworkCharacter == null) return;
            m_NetworkCharacter.OnRoleAssigned += OnRoleAssigned;
            m_NetworkCharacter.OnRoleReset += OnRoleReset;
            m_SubscribedToNetworkCharacter = true;
        }

        private void UnsubscribeNetworkCharacter()
        {
            if (!m_SubscribedToNetworkCharacter) return;
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleAssigned -= OnRoleAssigned;
                m_NetworkCharacter.OnRoleReset -= OnRoleReset;
            }

            m_SubscribedToNetworkCharacter = false;
        }

        private void OnRoleAssigned(NetworkCharacter.NetworkRole role)
        {
            m_HasLastSent = false;
            m_NextSampleTime = 0f;
            if (role == NetworkCharacter.NetworkRole.Server ||
                role == NetworkCharacter.NetworkRole.LocalClient)
            {
                m_HasPending = false;
            }
        }

        private void OnRoleReset()
        {
            m_HasLastSent = false;
            m_HasPending = false;
            m_HasAppliedSnapshot = false;
        }

        private void EnsureManager()
        {
            // Do not early-return on a cached manager: a disabled bridge unsubscribes in
            // OnDisable, and Fusion disables proxy characters while it admits their identities.
            // Re-subscribing here is what keeps those proxies receiving snapshots after the
            // object is enabled again.
            m_Manager = NetworkMalbersStateManager.Instance;
            if (m_Manager == null)
            {
#if UNITY_2023_1_OR_NEWER
                m_Manager = UnityObjectSearch.FindAny<NetworkMalbersStateManager>(
                    FindObjectsInactive.Include);
#else
                m_Manager = FindObjectOfType<NetworkMalbersStateManager>(true);
#endif
            }

            if (m_Manager == null && m_CreateManagerIfMissing)
            {
                var go = new GameObject("Network Malbers State Manager");
                m_Manager = go.AddComponent<NetworkMalbersStateManager>();
            }

            if (m_Manager == null) return;

            if (m_Subscribed && !ReferenceEquals(m_SubscribedManager, m_Manager))
            {
                if (m_SubscribedManager != null)
                {
                    m_SubscribedManager.StateReceived -= OnStateReceived;
                }

                m_SubscribedManager = null;
                m_Subscribed = false;
            }

            if (!m_Subscribed)
            {
                m_Manager.StateReceived += OnStateReceived;
                m_SubscribedManager = m_Manager;
                m_Subscribed = true;
                Log($"manager resolved '{m_Manager.name}'");
            }
        }

        private void UnsubscribeManager()
        {
            if (!m_Subscribed) return;
            if (m_SubscribedManager != null)
            {
                m_SubscribedManager.StateReceived -= OnStateReceived;
            }

            m_SubscribedManager = null;
            m_Subscribed = false;
        }

        private bool CanSend()
        {
            if (m_Animal == null || m_NetworkCharacter == null) return false;

            // Player-owned actors are driven by their owner; the server echoes the validated
            // snapshot. Server-authoritative NPCs are always simulated by the server, so the
            // authority check is enough and does not depend on a transport-assigned role.
            if (m_NetworkCharacter.IsPlayerOwnedActor)
            {
                return m_NetworkCharacter.Role == NetworkCharacter.NetworkRole.Server ||
                       (m_NetworkCharacter.Role == NetworkCharacter.NetworkRole.LocalClient &&
                        m_NetworkCharacter.IsOwnerInstance);
            }

            return m_NetworkCharacter.HasSimulationAuthority;
        }

        private void TrySendSnapshot()
        {
            if (Time.unscaledTime < m_NextSampleTime) return;
            m_NextSampleTime = Time.unscaledTime + Mathf.Max(0f, m_SendInterval);

            int stateId = m_Animal.ActiveStateID != null ? m_Animal.ActiveStateID.ID : 0;
            int modeId = m_Animal.ActiveModeID;
            int abilityIndex = m_Animal.ModeAbility;
            int stanceId = m_Animal.Stance != null ? m_Animal.Stance.ID : 0;
            bool grounded = m_Animal.Grounded;

            if (m_HasLastSent &&
                stateId == m_LastStateId &&
                modeId == m_LastModeId &&
                abilityIndex == m_LastAbilityIndex &&
                stanceId == m_LastStanceId &&
                grounded == m_LastGrounded &&
                (m_HeartbeatInterval <= 0f ||
                 Time.unscaledTime < m_NextHeartbeatTime))
            {
                return;
            }

            m_HasLastSent = true;
            m_NextHeartbeatTime = Time.unscaledTime + Mathf.Max(0f, m_HeartbeatInterval);
            m_LastStateId = stateId;
            m_LastModeId = modeId;
            m_LastAbilityIndex = abilityIndex;
            m_LastStanceId = stanceId;
            m_LastGrounded = grounded;

            var message = new NetworkMalbersStateMessage
            {
                CharacterNetworkId = m_NetworkCharacter.NetworkId,
                Flags = NetworkMalbersStateFlags.State |
                        NetworkMalbersStateFlags.Mode |
                        NetworkMalbersStateFlags.Stance |
                        NetworkMalbersStateFlags.Grounded,
                StateId = stateId,
                ModeId = modeId,
                AbilityIndex = abilityIndex,
                StanceId = stanceId,
                StateTime = m_Animal.StateTime,
                Grounded = grounded
            };

            Log(
                $"send character={message.CharacterNetworkId} state={stateId} " +
                $"mode={modeId}/{abilityIndex} stance={stanceId} grounded={grounded}");
            if (m_Manager == null)
            {
                Log("send dropped: manager is unavailable");
                return;
            }

            m_Manager.SendFromAuthority(message);
        }

        private void OnStateReceived(NetworkMalbersStateMessage message)
        {
            if (m_NetworkCharacter == null) return;

            if (message.CharacterNetworkId != m_NetworkCharacter.NetworkId)
            {
                // A per-peer network id mismatch silently blocks every snapshot, so report it
                // when diagnostics are on instead of dropping the message invisibly.
                Log(
                    $"ignored snapshot character={message.CharacterNetworkId} " +
                    $"local={m_NetworkCharacter.NetworkId} state={message.StateId}");
                return;
            }

            // Only proxies apply snapshots. A peer that already simulates the actor keeps its
            // own simulation, which also covers transports that never assign a proxy role.
            if (m_NetworkCharacter.HasSimulationAuthority)
            {
                Log(
                    $"ignored snapshot: local simulation authority " +
                    $"character={message.CharacterNetworkId} state={message.StateId}");
                return;
            }

            m_Pending = message;
            m_HasPending = true;
        }

        private void ApplySnapshot(NetworkMalbersStateMessage message)
        {
            if (m_Animal == null) return;

            m_LastApplied = message;
            m_HasAppliedSnapshot = true;
            m_NextReconcileTime = Time.unscaledTime + 0.5f;
            AppliedSnapshotCount++;

            if (message.Has(NetworkMalbersStateFlags.State) && message.StateId > 0)
            {
                int current = m_Animal.ActiveStateID != null ? m_Animal.ActiveStateID.ID : 0;
                if (current != message.StateId)
                {
                    Log($"apply state {current}->{message.StateId} character={message.CharacterNetworkId}");
                    m_Animal.State_Force(message.StateId);
                }
            }

            if (message.Has(NetworkMalbersStateFlags.Mode))
            {
                if (message.ModeId > 0)
                {
                    if (m_Animal.ActiveModeID != message.ModeId ||
                        m_Animal.ModeAbility != message.AbilityIndex)
                    {
                        Log(
                            $"apply mode {m_Animal.ActiveModeID}/{m_Animal.ModeAbility}->" +
                            $"{message.ModeId}/{message.AbilityIndex} " +
                            $"character={message.CharacterNetworkId}");
                        m_Animal.Mode_ForceActivate(message.ModeId, message.AbilityIndex);
                    }
                }
                else if (m_Animal.IsPlayingMode)
                {
                    Log($"apply mode stop character={message.CharacterNetworkId}");
                    m_Animal.Mode_Stop();
                }
            }

            if (message.Has(NetworkMalbersStateFlags.Stance))
            {
                int currentStance = m_Animal.Stance != null ? m_Animal.Stance.ID : 0;
                if (currentStance != message.StanceId)
                {
                    Log(
                        $"apply stance {currentStance}->{message.StanceId} " +
                        $"character={message.CharacterNetworkId}");
                    m_Animal.Stance_Set(message.StanceId);
                }
            }

            bool motorOwnsGrounded = m_MotorBridge != null && m_MotorBridge.SynchronizeGrounded;
            if (message.Has(NetworkMalbersStateFlags.Grounded) &&
                !motorOwnsGrounded &&
                m_ApplyGroundedWithoutMotorBridge)
            {
                m_Animal.Grounded = message.Grounded;
            }
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkMalbersStateBridge:{name}] {message}");
        }
    }
}
#endif
