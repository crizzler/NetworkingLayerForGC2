using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Transport-neutral proof surface for the Core ragdoll examples. The buttons execute
    /// authored GC2 Actions, while the diagnostics verify that the locomotion collision backend
    /// yields to the dynamic skeleton and is restored for GC2's get-up phase.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Demo/Network Ragdoll UI")]
    [DisallowMultipleComponent]
    public sealed class NetworkRagdollDemoUI : MonoBehaviour
    {
        private const int AUTOMATED_CYCLE_COUNT = 3;
        private const int MAX_VISIBLE_ACTORS = 8;
        private const float REGISTRY_REFRESH_INTERVAL = 0.2f;
        private const float START_EVENT_GRACE_SECONDS = 0.25f;

        private enum ObservedRagdollPhase : byte
        {
            Standing,
            Dynamic,
            Recovering
        }

        private readonly struct ControllerBaseline
        {
            public readonly CharacterController Component;
            public readonly bool Enabled;
            public readonly bool DetectCollisions;

            public ControllerBaseline(CharacterController component)
            {
                Component = component;
                Enabled = component != null && component.enabled;
                DetectCollisions = component != null && component.detectCollisions;
            }
        }

        private readonly struct AgentBaseline
        {
            public readonly NavMeshAgent Component;
            public readonly bool Enabled;
            public readonly bool HadStoppedState;
            public readonly bool IsStopped;

            public AgentBaseline(NavMeshAgent component)
            {
                Component = component;
                Enabled = component != null && component.enabled;
                HadStoppedState = Enabled && component.isOnNavMesh;
                IsStopped = HadStoppedState && component.isStopped;
            }
        }

        private readonly struct ColliderBaseline
        {
            public readonly Collider Component;
            public readonly bool Enabled;

            public ColliderBaseline(Collider component)
            {
                Component = component;
                Enabled = component != null && component.enabled;
            }
        }

        /// <summary>
        /// Captures component identity as well as the exact state restored by the shared guard.
        /// This intentionally allocates only when a proof cycle is armed, never in the registry
        /// or per-frame diagnostic path.
        /// </summary>
        private sealed class RootPhysicsBaseline
        {
            private readonly ControllerBaseline[] m_Controllers;
            private readonly AgentBaseline[] m_Agents;
            private readonly ColliderBaseline[] m_Colliders;

            public bool HasRootLocomotionPhysics =>
                m_Controllers.Length > 0 || m_Agents.Length > 0 || m_Colliders.Length > 0;

            public bool HasActiveRootCollision { get; }

            private RootPhysicsBaseline(
                ControllerBaseline[] controllers,
                AgentBaseline[] agents,
                ColliderBaseline[] colliders)
            {
                m_Controllers = controllers;
                m_Agents = agents;
                m_Colliders = colliders;

                for (int i = 0; i < m_Controllers.Length; ++i)
                {
                    if (!m_Controllers[i].Enabled ||
                        !m_Controllers[i].DetectCollisions) continue;
                    HasActiveRootCollision = true;
                    return;
                }

                for (int i = 0; i < m_Agents.Length; ++i)
                {
                    if (!m_Agents[i].Enabled) continue;
                    HasActiveRootCollision = true;
                    return;
                }

                for (int i = 0; i < m_Colliders.Length; ++i)
                {
                    if (!m_Colliders[i].Enabled) continue;
                    HasActiveRootCollision = true;
                    return;
                }
            }

            public static RootPhysicsBaseline Capture(Character character)
            {
                if (character == null) return null;

                CharacterController[] currentControllers =
                    character.GetComponents<CharacterController>();
                var controllers = new ControllerBaseline[currentControllers.Length];
                for (int i = 0; i < currentControllers.Length; ++i)
                {
                    controllers[i] = new ControllerBaseline(currentControllers[i]);
                }

                NavMeshAgent[] currentAgents = character.GetComponents<NavMeshAgent>();
                var agents = new AgentBaseline[currentAgents.Length];
                for (int i = 0; i < currentAgents.Length; ++i)
                {
                    agents[i] = new AgentBaseline(currentAgents[i]);
                }

                Collider[] currentColliders = character.GetComponents<Collider>();
                int colliderCount = 0;
                for (int i = 0; i < currentColliders.Length; ++i)
                {
                    if (!(currentColliders[i] is CharacterController)) colliderCount++;
                }

                var colliders = new ColliderBaseline[colliderCount];
                int colliderIndex = 0;
                for (int i = 0; i < currentColliders.Length; ++i)
                {
                    Collider collider = currentColliders[i];
                    if (collider is CharacterController) continue;
                    colliders[colliderIndex++] = new ColliderBaseline(collider);
                }

                return new RootPhysicsBaseline(controllers, agents, colliders);
            }

            public bool Matches(Character character)
            {
                if (character == null) return false;

                CharacterController[] currentControllers =
                    character.GetComponents<CharacterController>();
                NavMeshAgent[] currentAgents = character.GetComponents<NavMeshAgent>();
                Collider[] currentColliders = character.GetComponents<Collider>();
                return Matches(currentControllers, currentAgents, currentColliders);
            }

            public bool Matches(
                IReadOnlyList<CharacterController> currentControllers,
                IReadOnlyList<NavMeshAgent> currentAgents,
                IReadOnlyList<Collider> currentColliders)
            {
                if (currentControllers == null ||
                    currentAgents == null ||
                    currentColliders == null)
                {
                    return false;
                }

                if (currentControllers.Count != m_Controllers.Length) return false;
                for (int i = 0; i < m_Controllers.Length; ++i)
                {
                    ControllerBaseline baseline = m_Controllers[i];
                    CharacterController component = baseline.Component;
                    if (component == null ||
                        !ContainsReference(currentControllers, component) ||
                        component.enabled != baseline.Enabled ||
                        component.detectCollisions != baseline.DetectCollisions)
                    {
                        return false;
                    }
                }

                if (currentAgents.Count != m_Agents.Length) return false;
                for (int i = 0; i < m_Agents.Length; ++i)
                {
                    AgentBaseline baseline = m_Agents[i];
                    NavMeshAgent component = baseline.Component;
                    if (component == null ||
                        !ContainsReference(currentAgents, component) ||
                        component.enabled != baseline.Enabled)
                    {
                        return false;
                    }

                    if (baseline.HadStoppedState &&
                        (!component.isOnNavMesh || component.isStopped != baseline.IsStopped))
                    {
                        return false;
                    }
                }

                int currentColliderCount = 0;
                for (int i = 0; i < currentColliders.Count; ++i)
                {
                    if (!(currentColliders[i] is CharacterController)) currentColliderCount++;
                }
                if (currentColliderCount != m_Colliders.Length) return false;

                for (int i = 0; i < m_Colliders.Length; ++i)
                {
                    ColliderBaseline baseline = m_Colliders[i];
                    Collider component = baseline.Component;
                    if (component == null ||
                        !ContainsColliderReference(currentColliders, component) ||
                        component.enabled != baseline.Enabled)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool ContainsReference<T>(
                IReadOnlyList<T> components,
                T expected)
                where T : Component
            {
                for (int i = 0; i < components.Count; ++i)
                {
                    if (ReferenceEquals(components[i], expected)) return true;
                }
                return false;
            }

            private static bool ContainsColliderReference(
                IReadOnlyList<Collider> components,
                Collider expected)
            {
                for (int i = 0; i < components.Count; ++i)
                {
                    Collider component = components[i];
                    if (component is CharacterController) continue;
                    if (ReferenceEquals(component, expected)) return true;
                }
                return false;
            }
        }

        private sealed class ActorObservation : IDisposable
        {
            private readonly Character m_Character;
            private float m_PhaseChangedAt;

            public NetworkCharacter NetworkCharacter { get; }
            public ObservedRagdollPhase Phase { get; private set; }
            public int DynamicEpoch { get; private set; }
            public int RecoveringEpoch { get; private set; }
            public int CompletedEpoch { get; private set; }
            public RootPhysicsBaseline Baseline { get; private set; }

            public ActorObservation(NetworkCharacter networkCharacter)
            {
                NetworkCharacter = networkCharacter;
                m_Character = networkCharacter != null
                    ? networkCharacter.Character
                    : null;

                if (m_Character?.Ragdoll != null)
                {
                    m_Character.Ragdoll.EventBeforeStartRagdoll += OnBeforeStartRagdoll;
                    m_Character.Ragdoll.EventAfterStartRagdoll += OnAfterStartRagdoll;
                    m_Character.Ragdoll.EventBeforeStartRecover += OnBeforeStartRecover;
                    m_Character.Ragdoll.EventAfterStartRecover += OnAfterStartRecover;
                    m_Character.Ragdoll.EventAfterFinishRecover += OnAfterFinishRecover;
                }

                Phase = InferPhase();
                m_PhaseChangedAt = Time.realtimeSinceStartup;
                if (Phase == ObservedRagdollPhase.Standing)
                {
                    CaptureRootPhysicsBaseline();
                }
            }

            public void CaptureRootPhysicsBaseline()
            {
                Baseline = RootPhysicsBaseline.Capture(m_Character);
            }

            public void Dispose()
            {
                if (m_Character?.Ragdoll == null) return;

                m_Character.Ragdoll.EventBeforeStartRagdoll -= OnBeforeStartRagdoll;
                m_Character.Ragdoll.EventAfterStartRagdoll -= OnAfterStartRagdoll;
                m_Character.Ragdoll.EventBeforeStartRecover -= OnBeforeStartRecover;
                m_Character.Ragdoll.EventAfterStartRecover -= OnAfterStartRecover;
                m_Character.Ragdoll.EventAfterFinishRecover -= OnAfterFinishRecover;
            }

            public void Reconcile()
            {
                if (m_Character?.Ragdoll == null || !m_Character.Ragdoll.IsRagdoll)
                {
                    SetPhase(ObservedRagdollPhase.Standing);
                    return;
                }

                Animator animator = m_Character.Animim?.Animator;
                if (animator == null || !animator.enabled)
                {
                    SetPhase(ObservedRagdollPhase.Dynamic);
                    return;
                }

                // EventBeforeStartRagdoll runs immediately before RagdollDefault disables its
                // Animator. Preserve that event-derived Dynamic phase for the remainder of this
                // short synchronous hand-off instead of mislabeling it as recovery.
                if (Phase == ObservedRagdollPhase.Dynamic &&
                    Time.realtimeSinceStartup - m_PhaseChangedAt <=
                    START_EVENT_GRACE_SECONDS)
                {
                    return;
                }

                // RagdollDefault re-enables the Animator before EventAfterStartRecover and keeps
                // IsRagdoll true for the complete get-up gesture. This is intentionally distinct
                // from the physical Dynamic phase because root collision is safe to restore here.
                SetPhase(ObservedRagdollPhase.Recovering);
            }

            private ObservedRagdollPhase InferPhase()
            {
                if (m_Character?.Ragdoll == null || !m_Character.Ragdoll.IsRagdoll)
                {
                    return ObservedRagdollPhase.Standing;
                }

                Animator animator = m_Character.Animim?.Animator;
                return animator != null && animator.enabled
                    ? ObservedRagdollPhase.Recovering
                    : ObservedRagdollPhase.Dynamic;
            }

            private void OnBeforeStartRagdoll()
            {
                if (Phase != ObservedRagdollPhase.Dynamic) DynamicEpoch++;
                SetPhase(ObservedRagdollPhase.Dynamic, true);
            }

            private void OnAfterStartRagdoll()
            {
                SetPhase(ObservedRagdollPhase.Dynamic);
            }

            private void OnBeforeStartRecover()
            {
                // Bone bodies and the root guard are still in their Dynamic configuration until
                // GC2 finishes StopRagdoll and raises EventAfterStartRecover.
                SetPhase(ObservedRagdollPhase.Dynamic);
            }

            private void OnAfterStartRecover()
            {
                if (Phase != ObservedRagdollPhase.Recovering) RecoveringEpoch++;
                SetPhase(ObservedRagdollPhase.Recovering, true);
            }

            private void OnAfterFinishRecover()
            {
                CompletedEpoch++;
                SetPhase(ObservedRagdollPhase.Standing, true);
            }

            private void SetPhase(
                ObservedRagdollPhase phase,
                bool refreshTimestamp = false)
            {
                if (Phase == phase && !refreshTimestamp) return;
                Phase = phase;
                m_PhaseChangedAt = Time.realtimeSinceStartup;
            }
        }

        private readonly struct ActorDiagnostics
        {
            public readonly ObservedRagdollPhase Phase;
            public readonly bool IsLocal;
            public readonly bool HasDefaultRagdoll;
            public readonly bool HasGuard;
            public readonly bool AnimatorEnabled;
            public readonly int ControllerCount;
            public readonly int EnabledControllers;
            public readonly int DetectingControllers;
            public readonly int AgentCount;
            public readonly int EnabledAgents;
            public readonly int RootColliderCount;
            public readonly int EnabledRootColliders;
            public readonly int RigidbodyCount;
            public readonly int DynamicRigidbodyCount;
            public readonly bool HasExactRootPhysicsBaseline;
            public readonly bool ExactRootPhysicsBaselineRestored;

            public ActorDiagnostics(
                ObservedRagdollPhase phase,
                bool isLocal,
                bool hasDefaultRagdoll,
                bool hasGuard,
                bool animatorEnabled,
                int controllerCount,
                int enabledControllers,
                int detectingControllers,
                int agentCount,
                int enabledAgents,
                int rootColliderCount,
                int enabledRootColliders,
                int rigidbodyCount,
                int dynamicRigidbodyCount,
                bool hasExactRootPhysicsBaseline,
                bool exactRootPhysicsBaselineRestored)
            {
                Phase = phase;
                IsLocal = isLocal;
                HasDefaultRagdoll = hasDefaultRagdoll;
                HasGuard = hasGuard;
                AnimatorEnabled = animatorEnabled;
                ControllerCount = controllerCount;
                EnabledControllers = enabledControllers;
                DetectingControllers = detectingControllers;
                AgentCount = agentCount;
                EnabledAgents = enabledAgents;
                RootColliderCount = rootColliderCount;
                EnabledRootColliders = enabledRootColliders;
                RigidbodyCount = rigidbodyCount;
                DynamicRigidbodyCount = dynamicRigidbodyCount;
                HasExactRootPhysicsBaseline = hasExactRootPhysicsBaseline;
                ExactRootPhysicsBaselineRestored = exactRootPhysicsBaselineRestored;
            }

            public bool HasRootLocomotionPhysics =>
                ControllerCount > 0 || AgentCount > 0 || RootColliderCount > 0;

            public bool RootPhysicsSuspended =>
                EnabledControllers == 0 &&
                DetectingControllers == 0 &&
                EnabledAgents == 0 &&
                EnabledRootColliders == 0;

            public bool RootPhysicsRestored =>
                (EnabledControllers > 0 && DetectingControllers > 0) ||
                EnabledAgents > 0 ||
                EnabledRootColliders > 0;

            public bool IsExpectedState => Phase switch
            {
                ObservedRagdollPhase.Dynamic =>
                    HasDefaultRagdoll &&
                    HasGuard &&
                    HasRootLocomotionPhysics &&
                    RootPhysicsSuspended &&
                    !AnimatorEnabled &&
                    DynamicRigidbodyCount > 0,
                ObservedRagdollPhase.Recovering =>
                    HasDefaultRagdoll &&
                    HasGuard &&
                    RootPhysicsRestored &&
                    HasExactRootPhysicsBaseline &&
                    ExactRootPhysicsBaselineRestored &&
                    AnimatorEnabled &&
                    DynamicRigidbodyCount == 0,
                _ =>
                    HasDefaultRagdoll &&
                    HasGuard &&
                    RootPhysicsRestored &&
                    HasExactRootPhysicsBaseline &&
                    ExactRootPhysicsBaselineRestored &&
                    AnimatorEnabled &&
                    DynamicRigidbodyCount == 0
            };

            public bool IsExpectedStateWithoutExactBaseline => Phase switch
            {
                ObservedRagdollPhase.Dynamic => IsExpectedState,
                ObservedRagdollPhase.Recovering =>
                    HasDefaultRagdoll &&
                    HasGuard &&
                    RootPhysicsRestored &&
                    AnimatorEnabled &&
                    DynamicRigidbodyCount == 0,
                _ =>
                    HasDefaultRagdoll &&
                    HasGuard &&
                    RootPhysicsRestored &&
                    AnimatorEnabled &&
                    DynamicRigidbodyCount == 0
            };
        }

        [Header("Authored GC2 Actions")]
        [Tooltip("Actions list containing Network/Core/Ragdoll/Start Ragdoll for the Local Network Player.")]
        [SerializeField] private Actions m_StartActions;
        [Tooltip("Actions list containing Network/Core/Ragdoll/Recover Ragdoll for the Local Network Player.")]
        [SerializeField] private Actions m_RecoverActions;

        [Header("Proof Overlay")]
        [SerializeField] private bool m_ShowOverlay = true;
        [SerializeField] private string m_Title = "GC2 Network Ragdoll Proof";
        [SerializeField] private Vector2 m_OverlayPosition = new Vector2(16f, 294f);
        [Min(420f)]
        [SerializeField] private float m_OverlayWidth = 620f;

        [Header("Automated Three-Cycle Validation")]
        [Min(1f)]
        [SerializeField] private float m_RequestTimeoutSeconds = 8f;
        [Min(1f)]
        [SerializeField] private float m_StateTimeoutSeconds = 10f;
        [Min(0.1f)]
        [SerializeField] private float m_DynamicHoldSeconds = 0.75f;

        private readonly List<NetworkCharacter> m_RegisteredCharacters =
            new List<NetworkCharacter>(8);
        private readonly List<NetworkCharacter> m_RemovedCharacters =
            new List<NetworkCharacter>(4);
        private readonly Dictionary<NetworkCharacter, ActorObservation> m_Observations =
            new Dictionary<NetworkCharacter, ActorObservation>(8);
        private readonly List<CharacterController> m_ControllerScratch =
            new List<CharacterController>(2);
        private readonly List<NavMeshAgent> m_AgentScratch =
            new List<NavMeshAgent>(2);
        private readonly List<Collider> m_ColliderScratch = new List<Collider>(4);
        private readonly List<Rigidbody> m_RigidbodyScratch = new List<Rigidbody>(24);

        private NetworkTransportBridge m_TransportBridge;
        private float m_NextRegistryRefresh;
        private int m_AsyncGeneration;
        private bool m_Busy;
        private string m_Status = "Start or join a session to begin.";
        private uint m_LastKnownLocalCharacterId;

        private NetworkCoreVisualScriptingOperation m_ExpectedOperation;
        private uint m_ExpectedCharacterId;
        private NetworkCoreController m_ExpectedRequestSource;
        private bool m_ExpectedRequestIdentityCaptured;
        private ushort m_ExpectedRequestId;
        private uint m_ExpectedCorrelationId;
        private bool m_EarlyResponseAvailable;
        private NetworkCoreVisualScriptingResult m_EarlyResponse;
        private bool m_ResponseReceived;
        private NetworkCoreVisualScriptingResult m_Response;

        public Actions StartRagdollActions => m_StartActions;
        public Actions RecoverRagdollActions => m_RecoverActions;
        public bool IsBusy => m_Busy;
        public bool HasAutomationResult { get; private set; }
        public bool LastAutomationPassed { get; private set; }
        public int CompletedAutomationCycles { get; private set; }
        public string Status => m_Status;

        private void OnEnable()
        {
            NetworkCoreVisualScriptingContext.RequestCompleted += OnCoreRequestCompleted;
            RefreshRegistry(true);
        }

        private void OnDisable()
        {
            NetworkCoreVisualScriptingContext.RequestCompleted -= OnCoreRequestCompleted;
            m_AsyncGeneration++;
            m_Busy = false;
            ClearExpectedRequest();
            DisposeObservations();
        }

        private void Update()
        {
            RefreshRegistry(false);
        }

        private void OnGUI()
        {
            if (!m_ShowOverlay) return;

            RefreshRegistry(false);
            bool ready = TryGetLocalCharacter(
                out NetworkCharacter localCharacter,
                out string readiness);
            ActorObservation localObservation = ready
                ? GetObservation(localCharacter)
                : null;
            localObservation?.Reconcile();

            float actorRows = Mathf.Min(m_RegisteredCharacters.Count, MAX_VISIBLE_ACTORS);
            float height = 240f + actorRows * 52f;
            GUILayout.BeginArea(
                new Rect(
                    m_OverlayPosition.x,
                    m_OverlayPosition.y,
                    m_OverlayWidth,
                    height),
                GUI.skin.box);

            GUILayout.Label(string.IsNullOrWhiteSpace(m_Title)
                ? "GC2 Network Ragdoll Proof"
                : m_Title);
            GUILayout.Label("Readiness: " + readiness);
            GUILayout.Label(
                "Buttons execute authored GC2 Network Core Actions; authority validates and " +
                "broadcasts each transition.");

            bool previousEnabled = GUI.enabled;
            GUILayout.BeginHorizontal();
            GUI.enabled = ready && !m_Busy &&
                          localObservation != null &&
                          localObservation.Phase == ObservedRagdollPhase.Standing &&
                          m_StartActions != null;
            if (GUILayout.Button("Start Ragdoll")) StartRagdoll();

            GUI.enabled = ready && !m_Busy &&
                          localObservation != null &&
                          localObservation.Phase == ObservedRagdollPhase.Dynamic &&
                          m_RecoverActions != null;
            if (GUILayout.Button("Recover")) RecoverRagdoll();

            GUI.enabled = ready && !m_Busy &&
                          m_StartActions != null &&
                          m_RecoverActions != null;
            if (GUILayout.Button("Validate 3 Cycles")) StartAutomatedValidation();
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;

            GUILayout.Label("Status: " + m_Status);
            if (HasAutomationResult)
            {
                GUILayout.Label(
                    $"Automation: {(LastAutomationPassed ? "PASS" : "FAIL")} " +
                    $"({CompletedAutomationCycles}/{AUTOMATED_CYCLE_COUNT} cycles)");
            }

            GUILayout.Space(4f);
            GUILayout.Label("Registered actors on this peer:");
            int visible = Mathf.Min(m_RegisteredCharacters.Count, MAX_VISIBLE_ACTORS);
            for (int i = 0; i < visible; ++i)
            {
                DrawActor(m_RegisteredCharacters[i]);
            }

            if (m_RegisteredCharacters.Count > visible)
            {
                GUILayout.Label($"…and {m_RegisteredCharacters.Count - visible} more actor(s).");
            }
            else if (m_RegisteredCharacters.Count == 0)
            {
                GUILayout.Label("  No initialized Network Characters are registered yet.");
            }

            GUILayout.EndArea();
        }

        /// <summary>Executes the authored one-shot Start Ragdoll Actions list.</summary>
        public void StartRagdoll()
        {
            if (m_Busy) return;
            _ = RunOneShotAsync(NetworkCoreVisualScriptingOperation.StartRagdoll);
        }

        /// <summary>Executes the authored one-shot Recover Ragdoll Actions list.</summary>
        public void RecoverRagdoll()
        {
            if (m_Busy) return;
            _ = RunOneShotAsync(NetworkCoreVisualScriptingOperation.RecoverRagdoll);
        }

        /// <summary>
        /// Runs three bounded ragdoll/recovery cycles. This is intentionally public so an opt-in
        /// network smoke bootstrap can start it and poll the result without reaching into UI code.
        /// </summary>
        public void StartAutomatedValidation()
        {
            if (m_Busy) return;
            _ = RunAutomatedValidationAsync();
        }

        public void SetVisible(bool visible)
        {
            m_ShowOverlay = visible;
        }

        private async Task RunOneShotAsync(NetworkCoreVisualScriptingOperation operation)
        {
            int generation = ++m_AsyncGeneration;
            m_Busy = true;
            try
            {
                if (!TryGetLocalCharacter(
                        out NetworkCharacter localCharacter,
                        out string readiness))
                {
                    SetStatus(generation, readiness);
                    return;
                }

                ActorObservation observation = GetObservation(localCharacter);
                observation?.Reconcile();
                if (observation == null)
                {
                    SetStatus(generation, "The local character is not registered for diagnostics.");
                    return;
                }

                if (operation == NetworkCoreVisualScriptingOperation.StartRagdoll &&
                    observation.Phase != ObservedRagdollPhase.Standing)
                {
                    SetStatus(generation, "Recover the local character before starting ragdoll again.");
                    return;
                }

                if (operation == NetworkCoreVisualScriptingOperation.RecoverRagdoll &&
                    observation.Phase == ObservedRagdollPhase.Standing)
                {
                    SetStatus(generation, "The local character is already standing.");
                    return;
                }

                if (operation == NetworkCoreVisualScriptingOperation.RecoverRagdoll &&
                    observation.Phase == ObservedRagdollPhase.Recovering)
                {
                    SetStatus(generation, "The local character is already recovering.");
                    return;
                }

                if (operation == NetworkCoreVisualScriptingOperation.StartRagdoll)
                {
                    observation.CaptureRootPhysicsBaseline();
                    ActorDiagnostics baseline = CaptureDiagnostics(observation);
                    if (!baseline.HasExactRootPhysicsBaseline ||
                        !baseline.ExactRootPhysicsBaselineRestored ||
                        !baseline.RootPhysicsRestored)
                    {
                        SetStatus(
                            generation,
                            "FAIL — could not arm an active, exact root-physics baseline.");
                        return;
                    }
                }

                int dynamicEpoch = observation.DynamicEpoch;
                int recoveringEpoch = observation.RecoveringEpoch;
                int completedEpoch = observation.CompletedEpoch;
                bool requireExactRecoveryBaseline = observation.Baseline != null;
                Actions actions = operation == NetworkCoreVisualScriptingOperation.StartRagdoll
                    ? m_StartActions
                    : m_RecoverActions;

                SetStatus(generation, operation == NetworkCoreVisualScriptingOperation.StartRagdoll
                    ? "Requesting authoritative ragdoll…"
                    : "Requesting authoritative recovery…");
                if (!await ExecuteActionsAsync(
                        actions,
                        operation,
                        localCharacter.NetworkId,
                        generation))
                {
                    return;
                }

                if (operation == NetworkCoreVisualScriptingOperation.StartRagdoll)
                {
                    bool dynamic = await WaitForStateAsync(
                        observation,
                        diagnostics =>
                            observation.DynamicEpoch > dynamicEpoch &&
                            diagnostics.Phase == ObservedRagdollPhase.Dynamic &&
                            diagnostics.IsExpectedState,
                        m_StateTimeoutSeconds,
                        generation);
                    SetStatus(
                        generation,
                        dynamic
                            ? "PASS — dynamic bones are active and root locomotion collision is suspended."
                            : "FAIL — timed out waiting for the protected Dynamic ragdoll state.");
                }
                else
                {
                    bool recovering = await WaitForStateAsync(
                        observation,
                        diagnostics =>
                            observation.RecoveringEpoch > recoveringEpoch &&
                            diagnostics.Phase == ObservedRagdollPhase.Recovering &&
                            (requireExactRecoveryBaseline
                                ? diagnostics.IsExpectedState
                                : diagnostics.IsExpectedStateWithoutExactBaseline),
                        m_StateTimeoutSeconds,
                        generation);
                    if (!recovering)
                    {
                        SetStatus(
                            generation,
                            "FAIL — timed out waiting for recovery collision restoration.");
                        return;
                    }

                    bool standing = await WaitForStateAsync(
                        observation,
                        diagnostics =>
                            observation.CompletedEpoch > completedEpoch &&
                            diagnostics.Phase == ObservedRagdollPhase.Standing &&
                            (requireExactRecoveryBaseline
                                ? diagnostics.IsExpectedState
                                : diagnostics.IsExpectedStateWithoutExactBaseline),
                        m_StateTimeoutSeconds,
                        generation);
                    SetStatus(
                        generation,
                        standing
                            ? requireExactRecoveryBaseline
                                ? "PASS — recovery completed and the exact root-physics " +
                                  "baseline was restored."
                                : "Recovery completed, but no pre-ragdoll root-physics " +
                                  "baseline was available for an exact proof pass."
                            : "FAIL — timed out waiting for the restored Standing state.");
                }
            }
            catch (Exception exception)
            {
                SetStatus(generation, "FAIL — " + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (generation == m_AsyncGeneration) m_Busy = false;
            }
        }

        private async Task RunAutomatedValidationAsync()
        {
            int generation = ++m_AsyncGeneration;
            m_Busy = true;
            HasAutomationResult = false;
            LastAutomationPassed = false;
            CompletedAutomationCycles = 0;

            try
            {
                if (!TryGetLocalCharacter(
                        out NetworkCharacter localCharacter,
                        out string readiness))
                {
                    FailAutomation(generation, readiness);
                    return;
                }

                ActorObservation observation = GetObservation(localCharacter);
                if (observation == null)
                {
                    FailAutomation(
                        generation,
                        "The local character is not registered for diagnostics.");
                    return;
                }

                bool initialReady = await WaitForStateAsync(
                    observation,
                    diagnostics =>
                        diagnostics.HasDefaultRagdoll &&
                        diagnostics.HasGuard &&
                        diagnostics.IsExpectedStateWithoutExactBaseline,
                    m_StateTimeoutSeconds,
                    generation);
                if (!initialReady)
                {
                    FailAutomation(
                        generation,
                        "The local character never reached a guarded, ragdoll-ready state.");
                    return;
                }

                if (observation.Phase != ObservedRagdollPhase.Standing)
                {
                    SetStatus(generation, "Normalizing the local character to Standing…");
                    if (!await NormalizeStandingAsync(
                            localCharacter, observation, generation))
                    {
                        FailAutomation(generation, m_Status);
                        return;
                    }
                }

                for (int cycle = 1; cycle <= AUTOMATED_CYCLE_COUNT; ++cycle)
                {
                    if (!IsGenerationCurrent(generation)) return;

                    observation.CaptureRootPhysicsBaseline();
                    ActorDiagnostics baseline = CaptureDiagnostics(observation);
                    if (!baseline.HasExactRootPhysicsBaseline ||
                        !baseline.ExactRootPhysicsBaselineRestored ||
                        !baseline.RootPhysicsRestored)
                    {
                        FailAutomation(
                            generation,
                            $"Cycle {cycle}: could not arm an active, exact root-physics " +
                            "baseline before ragdoll.");
                        return;
                    }

                    int dynamicEpoch = observation.DynamicEpoch;
                    SetStatus(
                        generation,
                        $"Cycle {cycle}/{AUTOMATED_CYCLE_COUNT}: requesting ragdoll…");
                    if (!await ExecuteActionsAsync(
                            m_StartActions,
                            NetworkCoreVisualScriptingOperation.StartRagdoll,
                            localCharacter.NetworkId,
                            generation))
                    {
                        FailAutomation(generation, m_Status);
                        return;
                    }

                    bool dynamic = await WaitForStateAsync(
                        observation,
                        diagnostics =>
                            observation.DynamicEpoch > dynamicEpoch &&
                            diagnostics.Phase == ObservedRagdollPhase.Dynamic &&
                            diagnostics.IsExpectedState,
                        m_StateTimeoutSeconds,
                        generation);
                    if (!dynamic)
                    {
                        FailAutomation(
                            generation,
                            $"Cycle {cycle}: Dynamic state did not disable root collision " +
                            "while activating bone bodies.");
                        return;
                    }

                    SetStatus(
                        generation,
                        $"Cycle {cycle}/{AUTOMATED_CYCLE_COUNT}: holding protected Dynamic state…");
                    if (!await HoldExpectedStateAsync(
                            observation,
                            ObservedRagdollPhase.Dynamic,
                            m_DynamicHoldSeconds,
                            generation))
                    {
                        FailAutomation(
                            generation,
                            $"Cycle {cycle}: the protected Dynamic invariant was lost.");
                        return;
                    }

                    SetStatus(
                        generation,
                        $"Cycle {cycle}/{AUTOMATED_CYCLE_COUNT}: requesting recovery…");
                    if (!await RequestRecoveryAndWaitAsync(
                            localCharacter,
                            observation,
                            generation))
                    {
                        FailAutomation(generation, $"Cycle {cycle}: " + m_Status);
                        return;
                    }

                    CompletedAutomationCycles = cycle;
                }

                if (!IsGenerationCurrent(generation)) return;
                HasAutomationResult = true;
                LastAutomationPassed = true;
                SetStatus(
                    generation,
                    "PASS — all 3 authority-approved ragdoll/recovery cycles preserved " +
                    "the exact root-physics component identities and states.");
            }
            catch (Exception exception)
            {
                FailAutomation(
                    generation,
                    exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                if (generation == m_AsyncGeneration) m_Busy = false;
            }
        }

        private async Task<bool> RequestRecoveryAndWaitAsync(
            NetworkCharacter localCharacter,
            ActorObservation observation,
            int generation,
            bool requireExactBaseline = true)
        {
            int recoveringEpoch = observation.RecoveringEpoch;
            int completedEpoch = observation.CompletedEpoch;
            if (!await ExecuteActionsAsync(
                    m_RecoverActions,
                    NetworkCoreVisualScriptingOperation.RecoverRagdoll,
                    localCharacter.NetworkId,
                    generation))
            {
                return false;
            }

            bool recovering = await WaitForStateAsync(
                observation,
                diagnostics =>
                    observation.RecoveringEpoch > recoveringEpoch &&
                    diagnostics.Phase == ObservedRagdollPhase.Recovering &&
                    (requireExactBaseline
                        ? diagnostics.IsExpectedState
                        : diagnostics.IsExpectedStateWithoutExactBaseline),
                m_StateTimeoutSeconds,
                generation);
            if (!recovering)
            {
                SetStatus(
                    generation,
                    "recovery never reached the safe get-up collision state.");
                return false;
            }

            bool standing = await WaitForStateAsync(
                observation,
                diagnostics =>
                    observation.CompletedEpoch > completedEpoch &&
                    diagnostics.Phase == ObservedRagdollPhase.Standing &&
                    (requireExactBaseline
                        ? diagnostics.IsExpectedState
                        : diagnostics.IsExpectedStateWithoutExactBaseline),
                m_StateTimeoutSeconds,
                generation);
            if (!standing)
            {
                SetStatus(
                    generation,
                    "recovery never completed with restored root collision.");
            }
            return standing;
        }

        private async Task<bool> NormalizeStandingAsync(
            NetworkCharacter localCharacter,
            ActorObservation observation,
            int generation)
        {
            ActorDiagnostics diagnostics = CaptureDiagnostics(observation);
            if (diagnostics.Phase == ObservedRagdollPhase.Standing)
            {
                return diagnostics.IsExpectedStateWithoutExactBaseline;
            }

            if (diagnostics.Phase == ObservedRagdollPhase.Dynamic)
            {
                return await RequestRecoveryAndWaitAsync(
                    localCharacter,
                    observation,
                    generation,
                    requireExactBaseline: false);
            }

            int completedEpoch = observation.CompletedEpoch;
            bool standing = await WaitForStateAsync(
                observation,
                value =>
                    observation.CompletedEpoch > completedEpoch &&
                    value.Phase == ObservedRagdollPhase.Standing &&
                    value.IsExpectedStateWithoutExactBaseline,
                m_StateTimeoutSeconds,
                generation);
            if (!standing)
            {
                SetStatus(
                    generation,
                    "the in-progress recovery did not finish with restored root collision.");
            }
            return standing;
        }

        private async Task<bool> ExecuteActionsAsync(
            Actions actions,
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            int generation)
        {
            if (actions == null)
            {
                SetStatus(generation, $"FAIL — no authored {operation} Actions list is assigned.");
                return false;
            }

            NetworkCoreController requestSource =
                NetworkCoreManager.Instance?.CoreController;
            if (requestSource == null)
            {
                SetStatus(
                    generation,
                    $"FAIL — cannot correlate the authoritative {operation} request " +
                    "because the Core controller is unavailable.");
                return false;
            }

            BeginExpectedRequest(requestSource, operation, characterNetworkId);
            try
            {
                Task runTask = actions.Run(new Args(gameObject, gameObject));
                float deadline = Time.realtimeSinceStartup +
                                 Mathf.Max(1f, m_RequestTimeoutSeconds);
                while (IsGenerationCurrent(generation) &&
                       (!runTask.IsCompleted || !m_ResponseReceived) &&
                       Time.realtimeSinceStartup < deadline)
                {
                    await Task.Yield();
                }

                if (!IsGenerationCurrent(generation)) return false;

                if (!runTask.IsCompleted ||
                    !m_ExpectedRequestIdentityCaptured ||
                    !m_ResponseReceived)
                {
                    actions.Cancel();
                    SetStatus(
                        generation,
                        $"FAIL — {operation} did not receive its matching authoritative " +
                        $"response within {Mathf.Max(1f, m_RequestTimeoutSeconds):0.0}s.");
                    return false;
                }

                if (runTask.IsFaulted)
                {
                    Exception exception = runTask.Exception?.GetBaseException();
                    SetStatus(
                        generation,
                        $"FAIL — {operation} Actions failed: " +
                        (exception != null ? exception.Message : "unknown error"));
                    return false;
                }

                if (runTask.IsCanceled)
                {
                    SetStatus(generation, $"FAIL — {operation} Actions were canceled.");
                    return false;
                }

                if (!m_Response.Approved)
                {
                    SetStatus(
                        generation,
                        $"FAIL — authority rejected {operation}: {m_Response.RejectReason}.");
                    return false;
                }

                return true;
            }
            finally
            {
                ClearExpectedRequest();
            }
        }

        private async Task<bool> WaitForStateAsync(
            ActorObservation observation,
            Func<ActorDiagnostics, bool> predicate,
            float timeoutSeconds,
            int generation)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(1f, timeoutSeconds);
            while (IsGenerationCurrent(generation) &&
                   Time.realtimeSinceStartup < deadline)
            {
                RefreshRegistry(false);
                if (observation.NetworkCharacter == null) return false;
                ActorDiagnostics diagnostics = CaptureDiagnostics(observation);
                if (predicate(diagnostics)) return true;
                await Task.Yield();
            }
            return false;
        }

        private async Task<bool> HoldExpectedStateAsync(
            ActorObservation observation,
            ObservedRagdollPhase phase,
            float durationSeconds,
            int generation)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0.1f, durationSeconds);
            while (IsGenerationCurrent(generation) &&
                   Time.realtimeSinceStartup < deadline)
            {
                if (observation.NetworkCharacter == null) return false;
                ActorDiagnostics diagnostics = CaptureDiagnostics(observation);
                if (diagnostics.Phase != phase || !diagnostics.IsExpectedState) return false;
                await Task.Yield();
            }
            return IsGenerationCurrent(generation);
        }

        private void OnCoreRequestCompleted(NetworkCoreVisualScriptingResult result)
        {
            if (result.Operation != m_ExpectedOperation ||
                result.CharacterNetworkId != m_ExpectedCharacterId)
            {
                if (!m_Busy &&
                    result.CharacterNetworkId == m_LastKnownLocalCharacterId)
                {
                    SetResponseStatus(result);
                }
                return;
            }

            // Host loopback can complete synchronously inside SendRagdollRequestToServer, before
            // RequestStartRagdoll raises OnRagdollRequestSent. Retain that single candidate and
            // accept it only after the emitted request supplies the exact tuple.
            if (!m_ExpectedRequestIdentityCaptured)
            {
                m_EarlyResponse = result;
                m_EarlyResponseAvailable = true;
                return;
            }

            if (!MatchesExpectedResponse(result)) return;
            AcceptExpectedResponse(result);
        }

        private void BeginExpectedRequest(
            NetworkCoreController source,
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId)
        {
            ClearExpectedRequest();
            m_ExpectedOperation = operation;
            m_ExpectedCharacterId = characterNetworkId;
            m_ResponseReceived = false;
            m_Response = default;
            m_EarlyResponseAvailable = false;
            m_EarlyResponse = default;
            m_ExpectedRequestSource = source;
            m_ExpectedRequestSource.OnRagdollRequestSent += OnRagdollRequestSent;
        }

        private void ClearExpectedRequest()
        {
            if (m_ExpectedRequestSource != null)
            {
                m_ExpectedRequestSource.OnRagdollRequestSent -= OnRagdollRequestSent;
            }

            m_ExpectedRequestSource = null;
            m_ExpectedOperation = NetworkCoreVisualScriptingOperation.None;
            m_ExpectedCharacterId = 0;
            m_ExpectedRequestIdentityCaptured = false;
            m_ExpectedRequestId = 0;
            m_ExpectedCorrelationId = 0;
            m_EarlyResponseAvailable = false;
            m_EarlyResponse = default;
        }

        private void OnRagdollRequestSent(NetworkRagdollRequest request)
        {
            if (m_ExpectedRequestIdentityCaptured ||
                request.CharacterNetworkId != m_ExpectedCharacterId ||
                request.ActorNetworkId != m_ExpectedCharacterId ||
                !MatchesExpectedOperation(request.ActionType))
            {
                return;
            }

            m_ExpectedRequestIdentityCaptured = true;
            m_ExpectedRequestId = request.RequestId;
            m_ExpectedCorrelationId = request.CorrelationId;

            if (!m_EarlyResponseAvailable ||
                !MatchesExpectedResponse(m_EarlyResponse))
            {
                return;
            }

            NetworkCoreVisualScriptingResult response = m_EarlyResponse;
            m_EarlyResponseAvailable = false;
            m_EarlyResponse = default;
            AcceptExpectedResponse(response);
        }

        private bool MatchesExpectedOperation(RagdollActionType actionType)
        {
            return m_ExpectedOperation switch
            {
                NetworkCoreVisualScriptingOperation.StartRagdoll =>
                    actionType == RagdollActionType.StartRagdoll ||
                    actionType == RagdollActionType.StartRagdollWithForce,
                NetworkCoreVisualScriptingOperation.RecoverRagdoll =>
                    actionType == RagdollActionType.StartRecover ||
                    actionType == RagdollActionType.InstantRecover,
                _ => false
            };
        }

        private bool MatchesExpectedResponse(NetworkCoreVisualScriptingResult result)
        {
            return m_ExpectedRequestIdentityCaptured &&
                   result.Operation == m_ExpectedOperation &&
                   result.CharacterNetworkId == m_ExpectedCharacterId &&
                   result.RequestId == m_ExpectedRequestId &&
                   result.CorrelationId == m_ExpectedCorrelationId;
        }

        private void AcceptExpectedResponse(NetworkCoreVisualScriptingResult result)
        {
            m_Response = result;
            m_ResponseReceived = true;
            SetResponseStatus(result);
        }

        private void SetResponseStatus(NetworkCoreVisualScriptingResult result)
        {
            m_Status = result.Approved
                ? $"Authority approved {result.Operation} " +
                  $"(request {result.RequestId}, correlation {result.CorrelationId})."
                : $"Authority rejected {result.Operation}: {result.RejectReason} " +
                  $"(request {result.RequestId}, correlation {result.CorrelationId}).";
        }

        private void RefreshRegistry(bool force)
        {
            if (!force && Time.realtimeSinceStartup < m_NextRegistryRefresh) return;
            m_NextRegistryRefresh = Time.realtimeSinceStartup + REGISTRY_REFRESH_INTERVAL;

            NetworkTransportBridge active = NetworkTransportBridge.Active;
            if (active != m_TransportBridge)
            {
                DisposeObservations();
                m_TransportBridge = active;
            }

            m_RegisteredCharacters.Clear();
            m_TransportBridge?.CopyRegisteredCharacters(m_RegisteredCharacters);
            SortRegisteredCharacters();

            for (int i = 0; i < m_RegisteredCharacters.Count; ++i)
            {
                NetworkCharacter networkCharacter = m_RegisteredCharacters[i];
                if (!m_Observations.ContainsKey(networkCharacter))
                {
                    m_Observations.Add(
                        networkCharacter,
                        new ActorObservation(networkCharacter));
                }
            }

            m_RemovedCharacters.Clear();
            foreach (KeyValuePair<NetworkCharacter, ActorObservation> entry in m_Observations)
            {
                if (entry.Key == null || !m_RegisteredCharacters.Contains(entry.Key))
                {
                    m_RemovedCharacters.Add(entry.Key);
                }
            }

            for (int i = 0; i < m_RemovedCharacters.Count; ++i)
            {
                NetworkCharacter networkCharacter = m_RemovedCharacters[i];
                if (!m_Observations.TryGetValue(
                        networkCharacter,
                        out ActorObservation observation))
                {
                    continue;
                }

                observation.Dispose();
                m_Observations.Remove(networkCharacter);
            }
        }

        private void SortRegisteredCharacters()
        {
            // Stable insertion sort keeps the tiny demo list deterministic without comparer or
            // LINQ allocations in the frame loop.
            for (int i = 1; i < m_RegisteredCharacters.Count; ++i)
            {
                NetworkCharacter value = m_RegisteredCharacters[i];
                uint valueId = value != null ? value.NetworkId : uint.MaxValue;
                int target = i - 1;
                while (target >= 0)
                {
                    NetworkCharacter candidate = m_RegisteredCharacters[target];
                    uint candidateId = candidate != null
                        ? candidate.NetworkId
                        : uint.MaxValue;
                    if (candidateId <= valueId) break;
                    m_RegisteredCharacters[target + 1] = candidate;
                    target--;
                }
                m_RegisteredCharacters[target + 1] = value;
            }
        }

        private ActorObservation GetObservation(NetworkCharacter networkCharacter)
        {
            if (networkCharacter == null) return null;
            if (m_Observations.TryGetValue(networkCharacter, out ActorObservation observation))
            {
                return observation;
            }

            observation = new ActorObservation(networkCharacter);
            m_Observations.Add(networkCharacter, observation);
            return observation;
        }

        private bool TryGetLocalCharacter(
            out NetworkCharacter localCharacter,
            out string readiness)
        {
            localCharacter = null;
            NetworkTransportBridge bridge = m_TransportBridge ?? NetworkTransportBridge.Active;
            if (bridge == null)
            {
                readiness = "no active transport bridge";
                return false;
            }

            if (!bridge.IsRunning)
            {
                readiness = "session offline";
                return false;
            }

            if (!bridge.IsLocalGameplayReady)
            {
                readiness = "waiting for authoritative gameplay readiness";
                return false;
            }

            if (!bridge.TryGetLocalPlayer(out GameObject localPlayer) || localPlayer == null)
            {
                readiness = "waiting for the local Network Character";
                return false;
            }

            localCharacter = localPlayer.GetComponent<NetworkCharacter>() ??
                             localPlayer.GetComponentInParent<NetworkCharacter>();
            if (localCharacter == null || localCharacter.NetworkId == 0)
            {
                localCharacter = null;
                readiness = "local Network Character has no initialized network ID";
                return false;
            }

            NetworkCoreManager manager = NetworkCoreManager.Instance;
            if (manager == null || !manager.IsInitialized || manager.CoreController == null)
            {
                localCharacter = null;
                readiness = "waiting for the Network Core Manager";
                return false;
            }

            m_LastKnownLocalCharacterId = localCharacter.NetworkId;
            readiness =
                $"ready — local actor #{localCharacter.NetworkId}, role {localCharacter.Role}";
            return true;
        }

        private ActorDiagnostics CaptureDiagnostics(ActorObservation observation)
        {
            observation.Reconcile();
            NetworkCharacter networkCharacter = observation.NetworkCharacter;
            Character character = networkCharacter != null
                ? networkCharacter.Character
                : null;
            if (character == null) return default;

            m_ControllerScratch.Clear();
            character.GetComponents(m_ControllerScratch);
            int enabledControllers = 0;
            int detectingControllers = 0;
            for (int i = 0; i < m_ControllerScratch.Count; ++i)
            {
                CharacterController controller = m_ControllerScratch[i];
                if (controller == null) continue;
                if (controller.enabled) enabledControllers++;
                if (controller.detectCollisions) detectingControllers++;
            }

            m_AgentScratch.Clear();
            character.GetComponents(m_AgentScratch);
            int enabledAgents = 0;
            for (int i = 0; i < m_AgentScratch.Count; ++i)
            {
                NavMeshAgent agent = m_AgentScratch[i];
                if (agent != null && agent.enabled) enabledAgents++;
            }

            m_ColliderScratch.Clear();
            character.GetComponents(m_ColliderScratch);
            int rootColliderCount = 0;
            int enabledRootColliders = 0;
            for (int i = 0; i < m_ColliderScratch.Count; ++i)
            {
                Collider collider = m_ColliderScratch[i];
                if (collider == null || collider is CharacterController) continue;
                rootColliderCount++;
                if (collider.enabled) enabledRootColliders++;
            }

            NetworkRagdollPhysicsGuard.CollectRagdollRigidbodies(
                character,
                m_RigidbodyScratch);
            int dynamicRigidbodies = 0;
            for (int i = 0; i < m_RigidbodyScratch.Count; ++i)
            {
                Rigidbody body = m_RigidbodyScratch[i];
                if (body != null && !body.isKinematic) dynamicRigidbodies++;
            }

            Animator animator = character.Animim?.Animator;
            RootPhysicsBaseline baseline = observation.Baseline;
            return new ActorDiagnostics(
                observation.Phase,
                networkCharacter.IsLocalPlayer,
                character.Ragdoll?.Get<RagdollDefault>() != null,
                character.GetComponent<NetworkRagdollPhysicsGuard>() != null,
                animator != null && animator.enabled,
                m_ControllerScratch.Count,
                enabledControllers,
                detectingControllers,
                m_AgentScratch.Count,
                enabledAgents,
                rootColliderCount,
                enabledRootColliders,
                m_RigidbodyScratch.Count,
                dynamicRigidbodies,
                baseline != null,
                baseline != null && baseline.Matches(
                    m_ControllerScratch,
                    m_AgentScratch,
                    m_ColliderScratch));
        }

        private void DrawActor(NetworkCharacter networkCharacter)
        {
            ActorObservation observation = GetObservation(networkCharacter);
            if (observation == null) return;
            ActorDiagnostics diagnostics = CaptureDiagnostics(observation);
            string local = diagnostics.IsLocal ? " LOCAL" : string.Empty;
            GUILayout.Label(
                $"  #{networkCharacter.NetworkId}{local} — {networkCharacter.Role} — " +
                $"{diagnostics.Phase} — {(diagnostics.IsExpectedState ? "PASS" : "CHECK")}");
            string baselineStatus = !diagnostics.HasExactRootPhysicsBaseline
                ? "not armed"
                : diagnostics.ExactRootPhysicsBaselineRestored
                    ? "restored"
                    : "DIFF";
            GUILayout.Label(
                $"    Guard {(diagnostics.HasGuard ? "yes" : "NO")} | " +
                $"Animator {(diagnostics.AnimatorEnabled ? "on" : "off")} | " +
                $"CC {diagnostics.EnabledControllers}/{diagnostics.ControllerCount} enabled, " +
                $"{diagnostics.DetectingControllers} detecting | " +
                $"Agent {diagnostics.EnabledAgents}/{diagnostics.AgentCount} | " +
                $"root Collider {diagnostics.EnabledRootColliders}/" +
                $"{diagnostics.RootColliderCount} | dynamic bones " +
                $"{diagnostics.DynamicRigidbodyCount}/{diagnostics.RigidbodyCount} | " +
                $"exact baseline {baselineStatus}");
        }

        private void DisposeObservations()
        {
            foreach (ActorObservation observation in m_Observations.Values)
            {
                observation.Dispose();
            }
            m_Observations.Clear();
            m_RegisteredCharacters.Clear();
            m_RemovedCharacters.Clear();
        }

        private bool IsGenerationCurrent(int generation)
        {
            return generation == m_AsyncGeneration && this != null && isActiveAndEnabled;
        }

        private void SetStatus(int generation, string status)
        {
            if (!IsGenerationCurrent(generation)) return;
            m_Status = status ?? string.Empty;
        }

        private void FailAutomation(int generation, string reason)
        {
            if (!IsGenerationCurrent(generation)) return;
            HasAutomationResult = true;
            LastAutomationPassed = false;
            m_Status = reason != null && reason.StartsWith("FAIL", StringComparison.Ordinal)
                ? reason
                : "FAIL — " + (reason ?? "unknown validation error");
        }
    }
}
