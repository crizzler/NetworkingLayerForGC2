using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Melee;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Optional-type-agnostic runtime evidence for the Free Flow Combat demo smoke players.
    /// Concrete Free Flow and Behavior components remain in optional assemblies, so this probe
    /// resolves them by stable type/member names instead of adding hard package dependencies.
    /// </summary>
    public static class NetworkFreeFlowCombatSmokeUtility
    {
        public const int ExpectedNpcCount = 5;

        private const string AuthorityValidatedMarkerSuffix = ".authority-validated";

        private const float NpcMotionThreshold = 0.1f;
        private const float NpcCounterFacingToleranceDegrees = 10f;
        private const string CanonicalCounterSkillName = "FreeFlow_Brawl_Counter";

        private const string FreeFlowRuntimeType =
            "Arawn.FreeFlowCombat.FreeFlowCombatRuntime, Assembly-CSharp";
        private const string FreeFlowIntegrationType =
            "Arawn.FreeFlowCombat.FreeFlowNetworkIntegration, Assembly-CSharp";
        private const string FreeFlowAgentType =
            "Arawn.FreeFlowCombat.AI.FreeFlowEnemyAgent, Assembly-CSharp";
        private const string BehaviorProcessorType =
            "GameCreator.Runtime.Behavior.Processor, GameCreator.Runtime.Behavior";

        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Enables the already-existing Melee diagnostics only for an explicitly requested
        /// Free Flow smoke process. Production players never create the smoke bootstrap, so
        /// these flags remain off in normal builds.
        /// </summary>
        public static void EnableSemanticAttackDiagnostics()
        {
            NetworkMeleeDebug.ForceSkillDiagnostics = true;
            NetworkMeleeDebug.ForceInputLockDiagnostics = true;
        }

        /// <summary>
        /// Attaches semantic evidence listeners as early as the smoke bootstrap permits. Calling
        /// this before transport startup guarantees that an authority-accepted request cannot
        /// precede observation of <see cref="NetworkMeleeManager.OnSkillValidated"/>. Repeated
        /// calls also follow a manager replacement without adding duplicate subscriptions.
        /// </summary>
        public static void BeginSemanticObservation()
        {
            // Controllers route through the singleton, but include every discovered instance so
            // a transport-driven singleton replacement cannot create an unobserved validation
            // window between scene startup and the next smoke capture.
            ObserveMeleeManager(NetworkMeleeManager.Instance);
            NetworkMeleeManager[] managers = UnityObjectSearch.FindAll<NetworkMeleeManager>(
                FindObjectsInactive.Include);
            for (int i = 0; i < managers.Length; i++) ObserveMeleeManager(managers[i]);
            ObserveMeleeControllers();
        }

        [Serializable]
        public sealed class TransportEvidence
        {
            public bool admissionRequired;
            public bool localGameplayReadyObserved;
            public bool remoteGameplayReadyRequired;
            public bool remoteGameplayReadyObserved;
            public int spawnedAtFirstLocalReady;
            public int admittedAtFirstLocalReady;
            public int spawnedAtFirstRemoteReady;
            public int admittedAtFirstRemoteReady;
            public int currentlySpawned;
            public int currentlyAdmitted;
            public string diagnostic;
        }

        [Serializable]
        public sealed class Result
        {
            public bool passed;
            public string role;
            public string phase;
            public string message;
            public float elapsedSeconds;

            public int expectedHumans;
            public int authenticatedHumans;
            public int expectedNpcs = ExpectedNpcCount;
            public int explicitServerNpcs;
            public int npcSimulationAuthorities;
            public int npcAuthorityGatesRunning;
            public int npcSelectedTargets;

            public int npcReceiversReady;
            public int npcIntegrationsReady;
            public int npcAgentsReady;
            public int npcAgentTargetsReady;
            public int npcDirectSkillAllowListsReady;
            public int npcProcessorsReady;
            public int npcProcessorsEnabled;
            public int npcProcessorsIterated;
            public int npcRuntimesReady;
            public int npcRuntimesEnabled;

            public int npcServerNavMeshDriversReady;
            public int npcNavMeshAgentsReady;
            public int npcNavMeshAgentBindingsReady;
            public int npcNavMeshAgentsOnMesh;
            public int npcNetworkFacingsReady;
            public int npcAuthoredMovementActors;
            public int npcAuthoredMovementDisplacedActors;
            public int npcDisplacedActors;
            public float npcMaximumDisplacement;
            public bool npcMotionObserved;
            public int npcCounterTelegraphActors;
            public int npcCounterTelegraphFacedActors;
            public float npcBestCounterFacingAngle = -1f;
            public bool npcCounterFacingObserved;

            public int npcRendererSetsReady;
            public int npcPresentationsEnabled;
            public bool npcPresentationRequired;

            public bool localPlayerRequired;
            public bool localPlayerReady;
            public uint localPlayerNetworkId;
            public bool localPlayerReceiverReady;
            public bool localPlayerRuntimeReady;
            public bool localPlayerRuntimeEnabled;
            public int localCandidateCount;
            public bool localTargetIndicatorActive;
            public bool localFallbackIndicatorActive;

            public int meleeSkillsValidated;
            public int meleeHitsValidated;
            public int observedNpcSkillEvents;
            public int observedPlayerSkillEvents;

            public bool semanticPlayerAttackRequired;
            public bool semanticPlayerAttackIssued;
            public bool semanticPlayerAttackAuthorityValidated;
            public bool semanticPlayerAttackObserverReceived;
            public uint semanticPlayerAttackActorNetworkId;
            public uint semanticPlayerAttackTargetNetworkId;
            public int semanticPlayerAttackSkillHash;
            public float semanticPlayerAttackStartDistance;
            public float semanticPlayerAttackMinimumDistance;
            public float semanticPlayerAttackRelativeProgress;
            public float semanticPlayerAttackMotionDistance;
            public bool semanticPlayerAttackMotionObserved;

            public bool semanticCounterRequired;
            public bool semanticCounterActionIssued;
            public int semanticCounterLocalAttempts;
            public bool semanticCounterAuthorityValidated;
            public bool semanticCounterObserverReceived;
            public uint semanticCounterActorNetworkId;
            public uint semanticCounterTargetNetworkId;
            public int semanticCounterSkillHash;
            public bool semanticCounterSkillMotionWarp;

            public bool fusionAdmissionRequired;
            public bool fusionLocalGameplayReadyObserved;
            public bool fusionRemoteGameplayReadyRequired;
            public bool fusionRemoteGameplayReadyObserved;
            public int fusionSpawnedAtFirstLocalReady;
            public int fusionAdmittedAtFirstLocalReady;
            public int fusionSpawnedAtFirstRemoteReady;
            public int fusionAdmittedAtFirstRemoteReady;
            public int fusionCurrentlySpawned;
            public int fusionCurrentlyAdmitted;
            public string transportDiagnostic;
            public string optionalRuntimeDiagnostic;
        }

        private static readonly List<NetworkCharacter> s_Characters = new(32);
        private static readonly List<NetworkCharacter> s_ObservedCharacters = new(32);
        private static readonly HashSet<uint> s_NpcNetworkIds = new();
        private static readonly Dictionary<uint, NpcMotionEvidence> s_NpcMotionEvidence = new();

        private const int MaximumRecentSkillEvents = 64;
        private const int MaximumDiagnosticSkillEvents = 12;
        private const float SemanticAttackObservationSeconds = 0.75f;
        private const float SemanticAttackMotionThreshold = 0.05f;

        private static readonly HashSet<NetworkMeleeManager> s_ObservedMeleeManagers = new();
        private static readonly HashSet<NetworkMeleeController> s_ObservedMeleeControllers =
            new();
        private static readonly List<NetworkSkillBroadcast> s_ValidatedSkillEvents =
            new(MaximumRecentSkillEvents);
        private static readonly List<NetworkSkillBroadcast> s_ObservedSkillEvents =
            new(MaximumRecentSkillEvents);
        private static int s_ObservedNpcSkillEvents;
        private static int s_ObservedPlayerSkillEvents;

        private static bool s_LocalSemanticAttackIssued;
        private static uint s_LocalSemanticAttackActorNetworkId;
        private static uint s_LocalSemanticAttackTargetNetworkId;
        private static int s_LocalSemanticAttackSkillHash;
        private static float s_LocalSemanticAttackIssuedAt;
        private static Vector3 s_LocalSemanticAttackStartPosition;
        private static float s_LocalSemanticAttackStartDistance;
        private static float s_LocalSemanticAttackMinimumDistance;
        private static Transform s_LocalSemanticAttackActor;
        private static Transform s_LocalSemanticAttackTarget;
        private static int s_LocalSemanticAttackObservedEventStartIndex;
        private static readonly Dictionary<uint, float> s_LocalSemanticAttackStartDistances =
            new();

        private static bool s_SemanticCounterArmed;
        private static bool s_SemanticCounterFrozen;
        private static bool s_SemanticCounterActionIssued;
        private static int s_SemanticCounterLocalAttempts;
        private static uint s_SemanticCounterActorNetworkId;
        private static uint s_SemanticCounterTargetNetworkId;
        private static int s_SemanticCounterSkillHash;
        private static readonly Dictionary<uint, uint> s_SemanticCounterAttemptedRevisions =
            new();

        private sealed class NpcMotionEvidence
        {
            public Transform actor;
            public Vector3 startPosition;
            public float maximumDisplacement;
            public bool authoredMovementObserved;
            public bool counterTelegraphObserved;
            public bool counterFacingObserved;
            public float bestCounterFacingAngle = float.PositiveInfinity;
        }

        [Serializable]
        private sealed class SemanticAttackMarker
        {
            public string role;
            public uint actorNetworkId;
            public uint targetNetworkId;
            public int skillHash;
            public uint counterActorNetworkId;
            public uint counterTargetNetworkId;
            public int counterSkillHash;
            public bool counterSkillMotionWarp;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            foreach (NetworkMeleeManager manager in s_ObservedMeleeManagers)
            {
                if (manager != null) manager.OnSkillValidated -= OnSkillValidated;
            }
            foreach (NetworkMeleeController controller in s_ObservedMeleeControllers)
            {
                if (controller != null) controller.OnSkillExecuted -= OnSkillExecuted;
            }

            s_Characters.Clear();
            s_ObservedCharacters.Clear();
            s_NpcNetworkIds.Clear();
            s_NpcMotionEvidence.Clear();
            s_ObservedMeleeManagers.Clear();
            s_ObservedMeleeControllers.Clear();
            s_ValidatedSkillEvents.Clear();
            s_ObservedSkillEvents.Clear();
            s_ObservedNpcSkillEvents = 0;
            s_ObservedPlayerSkillEvents = 0;
            s_LocalSemanticAttackIssued = false;
            s_LocalSemanticAttackActorNetworkId = 0;
            s_LocalSemanticAttackTargetNetworkId = 0;
            s_LocalSemanticAttackSkillHash = 0;
            s_LocalSemanticAttackIssuedAt = 0f;
            s_LocalSemanticAttackStartPosition = Vector3.zero;
            s_LocalSemanticAttackStartDistance = 0f;
            s_LocalSemanticAttackMinimumDistance = 0f;
            s_LocalSemanticAttackActor = null;
            s_LocalSemanticAttackTarget = null;
            s_LocalSemanticAttackObservedEventStartIndex = 0;
            s_LocalSemanticAttackStartDistances.Clear();
            s_SemanticCounterArmed = false;
            s_SemanticCounterFrozen = false;
            s_SemanticCounterActionIssued = false;
            s_SemanticCounterLocalAttempts = 0;
            s_SemanticCounterActorNetworkId = 0;
            s_SemanticCounterTargetNetworkId = 0;
            s_SemanticCounterSkillHash = 0;
            s_SemanticCounterAttemptedRevisions.Clear();
        }

        /// <summary>
        /// Samples the real Free Flow NPC simulation every rendered frame. Counter windows can
        /// be shorter than the smoke monitor's normal polling interval, so the transport
        /// bootstraps call this from Update and TryCapture consumes the cumulative evidence.
        /// </summary>
        public static void ObserveNpcMotionAndFacing()
        {
            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning) return;

            bridge.CopyRegisteredCharacters(s_ObservedCharacters);
            string optionalDiagnostic = string.Empty;
            for (int i = 0; i < s_ObservedCharacters.Count; i++)
            {
                NetworkCharacter character = s_ObservedCharacters[i];
                if (character == null || character.NetworkId == 0 ||
                    character.ActorType != NetworkCharacterActorType.NPC ||
                    !character.IsServerAuthoritativeNPC)
                {
                    continue;
                }

                Component agent = FindOptionalComponent(
                    character.gameObject,
                    FreeFlowAgentType,
                    ref optionalDiagnostic);
                NetworkFreeFlowCombatAdapter adapter =
                    character.GetComponent<NetworkFreeFlowCombatAdapter>();
                if (agent == null ||
                    adapter == null ||
                    GetPropertyValue(agent, "PlayerTarget") is not GameObject target ||
                    !IsAuthenticatedPlayer(target))
                {
                    continue;
                }

                if (!s_NpcMotionEvidence.TryGetValue(
                        character.NetworkId,
                        out NpcMotionEvidence evidence) ||
                    evidence.actor != character.transform)
                {
                    evidence = new NpcMotionEvidence
                    {
                        actor = character.transform,
                        startPosition = character.transform.position
                    };
                    s_NpcMotionEvidence[character.NetworkId] = evidence;
                }

                Vector3 displacement = character.transform.position - evidence.startPosition;
                displacement.y = 0f;
                evidence.maximumDisplacement = Mathf.Max(
                    evidence.maximumDisplacement,
                    displacement.magnitude);

                if (bridge.IsServer && character.HasSimulationAuthority &&
                    character.Character?.Motion != null &&
                    character.Character.Motion.MovementType !=
                    GameCreator.Runtime.Characters.Character.MovementType.None)
                {
                    evidence.authoredMovementObserved = true;
                }

                // Observer presentation intentionally exposes the counter indicator only to the
                // assigned local player. The adapter state remains the authority-owned global
                // truth on every peer, so use it for transport-wide facing evidence while keeping
                // actual Counter invocation restricted to the assigned local owner below.
                if (!adapter.CurrentState.IsCounterable) continue;

                evidence.counterTelegraphObserved = true;
                Vector3 targetDirection = target.transform.position - character.transform.position;
                targetDirection.y = 0f;
                Vector3 forward = character.transform.forward;
                forward.y = 0f;
                if (targetDirection.sqrMagnitude <= 0.0001f || forward.sqrMagnitude <= 0.0001f)
                {
                    continue;
                }

                float angle = Vector3.Angle(forward, targetDirection);
                evidence.bestCounterFacingAngle = Mathf.Min(
                    evidence.bestCounterFacingAngle,
                    angle);
                if (angle <= NpcCounterFacingToleranceDegrees)
                {
                    evidence.counterFacingObserved = true;
                }
            }

            if (s_SemanticCounterArmed)
            {
                TryInvokeAssignedLocalPlayerCounter(out _);
            }
        }

        public static bool TryGetArgument(string name, out string value) =>
            NetworkEnemyShooterSmokeUtility.TryGetArgument(name, out value);

        public static int GetIntArgument(string name, int fallback) =>
            NetworkEnemyShooterSmokeUtility.GetIntArgument(name, fallback);

        /// <summary>
        /// Enables the smoke-only automatic Counter path. Every client-capable peer is armed so
        /// Host/Shared sessions remain deterministic regardless of which human the nearest-NPC
        /// selector assigns. The invocation itself still requires an authority-owned counter
        /// revision whose selected target is this process's authenticated local player.
        /// </summary>
        public static void ArmAssignedLocalPlayerCounter()
        {
            if (s_SemanticCounterFrozen) return;
            s_SemanticCounterArmed = true;
        }

        /// <summary>
        /// Stops smoke-only automatic Counter invocations after the observer freezes its exact
        /// semantic marker. This prevents later valid counter windows from displacing the
        /// marker's original Attack tuple from the bounded evidence history.
        /// </summary>
        public static void FreezeAssignedLocalPlayerCounter()
        {
            s_SemanticCounterArmed = false;
            s_SemanticCounterFrozen = true;
        }

        /// <summary>
        /// Invokes the real optional Free Flow <c>Counter()</c> entry point for an NPC counter
        /// window assigned to this process's authenticated local player. A state revision is
        /// attempted at most once; a later window can retry with its newer revision.
        /// </summary>
        public static bool TryInvokeAssignedLocalPlayerCounter(out string message)
        {
            message = string.Empty;
            if (!s_SemanticCounterArmed)
            {
                message = "The automatic Free Flow counter is not armed.";
                return false;
            }

            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning || !bridge.IsClient ||
                !bridge.IsLocalGameplayReady ||
                !bridge.TryGetLocalPlayer(out GameObject localPlayer) ||
                localPlayer == null)
            {
                message = "No ready authenticated local player can issue the Free Flow counter.";
                return false;
            }

            NetworkCharacter actor = ResolveNetworkCharacter(localPlayer);
            if (!IsAuthenticatedPlayer(actor) || !actor.IsLocalPlayer || actor.NetworkId == 0 ||
                actor.Character == null || actor.Character.IsDead ||
                actor.Character.Busy == null || actor.Character.Busy.IsBusy)
            {
                message = "The assigned local player is not currently available to counter.";
                return false;
            }

            NetworkMeleeController meleeController =
                actor.GetComponent<NetworkMeleeController>();
            NetworkFreeFlowCombatAdapter actorAdapter =
                actor.GetComponent<NetworkFreeFlowCombatAdapter>();
            if (meleeController == null || !meleeController.IsLocalClient ||
                meleeController.CurrentMeleeWeapon == null || actorAdapter == null)
            {
                message = "The assigned local player has no ready local Melee controller.";
                return false;
            }

            string optionalDiagnostic = string.Empty;
            Component runtime = FindOptionalComponent(
                actor.gameObject,
                FreeFlowRuntimeType,
                ref optionalDiagnostic);
            if (runtime is not Behaviour runtimeBehaviour || !runtimeBehaviour.enabled)
            {
                message = string.IsNullOrEmpty(optionalDiagnostic)
                    ? "The assigned local player's Free Flow runtime is not enabled."
                    : optionalDiagnostic;
                return false;
            }

            bridge.CopyRegisteredCharacters(s_ObservedCharacters);
            NetworkCharacter selectedNpc = null;
            uint selectedRevision = 0;
            float nearestSqrDistance = float.PositiveInfinity;
            for (int i = 0; i < s_ObservedCharacters.Count; i++)
            {
                NetworkCharacter candidate = s_ObservedCharacters[i];
                if (candidate == null || candidate.NetworkId == 0 ||
                    candidate.ActorType != NetworkCharacterActorType.NPC ||
                    !candidate.IsServerAuthoritativeNPC)
                {
                    continue;
                }

                NetworkFreeFlowCombatAdapter adapter =
                    candidate.GetComponent<NetworkFreeFlowCombatAdapter>();
                if (adapter == null ||
                    !actorAdapter.IsCounterAvailableToLocalPlayer(candidate.gameObject))
                {
                    continue;
                }

                NetworkFreeFlowCombatState state = adapter != null
                    ? adapter.CurrentState
                    : default;
                if (!state.IsCounterable || state.StateVersion == 0 ||
                    state.TargetNetworkId != actor.NetworkId ||
                    (s_SemanticCounterAttemptedRevisions.TryGetValue(
                         candidate.NetworkId,
                         out uint attemptedRevision) &&
                     attemptedRevision == state.StateVersion))
                {
                    continue;
                }

                float sqrDistance = (candidate.transform.position - actor.transform.position)
                    .sqrMagnitude;
                if (sqrDistance >= nearestSqrDistance) continue;
                selectedNpc = candidate;
                selectedRevision = state.StateVersion;
                nearestSqrDistance = sqrDistance;
            }

            if (selectedNpc == null)
            {
                message = "Waiting for an authority counter window assigned to this local player.";
                return false;
            }

            try
            {
                MethodInfo refresh = runtime.GetType().GetMethod(
                    "RefreshCandidates",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                MethodInfo counter = runtime.GetType().GetMethod(
                    "Counter",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (refresh == null || counter == null)
                {
                    message = "The optional Free Flow runtime has no public " +
                              "RefreshCandidates/Counter API.";
                    return false;
                }

                refresh.Invoke(runtime, null);
                actor.Character.Combat.Targets.AddCandidate(selectedNpc.gameObject);
                actor.Character.Combat.Targets.Primary = selectedNpc.gameObject;

                // Mark before invoking to prevent the next rendered frame from re-entering the
                // same asynchronous GC2 instruction list before it has set Character.Busy.
                s_SemanticCounterAttemptedRevisions[selectedNpc.NetworkId] = selectedRevision;
                s_SemanticCounterActionIssued = true;
                s_SemanticCounterLocalAttempts++;
                s_SemanticCounterActorNetworkId = actor.NetworkId;
                s_SemanticCounterTargetNetworkId = selectedNpc.NetworkId;
                counter.Invoke(runtime, null);

                message = $"Invoked the real assigned Free Flow Counter actor={actor.NetworkId} " +
                          $"target={selectedNpc.NetworkId} revision={selectedRevision}.";
                return true;
            }
            catch (Exception exception)
            {
                // Do not spin on the same authority revision if the optional action throws.
                s_SemanticCounterAttemptedRevisions[selectedNpc.NetworkId] = selectedRevision;
                message = "The assigned Free Flow Counter invocation failed: " +
                          UnwrapException(exception).Message;
                return false;
            }
        }

        /// <summary>
        /// Captures the durable actor, authority, transport, optional-integration, and local
        /// runtime prerequisites needed to start the independent semantic smoke probes. This
        /// deliberately does not require NPC displacement, counter-facing evidence, active
        /// presentation, or a currently visible local candidate. Those are transient gameplay
        /// outcomes and remain mandatory in the strict <see cref="TryCapture"/> result.
        /// </summary>
        public static bool TryCaptureFoundation(
            string role,
            int expectedHumans,
            bool authority,
            bool requireNpcPresentation,
            string phase,
            float elapsedSeconds,
            TransportEvidence transportEvidence,
            out Result result)
        {
            return TryCaptureInternal(
                role,
                expectedHumans,
                authority,
                requireNpcPresentation,
                phase,
                elapsedSeconds,
                transportEvidence,
                requireGameplayEvidence: false,
                out result);
        }

        /// <summary>
        /// Captures the complete Free Flow smoke contract. Unlike
        /// <see cref="TryCaptureFoundation"/>, this remains strict about observed authored NPC
        /// motion, replicated displacement, counter-facing, presentation, candidates, and UI.
        /// </summary>
        public static bool TryCapture(
            string role,
            int expectedHumans,
            bool authority,
            bool requireNpcPresentation,
            string phase,
            float elapsedSeconds,
            TransportEvidence transportEvidence,
            out Result result)
        {
            return TryCaptureInternal(
                role,
                expectedHumans,
                authority,
                requireNpcPresentation,
                phase,
                elapsedSeconds,
                transportEvidence,
                requireGameplayEvidence: true,
                out result);
        }

        private static bool TryCaptureInternal(
            string role,
            int expectedHumans,
            bool authority,
            bool requireNpcPresentation,
            string phase,
            float elapsedSeconds,
            TransportEvidence transportEvidence,
            bool requireGameplayEvidence,
            out Result result)
        {
            result = new Result
            {
                role = role,
                phase = phase,
                expectedHumans = expectedHumans,
                semanticCounterRequired = true,
                npcPresentationRequired = requireNpcPresentation,
                elapsedSeconds = elapsedSeconds,
                message = "Transport bridge is not running."
            };
            ApplyTransportEvidence(result, transportEvidence);

            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning)
            {
                result.message = bridge == null
                    ? "No NetworkTransportBridge exists in the Free Flow smoke scene."
                    : $"Transport bridge '{bridge.name}' ({bridge.GetType().Name}) is not " +
                      $"running (server={bridge.IsServer}, client={bridge.IsClient}, " +
                      $"lastError='{bridge.LastSessionError}', " +
                      $"lastStop='{bridge.LastSessionStopReason}').";
                return false;
            }

            if (authority && !bridge.IsServer)
            {
                result.message = "The authority process does not own server simulation.";
                return false;
            }
            if (!authority && bridge.IsServer)
            {
                result.message = "The observer process unexpectedly owns server simulation.";
                return false;
            }
            if (bridge.IsClient && !bridge.IsLocalGameplayReady)
            {
                result.message = "The local client has not completed gameplay readiness.";
                return false;
            }

            bridge.CopyRegisteredCharacters(s_Characters);
            s_NpcNetworkIds.Clear();
            string optionalDiagnostic = string.Empty;

            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter character = s_Characters[i];
                if (character == null) continue;

                if (character.ActorType == NetworkCharacterActorType.PlayerOwned &&
                    character.IsPlayerOwnedActor &&
                    character.HasAuthenticatedPlayerOwner)
                {
                    result.authenticatedHumans++;
                }

                if (character.ActorType != NetworkCharacterActorType.NPC ||
                    !character.IsServerAuthoritativeNPC)
                {
                    continue;
                }

                result.explicitServerNpcs++;
                if (character.NetworkId != 0) s_NpcNetworkIds.Add(character.NetworkId);
                if (character.HasSimulationAuthority) result.npcSimulationAuthorities++;

                NetworkCharacterAuthorityGate gate =
                    character.GetComponent<NetworkCharacterAuthorityGate>();
                bool gateRunning = gate != null &&
                    gate.AuthorityEnabled &&
                    HasActiveAuthorityRoot(gate);
                if (gateRunning) result.npcAuthorityGatesRunning++;

                NetworkNpcTargetSelector selector =
                    character.GetComponent<NetworkNpcTargetSelector>();
                if (selector != null && IsAuthenticatedPlayer(selector.SelectedTarget))
                {
                    result.npcSelectedTargets++;
                }

                NetworkFreeFlowCombatAdapter adapter =
                    character.GetComponent<NetworkFreeFlowCombatAdapter>();
                if (adapter != null && adapter.HasReceiver)
                {
                    result.npcReceiversReady++;
                }
                if (adapter != null && adapter.RegisteredDirectSkillCount > 0)
                {
                    result.npcDirectSkillAllowListsReady++;
                }

                Component integration = FindOptionalComponent(
                    character.gameObject,
                    FreeFlowIntegrationType,
                    ref optionalDiagnostic);
                if (integration != null) result.npcIntegrationsReady++;

                Component agent = FindOptionalComponent(
                    character.gameObject,
                    FreeFlowAgentType,
                    ref optionalDiagnostic);
                if (agent != null)
                {
                    result.npcAgentsReady++;
                    object playerTarget = GetPropertyValue(agent, "PlayerTarget");
                    if (playerTarget is GameObject target && IsAuthenticatedPlayer(target))
                    {
                        result.npcAgentTargetsReady++;
                    }
                }

                Component processor = FindOptionalComponent(
                    character.gameObject,
                    BehaviorProcessorType,
                    ref optionalDiagnostic);
                if (processor != null)
                {
                    result.npcProcessorsReady++;
                    bool processorEnabled = processor is Behaviour processorBehaviour &&
                        processorBehaviour.enabled;
                    if (processorEnabled) result.npcProcessorsEnabled++;

                    if (TryReadFloatField(processor, "m_LastUpdateTime", out float lastUpdate) &&
                        lastUpdate > -9998f)
                    {
                        result.npcProcessorsIterated++;
                    }
                }

                Component runtime = FindOptionalComponent(
                    character.gameObject,
                    FreeFlowRuntimeType,
                    ref optionalDiagnostic);
                if (runtime != null)
                {
                    result.npcRuntimesReady++;
                    if (runtime is Behaviour runtimeBehaviour && runtimeBehaviour.enabled)
                    {
                        result.npcRuntimesEnabled++;
                    }
                }

                if (character.ActiveDriver is UnitDriverNavmeshNetworkServer navMeshDriver)
                {
                    result.npcServerNavMeshDriversReady++;
                    NavMeshAgent navMeshAgent = character.GetComponent<NavMeshAgent>();
                    if (navMeshAgent != null)
                    {
                        result.npcNavMeshAgentsReady++;
                        NavMeshAgent boundAgent = navMeshDriver.BoundAgent;
                        if (boundAgent != null && navMeshAgent == boundAgent)
                        {
                            result.npcNavMeshAgentBindingsReady++;
                        }
                        if (navMeshAgent.enabled && navMeshAgent.isOnNavMesh)
                        {
                            result.npcNavMeshAgentsOnMesh++;
                        }
                    }
                }

                if (character.NetworkFacingUnit is UnitFacingNetworkPivot facing &&
                    facing.IsNetworkInitialized)
                {
                    result.npcNetworkFacingsReady++;
                }

                Renderer[] renderers = character.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length > 0) result.npcRendererSetsReady++;
                if (HasEnabledPresentation(renderers)) result.npcPresentationsEnabled++;
            }

            CaptureLocalPlayer(bridge, result, ref optionalDiagnostic);
            result.optionalRuntimeDiagnostic = optionalDiagnostic;
            CaptureMeleeEvidence(result);
            CaptureLocalSemanticAttackEvidence(result);
            CaptureNpcMotionEvidence(result);
            CaptureSemanticCounterEvidence(result);

            if (result.authenticatedHumans != expectedHumans)
            {
                result.message =
                    $"Registered explicit authenticated humans={result.authenticatedHumans}; " +
                    $"expected={expectedHumans}.";
                return false;
            }
            if (result.explicitServerNpcs != ExpectedNpcCount)
            {
                result.message =
                    $"Registered explicit server-authoritative NPCs={result.explicitServerNpcs}; " +
                    $"expected exactly {ExpectedNpcCount}.";
                return false;
            }
            if (result.npcReceiversReady != ExpectedNpcCount ||
                result.npcIntegrationsReady != ExpectedNpcCount)
            {
                result.message =
                    $"Free Flow NPC receivers/integrations are not ready " +
                    $"({result.npcReceiversReady}/{result.npcIntegrationsReady}/" +
                    $"{ExpectedNpcCount}).";
                return false;
            }
            if (result.npcAgentsReady != ExpectedNpcCount ||
                result.npcProcessorsReady != ExpectedNpcCount ||
                result.npcRuntimesReady != ExpectedNpcCount)
            {
                result.message =
                    "Dynamic Free Flow NPC setup is incomplete: " +
                    $"agents={result.npcAgentsReady}, processors={result.npcProcessorsReady}, " +
                    $"runtimes={result.npcRuntimesReady}, expected={ExpectedNpcCount}.";
                return false;
            }
            if (result.npcRuntimesEnabled != 0)
            {
                result.message =
                    $"NPC-local Free Flow runtimes enabled={result.npcRuntimesEnabled}; " +
                    "NPC gameplay must be driven by the authority-only Behavior Processor.";
                return false;
            }

            if (authority)
            {
                if (result.npcSimulationAuthorities != ExpectedNpcCount ||
                    result.npcAuthorityGatesRunning != ExpectedNpcCount)
                {
                    result.message =
                        "Authority has not admitted every NPC simulation/gate: " +
                        $"simulation={result.npcSimulationAuthorities}, " +
                        $"gates={result.npcAuthorityGatesRunning}, " +
                        $"expected={ExpectedNpcCount}.";
                    return false;
                }
                if (result.npcSelectedTargets != ExpectedNpcCount ||
                    result.npcAgentTargetsReady != ExpectedNpcCount)
                {
                    result.message =
                        "Authority target propagation is incomplete: " +
                        $"selectors={result.npcSelectedTargets}, " +
                        $"agents={result.npcAgentTargetsReady}, expected={ExpectedNpcCount}.";
                    return false;
                }
                if (result.npcDirectSkillAllowListsReady != ExpectedNpcCount)
                {
                    result.message =
                        $"Authority NPC direct-skill allow-lists ready=" +
                        $"{result.npcDirectSkillAllowListsReady}; expected={ExpectedNpcCount}.";
                    return false;
                }
                if (result.npcProcessorsEnabled != ExpectedNpcCount ||
                    result.npcProcessorsIterated != ExpectedNpcCount)
                {
                    result.message =
                        "Authority-only Behavior execution is incomplete: " +
                        $"enabled={result.npcProcessorsEnabled}, " +
                        $"iterated={result.npcProcessorsIterated}, expected={ExpectedNpcCount}.";
                    return false;
                }
                if (result.npcServerNavMeshDriversReady != ExpectedNpcCount ||
                    result.npcNavMeshAgentsReady != ExpectedNpcCount ||
                    result.npcNavMeshAgentBindingsReady != ExpectedNpcCount ||
                    result.npcNavMeshAgentsOnMesh != ExpectedNpcCount ||
                    result.npcNetworkFacingsReady != ExpectedNpcCount)
                {
                    result.message =
                        "Authority NPC NavMesh/facing binding is incomplete: " +
                        $"drivers={result.npcServerNavMeshDriversReady}, " +
                        $"agents={result.npcNavMeshAgentsReady}, " +
                        $"bindings={result.npcNavMeshAgentBindingsReady}, " +
                        $"onMesh={result.npcNavMeshAgentsOnMesh}, " +
                        $"facings={result.npcNetworkFacingsReady}, " +
                        $"expected={ExpectedNpcCount}.";
                    return false;
                }
                if (requireGameplayEvidence &&
                    result.npcAuthoredMovementDisplacedActors < 1)
                {
                    result.message =
                        "Authority has not observed the same NPC both enter authored GC2 " +
                        $"movement and displace at least {NpcMotionThreshold:F2}m " +
                        $"(authored={result.npcAuthoredMovementActors}, " +
                        $"displaced={result.npcDisplacedActors}, " +
                        $"intersection={result.npcAuthoredMovementDisplacedActors}, " +
                        $"maximum={result.npcMaximumDisplacement:F3}m).";
                    return false;
                }
            }
            else
            {
                if (result.npcSimulationAuthorities != 0 ||
                    result.npcAuthorityGatesRunning != 0 ||
                    result.npcProcessorsEnabled != 0)
                {
                    result.message =
                        "An observer is running authority-only NPC simulation: " +
                        $"simulation={result.npcSimulationAuthorities}, " +
                        $"gates={result.npcAuthorityGatesRunning}, " +
                        $"processors={result.npcProcessorsEnabled}.";
                    return false;
                }
                if (requireGameplayEvidence && result.npcDisplacedActors < 1)
                {
                    result.message =
                        "The observer has not received replicated NPC displacement of at least " +
                        $"{NpcMotionThreshold:F2}m (maximum={result.npcMaximumDisplacement:F3}m).";
                    return false;
                }
            }
            if (requireGameplayEvidence &&
                (result.npcCounterTelegraphActors < 1 ||
                 !result.npcCounterFacingObserved))
            {
                result.message =
                    "This peer has not observed a counter-telegraph NPC facing its authority-" +
                    "selected " +
                    $"player within {NpcCounterFacingToleranceDegrees:F0} degrees " +
                    $"(telegraphs={result.npcCounterTelegraphActors}, " +
                    $"faced={result.npcCounterTelegraphFacedActors}, " +
                    $"best={result.npcBestCounterFacingAngle:F2}).";
                return false;
            }

            if (requireNpcPresentation &&
                result.npcRendererSetsReady != ExpectedNpcCount)
            {
                result.message =
                    "NPC presentation setup is incomplete on this client-capable peer: " +
                    $"rendererSets={result.npcRendererSetsReady}, " +
                    $"expected={ExpectedNpcCount}.";
                return false;
            }

            if (requireGameplayEvidence && requireNpcPresentation &&
                (result.npcRendererSetsReady != ExpectedNpcCount ||
                 result.npcPresentationsEnabled != ExpectedNpcCount))
            {
                result.message =
                    "NPC presentation is not enabled on this client-capable peer: " +
                    $"rendererSets={result.npcRendererSetsReady}, " +
                    $"presented={result.npcPresentationsEnabled}, " +
                    $"expected={ExpectedNpcCount}.";
                return false;
            }

            if (result.localPlayerRequired)
            {
                if (!result.localPlayerReady ||
                    !result.localPlayerReceiverReady ||
                    !result.localPlayerRuntimeReady ||
                    !result.localPlayerRuntimeEnabled)
                {
                    result.message =
                        "The authenticated local player's Free Flow runtime is not ready: " +
                        $"player={result.localPlayerReady}, " +
                        $"receiver={result.localPlayerReceiverReady}, " +
                        $"runtime={result.localPlayerRuntimeReady}/" +
                        $"{result.localPlayerRuntimeEnabled}.";
                    return false;
                }
                if (requireGameplayEvidence && result.localCandidateCount < 1)
                {
                    result.message =
                        "The local Free Flow runtime found no candidate after an explicit " +
                        "RefreshCandidates call.";
                    return false;
                }
                if (requireGameplayEvidence &&
                    !result.localTargetIndicatorActive &&
                    !result.localFallbackIndicatorActive)
                {
                    result.message =
                        "The local Free Flow runtime found candidates but instantiated no " +
                        "active target or fallback indicator.";
                    return false;
                }
            }

            if (!ValidateTransportEvidence(result)) return false;

            result.passed = true;
            result.message = requireGameplayEvidence
                ? "Free Flow actors, authority-only Behavior execution, candidates, admission, " +
                  "and presentation satisfy the smoke contract."
                : "Free Flow authority, actors, transport admission, optional integration, " +
                  "and local runtime foundation are ready for independent gameplay probes.";
            return true;
        }

        /// <summary>
        /// Invokes the authenticated local player's real Free Flow <c>Attack</c> route once.
        /// The runtime performs its normal candidate refresh, target selection, GC2 combo/Skill
        /// transition, patched semantic request, authority validation, and observer broadcast.
        /// No smoke-only request or evidence counter is fabricated here.
        /// </summary>
        public static bool TryBeginLocalPlayerTargetedAttack(out string message)
        {
            message = string.Empty;
            if (s_LocalSemanticAttackIssued)
            {
                message = "The local semantic Free Flow attack was already issued.";
                return true;
            }

            NetworkTransportBridge bridge = ResolveRunningBridge();
            if (bridge == null || !bridge.IsRunning || !bridge.IsClient ||
                !bridge.IsLocalGameplayReady)
            {
                message = "The local client is not ready to issue the semantic Free Flow attack.";
                return false;
            }
            if (!bridge.TryGetLocalPlayer(out GameObject localPlayer) || localPlayer == null)
            {
                message = "The transport has no authenticated local player for the semantic attack.";
                return false;
            }

            NetworkCharacter actor = ResolveNetworkCharacter(localPlayer);
            if (!IsAuthenticatedPlayer(actor) || !actor.IsLocalPlayer || actor.NetworkId == 0 ||
                actor.Character == null)
            {
                message = "The resolved local character is not an authenticated PlayerOwned actor.";
                return false;
            }
            if (actor.Character.IsDead)
            {
                message = "The authenticated local player died before the semantic Free Flow attack.";
                return false;
            }
            if (actor.Character.Busy.IsBusy)
            {
                message = "Waiting for the authenticated local player to leave its current GC2 " +
                          "busy action before issuing the semantic Free Flow attack.";
                return false;
            }

            NetworkMeleeController meleeController =
                actor.GetComponent<NetworkMeleeController>();
            if (meleeController == null || meleeController.CurrentMeleeWeapon == null)
            {
                message = "The authenticated local player has no ready equipped Melee weapon.";
                return false;
            }
            if (!meleeController.IsLocalClient)
            {
                message =
                    "The authenticated local player reached Free Flow readiness, but its " +
                    "NetworkMeleeController is not initialized as the local client " +
                    $"(server={meleeController.IsServer}, actorRole={actor.Role}).";
                return false;
            }

            string diagnostic = string.Empty;
            Component runtime = FindOptionalComponent(
                actor.gameObject,
                FreeFlowRuntimeType,
                ref diagnostic);
            if (runtime is not Behaviour runtimeBehaviour || !runtimeBehaviour.enabled)
            {
                message = string.IsNullOrEmpty(diagnostic)
                    ? "The authenticated player's Free Flow runtime is not enabled."
                    : diagnostic;
                return false;
            }

            try
            {
                MethodInfo refresh = runtime.GetType().GetMethod(
                    "RefreshCandidates",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                MethodInfo attack = runtime.GetType().GetMethod(
                    "Attack",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (refresh == null || attack == null)
                {
                    message = "The optional Free Flow runtime has no public RefreshCandidates/Attack API.";
                    return false;
                }

                refresh.Invoke(runtime, null);
                object candidates = GetPropertyValue(runtime, "Candidates");
                if (!TrySelectNearestServerNpcCandidate(
                        actor,
                        candidates,
                        out NetworkCharacter target,
                        out GameObject targetObject))
                {
                    message = "The real Free Flow candidate list contains no attackable " +
                              "server-authoritative NPC.";
                    return false;
                }

                // Attack() remains the only action entry point. Priming a valid candidate keeps
                // the real target and indicator path active without bypassing Free Flow's
                // authored directional/no-target selection policy, GC2's Melee stance, or the
                // networking authority route. The exact selected target is learned from the
                // first authority broadcast produced after this invocation.
                actor.Character.Combat.Targets.AddCandidate(targetObject);
                actor.Character.Combat.Targets.Primary = targetObject;

                s_LocalSemanticAttackActorNetworkId = actor.NetworkId;
                s_LocalSemanticAttackTargetNetworkId = 0;
                s_LocalSemanticAttackSkillHash = 0;
                s_LocalSemanticAttackIssuedAt = Time.realtimeSinceStartup;
                s_LocalSemanticAttackActor = actor.transform;
                s_LocalSemanticAttackTarget = null;
                s_LocalSemanticAttackStartPosition = actor.transform.position;
                s_LocalSemanticAttackStartDistance = 0f;
                s_LocalSemanticAttackMinimumDistance = 0f;
                s_LocalSemanticAttackObservedEventStartIndex = s_ObservedSkillEvents.Count;
                s_LocalSemanticAttackStartDistances.Clear();
                for (int i = 0; i < s_Characters.Count; i++)
                {
                    NetworkCharacter candidate = s_Characters[i];
                    if (candidate == null || candidate.NetworkId == 0 ||
                        !s_NpcNetworkIds.Contains(candidate.NetworkId))
                    {
                        continue;
                    }

                    s_LocalSemanticAttackStartDistances[candidate.NetworkId] =
                        Vector3.Distance(
                            s_LocalSemanticAttackStartPosition,
                            candidate.transform.position);
                }
                s_LocalSemanticAttackIssued = true;

                attack.Invoke(runtime, null);
                message = $"Issued the real Free Flow Attack for actor={actor.NetworkId} " +
                          $"after priming candidate={target.NetworkId} distance=" +
                          $"{Vector3.Distance(actor.transform.position, target.transform.position):F3} " +
                          $"weapon='{meleeController.CurrentMeleeWeapon.name}' " +
                          $"phase={meleeController.LivePhase}; awaiting the exact " +
                          "authority-validated player-to-NPC tuple.";
                Debug.Log("[GC2 Free Flow Smoke] " + message);
                return true;
            }
            catch (Exception exception)
            {
                s_LocalSemanticAttackIssued = false;
                s_LocalSemanticAttackActorNetworkId = 0;
                s_LocalSemanticAttackTargetNetworkId = 0;
                s_LocalSemanticAttackActor = null;
                s_LocalSemanticAttackTarget = null;
                s_LocalSemanticAttackObservedEventStartIndex = 0;
                s_LocalSemanticAttackStartDistances.Clear();
                message = "The real Free Flow Attack invocation failed: " +
                          UnwrapException(exception).Message;
                return false;
            }
        }

        /// <summary>
        /// Requires the connected observer to receive the first authority broadcast produced by
        /// the real Attack invocation for the authenticated actor and an explicit server NPC.
        /// That broadcast becomes the exact actor/target/Skill tuple acknowledged to authority.
        /// Positional motion is recorded after a bounded sample window but is not a pass condition:
        /// NPC movement and already-close authored spawn positions make a distance-only assertion
        /// non-deterministic.
        /// </summary>
        public static bool TryValidateLocalPlayerTargetedAttack(
            Result result,
            out string message)
        {
            CaptureLocalSemanticAttackEvidence(result);
            // The monitor retains the last fully ready structural result after Attack(). Refresh
            // the generic event counters as well as the exact semantic tuple so the final artifact
            // proves that the observer callback delivered the broadcast.
            CaptureMeleeEvidence(result);
            if (!s_LocalSemanticAttackIssued)
            {
                message = "The semantic Free Flow attack has not been issued.";
                return false;
            }
            if (!TryFindLocalSemanticSkillEvent(out NetworkSkillBroadcast broadcast))
            {
                message = "Waiting for the real Attack invocation's player-to-NPC Skill " +
                          "broadcast on this observer.";
                return false;
            }

            AdoptLocalSemanticSkillEvent(broadcast);
            s_LocalSemanticAttackSkillHash = broadcast.SkillHash;
            CaptureLocalSemanticAttackEvidence(result);
            if (Time.realtimeSinceStartup - s_LocalSemanticAttackIssuedAt <
                SemanticAttackObservationSeconds)
            {
                message = "The exact Skill broadcast arrived; sampling its bounded motion window.";
                return false;
            }

            result.semanticPlayerAttackObserverReceived = true;
            message = "The observer received the exact authority-validated player-to-NPC " +
                      $"Skill actor={broadcast.CharacterNetworkId} " +
                      $"target={broadcast.TargetNetworkId} skill={broadcast.SkillHash}.";
            return true;
        }

        /// <summary>
        /// Requires this peer to have received the canonical counter Skill produced by one of the
        /// smoke's real assigned-local-player <c>Counter()</c> invocations. The runtime registry,
        /// rather than an asset-database-only assertion, must resolve that Skill as MotionWarp.
        /// </summary>
        public static bool TryValidateObservedCanonicalCounter(
            Result result,
            out string message)
        {
            CaptureMeleeEvidence(result);
            CaptureSemanticCounterEvidence(result);
            if (!TryFindCanonicalCounterSkillEvent(
                    s_ObservedSkillEvents,
                    out NetworkSkillBroadcast broadcast))
            {
                message = s_SemanticCounterLocalAttempts > 0
                    ? "Waiting for authority to validate and broadcast an assigned-player " +
                      $"{CanonicalCounterSkillName} action after " +
                      $"{s_SemanticCounterLocalAttempts} local attempt(s)."
                    : "Waiting for an assigned local player on either peer to invoke the real " +
                      $"Free Flow Counter() during an authority counter window.";
                return false;
            }

            if (!TryResolveCanonicalCounterMotionWarp(
                    broadcast.SkillHash,
                    out _,
                    out message))
            {
                return false;
            }

            AdoptSemanticCounterEvent(broadcast);
            CaptureSemanticCounterEvidence(result);
            result.semanticCounterRequired = true;
            result.semanticCounterActionIssued = true;
            result.semanticCounterObserverReceived = true;
            result.semanticCounterSkillMotionWarp = true;
            message = $"This peer received canonical MotionWarp counter actor=" +
                      $"{broadcast.CharacterNetworkId} target={broadcast.TargetNetworkId} " +
                      $"skill={broadcast.SkillHash}.";
            return true;
        }

        /// <summary>
        /// Requires the authority process to have validated the same exact Skill tuple written
        /// by the observer after it received the broadcast.
        /// </summary>
        public static bool TryValidateAuthorityPlayerTargetedAttack(
            Result result,
            out string message)
        {
            CaptureMeleeEvidence(result);
            if (!TryReadSemanticAttackMarker(out SemanticAttackMarker marker))
            {
                message = "Waiting for the observer's validated semantic-attack marker.";
                return false;
            }
            if (marker.actorNetworkId == 0 || marker.targetNetworkId == 0 ||
                marker.skillHash == 0)
            {
                message = "The observer semantic-attack marker has an invalid actor/target/Skill tuple.";
                return false;
            }
            NetworkCharacter actor = FindRegisteredCharacter(marker.actorNetworkId);
            if (!IsAuthenticatedPlayer(actor))
            {
                message = $"The semantic-attack marker actor {marker.actorNetworkId} is not " +
                          "a registered authenticated PlayerOwned character.";
                return false;
            }
            if (!s_NpcNetworkIds.Contains(marker.targetNetworkId))
            {
                message = $"The semantic-attack marker target {marker.targetNetworkId} is not " +
                          "a registered explicit server-authoritative NPC.";
                return false;
            }
            if (!TryFindSkillEvent(
                    s_ValidatedSkillEvents,
                    marker.actorNetworkId,
                    marker.targetNetworkId,
                    marker.skillHash,
                    out _))
            {
                message = "Authority has not validated the exact Skill tuple acknowledged by " +
                          "the observer. " + BuildSkillEventSummary(
                              s_ValidatedSkillEvents,
                              marker.actorNetworkId,
                              marker.targetNetworkId,
                              marker.skillHash);
                return false;
            }
            if (result.meleeSkillsValidated < 1)
            {
                message = "Authority matched the Skill event but its validated-Skill statistic " +
                          "did not advance.";
                return false;
            }

            result.semanticPlayerAttackRequired = true;
            result.semanticPlayerAttackIssued = true;
            result.semanticPlayerAttackAuthorityValidated = true;
            result.semanticPlayerAttackObserverReceived = true;
            result.semanticPlayerAttackActorNetworkId = marker.actorNetworkId;
            result.semanticPlayerAttackTargetNetworkId = marker.targetNetworkId;
            result.semanticPlayerAttackSkillHash = marker.skillHash;
            message = "Authority validated the exact player-to-NPC Skill that the observer " +
                      $"received (actor={marker.actorNetworkId}, target={marker.targetNetworkId}, " +
                      $"skill={marker.skillHash}).";
            return true;
        }

        /// <summary>
        /// Requires authority to have both validated and applied the exact canonical MotionWarp
        /// counter tuple acknowledged by the observer. Counter request validation consumes the
        /// authority-owned NPC revision, so this also proves the action was assigned legitimately.
        /// </summary>
        public static bool TryValidateAuthorityPlayerCounter(
            Result result,
            out string message)
        {
            CaptureMeleeEvidence(result);
            if (!TryReadSemanticAttackMarker(out SemanticAttackMarker marker))
            {
                message = "Waiting for the observer's validated semantic-action marker.";
                return false;
            }
            if (marker.counterActorNetworkId == 0 || marker.counterTargetNetworkId == 0 ||
                marker.counterSkillHash == 0 || !marker.counterSkillMotionWarp)
            {
                message = "The observer marker has no valid canonical MotionWarp counter tuple.";
                return false;
            }

            int canonicalHash = StableHashUtility.GetStableHash(CanonicalCounterSkillName);
            if (marker.counterSkillHash != canonicalHash)
            {
                message = $"Observer counter Skill hash={marker.counterSkillHash} does not match " +
                          $"canonical {CanonicalCounterSkillName} hash={canonicalHash}.";
                return false;
            }

            NetworkCharacter actor = FindRegisteredCharacter(marker.counterActorNetworkId);
            if (!IsAuthenticatedPlayer(actor))
            {
                message = $"Counter marker actor {marker.counterActorNetworkId} is not a " +
                          "registered authenticated PlayerOwned character.";
                return false;
            }
            if (!s_NpcNetworkIds.Contains(marker.counterTargetNetworkId))
            {
                message = $"Counter marker target {marker.counterTargetNetworkId} is not a " +
                          "registered explicit server-authoritative NPC.";
                return false;
            }
            if (!TryFindSkillEvent(
                    s_ValidatedSkillEvents,
                    marker.counterActorNetworkId,
                    marker.counterTargetNetworkId,
                    marker.counterSkillHash,
                    out _))
            {
                message = "Authority has not validated the exact canonical counter tuple " +
                          "acknowledged by the observer. " + BuildSkillEventSummary(
                              s_ValidatedSkillEvents,
                              marker.counterActorNetworkId,
                              marker.counterTargetNetworkId,
                              marker.counterSkillHash);
                return false;
            }
            // A Host/Shared authority also owns a local client and therefore receives its own
            // transport broadcast. A true dedicated server deliberately has no client endpoint,
            // so an ObserversRpc is not looped back through ReceiveSkillBroadcast there. For that
            // topology the exact OnSkillValidated event above is already downstream of
            // ProcessSkillRequest: the authoritative controller has played the Skill, committed
            // the Free Flow revision, and recorded its attack lease before the manager raises it.
            NetworkTransportBridge bridge = ResolveRunningBridge();
            bool requiresAuthorityClientLoopback = bridge == null || bridge.IsClient;
            if (requiresAuthorityClientLoopback &&
                !TryFindSkillEvent(
                    s_ObservedSkillEvents,
                    marker.counterActorNetworkId,
                    marker.counterTargetNetworkId,
                    marker.counterSkillHash,
                    out _))
            {
                message = "Authority validated the canonical counter but has not applied its " +
                          "Skill broadcast locally. " + BuildSkillEventSummary(
                              s_ObservedSkillEvents,
                              marker.counterActorNetworkId,
                              marker.counterTargetNetworkId,
                              marker.counterSkillHash);
                return false;
            }
            if (!TryResolveCanonicalCounterMotionWarp(
                    marker.counterSkillHash,
                    out _,
                    out message))
            {
                return false;
            }

            result.semanticCounterRequired = true;
            result.semanticCounterActionIssued = true;
            result.semanticCounterAuthorityValidated = true;
            result.semanticCounterObserverReceived = true;
            result.semanticCounterActorNetworkId = marker.counterActorNetworkId;
            result.semanticCounterTargetNetworkId = marker.counterTargetNetworkId;
            result.semanticCounterSkillHash = marker.counterSkillHash;
            result.semanticCounterSkillMotionWarp = true;
            message = "Authority validated and applied the exact canonical MotionWarp counter " +
                      $"received by the observer (actor={marker.counterActorNetworkId}, " +
                      $"target={marker.counterTargetNetworkId}, " +
                      $"skill={marker.counterSkillHash}).";
            return true;
        }

        public static bool IsObserverReadySignaled()
        {
            if (!TryGetArgument("--gc2-network-smoke-ready", out string path) ||
                string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            return File.Exists(Path.GetFullPath(path));
        }

        public static void SignalObserverReady(string role, Result result)
        {
            if (!TryGetArgument("--gc2-network-smoke-ready", out string path) ||
                string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (result == null ||
                !result.semanticPlayerAttackObserverReceived ||
                result.semanticPlayerAttackActorNetworkId == 0 ||
                result.semanticPlayerAttackTargetNetworkId == 0 ||
                result.semanticPlayerAttackSkillHash == 0 ||
                !result.semanticCounterObserverReceived ||
                !result.semanticCounterSkillMotionWarp ||
                result.semanticCounterActorNetworkId == 0 ||
                result.semanticCounterTargetNetworkId == 0 ||
                result.semanticCounterSkillHash == 0)
            {
                throw new InvalidOperationException(
                    "Cannot signal Free Flow observer completion without exact received attack " +
                    "and canonical MotionWarp counter tuples.");
            }

            // The marker below is the immutable cross-process contract. No later automatic
            // counter request may change the tuple being acknowledged or evict the earlier
            // targeted Attack from the bounded authority evidence ring.
            FreezeAssignedLocalPlayerCounter();
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllText(
                fullPath,
                JsonUtility.ToJson(new SemanticAttackMarker
                {
                    role = role ?? string.Empty,
                    actorNetworkId = result.semanticPlayerAttackActorNetworkId,
                    targetNetworkId = result.semanticPlayerAttackTargetNetworkId,
                    skillHash = result.semanticPlayerAttackSkillHash,
                    counterActorNetworkId = result.semanticCounterActorNetworkId,
                    counterTargetNetworkId = result.semanticCounterTargetNetworkId,
                    counterSkillHash = result.semanticCounterSkillHash,
                    counterSkillMotionWarp = result.semanticCounterSkillMotionWarp
                }, true));
        }

        /// <summary>
        /// Returns true only after authority has validated the observer marker while its
        /// authenticated player actor is still registered, and acknowledged the exact same
        /// Attack and canonical Counter tuples. This replaces a timing-only disconnect delay.
        /// </summary>
        public static bool IsAuthorityValidationSignaled()
        {
            if (!TryGetArgument("--gc2-network-smoke-ready", out string path) ||
                string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            string observerPath = Path.GetFullPath(path);
            if (!TryReadSemanticMarker(
                    observerPath,
                    out SemanticAttackMarker observerMarker) ||
                !TryReadSemanticMarker(
                    observerPath + AuthorityValidatedMarkerSuffix,
                    out SemanticAttackMarker authorityMarker))
            {
                return false;
            }

            return observerMarker.actorNetworkId != 0 &&
                   observerMarker.targetNetworkId != 0 &&
                   observerMarker.skillHash != 0 &&
                   observerMarker.counterActorNetworkId != 0 &&
                   observerMarker.counterTargetNetworkId != 0 &&
                   observerMarker.counterSkillHash != 0 &&
                   observerMarker.counterSkillMotionWarp &&
                   authorityMarker.actorNetworkId == observerMarker.actorNetworkId &&
                   authorityMarker.targetNetworkId == observerMarker.targetNetworkId &&
                   authorityMarker.skillHash == observerMarker.skillHash &&
                   authorityMarker.counterActorNetworkId ==
                   observerMarker.counterActorNetworkId &&
                   authorityMarker.counterTargetNetworkId ==
                   observerMarker.counterTargetNetworkId &&
                   authorityMarker.counterSkillHash == observerMarker.counterSkillHash &&
                   authorityMarker.counterSkillMotionWarp;
        }

        /// <summary>
        /// Writes authority acknowledgement only after both exact semantic tuples passed the
        /// live authenticated-owner, explicit-NPC, validated-event, and MotionWarp checks.
        /// </summary>
        public static void SignalAuthorityValidated(string role, Result result)
        {
            if (!TryGetArgument("--gc2-network-smoke-ready", out string path) ||
                string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (result == null ||
                !result.semanticPlayerAttackAuthorityValidated ||
                result.semanticPlayerAttackActorNetworkId == 0 ||
                result.semanticPlayerAttackTargetNetworkId == 0 ||
                result.semanticPlayerAttackSkillHash == 0 ||
                !result.semanticCounterAuthorityValidated ||
                !result.semanticCounterSkillMotionWarp ||
                result.semanticCounterActorNetworkId == 0 ||
                result.semanticCounterTargetNetworkId == 0 ||
                result.semanticCounterSkillHash == 0)
            {
                throw new InvalidOperationException(
                    "Cannot acknowledge Free Flow authority completion without exact " +
                    "authority-validated Attack and canonical MotionWarp Counter tuples.");
            }

            string fullPath = Path.GetFullPath(path) + AuthorityValidatedMarkerSuffix;
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
            File.WriteAllText(
                fullPath,
                JsonUtility.ToJson(new SemanticAttackMarker
                {
                    role = role ?? string.Empty,
                    actorNetworkId = result.semanticPlayerAttackActorNetworkId,
                    targetNetworkId = result.semanticPlayerAttackTargetNetworkId,
                    skillHash = result.semanticPlayerAttackSkillHash,
                    counterActorNetworkId = result.semanticCounterActorNetworkId,
                    counterTargetNetworkId = result.semanticCounterTargetNetworkId,
                    counterSkillHash = result.semanticCounterSkillHash,
                    counterSkillMotionWarp = result.semanticCounterSkillMotionWarp
                }, true));
        }

        public static void Finish(Result result, int exitCode)
        {
            result ??= new Result { message = "No Free Flow smoke result was produced." };
            result.passed = exitCode == 0;
            if (TryGetArgument("--gc2-network-smoke-result", out string path) &&
                !string.IsNullOrWhiteSpace(path))
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, JsonUtility.ToJson(result, true));
            }

            string message =
                $"[GC2 Free Flow Smoke] role={result.role} phase={result.phase} " +
                $"passed={result.passed} humans={result.authenticatedHumans}/" +
                $"{result.expectedHumans} npcs={result.explicitServerNpcs}/" +
                $"{result.expectedNpcs} receivers={result.npcReceiversReady} " +
                $"processors={result.npcProcessorsEnabled}/{result.npcProcessorsIterated} " +
                $"navmesh={result.npcNavMeshAgentBindingsReady}/" +
                $"{result.npcNavMeshAgentsOnMesh} motion=" +
                $"{result.npcAuthoredMovementDisplacedActors}/" +
                $"{result.npcDisplacedActors}/{result.npcMaximumDisplacement:F3}m " +
                $"telegraphFacing={result.npcCounterTelegraphFacedActors}/" +
                $"{result.npcCounterTelegraphActors} " +
                $"candidates={result.localCandidateCount} " +
                $"semanticSkill={result.semanticPlayerAttackActorNetworkId}->" +
                $"{result.semanticPlayerAttackTargetNetworkId}/" +
                $"{result.semanticPlayerAttackSkillHash} counterSkill=" +
                $"{result.semanticCounterActorNetworkId}->" +
                $"{result.semanticCounterTargetNetworkId}/" +
                $"{result.semanticCounterSkillHash}/" +
                $"{result.semanticCounterSkillMotionWarp} " +
                $"admission={result.fusionCurrentlyAdmitted}/" +
                $"{result.fusionCurrentlySpawned}: {result.message}";
            if (exitCode == 0) Debug.Log(message);
            else Debug.LogError(message);
            Application.Quit(exitCode);
        }

        private static void CaptureLocalPlayer(
            NetworkTransportBridge bridge,
            Result result,
            ref string optionalDiagnostic)
        {
            result.localPlayerRequired = bridge.IsClient;
            if (!bridge.IsClient) return;
            if (!bridge.TryGetLocalPlayer(out GameObject localPlayer) || localPlayer == null) return;

            NetworkCharacter character = ResolveNetworkCharacter(localPlayer);
            if (character == null ||
                character.ActorType != NetworkCharacterActorType.PlayerOwned ||
                !character.IsLocalPlayer ||
                !character.HasAuthenticatedPlayerOwner)
            {
                return;
            }

            result.localPlayerReady = true;
            result.localPlayerNetworkId = character.NetworkId;
            NetworkFreeFlowCombatAdapter adapter =
                character.GetComponent<NetworkFreeFlowCombatAdapter>();
            result.localPlayerReceiverReady = adapter != null && adapter.HasReceiver;

            Component runtime = FindOptionalComponent(
                character.gameObject,
                FreeFlowRuntimeType,
                ref optionalDiagnostic);
            if (runtime == null) return;

            result.localPlayerRuntimeReady = true;
            result.localPlayerRuntimeEnabled =
                runtime is Behaviour behaviour && behaviour.enabled;
            if (!result.localPlayerRuntimeEnabled) return;

            try
            {
                runtime.GetType().GetMethod(
                        "RefreshCandidates",
                        BindingFlags.Instance | BindingFlags.Public,
                        null,
                        Type.EmptyTypes,
                        null)
                    ?.Invoke(runtime, null);
                object candidates = GetPropertyValue(runtime, "Candidates");
                result.localCandidateCount = GetCollectionCount(candidates);
                if (!s_LocalSemanticAttackIssued &&
                    result.localCandidateCount > 0 &&
                    character.Character != null &&
                    TryGetFirstItem(candidates, out object firstCandidate) &&
                    GetPropertyValue(firstCandidate, "TargetObject") is GameObject target)
                {
                    // Selecting a local GC2 combat target is presentation-only. It exercises
                    // the real Free Flow indicator path without issuing a melee request or
                    // mutating authority-owned gameplay state.
                    character.Character.Combat.Targets.AddCandidate(target);
                    character.Character.Combat.Targets.Primary = target;
                }

                runtime.GetType().GetMethod(
                        "UpdateTargetIndicator",
                        InstanceMembers,
                        null,
                        Type.EmptyTypes,
                        null)
                    ?.Invoke(runtime, null);
                runtime.GetType().GetMethod(
                        "UpdateFallbackCandidateIndicator",
                        InstanceMembers,
                        null,
                        Type.EmptyTypes,
                        null)
                    ?.Invoke(runtime, null);
                result.localTargetIndicatorActive =
                    IsGameObjectFieldActive(runtime, "m_TargetIndicatorInstance");
                result.localFallbackIndicatorActive =
                    IsGameObjectFieldActive(runtime, "m_FallbackCandidateIndicatorInstance");
            }
            catch (Exception exception)
            {
                AppendDiagnostic(
                    ref optionalDiagnostic,
                    "Local Free Flow runtime inspection failed: " +
                    UnwrapException(exception).Message);
            }
        }

        private static void CaptureMeleeEvidence(Result result)
        {
            BeginSemanticObservation();
            foreach (NetworkMeleeManager manager in s_ObservedMeleeManagers)
            {
                if (manager == null) continue;
                NetworkMeleeManager.MeleeNetworkStats stats = manager.Stats;
                result.meleeSkillsValidated = Mathf.Max(
                    result.meleeSkillsValidated,
                    stats.SkillsValidated);
                result.meleeHitsValidated = Mathf.Max(
                    result.meleeHitsValidated,
                    stats.HitsValidated);
            }

            result.observedNpcSkillEvents = s_ObservedNpcSkillEvents;
            result.observedPlayerSkillEvents = s_ObservedPlayerSkillEvents;
        }

        private static void ObserveMeleeManager(NetworkMeleeManager manager)
        {
            if (manager == null || !s_ObservedMeleeManagers.Add(manager)) return;
            manager.OnSkillValidated += OnSkillValidated;
        }

        private static void OnSkillValidated(NetworkSkillBroadcast broadcast)
        {
            AppendRecentSkillEvent(s_ValidatedSkillEvents, broadcast);
        }

        private static void ObserveMeleeControllers()
        {
            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter character = s_Characters[i];
                if (character == null) continue;
                NetworkMeleeController controller =
                    character.GetComponent<NetworkMeleeController>();
                if (controller == null || !s_ObservedMeleeControllers.Add(controller)) continue;
                controller.OnSkillExecuted += OnSkillExecuted;
            }
        }

        private static void OnSkillExecuted(NetworkSkillBroadcast broadcast)
        {
            AppendRecentSkillEvent(s_ObservedSkillEvents, broadcast);
            if (s_NpcNetworkIds.Contains(broadcast.CharacterNetworkId))
                s_ObservedNpcSkillEvents++;
            else
                s_ObservedPlayerSkillEvents++;
        }

        private static void CaptureLocalSemanticAttackEvidence(Result result)
        {
            if (result == null || !s_LocalSemanticAttackIssued) return;

            if (TryFindLocalSemanticSkillEvent(out NetworkSkillBroadcast observed))
            {
                AdoptLocalSemanticSkillEvent(observed);
                result.semanticPlayerAttackObserverReceived = true;
            }

            if (s_LocalSemanticAttackActor != null)
            {
                result.semanticPlayerAttackMotionDistance = Vector3.Distance(
                    s_LocalSemanticAttackStartPosition,
                    s_LocalSemanticAttackActor.position);
            }

            if (s_LocalSemanticAttackActor != null && s_LocalSemanticAttackTarget != null)
            {
                float distance = Vector3.Distance(
                    s_LocalSemanticAttackActor.position,
                    s_LocalSemanticAttackTarget.position);
                s_LocalSemanticAttackMinimumDistance = Mathf.Min(
                    s_LocalSemanticAttackMinimumDistance,
                    distance);
            }

            result.semanticPlayerAttackRequired = true;
            result.semanticPlayerAttackIssued = true;
            result.semanticPlayerAttackActorNetworkId = s_LocalSemanticAttackActorNetworkId;
            result.semanticPlayerAttackTargetNetworkId = s_LocalSemanticAttackTargetNetworkId;
            result.semanticPlayerAttackSkillHash = s_LocalSemanticAttackSkillHash;
            result.semanticPlayerAttackStartDistance = s_LocalSemanticAttackStartDistance;
            result.semanticPlayerAttackMinimumDistance = s_LocalSemanticAttackMinimumDistance;
            result.semanticPlayerAttackRelativeProgress = Mathf.Max(
                0f,
                s_LocalSemanticAttackStartDistance - s_LocalSemanticAttackMinimumDistance);
            result.semanticPlayerAttackMotionObserved =
                result.semanticPlayerAttackMotionDistance >= SemanticAttackMotionThreshold ||
                result.semanticPlayerAttackRelativeProgress >= SemanticAttackMotionThreshold;
        }

        private static void CaptureNpcMotionEvidence(Result result)
        {
            result.npcBestCounterFacingAngle = -1f;
            foreach (NpcMotionEvidence evidence in s_NpcMotionEvidence.Values)
            {
                if (evidence == null || evidence.actor == null) continue;
                if (evidence.authoredMovementObserved) result.npcAuthoredMovementActors++;
                if (evidence.maximumDisplacement >= NpcMotionThreshold)
                {
                    result.npcDisplacedActors++;
                    if (evidence.authoredMovementObserved)
                    {
                        // Count the intersection, not two unrelated NPC totals. This proves one
                        // authority actor both received a real authored GC2 move command and
                        // actually changed position beyond the noise threshold.
                        result.npcAuthoredMovementDisplacedActors++;
                    }
                }
                result.npcMaximumDisplacement = Mathf.Max(
                    result.npcMaximumDisplacement,
                    evidence.maximumDisplacement);
                if (evidence.counterTelegraphObserved) result.npcCounterTelegraphActors++;
                if (evidence.counterFacingObserved) result.npcCounterTelegraphFacedActors++;
                if (!float.IsPositiveInfinity(evidence.bestCounterFacingAngle))
                {
                    result.npcBestCounterFacingAngle = result.npcBestCounterFacingAngle < 0f
                        ? evidence.bestCounterFacingAngle
                        : Mathf.Min(
                            result.npcBestCounterFacingAngle,
                            evidence.bestCounterFacingAngle);
                }
            }

            result.npcMotionObserved = result.npcDisplacedActors > 0;
            result.npcCounterFacingObserved = result.npcCounterTelegraphFacedActors > 0;
        }

        private static void CaptureSemanticCounterEvidence(Result result)
        {
            if (result == null) return;
            result.semanticCounterRequired = true;
            result.semanticCounterLocalAttempts = s_SemanticCounterLocalAttempts;
            result.semanticCounterActionIssued = s_SemanticCounterActionIssued;
            result.semanticCounterActorNetworkId = s_SemanticCounterActorNetworkId;
            result.semanticCounterTargetNetworkId = s_SemanticCounterTargetNetworkId;
            result.semanticCounterSkillHash = s_SemanticCounterSkillHash;

            if (!TryFindCanonicalCounterSkillEvent(
                    s_ObservedSkillEvents,
                    out NetworkSkillBroadcast broadcast) ||
                !TryResolveCanonicalCounterMotionWarp(
                    broadcast.SkillHash,
                    out _,
                    out _))
            {
                return;
            }

            AdoptSemanticCounterEvent(broadcast);
            result.semanticCounterActionIssued = true;
            result.semanticCounterObserverReceived = true;
            result.semanticCounterActorNetworkId = broadcast.CharacterNetworkId;
            result.semanticCounterTargetNetworkId = broadcast.TargetNetworkId;
            result.semanticCounterSkillHash = broadcast.SkillHash;
            result.semanticCounterSkillMotionWarp = true;
        }

        private static bool TrySelectNearestServerNpcCandidate(
            NetworkCharacter actor,
            object candidates,
            out NetworkCharacter target,
            out GameObject targetObject)
        {
            target = null;
            targetObject = null;
            if (actor == null || candidates is not IEnumerable enumerable) return false;

            float nearestSqrDistance = float.PositiveInfinity;
            foreach (object candidate in enumerable)
            {
                if (candidate == null) continue;
                object attackableValue = GetPropertyValue(candidate, "IsAttackable");
                if (attackableValue is bool attackable && !attackable) continue;
                if (GetPropertyValue(candidate, "TargetObject") is not GameObject candidateObject ||
                    candidateObject == null)
                {
                    continue;
                }

                NetworkCharacter candidateCharacter = ResolveNetworkCharacter(candidateObject);
                if (candidateCharacter == null || candidateCharacter.NetworkId == 0 ||
                    candidateCharacter.ActorType != NetworkCharacterActorType.NPC ||
                    !candidateCharacter.IsServerAuthoritativeNPC)
                {
                    continue;
                }

                float sqrDistance = (candidateCharacter.transform.position -
                                     actor.transform.position).sqrMagnitude;
                bool closer = sqrDistance < nearestSqrDistance - 0.0001f;
                bool stableTie = Mathf.Abs(sqrDistance - nearestSqrDistance) <= 0.0001f &&
                                 (target == null ||
                                  candidateCharacter.NetworkId < target.NetworkId);
                if (!closer && !stableTie) continue;

                nearestSqrDistance = sqrDistance;
                target = candidateCharacter;
                targetObject = candidateObject;
            }

            return target != null && targetObject != null;
        }

        private static void AppendRecentSkillEvent(
            List<NetworkSkillBroadcast> events,
            NetworkSkillBroadcast broadcast)
        {
            if (events == null) return;
            if (events.Count >= MaximumRecentSkillEvents)
            {
                events.RemoveAt(0);
                if (ReferenceEquals(events, s_ObservedSkillEvents) &&
                    s_LocalSemanticAttackObservedEventStartIndex > 0)
                {
                    s_LocalSemanticAttackObservedEventStartIndex--;
                }
            }
            events.Add(broadcast);
        }

        private static bool TryFindLocalSemanticSkillEvent(
            out NetworkSkillBroadcast match)
        {
            int startIndex = Mathf.Clamp(
                s_LocalSemanticAttackObservedEventStartIndex,
                0,
                s_ObservedSkillEvents.Count);
            for (int i = startIndex; i < s_ObservedSkillEvents.Count; i++)
            {
                NetworkSkillBroadcast candidate = s_ObservedSkillEvents[i];
                if (candidate.CharacterNetworkId != s_LocalSemanticAttackActorNetworkId ||
                    candidate.SkillHash == 0 || candidate.TargetNetworkId == 0 ||
                    !IsLocalSemanticNpcTarget(candidate.TargetNetworkId))
                {
                    continue;
                }

                match = candidate;
                return true;
            }

            match = default;
            return false;
        }

        private static void AdoptLocalSemanticSkillEvent(NetworkSkillBroadcast broadcast)
        {
            if (broadcast.CharacterNetworkId != s_LocalSemanticAttackActorNetworkId ||
                broadcast.TargetNetworkId == 0 || broadcast.SkillHash == 0 ||
                !IsLocalSemanticNpcTarget(broadcast.TargetNetworkId))
            {
                return;
            }

            s_LocalSemanticAttackTargetNetworkId = broadcast.TargetNetworkId;
            s_LocalSemanticAttackSkillHash = broadcast.SkillHash;
            NetworkCharacter target = FindRegisteredCharacter(broadcast.TargetNetworkId);
            s_LocalSemanticAttackTarget = target != null ? target.transform : null;

            if (s_LocalSemanticAttackStartDistances.TryGetValue(
                    broadcast.TargetNetworkId,
                    out float startDistance))
            {
                s_LocalSemanticAttackStartDistance = startDistance;
            }
            else if (s_LocalSemanticAttackTarget != null)
            {
                s_LocalSemanticAttackStartDistance = Vector3.Distance(
                    s_LocalSemanticAttackStartPosition,
                    s_LocalSemanticAttackTarget.position);
            }

            if (s_LocalSemanticAttackMinimumDistance <= 0f)
            {
                s_LocalSemanticAttackMinimumDistance = s_LocalSemanticAttackStartDistance;
            }
        }

        private static bool IsLocalSemanticNpcTarget(uint networkId)
        {
            if (networkId == 0) return false;

            // Capture the complete explicit-NPC set immediately before Attack(). A transient
            // post-action readiness failure may rebuild or clear the current registry view, but
            // it must not invalidate the exact authority broadcast produced by that invocation.
            return s_LocalSemanticAttackStartDistances.ContainsKey(networkId) ||
                   s_NpcNetworkIds.Contains(networkId);
        }

        private static bool TryFindSkillEvent(
            List<NetworkSkillBroadcast> events,
            uint actorNetworkId,
            uint targetNetworkId,
            int requiredSkillHash,
            out NetworkSkillBroadcast match)
        {
            for (int i = events.Count - 1; i >= 0; i--)
            {
                NetworkSkillBroadcast candidate = events[i];
                if (candidate.CharacterNetworkId != actorNetworkId ||
                    candidate.TargetNetworkId != targetNetworkId ||
                    candidate.SkillHash == 0 ||
                    (requiredSkillHash != 0 && candidate.SkillHash != requiredSkillHash))
                {
                    continue;
                }

                match = candidate;
                return true;
            }

            match = default;
            return false;
        }

        private static string BuildSkillEventSummary(
            List<NetworkSkillBroadcast> events,
            uint actorNetworkId,
            uint targetNetworkId,
            int skillHash)
        {
            int aliveManagers = 0;
            foreach (NetworkMeleeManager manager in s_ObservedMeleeManagers)
            {
                if (manager != null) aliveManagers++;
            }

            bool singletonObserved = NetworkMeleeManager.Instance != null &&
                                     s_ObservedMeleeManagers.Contains(
                                         NetworkMeleeManager.Instance);
            int count = events?.Count ?? 0;
            int start = Mathf.Max(0, count - MaximumDiagnosticSkillEvents);
            var tuples = new List<string>(Mathf.Min(MaximumDiagnosticSkillEvents, count));
            for (int i = start; i < count; i++)
            {
                NetworkSkillBroadcast candidate = events[i];
                tuples.Add(
                    $"{candidate.CharacterNetworkId}->{candidate.TargetNetworkId}/" +
                    $"{candidate.SkillHash}@{candidate.ServerTimestamp:F3}");
            }

            return $"expected={actorNetworkId}->{targetNetworkId}/{skillHash}; " +
                   $"captured={count}/{MaximumRecentSkillEvents}; " +
                   $"managers={aliveManagers}/{s_ObservedMeleeManagers.Count}; " +
                   $"singletonObserved={singletonObserved}; " +
                   $"recent=[{string.Join(",", tuples)}].";
        }

        private static bool TryFindCanonicalCounterSkillEvent(
            List<NetworkSkillBroadcast> events,
            out NetworkSkillBroadcast match)
        {
            int canonicalHash = StableHashUtility.GetStableHash(CanonicalCounterSkillName);
            for (int i = events.Count - 1; i >= 0; i--)
            {
                NetworkSkillBroadcast candidate = events[i];
                if (candidate.SkillHash != canonicalHash ||
                    candidate.CharacterNetworkId == 0 ||
                    candidate.TargetNetworkId == 0 ||
                    !s_NpcNetworkIds.Contains(candidate.TargetNetworkId) ||
                    !IsAuthenticatedPlayer(
                        FindRegisteredCharacter(candidate.CharacterNetworkId)))
                {
                    continue;
                }

                match = candidate;
                return true;
            }

            match = default;
            return false;
        }

        private static void AdoptSemanticCounterEvent(NetworkSkillBroadcast broadcast)
        {
            s_SemanticCounterActionIssued = true;
            s_SemanticCounterActorNetworkId = broadcast.CharacterNetworkId;
            s_SemanticCounterTargetNetworkId = broadcast.TargetNetworkId;
            s_SemanticCounterSkillHash = broadcast.SkillHash;
        }

        private static bool TryResolveCanonicalCounterMotionWarp(
            int skillHash,
            out Skill skill,
            out string message)
        {
            skill = null;
            int canonicalHash = StableHashUtility.GetStableHash(CanonicalCounterSkillName);
            if (skillHash != canonicalHash)
            {
                message = $"Observed counter Skill hash={skillHash} does not match canonical " +
                          $"{CanonicalCounterSkillName} hash={canonicalHash}.";
                return false;
            }

            skill = NetworkMeleeManager.GetSkillByHash(skillHash);
            if (skill == null)
            {
                message = $"Runtime Melee registry has not resolved {CanonicalCounterSkillName} " +
                          $"for hash={skillHash}.";
                return false;
            }
            if (!string.Equals(skill.name, CanonicalCounterSkillName, StringComparison.Ordinal))
            {
                message = $"Runtime counter Skill name='{skill.name}' does not match " +
                          $"'{CanonicalCounterSkillName}'.";
                return false;
            }
            if (skill.Motion != MeleeMotion.MotionWarp)
            {
                message = $"Runtime {CanonicalCounterSkillName} Motion={skill.Motion}; " +
                          $"expected {MeleeMotion.MotionWarp}.";
                return false;
            }

            message = $"Runtime {CanonicalCounterSkillName} resolves with MotionWarp.";
            return true;
        }

        private static bool TryReadSemanticAttackMarker(out SemanticAttackMarker marker)
        {
            marker = null;
            if (!TryGetArgument("--gc2-network-smoke-ready", out string path) ||
                string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            return TryReadSemanticMarker(Path.GetFullPath(path), out marker);
        }

        private static bool TryReadSemanticMarker(
            string fullPath,
            out SemanticAttackMarker marker)
        {
            marker = null;
            if (!File.Exists(fullPath)) return false;
            try
            {
                marker = JsonUtility.FromJson<SemanticAttackMarker>(
                    File.ReadAllText(fullPath));
                return marker != null;
            }
            catch (Exception)
            {
                marker = null;
                return false;
            }
        }

        private static void ApplyTransportEvidence(Result result, TransportEvidence evidence)
        {
            if (evidence == null) return;
            result.fusionAdmissionRequired = evidence.admissionRequired;
            result.fusionLocalGameplayReadyObserved = evidence.localGameplayReadyObserved;
            result.fusionRemoteGameplayReadyRequired = evidence.remoteGameplayReadyRequired;
            result.fusionRemoteGameplayReadyObserved = evidence.remoteGameplayReadyObserved;
            result.fusionSpawnedAtFirstLocalReady = evidence.spawnedAtFirstLocalReady;
            result.fusionAdmittedAtFirstLocalReady = evidence.admittedAtFirstLocalReady;
            result.fusionSpawnedAtFirstRemoteReady = evidence.spawnedAtFirstRemoteReady;
            result.fusionAdmittedAtFirstRemoteReady = evidence.admittedAtFirstRemoteReady;
            result.fusionCurrentlySpawned = evidence.currentlySpawned;
            result.fusionCurrentlyAdmitted = evidence.currentlyAdmitted;
            result.transportDiagnostic = evidence.diagnostic;
        }

        private static bool ValidateTransportEvidence(Result result)
        {
            if (!result.fusionAdmissionRequired) return true;
            if (!result.fusionLocalGameplayReadyObserved)
            {
                result.message =
                    "Fusion local gameplay readiness has not been observed; " +
                    result.transportDiagnostic;
                return false;
            }
            if (result.fusionSpawnedAtFirstLocalReady != ExpectedNpcCount ||
                result.fusionAdmittedAtFirstLocalReady != ExpectedNpcCount)
            {
                result.message =
                    "Fusion did not have all five NPCs spawned/admitted at first local " +
                    $"gameplay readiness ({result.fusionSpawnedAtFirstLocalReady}/" +
                    $"{result.fusionAdmittedAtFirstLocalReady}); " +
                    result.transportDiagnostic;
                return false;
            }
            if (result.fusionRemoteGameplayReadyRequired &&
                !result.fusionRemoteGameplayReadyObserved)
            {
                result.message =
                    "Fusion authority has not observed a ready remote client; " +
                    result.transportDiagnostic;
                return false;
            }
            if (result.fusionRemoteGameplayReadyRequired &&
                (result.fusionSpawnedAtFirstRemoteReady != ExpectedNpcCount ||
                 result.fusionAdmittedAtFirstRemoteReady != ExpectedNpcCount))
            {
                result.message =
                    "Fusion did not have all five NPCs spawned/admitted when the first remote " +
                    $"client became ready ({result.fusionSpawnedAtFirstRemoteReady}/" +
                    $"{result.fusionAdmittedAtFirstRemoteReady}); " +
                    result.transportDiagnostic;
                return false;
            }
            if (result.fusionCurrentlySpawned != ExpectedNpcCount ||
                result.fusionCurrentlyAdmitted != ExpectedNpcCount)
            {
                result.message =
                    "Fusion NPC admission is not currently complete " +
                    $"({result.fusionCurrentlySpawned}/" +
                    $"{result.fusionCurrentlyAdmitted}/{ExpectedNpcCount}); " +
                    result.transportDiagnostic;
                return false;
            }

            return true;
        }

        private static NetworkCharacter ResolveNetworkCharacter(GameObject gameObject)
        {
            if (gameObject == null) return null;
            return gameObject.GetComponent<NetworkCharacter>() ??
                   gameObject.GetComponentInParent<NetworkCharacter>() ??
                   gameObject.GetComponentInChildren<NetworkCharacter>(true);
        }

        private static NetworkCharacter FindRegisteredCharacter(uint networkId)
        {
            if (networkId == 0) return null;
            for (int i = 0; i < s_Characters.Count; i++)
            {
                NetworkCharacter character = s_Characters[i];
                if (character != null && character.NetworkId == networkId) return character;
            }

            return null;
        }

        private static bool IsAuthenticatedPlayer(GameObject gameObject)
        {
            NetworkCharacter character = ResolveNetworkCharacter(gameObject);
            return IsAuthenticatedPlayer(character);
        }

        private static bool IsAuthenticatedPlayer(NetworkCharacter character)
        {
            return character != null &&
                   character.ActorType == NetworkCharacterActorType.PlayerOwned &&
                   character.IsPlayerOwnedActor &&
                   character.HasAuthenticatedPlayerOwner;
        }

        private static bool HasActiveAuthorityRoot(NetworkCharacterAuthorityGate gate)
        {
            GameObject[] roots = gate.AuthorityOnlyRoots;
            int count = roots?.Length ?? 0;
            for (int i = 0; i < count; i++)
            {
                GameObject root = roots[i];
                if (root != null && root.activeInHierarchy) return true;
            }

            return false;
        }

        private static bool HasEnabledPresentation(Renderer[] renderers)
        {
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null &&
                    renderer.enabled &&
                    renderer.gameObject.activeInHierarchy &&
                    !renderer.forceRenderingOff)
                {
                    return true;
                }
            }

            return false;
        }

        private static Component FindOptionalComponent(
            GameObject root,
            string assemblyQualifiedType,
            ref string diagnostic)
        {
            if (root == null) return null;
            Type type = Type.GetType(assemblyQualifiedType, false);
            if (type == null)
            {
                AppendDiagnostic(
                    ref diagnostic,
                    $"Optional type '{assemblyQualifiedType}' could not be resolved.");
                return null;
            }

            return root.GetComponent(type) as Component ??
                   root.GetComponentInChildren(type, true) as Component;
        }

        private static object GetPropertyValue(object instance, string name)
        {
            return instance?.GetType().GetProperty(name, InstanceMembers)?.GetValue(instance);
        }

        private static bool TryReadFloatField(Component component, string name, out float value)
        {
            value = 0f;
            object raw = component?.GetType().GetField(name, InstanceMembers)?.GetValue(component);
            if (raw is not IConvertible convertible) return false;
            try
            {
                value = convertible.ToSingle(System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int GetCollectionCount(object collection)
        {
            if (collection == null) return 0;
            if (collection is ICollection nonGeneric) return nonGeneric.Count;
            object count = collection.GetType().GetProperty("Count", InstanceMembers)
                ?.GetValue(collection);
            return count is IConvertible convertible ? convertible.ToInt32(null) : 0;
        }

        private static bool TryGetFirstItem(object collection, out object item)
        {
            item = null;
            if (collection is not IEnumerable enumerable) return false;
            IEnumerator enumerator = enumerable.GetEnumerator();
            try
            {
                if (!enumerator.MoveNext()) return false;
                item = enumerator.Current;
                return item != null;
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        private static bool IsGameObjectFieldActive(Component component, string name)
        {
            object value = component?.GetType().GetField(name, InstanceMembers)?.GetValue(component);
            return value is GameObject gameObject && gameObject.activeInHierarchy;
        }

        private static void AppendDiagnostic(ref string diagnostic, string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (diagnostic.IndexOf(message, StringComparison.Ordinal) >= 0) return;
            diagnostic = string.IsNullOrEmpty(diagnostic)
                ? message
                : diagnostic + " | " + message;
        }

        private static Exception UnwrapException(Exception exception)
        {
            return exception is TargetInvocationException { InnerException: not null } target
                ? target.InnerException
                : exception;
        }

        private static NetworkTransportBridge ResolveRunningBridge()
        {
            NetworkTransportBridge active = NetworkTransportBridge.Active;
            if (active != null && active.IsRunning) return active;

            NetworkTransportBridge[] bridges =
                UnityObjectSearch.FindAll<NetworkTransportBridge>(FindObjectsInactive.Include);
            for (int i = 0; i < bridges.Length; i++)
            {
                if (bridges[i] != null && bridges[i].IsRunning) return bridges[i];
            }

            return active;
        }
    }
}
