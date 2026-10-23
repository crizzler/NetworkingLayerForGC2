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

        private sealed class RigidbodyState
        {
            public Rigidbody Body;
            public bool Active;
            public bool Captured;
            public bool WasKinematic;
            public bool WasUseGravity;
            public bool WasDetectingCollisions;
        }

        private readonly List<CharacterControllerState> m_Controllers =
            new List<CharacterControllerState>(1);
        private readonly List<NavMeshAgentState> m_Agents =
            new List<NavMeshAgentState>(1);
        private readonly List<ColliderState> m_Colliders =
            new List<ColliderState>(1);
        private readonly List<RigidbodyState> m_Rigidbodies =
            new List<RigidbodyState>(1);

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
        /// Registers the root locomotion Rigidbody. The guard preserves its kinematic, gravity,
        /// and collision-query state and makes it inert while GC2's dynamic ragdoll bones are
        /// active. This keeps a Malbers/MAnimal root body from competing with the ragdoll.
        /// </summary>
        public static void Register(Character character, Rigidbody body)
        {
            if (character == null || body == null) return;

            NetworkRagdollPhysicsGuard guard = Require(character);
            guard?.RegisterRigidbody(body);
        }

        /// <summary>
        /// Adopts the root locomotion Rigidbody during an existing ragdoll epoch. Unlike
        /// ordinary registration, this supplies the role's intended post-ragdoll state instead
        /// of capturing the currently suspended body, which may already belong to a different
        /// role or a presentation baseline. An already captured baseline is preserved unless
        /// <paramref name="replaceActiveBaseline"/> is true, matching controller adoption.
        /// </summary>
        public static void Adopt(
            Character character,
            Rigidbody body,
            bool intendedKinematic,
            bool intendedUseGravity,
            bool intendedDetectCollisions,
            bool replaceActiveBaseline = false)
        {
            if (character == null || body == null) return;

            NetworkRagdollPhysicsGuard guard = Require(character);
            guard?.AdoptRigidbody(
                body,
                intendedKinematic,
                intendedUseGravity,
                intendedDetectCollisions,
                replaceActiveBaseline);
        }

        /// <summary>
        /// Replaces the post-ragdoll baseline for a registered root Rigidbody without changing
        /// its current suspended state. Role transitions use this to ensure recovery restores
        /// the newly intended owner's physics state even if the caller is disabled or destroyed
        /// before GC2 finishes the ragdoll epoch.
        /// </summary>
        public static void SetIntendedRigidbodyState(
            Character character,
            Rigidbody body,
            bool intendedKinematic,
            bool intendedUseGravity,
            bool intendedDetectCollisions)
        {
            if (character == null || body == null) return;

            NetworkRagdollPhysicsGuard guard =
                character.GetComponent<NetworkRagdollPhysicsGuard>();
            guard?.SetRigidbodyIntendedState(
                body,
                intendedKinematic,
                intendedUseGravity,
                intendedDetectCollisions);
        }

        /// <summary>
        /// Reads the post-ragdoll baseline currently held for a registered root Rigidbody. This
        /// lets a replacement bridge recover authored values instead of caching the temporarily
        /// suspended state from the live body.
        /// </summary>
        public static bool TryGetRigidbodyBaseline(
            Character character,
            Rigidbody body,
            out bool isKinematic,
            out bool useGravity,
            out bool detectCollisions)
        {
            isKinematic = false;
            useGravity = false;
            detectCollisions = false;
            if (character == null || body == null) return false;

            NetworkRagdollPhysicsGuard guard =
                character.GetComponent<NetworkRagdollPhysicsGuard>();
            return guard != null &&
                   guard.TryGetRigidbodyBaseline(
                       body,
                       out isKinematic,
                       out useGravity,
                       out detectCollisions);
        }

        /// <summary>
        /// Applies a captured baseline when an epoch ended without its normal finish callback.
        /// The bridge uses this before releasing deferred bookkeeping to a foreign driver.
        /// </summary>
        public static void ApplyPendingRigidbodyBaseline(Character character, Rigidbody body)
        {
            if (character == null || body == null) return;

            NetworkRagdollPhysicsGuard guard =
                character.GetComponent<NetworkRagdollPhysicsGuard>();
            guard?.ApplyPendingRigidbodyBaseline(body);
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

        /// <summary>
        /// Collects the GC2 ragdoll bone bodies from the Animator hierarchy. RagdollDefault
        /// temporarily detaches that hierarchy from the Character while its bodies are dynamic,
        /// so a Character-subtree query cannot observe or affect them during physical ragdoll.
        /// </summary>
        internal static void CollectRagdollRigidbodies(
            Character character,
            List<Rigidbody> destination)
        {
            if (destination == null) return;
            destination.Clear();
            if (character == null) return;

            Animator animator = character.Animim?.Animator;
            if (animator != null)
            {
                animator.GetComponentsInChildren(true, destination);
                return;
            }

            // Preserve the former fallback for custom ragdoll systems which do not expose a GC2
            // Animator but keep their bodies below the Character root.
            character.GetComponentsInChildren(true, destination);
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
            m_Rigidbodies.Clear();
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

        private void RegisterRigidbody(Rigidbody body)
        {
            RigidbodyState state = FindRigidbodyState(body);
            if (state == null)
            {
                state = new RigidbodyState
                {
                    Body = body,
                    Active = true
                };
                m_Rigidbodies.Add(state);
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

        private void AdoptRigidbody(
            Rigidbody body,
            bool intendedKinematic,
            bool intendedUseGravity,
            bool intendedDetectCollisions,
            bool replaceActiveBaseline)
        {
            RigidbodyState state = FindRigidbodyState(body);
            if (state == null)
            {
                state = new RigidbodyState { Body = body };
                m_Rigidbodies.Add(state);
            }

            bool preserveActiveEpochBaseline =
                !replaceActiveBaseline && state.Active && state.Captured;
            state.Active = true;

            if (preserveActiveEpochBaseline)
            {
                if (m_Phase == RagdollPhase.Dynamic)
                {
                    Suspend(state);
                }
                else
                {
                    ApplyIntendedState(state);
                    state.Captured = false;
                }

                return;
            }

            state.WasKinematic = intendedKinematic;
            state.WasUseGravity = intendedUseGravity;
            state.WasDetectingCollisions = intendedDetectCollisions;

            if (m_Phase == RagdollPhase.Dynamic)
            {
                state.Captured = true;
                Suspend(state);
            }
            else
            {
                ApplyIntendedState(state);
                state.Captured = false;
            }
        }

        private bool TryGetRigidbodyBaseline(
            Rigidbody body,
            out bool isKinematic,
            out bool useGravity,
            out bool detectCollisions)
        {
            isKinematic = false;
            useGravity = false;
            detectCollisions = false;

            RigidbodyState state = FindRigidbodyState(body);
            if (state == null || !state.Active || !state.Captured) return false;

            isKinematic = state.WasKinematic;
            useGravity = state.WasUseGravity;
            detectCollisions = state.WasDetectingCollisions;
            return true;
        }

        private void ApplyPendingRigidbodyBaseline(Rigidbody body)
        {
            RigidbodyState state = FindRigidbodyState(body);
            if (state == null || !state.Active || !state.Captured) return;

            ApplyIntendedState(state);
            state.Captured = false;
        }

        private void SetRigidbodyIntendedState(
            Rigidbody body,
            bool intendedKinematic,
            bool intendedUseGravity,
            bool intendedDetectCollisions)
        {
            RigidbodyState state = FindRigidbodyState(body);
            if (state == null) return;

            state.WasKinematic = intendedKinematic;
            state.WasUseGravity = intendedUseGravity;
            state.WasDetectingCollisions = intendedDetectCollisions;

            if (m_Phase == RagdollPhase.Dynamic || m_Phase == RagdollPhase.Recovering)
            {
                // FinishRagdoll applies any baseline changed after BeginRecovery restored the
                // previous one. Keeping the body suspended until then avoids a dynamic root
                // beside still-recovering bones.
                state.Captured = true;
                if (m_Phase == RagdollPhase.Dynamic) Suspend(state);
            }
        }

        private RigidbodyState FindRigidbodyState(Rigidbody body)
        {
            for (int i = 0; i < m_Rigidbodies.Count; ++i)
            {
                if (ReferenceEquals(m_Rigidbodies[i].Body, body)) return m_Rigidbodies[i];
            }

            return null;
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

            for (int i = m_Rigidbodies.Count - 1; i >= 0; --i)
            {
                if (ReferenceEquals(m_Rigidbodies[i].Body, component))
                {
                    if (m_Phase == RagdollPhase.None) m_Rigidbodies.RemoveAt(i);
                    else m_Rigidbodies[i].Active = false;
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

            for (int i = 0; i < m_Rigidbodies.Count; ++i)
            {
                if (m_Rigidbodies[i].Active) CaptureAndSuspend(m_Rigidbodies[i]);
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

            // BeginRecovery normally restores and clears Captured. A role transition during
            // recovery may install a new intended baseline afterwards, so apply any remaining
            // captured state once more before ending the epoch.
            if (m_Phase == RagdollPhase.Dynamic)
            {
                m_Phase = RagdollPhase.Recovering;
            }

            RestoreCapturedState();
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

        private static void CaptureAndSuspend(RigidbodyState state)
        {
            Rigidbody body = state.Body;
            if (body == null) return;

            if (!state.Captured)
            {
                state.WasKinematic = body.isKinematic;
                state.WasUseGravity = body.useGravity;
                state.WasDetectingCollisions = body.detectCollisions;
                state.Captured = true;
            }

            Suspend(state);
        }

        private static void Suspend(RigidbodyState state)
        {
            Rigidbody body = state.Body;
            if (body == null) return;

            if (!body.isKinematic) body.isKinematic = true;
            if (body.useGravity) body.useGravity = false;
            if (body.detectCollisions) body.detectCollisions = false;

#if UNITY_6000_0_OR_NEWER
            body.linearVelocity = Vector3.zero;
#else
            body.velocity = Vector3.zero;
#endif
            body.angularVelocity = Vector3.zero;
        }

        private static void ApplyIntendedState(RigidbodyState state)
        {
            Rigidbody body = state.Body;
            if (body == null) return;

            body.isKinematic = state.WasKinematic;
            body.useGravity = state.WasUseGravity;
            body.detectCollisions = state.WasDetectingCollisions;
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

            for (int i = 0; i < m_Rigidbodies.Count; ++i)
            {
                RigidbodyState state = m_Rigidbodies[i];
                if (!state.Captured) continue;

                Rigidbody body = state.Body;
                if (state.Active && body != null)
                {
                    ApplyIntendedState(state);
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

            for (int i = m_Rigidbodies.Count - 1; i >= 0; --i)
            {
                if (!m_Rigidbodies[i].Active) m_Rigidbodies.RemoveAt(i);
            }
        }
    }
}
