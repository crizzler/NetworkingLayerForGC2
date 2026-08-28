using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Keeps locomotion physics inert while GC2's default ragdoll activates its dynamic bone
    /// bodies. The guard belongs to the Character rather than a driver, so its captured state
    /// survives authority and driver-family role changes during one ragdoll epoch.
    /// </summary>
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class NetworkRagdollPhysicsGuard : MonoBehaviour
    {
        private enum RagdollPhase : byte
        {
            None,
            Dynamic,
            Recovering
        }

        private sealed class CharacterControllerState
        {
            public CharacterController Controller;
            public bool Active;
            public bool Captured;
            public bool WasEnabled;
            public bool WasDetectingCollisions;
        }

        private sealed class NavMeshAgentState
        {
            public NavMeshAgent Agent;
            public bool Active;
            public bool Captured;
            public bool WasEnabled;
            public bool HadStoppedState;
            public bool WasStopped;
        }

        private sealed class ColliderState
        {
            public Collider Collider;
            public bool Active;
            public bool Captured;
            public bool WasEnabled;
        }

        private readonly List<CharacterControllerState> m_Controllers =
            new List<CharacterControllerState>(1);
        private readonly List<NavMeshAgentState> m_Agents =
            new List<NavMeshAgentState>(1);
        private readonly List<ColliderState> m_Colliders =
            new List<ColliderState>(1);

        private Character m_Character;
        private RagdollPhase m_Phase;

        /// <summary>
        /// Registers a GC2/controller-driver capsule. Registration is idempotent and, when it
        /// occurs during physical ragdoll, immediately adopts the existing epoch without
        /// recapturing a component which is already registered.
        /// </summary>
        public static void Register(Character character, CharacterController controller)
        {
            if (character == null || controller == null) return;

            NetworkRagdollPhysicsGuard guard = Require(character);
            guard?.RegisterController(controller);
        }

        /// <summary>
        /// Adopts a controller which becomes the active locomotion backend during an existing
        /// ragdoll epoch. Unlike ordinary registration, this supplies the role's intended
        /// post-ragdoll state without briefly enabling collision beside the dynamic bones.
        /// </summary>
        public static void Adopt(
            Character character,
            CharacterController controller,
            bool intendedEnabled,
            bool intendedDetectCollisions)
        {
            if (character == null || controller == null) return;

            NetworkRagdollPhysicsGuard guard = Require(character);
            guard?.AdoptController(
                controller,
                intendedEnabled,
                intendedDetectCollisions);
        }

        /// <summary>
        /// Registers a server-authoritative NavMesh agent. The guard preserves its enabled and
        /// stopped state and stops it safely before disabling it for ragdoll.
        /// </summary>
        public static void Register(Character character, NavMeshAgent agent)
        {
            if (character == null || agent == null) return;

            NetworkRagdollPhysicsGuard guard = Require(character);
            guard?.RegisterAgent(agent);
        }

        /// <summary>
        /// Registers a locomotion collider such as the NavMesh driver's capsule. Passing a
        /// CharacterController still uses the controller-specific collision-state path.
        /// </summary>
        public static void Register(Character character, Collider collider)
        {
            if (character == null || collider == null) return;

            if (collider is CharacterController controller)
            {
                Register(character, controller);
                return;
            }

            NetworkRagdollPhysicsGuard guard = Require(character);
            guard?.RegisterCollider(collider);
        }

        /// <summary>
        /// Stops tracking a role-specific physics component without restoring it. Drivers use
        /// this while relinquishing a role during ragdoll so an obsolete backend is not revived
        /// when recovery starts.
        /// </summary>
        public static void Unregister(Character character, Component component)
        {
            if (character == null || component == null) return;

            NetworkRagdollPhysicsGuard guard =
                character.GetComponent<NetworkRagdollPhysicsGuard>();
            guard?.UnregisterComponent(component);
        }

        private static NetworkRagdollPhysicsGuard Require(Character character)
        {
            NetworkRagdollPhysicsGuard guard =
                character.GetComponent<NetworkRagdollPhysicsGuard>();

            if (guard == null)
            {
                guard = character.gameObject.AddComponent<NetworkRagdollPhysicsGuard>();
                guard.hideFlags |= HideFlags.HideInInspector;
            }

            guard.Initialize(character);
            return guard;
        }

        private void Awake()
        {
            Initialize(GetComponent<Character>());
        }

        private void OnDestroy()
        {
            Unsubscribe();
            m_Controllers.Clear();
            m_Agents.Clear();
            m_Colliders.Clear();
            m_Character = null;
            m_Phase = RagdollPhase.None;
        }

        private void Initialize(Character character)
        {
            if (character == null) return;

            if (!ReferenceEquals(m_Character, character))
            {
                Unsubscribe();
                m_Character = character;
                Subscribe();
            }

            bool isRagdoll = m_Character.Ragdoll != null &&
                              m_Character.Ragdoll.IsRagdoll;
            if (isRagdoll && m_Phase == RagdollPhase.None)
            {
                m_Phase = RagdollPhase.Dynamic;
            }
            else if (!isRagdoll && m_Phase == RagdollPhase.Recovering)
            {
                // Do not infer completion from IsRagdoll while the phase is Dynamic. GC2 raises
                // EventBeforeStartRagdoll before it flips IsRagdoll, and an authority/driver
                // change can register another backend during that asynchronous start window.
                FinishRagdoll();
            }
        }

        private void Subscribe()
        {
            if (m_Character?.Ragdoll == null) return;

            m_Character.Ragdoll.EventBeforeStartRagdoll -= BeginRagdoll;
            m_Character.Ragdoll.EventBeforeStartRagdoll += BeginRagdoll;
            m_Character.Ragdoll.EventAfterStartRecover -= BeginRecovery;
            m_Character.Ragdoll.EventAfterStartRecover += BeginRecovery;
            m_Character.Ragdoll.EventAfterFinishRecover -= FinishRagdoll;
            m_Character.Ragdoll.EventAfterFinishRecover += FinishRagdoll;
        }

        private void Unsubscribe()
        {
            if (m_Character?.Ragdoll == null) return;

            m_Character.Ragdoll.EventBeforeStartRagdoll -= BeginRagdoll;
            m_Character.Ragdoll.EventAfterStartRecover -= BeginRecovery;
            m_Character.Ragdoll.EventAfterFinishRecover -= FinishRagdoll;
        }

        private void RegisterController(CharacterController controller)
        {
            CharacterControllerState state = null;
            for (int i = 0; i < m_Controllers.Count; ++i)
            {
                if (!ReferenceEquals(m_Controllers[i].Controller, controller)) continue;
                state = m_Controllers[i];
                break;
            }

            if (state == null)
            {
                state = new CharacterControllerState
                {
                    Controller = controller,
                    Active = true
                };
                m_Controllers.Add(state);
            }
            else
            {
                state.Active = true;
            }

            if (m_Phase == RagdollPhase.Dynamic)
            {
                CaptureAndSuspend(state);
            }
        }

        private void AdoptController(
            CharacterController controller,
            bool intendedEnabled,
            bool intendedDetectCollisions)
        {
            CharacterControllerState state = null;
            for (int i = 0; i < m_Controllers.Count; ++i)
            {
                if (!ReferenceEquals(m_Controllers[i].Controller, controller)) continue;
                state = m_Controllers[i];
                break;
            }

            if (state == null)
            {
                state = new CharacterControllerState { Controller = controller };
                m_Controllers.Add(state);
            }

            bool preserveActiveEpochBaseline = state.Active && state.Captured;
            state.Active = true;

            switch (m_Phase)
            {
                case RagdollPhase.Dynamic:
                    if (!preserveActiveEpochBaseline)
                    {
                        state.WasEnabled = intendedEnabled;
                        state.WasDetectingCollisions = intendedDetectCollisions;
                        state.Captured = true;
                    }

                    Suspend(state);
                    break;

                case RagdollPhase.Recovering:
                    // GC2 has already disabled its dynamic bone colliders. Applying the intended
                    // role state now is physically safe, while the driver still suppresses all
                    // locomotion until EventAfterFinishRecover.
                    controller.detectCollisions = intendedDetectCollisions;
                    controller.enabled = intendedEnabled;
                    state.Captured = false;
                    break;

                default:
                    controller.detectCollisions = intendedDetectCollisions;
                    controller.enabled = intendedEnabled;
                    state.Captured = false;
                    break;
            }
        }

        private void RegisterAgent(NavMeshAgent agent)
        {
            NavMeshAgentState state = null;
            for (int i = 0; i < m_Agents.Count; ++i)
            {
                if (!ReferenceEquals(m_Agents[i].Agent, agent)) continue;
                state = m_Agents[i];
                break;
            }

            if (state == null)
            {
                state = new NavMeshAgentState
                {
                    Agent = agent,
                    Active = true
                };
                m_Agents.Add(state);
            }
            else
            {
                state.Active = true;
            }

            if (m_Phase == RagdollPhase.Dynamic)
            {
                CaptureAndSuspend(state);
            }
        }

        private void RegisterCollider(Collider collider)
        {
            ColliderState state = null;
            for (int i = 0; i < m_Colliders.Count; ++i)
            {
                if (!ReferenceEquals(m_Colliders[i].Collider, collider)) continue;
                state = m_Colliders[i];
                break;
            }

            if (state == null)
            {
                state = new ColliderState
                {
                    Collider = collider,
                    Active = true
                };
                m_Colliders.Add(state);
            }
            else
            {
                state.Active = true;
            }

            if (m_Phase == RagdollPhase.Dynamic)
            {
                CaptureAndSuspend(state);
            }
        }

        private void UnregisterComponent(Component component)
        {
            for (int i = m_Controllers.Count - 1; i >= 0; --i)
            {
                if (ReferenceEquals(m_Controllers[i].Controller, component))
                {
                    if (m_Phase == RagdollPhase.None) m_Controllers.RemoveAt(i);
                    else m_Controllers[i].Active = false;
                }
            }

            for (int i = m_Agents.Count - 1; i >= 0; --i)
            {
                if (ReferenceEquals(m_Agents[i].Agent, component))
                {
                    if (m_Phase == RagdollPhase.None) m_Agents.RemoveAt(i);
                    else m_Agents[i].Active = false;
                }
            }

            for (int i = m_Colliders.Count - 1; i >= 0; --i)
            {
                if (ReferenceEquals(m_Colliders[i].Collider, component))
                {
                    if (m_Phase == RagdollPhase.None) m_Colliders.RemoveAt(i);
                    else m_Colliders[i].Active = false;
                }
            }
        }

        private void BeginRagdoll()
        {
            // GC2 retains IsRagdoll throughout its get-up gesture. A duplicate callback or a
            // driver registration in that interval must not begin a second physics epoch.
            if (m_Phase == RagdollPhase.Recovering) return;
            m_Phase = RagdollPhase.Dynamic;

            // Stop active simulation before disabling collision/query components.
            for (int i = 0; i < m_Agents.Count; ++i)
            {
                if (m_Agents[i].Active) CaptureAndSuspend(m_Agents[i]);
            }

            for (int i = 0; i < m_Controllers.Count; ++i)
            {
                if (m_Controllers[i].Active) CaptureAndSuspend(m_Controllers[i]);
            }

            for (int i = 0; i < m_Colliders.Count; ++i)
            {
                if (m_Colliders[i].Active) CaptureAndSuspend(m_Colliders[i]);
            }
        }

        private void BeginRecovery()
        {
            if (m_Phase != RagdollPhase.Dynamic) return;

            // Change phase before touching components so re-entrant events cannot restore twice.
            m_Phase = RagdollPhase.Recovering;
            RestoreCapturedState();
        }

        private void FinishRagdoll()
        {
            if (m_Phase == RagdollPhase.None) return;

            // Fallback for a guard created after EventAfterStartRecover or an interrupted role
            // transition. Normal recovery already restored in BeginRecovery.
            if (m_Phase == RagdollPhase.Dynamic)
            {
                m_Phase = RagdollPhase.Recovering;
                RestoreCapturedState();
            }

            m_Phase = RagdollPhase.None;
            PruneInactiveComponents();
        }

        private static void CaptureAndSuspend(CharacterControllerState state)
        {
            CharacterController controller = state.Controller;
            if (controller == null) return;

            if (!state.Captured)
            {
                state.WasEnabled = controller.enabled;
                state.WasDetectingCollisions = controller.detectCollisions;
                state.Captured = true;
            }

            Suspend(state);
        }

        private static void Suspend(CharacterControllerState state)
        {
            CharacterController controller = state.Controller;
            if (controller == null) return;

            if (controller.enabled) controller.enabled = false;
            if (controller.detectCollisions) controller.detectCollisions = false;
        }

        private static void CaptureAndSuspend(NavMeshAgentState state)
        {
            NavMeshAgent agent = state.Agent;
            if (agent == null) return;

            if (!state.Captured)
            {
                state.WasEnabled = agent.enabled;
                state.HadStoppedState = agent.enabled && agent.isOnNavMesh;
                state.WasStopped = state.HadStoppedState && agent.isStopped;
                state.Captured = true;
            }

            if (agent.enabled && agent.isOnNavMesh)
            {
                agent.isStopped = true;
                agent.velocity = Vector3.zero;
            }

            if (agent.enabled) agent.enabled = false;
        }

        private static void CaptureAndSuspend(ColliderState state)
        {
            Collider collider = state.Collider;
            if (collider == null) return;

            if (!state.Captured)
            {
                state.WasEnabled = collider.enabled;
                state.Captured = true;
            }

            if (collider.enabled) collider.enabled = false;
        }

        private void RestoreCapturedState()
        {
            // Restore collision before reactivating an agent which may immediately resume motion.
            for (int i = 0; i < m_Colliders.Count; ++i)
            {
                ColliderState state = m_Colliders[i];
                if (!state.Captured) continue;

                if (state.Active && state.Collider != null)
                {
                    state.Collider.enabled = state.WasEnabled;
                }
                state.Captured = false;
            }

            for (int i = 0; i < m_Controllers.Count; ++i)
            {
                CharacterControllerState state = m_Controllers[i];
                if (!state.Captured) continue;

                if (state.Active && state.Controller != null)
                {
                    state.Controller.detectCollisions = state.WasDetectingCollisions;
                    state.Controller.enabled = state.WasEnabled;
                }

                state.Captured = false;
            }

            for (int i = 0; i < m_Agents.Count; ++i)
            {
                NavMeshAgentState state = m_Agents[i];
                if (!state.Captured) continue;

                NavMeshAgent agent = state.Agent;
                if (state.Active && agent != null)
                {
                    agent.enabled = state.WasEnabled;
                    if (state.WasEnabled && state.HadStoppedState && agent.isOnNavMesh)
                    {
                        agent.isStopped = state.WasStopped;
                    }
                }

                state.Captured = false;
            }
        }

        private void PruneInactiveComponents()
        {
            for (int i = m_Controllers.Count - 1; i >= 0; --i)
            {
                if (!m_Controllers[i].Active) m_Controllers.RemoveAt(i);
            }

            for (int i = m_Agents.Count - 1; i >= 0; --i)
            {
                if (!m_Agents[i].Active) m_Agents.RemoveAt(i);
            }

            for (int i = m_Colliders.Count - 1; i >= 0; --i)
            {
                if (!m_Colliders[i].Active) m_Colliders.RemoveAt(i);
            }
        }
    }
}
