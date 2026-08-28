#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using Fusion;
using Fusion.Photon.Realtime;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Traversal;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.Fusion
{
    /// <summary>
    /// Opt-in Host and Shared presentation regression for the exact vertical center column of
    /// Ledge_Hold_Climb fixtures in the installed Fusion Climb demo. The tested logical owner
    /// enters the lower hold and uses GC2's patched TryJump route to reach the middle and upper
    /// holds while the opposite peer measures the committed transport presentation. It is
    /// excluded from non-development players.
    /// </summary>
    internal sealed class FusionTraversalLedgeTransitionSmokeBootstrap : MonoBehaviour
    {
        private const string Scenario = "fusion-traversal-ledge-transition";
        private const string FixtureRootName = "Ledge_Hold_Climb";
        private const string MotionName = "Motion_Ledge_Climb";

        private const float FixtureTolerance = 0.05f;
        private const float SetupPositionTolerance = 0.25f;
        private const float OwnerGameplayReadySettleSeconds = 0.25f;
        private const float SetupTeleportRetrySeconds = 2f;
        private const int MaxSetupTeleportAttempts = 3;
        private const float HostTransitionSettleSeconds = 0.35f;
        private const float ObserverCompletionGraceSeconds = 1f;
        private const float RequiredTransitionProgress = 1.5f;
        private const float RequiredConnectedOwnerMoveDistance = 0.35f;
        private const float FinalConvergenceTolerance = 0.35f;
        private const float MaximumFrameStep = 0.25f;
        private const float MaximumReverseDistance = 0.25f;
        private const float MaximumReverseStep = 0.12f;
        private const float MinimumDistinctSampleDelta = 0.02f;
        private const float IntermediateEndpointMargin = 0.08f;
        private const int RequiredSourceStableFrames = 12;
        private const int RequiredFinalStableFrames = 10;
        private const int RequiredIntermediateSamples = 8;

        private const string SourceMarkerSuffix = ".source-attached";
        private const string OwnerSourceSettledMarkerSuffix = ".owner-source-settled";
        private const string MiddleMarkerSuffix = ".middle-observed";
        private const string CompleteMarkerSuffix = ".complete";
        private const string ObserverFinishedMarkerSuffix = ".observer-finished";

        private static readonly Vector3 CenterLowLocalPosition = new(0f, 6f, -0.05f);
        private static readonly Vector3 CenterMiddleLocalPosition = new(0f, 8f, -0.05f);
        private static readonly Vector3 CenterTopLocalPosition = new(0f, 10f, -0.05f);
        private static readonly PropertyInfo s_InInteractiveTransitionProperty =
            typeof(TraversalStance).GetProperty(
                "InInteractiveTransition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [Serializable]
        private struct Result
        {
            public bool passed;
            public string role;
            public string phase;
            public string message;
            public uint actorNetworkId;
            public bool sourceAttached;
            public bool middleAttached;
            public bool topAttached;
            public int transitionCount;
            public int firstIntermediateSamples;
            public int secondIntermediateSamples;
            public int totalSampleCount;
            public int reverseCorrectionCount;
            public int largeTeleportCount;
            public float firstProgress;
            public float secondProgress;
            public float firstFinalError;
            public float secondFinalError;
            public float reverseDistance;
            public float maxReverseStep;
            public float maxFrameStep;
            public bool connectedOwnerMovementTested;
            public bool connectedOwnerInputHealthy;
            public float connectedOwnerMoveDistance;
            public int setupTeleportAttempts;
            public bool setupTeleportResolved;
            public Vector3 finalPosition;
            public float elapsedSeconds;
        }

        private struct TransitionMetrics
        {
            public Vector3 Start;
            public Vector3 Expected;
            public Vector3 Previous;
            public Vector3 Axis;
            public float Distance;
            public float Progress;
            public float FinalError;
            public float ReverseDistance;
            public float MaxReverseStep;
            public float MaxFrameStep;
            public float LastDistinctProgress;
            public int SampleCount;
            public int IntermediateSamples;
            public int ReverseCorrectionCount;
            public int LargeTeleportCount;
            public int FinalStableFrames;
            public bool TargetStanceObserved;
            public bool ForwardMotionStarted;

            public bool IsConverged =>
                TargetStanceObserved &&
                FinalStableFrames >= RequiredFinalStableFrames;

            public bool IsSmooth =>
                IsConverged &&
                Progress >= RequiredTransitionProgress &&
                IntermediateSamples >= RequiredIntermediateSamples &&
                ReverseDistance <= MaximumReverseDistance &&
                MaxReverseStep <= MaximumReverseStep &&
                MaxFrameStep <= MaximumFrameStep &&
                ReverseCorrectionCount == 0 &&
                LargeTeleportCount == 0;

            public static TransitionMetrics Begin(Vector3 start, Vector3 fixtureDelta)
            {
                float distance = fixtureDelta.magnitude;
                return new TransitionMetrics
                {
                    Start = start,
                    Expected = start + fixtureDelta,
                    Previous = start,
                    Axis = distance > 0.0001f ? fixtureDelta / distance : Vector3.up,
                    Distance = distance,
                    FinalError = distance,
                    LastDistinctProgress = float.NegativeInfinity
                };
            }

            public void Capture(Vector3 current, bool targetStanceObserved)
            {
                Vector3 delta = current - Previous;
                float signedStep = Vector3.Dot(delta, Axis);
                float frameStep = delta.magnitude;
                float signedProgress = Vector3.Dot(current - Start, Axis);
                Progress = Mathf.Max(Progress, signedProgress);
                FinalError = Vector3.Distance(current, Expected);
                MaxFrameStep = Mathf.Max(MaxFrameStep, frameStep);
                TargetStanceObserved |= targetStanceObserved;
                SampleCount++;

                // TraverseInteractive first aligns the character with the destination
                // connection. For the installed vertical ledge fixture that authored pre-roll
                // moves about eight centimetres below the source before the upward transition
                // begins. It is part of the same motion on the Host, not a network rollback.
                // Arm correction detection only after observable forward travel; all subsequent
                // reverse movement and every absolute frame step remain subject to the original
                // strict limits.
                ForwardMotionStarted |= signedProgress > IntermediateEndpointMargin;
                if (ForwardMotionStarted && signedStep < -0.001f)
                {
                    float reverseStep = -signedStep;
                    ReverseDistance += reverseStep;
                    MaxReverseStep = Mathf.Max(MaxReverseStep, reverseStep);
                    if (reverseStep > 0.01f) ReverseCorrectionCount++;
                }

                if (frameStep > MaximumFrameStep) LargeTeleportCount++;

                if (signedProgress > IntermediateEndpointMargin &&
                    signedProgress < Distance - IntermediateEndpointMargin &&
                    (float.IsNegativeInfinity(LastDistinctProgress) ||
                     Mathf.Abs(signedProgress - LastDistinctProgress) >=
                     MinimumDistinctSampleDelta))
                {
                    LastDistinctProgress = signedProgress;
                    IntermediateSamples++;
                }

                bool finalConverged =
                    TargetStanceObserved &&
                    FinalError <= FinalConvergenceTolerance &&
                    signedProgress >= Distance - FinalConvergenceTolerance;
                FinalStableFrames = finalConverged ? FinalStableFrames + 1 : 0;
                Previous = current;
            }

            public string ToTrace()
            {
                return $"progress={Progress:F3}/{Distance:F3} finalError={FinalError:F3} " +
                       $"intermediate={IntermediateSamples} samples={SampleCount} " +
                       $"reverse={ReverseDistance:F3} maxReverse={MaxReverseStep:F3} " +
                       $"maxStep={MaxFrameStep:F3} reverseCorrections={ReverseCorrectionCount} " +
                       $"largeTeleports={LargeTeleportCount} targetObserved={TargetStanceObserved} " +
                       $"stable={FinalStableFrames}";
            }
        }

        private string m_Role;
        private string m_ResultPath;
        private string m_ReadyPath;
        private float m_StartedAt;
        private Transform m_InputCamera;
        private bool m_Finished;
        private int m_SetupTeleportAttempts;
        private bool m_SetupTeleportResolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!TryGetArgument("--gc2-network-smoke-scenario", out string scenario) ||
                !string.Equals(scenario, Scenario, StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("Fusion Traversal Ledge Transition Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionTraversalLedgeTransitionSmokeBootstrap>();
        }

        private void OnDisable()
        {
            StopConnectedOwnerInput();
        }

        private async void Start()
        {
            Application.SetStackTraceLogType(UnityEngine.LogType.Log, StackTraceLogType.None);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            m_StartedAt = Time.realtimeSinceStartup;

            TryGetArgument("--gc2-network-smoke-role", out m_Role);
            TryGetArgument("--gc2-network-smoke-result", out m_ResultPath);
            TryGetArgument("--gc2-network-smoke-ready", out m_ReadyPath);
            TryGetArgument("--gc2-network-smoke-session", out string session);
            if (string.IsNullOrWhiteSpace(session))
            {
                session = "GC2-Fusion-Traversal-Ledge-Transition-Smoke";
            }

            TryGetArgument("--gc2-network-smoke-region", out string region);
            var startOptions = new FusionSessionStartOptions(session, region);

            string appId = Environment.GetEnvironmentVariable("GC2_NETWORK_SMOKE_PHOTON_APP_ID");
            if (!string.IsNullOrWhiteSpace(appId))
            {
                PhotonAppSettings settings = PhotonAppSettings.Global;
                if (settings != null) settings.AppSettings.AppIdFusion = appId.Trim();
            }

            FusionSessionBootstrap bootstrap = UnityObjectSearch.FindAny<FusionSessionBootstrap>();
            if (bootstrap == null)
            {
                Finish(false, "start", "FusionSessionBootstrap is missing.");
                return;
            }

            try
            {
                StartGameResult startResult = m_Role switch
                {
                    "fusion-host" => await bootstrap.StartHostAsync(startOptions),
                    "fusion-client" => await bootstrap.JoinHostAsync(startOptions),
                    "fusion-shared-master" =>
                        await bootstrap.CreateSharedWithExactSessionNameAsync(startOptions),
                    "fusion-shared-client" => await bootstrap.JoinSharedAsync(startOptions),
                    _ => default
                };

                if (!startResult.Ok)
                {
                    Finish(
                        false,
                        "start",
                        $"Fusion start failed: {startResult.ShutdownReason} " +
                        startResult.ErrorMessage);
                    return;
                }
            }
            catch (Exception exception)
            {
                Finish(false, "start", exception.GetType().Name + ": " + exception.Message);
                return;
            }

            NetworkCiTrace.Log(
                "fusion-ledge-transition-smoke",
                "session-started",
                0,
                0,
                $"role={m_Role} session='{session}' region='{region}'",
                this);

            if (!IsTransitionOwnerRole && !IsTransitionObserverRole)
            {
                Finish(false, "start-role", $"Unsupported ledge-transition role '{m_Role}'.");
                return;
            }

            StartCoroutine(IsTransitionOwnerRole ? RunTransitionOwner() : RunObserver());
        }

        private bool IsTransitionOwnerRole =>
            string.Equals(m_Role, "fusion-host", StringComparison.Ordinal) ||
            string.Equals(m_Role, "fusion-shared-client", StringComparison.Ordinal);

        private bool IsTransitionObserverRole =>
            string.Equals(m_Role, "fusion-client", StringComparison.Ordinal) ||
            string.Equals(m_Role, "fusion-shared-master", StringComparison.Ordinal);

        private bool IsSharedTransition =>
            string.Equals(m_Role, "fusion-shared-client", StringComparison.Ordinal) ||
            string.Equals(m_Role, "fusion-shared-master", StringComparison.Ordinal);

        private string TransitionOwnerLabel =>
            IsSharedTransition ? "Shared logical owner" : "Host";

        private string OwnerTraceStage(string suffix) =>
            (IsSharedTransition ? "shared-owner-" : "host-") + suffix;

        private IEnumerator RunTransitionOwner()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            NetworkCharacter remote = null;
            FusionTransportBridge transport = null;
            TraverseInteractive source = null;
            TraverseInteractive middle = null;
            TraverseInteractive top = null;
            float ownerGameplayReadyAt = -1f;
            float lastTeleportRequestAt = -1f;
            int teleportAttempt = 0;
            bool teleportResolved = false;
            bool teleportAccepted = false;
            bool sourceEnterRequested = false;
            bool sourceAttached = false;
            bool ownerSourceSettledSignaled = false;
            bool firstTransitionRequested = false;
            bool middleAttached = false;
            bool secondTransitionRequested = false;
            bool topAttached = false;
            Vector3 firstStart = default;
            Vector3 secondStart = default;
            float firstCompletedProgress = 0f;
            float middleAttachedAt = -1f;
            float topAttachedAt = -1f;

            while (Elapsed < timeout)
            {
                if (actor == null) actor = FindLocalPlayer();
                if (remote == null) remote = FindRemotePlayer();
                if (transport == null)
                {
                    transport = UnityObjectSearch.FindAny<FusionTransportBridge>();
                }
                if (source == null) source = FindExactFixture(CenterLowLocalPosition);
                if (middle == null) middle = FindExactFixture(CenterMiddleLocalPosition);
                if (top == null) top = FindExactFixture(CenterTopLocalPosition);

                if (actor == null || remote == null || transport == null || source == null ||
                    middle == null || top == null ||
                    !IsMarkerForActor(m_ReadyPath, actor.NetworkId) ||
                    !transport.IsLocalGameplayReady)
                {
                    if (teleportAttempt == 0) ownerGameplayReadyAt = -1f;
                    yield return new WaitForSecondsRealtime(0.05f);
                    continue;
                }

                if (ownerGameplayReadyAt < 0f)
                {
                    ownerGameplayReadyAt = Elapsed;
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        OwnerTraceStage("gameplay-ready"),
                        actor.NetworkId,
                        0,
                        "Both the transition owner's local snapshot handshake and the " +
                        "observer proxy routing gate are ready.",
                        actor);
                }

                if (Elapsed - ownerGameplayReadyAt < OwnerGameplayReadySettleSeconds)
                {
                    yield return null;
                    continue;
                }

                if (!IsConnectedOwnerInputHealthy(actor))
                {
                    Finish(
                        false,
                        IsSharedTransition ? "shared-owner-unhealthy" : "host-shortcut",
                        $"GC2 no longer recognizes the authenticated {TransitionOwnerLabel} " +
                        "as the controllable ShortcutPlayer.",
                        actor,
                        sourceAttached,
                        middleAttached,
                        topAttached);
                    yield break;
                }

                Vector3 setupRoot = CalculateCharacterRootAtFixture(actor.Character, source);
                if (!teleportResolved &&
                    teleportAttempt >= MaxSetupTeleportAttempts &&
                    Elapsed - lastTeleportRequestAt >= SetupTeleportRetrySeconds)
                {
                    Finish(
                        false,
                        IsSharedTransition
                            ? "shared-owner-source-position-no-response"
                            : "host-source-position-no-response",
                        $"No authority result was received after {teleportAttempt} setup " +
                        "teleport attempts.",
                        actor);
                    yield break;
                }

                if (!teleportResolved &&
                    (teleportAttempt == 0 ||
                     Elapsed - lastTeleportRequestAt >= SetupTeleportRetrySeconds))
                {
                    UnitMotionNetworkController motion = actor.MotionController;
                    if (motion == null)
                    {
                        yield return null;
                        continue;
                    }

                    Vector3 driverTarget = ToDriverPosition(actor.Character, setupRoot);
                    int requestAttempt = ++teleportAttempt;
                    m_SetupTeleportAttempts = teleportAttempt;
                    lastTeleportRequestAt = Elapsed;
                    motion.RequestTeleport(
                        driverTarget,
                        float.NaN,
                        true,
                        result =>
                        {
                            if (teleportResolved ||
                                (requestAttempt != teleportAttempt && !result.approved))
                            {
                                return;
                            }

                            teleportResolved = true;
                            m_SetupTeleportResolved = true;
                            teleportAccepted = result.approved;
                            if (teleportAccepted && !sourceEnterRequested)
                            {
                                // A Shared logical owner receives the teleport result after a
                                // round trip. Enter the hold in that same main-thread callback so
                                // gravity cannot advance the freshly positioned character before
                                // GC2 installs its Traversal stance.
                                sourceEnterRequested = TryRequestSourceEntry(actor, source);
                            }
                            NetworkCiTrace.Log(
                                "fusion-ledge-transition-smoke",
                                OwnerTraceStage("source-position-resolved"),
                                actor.NetworkId,
                                0,
                                $"attempt={requestAttempt} approved={result.approved} " +
                                $"root={actor.transform.position:F3} " +
                                $"target={setupRoot:F3} driverTarget={driverTarget:F3}",
                                actor);
                        });
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        OwnerTraceStage("source-position-requested"),
                        actor.NetworkId,
                        0,
                        $"attempt={requestAttempt} target={setupRoot:F3} " +
                        $"driverTarget={driverTarget:F3} " +
                        $"source='{HierarchyPath(source.transform)}'",
                        actor);
                }

                if (teleportResolved && !teleportAccepted)
                {
                    Finish(
                        false,
                        IsSharedTransition
                            ? "shared-owner-position-rejected"
                            : "host-position-rejected",
                        "Authority rejected the setup teleport to the center-low ledge hold.",
                        actor);
                    yield break;
                }

                if (!teleportResolved ||
                    (!sourceEnterRequested &&
                     Vector3.Distance(actor.transform.position, setupRoot) >
                     SetupPositionTolerance))
                {
                    yield return null;
                    continue;
                }

                if (!sourceEnterRequested)
                {
                    sourceEnterRequested = TryRequestSourceEntry(actor, source);
                }

                TraversalStance stance = GetStance(actor.Character);
                sourceAttached |= ReferenceEquals(stance?.Traverse, source);
                if (!ownerSourceSettledSignaled &&
                    sourceAttached &&
                    !IsInteractiveTransitionActive(stance))
                {
                    ownerSourceSettledSignaled = WriteMarker(
                        MarkerPath(OwnerSourceSettledMarkerSuffix),
                        actor.NetworkId);
                    if (ownerSourceSettledSignaled)
                    {
                        NetworkCiTrace.Log(
                            "fusion-ledge-transition-smoke",
                            OwnerTraceStage("source-entry-settled"),
                            actor.NetworkId,
                            0,
                            $"position={actor.transform.position:F3}",
                            actor);
                    }
                }

                if (!ownerSourceSettledSignaled ||
                    !IsMarkerForActor(MarkerPath(SourceMarkerSuffix), actor.NetworkId))
                {
                    yield return null;
                    continue;
                }

                if (!firstTransitionRequested)
                {
                    firstTransitionRequested = true;
                    firstStart = actor.transform.position;
                    stance.TryJump();
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        OwnerTraceStage("transition-requested"),
                        actor.NetworkId,
                        0,
                        $"index=1 source='{HierarchyPath(source.transform)}' " +
                        $"expected='{HierarchyPath(middle.transform)}' start={firstStart:F3}",
                        actor);
                }

                if (!middleAttached && ReferenceEquals(stance?.Traverse, middle))
                {
                    middleAttached = true;
                    middleAttachedAt = Elapsed;
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        OwnerTraceStage("middle-attached"),
                        actor.NetworkId,
                        0,
                        $"position={actor.transform.position:F3}",
                        actor);
                }

                float firstProgress = Vector3.Dot(
                    actor.transform.position - firstStart,
                    FixtureAxis(source, middle));
                if (!middleAttached ||
                    Elapsed - middleAttachedAt < HostTransitionSettleSeconds ||
                    firstProgress < RequiredTransitionProgress ||
                    IsInteractiveTransitionActive(stance) ||
                    !IsMarkerForActor(MarkerPath(MiddleMarkerSuffix), actor.NetworkId))
                {
                    yield return null;
                    continue;
                }

                if (!secondTransitionRequested)
                {
                    secondTransitionRequested = true;
                    firstCompletedProgress = firstProgress;
                    secondStart = actor.transform.position;
                    stance.TryJump();
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        OwnerTraceStage("transition-requested"),
                        actor.NetworkId,
                        0,
                        $"index=2 source='{HierarchyPath(middle.transform)}' " +
                        $"expected='{HierarchyPath(top.transform)}' start={secondStart:F3}",
                        actor);
                }

                if (!topAttached && ReferenceEquals(stance?.Traverse, top))
                {
                    topAttached = true;
                    topAttachedAt = Elapsed;
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        OwnerTraceStage("top-attached"),
                        actor.NetworkId,
                        0,
                        $"position={actor.transform.position:F3}",
                        actor);
                }

                float secondProgress = Vector3.Dot(
                    actor.transform.position - secondStart,
                    FixtureAxis(middle, top));
                if (topAttached &&
                    Elapsed - topAttachedAt >= HostTransitionSettleSeconds &&
                    secondProgress >= RequiredTransitionProgress &&
                    !IsInteractiveTransitionActive(stance) &&
                    IsMarkerForActor(MarkerPath(CompleteMarkerSuffix), actor.NetworkId) &&
                    (!IsSharedTransition ||
                     IsMarkerForActor(
                         MarkerPath(ObserverFinishedMarkerSuffix),
                         actor.NetworkId)))
                {
                    Finish(
                        true,
                        IsSharedTransition
                            ? "shared-owner-center-low-middle-top-transitioned"
                            : "host-center-low-middle-top-transitioned",
                        $"{TransitionOwnerLabel} used GC2's authoritative TryJump connection " +
                        "route for both vertical ledge transitions, and the observer " +
                        "acknowledged both.",
                        actor,
                        true,
                        true,
                        true,
                        transitionCount: 2,
                        firstProgress: firstCompletedProgress,
                        secondProgress: secondProgress,
                        finalPosition: actor.transform.position);
                    yield break;
                }

                yield return null;
            }

            Finish(
                false,
                IsSharedTransition
                    ? !sourceAttached
                        ? "shared-owner-no-source-attach"
                        : !middleAttached
                            ? "shared-owner-no-middle-transition"
                            : !topAttached
                                ? "shared-owner-no-top-transition"
                                : "shared-owner-observer-no-completion"
                    : !sourceAttached
                        ? "host-no-source-attach"
                        : !middleAttached
                            ? "host-no-middle-transition"
                            : !topAttached
                                ? "host-no-top-transition"
                                : "host-observer-no-completion",
                $"Timed out proving {TransitionOwnerLabel}'s two authoritative vertical " +
                "ledge transitions.",
                actor,
                sourceAttached,
                middleAttached,
                topAttached,
                transitionCount: (middleAttached ? 1 : 0) + (topAttached ? 1 : 0),
                firstProgress: secondTransitionRequested
                    ? firstCompletedProgress
                    : actor != null && firstTransitionRequested
                        ? Vector3.Dot(
                            actor.transform.position - firstStart,
                            FixtureAxis(source, middle))
                        : 0f,
                secondProgress: actor != null && secondTransitionRequested
                    ? Vector3.Dot(actor.transform.position - secondStart, FixtureAxis(middle, top))
                    : 0f,
                finalPosition: actor != null ? actor.transform.position : default);
        }

        private bool TryRequestSourceEntry(
            NetworkCharacter actor,
            TraverseInteractive source)
        {
            if (actor == null || source == null || actor.Character == null ||
                ShortcutPlayer.Instance != actor.gameObject)
            {
                return false;
            }

            _ = source.Enter(actor.Character, default);
            NetworkCiTrace.Log(
                "fusion-ledge-transition-smoke",
                OwnerTraceStage("source-enter-requested"),
                actor.NetworkId,
                0,
                $"source='{HierarchyPath(source.transform)}'",
                actor);
            return true;
        }

        private IEnumerator RunObserver()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            NetworkCharacter localOwner = null;
            FusionTransportBridge transport = null;
            TraverseInteractive source = null;
            TraverseInteractive middle = null;
            TraverseInteractive top = null;
            bool routingReadySignaled = false;
            bool sourceAttached = false;
            bool middleAttached = false;
            bool topAttached = false;
            int sourceStableFrames = 0;
            Vector3 sourceStablePosition = default;
            bool firstMetricsStarted = false;
            bool firstTransitionComplete = false;
            bool secondMetricsStarted = false;
            TransitionMetrics first = default;
            TransitionMetrics second = default;
            bool connectedOwnerMovementTested = false;
            bool connectedOwnerInputHealthy = true;
            Vector3 connectedOwnerMoveStart = default;
            float connectedOwnerMoveDistance = 0f;
            float nextTraceAt = 0f;

            while (Elapsed < timeout)
            {
                if (actor == null) actor = FindRemotePlayer();
                if (localOwner == null) localOwner = FindLocalPlayer();
                if (transport == null)
                {
                    transport = UnityObjectSearch.FindAny<FusionTransportBridge>();
                }
                if (source == null) source = FindExactFixture(CenterLowLocalPosition);
                if (middle == null) middle = FindExactFixture(CenterMiddleLocalPosition);
                if (top == null) top = FindExactFixture(CenterTopLocalPosition);

                if (actor == null || localOwner == null || transport == null || source == null ||
                    middle == null || top == null)
                {
                    yield return new WaitForSecondsRealtime(0.05f);
                    continue;
                }

                NetworkTraversalController controller =
                    actor.GetComponent<NetworkTraversalController>();
                FusionNetworkIdentity identity = actor.GetComponent<FusionNetworkIdentity>();
                uint ownerClientId = 0;
                bool remoteOwnerGameplayReady =
                    !IsSharedTransition ||
                    (transport.IsServer &&
                     actor.IsServerInstance &&
                     !actor.IsOwnerInstance &&
                     actor.HasAuthenticatedPlayerOwner &&
                     identity != null &&
                     identity.TransportAdmitted &&
                     identity.HasAuthorityAdmission &&
                     identity.IsLogicalAuthority &&
                     identity.TryGetLogicalOwnerClientId(out ownerClientId) &&
                     transport.IsClientReady(ownerClientId) &&
                     controller != null &&
                     controller.IsServer);
                if (!routingReadySignaled &&
                    transport.IsLocalGameplayReady &&
                    remoteOwnerGameplayReady &&
                    controller != null &&
                    controller.IsReadyForNetworkRouting)
                {
                    routingReadySignaled = WriteMarker(m_ReadyPath, actor.NetworkId);
                    if (routingReadySignaled)
                    {
                        string readinessDetail = IsSharedTransition
                            ? $" and ownerClient={ownerClientId} is gameplay-ready"
                            : string.Empty;
                        NetworkCiTrace.Log(
                            "fusion-ledge-transition-smoke",
                            "observer-routing-ready",
                            actor.NetworkId,
                            0,
                            $"{TransitionOwnerLabel} proxy has a routing-ready Traversal " +
                            $"controller{readinessDetail} before source entry.",
                            actor);
                    }
                }

                if (!routingReadySignaled)
                {
                    yield return null;
                    continue;
                }

                TraversalStance stance = GetStance(actor.Character);
                Vector3 presented = GetPresentedPosition(actor);
                bool onSource = ReferenceEquals(stance?.Traverse, source);

                if (!sourceAttached)
                {
                    Vector3 expectedSource = CalculateCharacterRootAtFixture(
                        actor.Character,
                        source);
                    bool ownerSourceSettled = IsMarkerForActor(
                        MarkerPath(OwnerSourceSettledMarkerSuffix),
                        actor.NetworkId);
                    if (!onSource ||
                        !ownerSourceSettled ||
                        IsInteractiveTransitionActive(stance) ||
                        Vector3.Distance(presented, expectedSource) >
                        FinalConvergenceTolerance)
                    {
                        sourceStableFrames = 0;
                        sourceStablePosition = presented;
                        yield return null;
                        continue;
                    }

                    if (sourceStableFrames == 0 ||
                        Vector3.Distance(sourceStablePosition, presented) > 0.03f)
                    {
                        sourceStablePosition = presented;
                        sourceStableFrames = 1;
                    }
                    else
                    {
                        sourceStableFrames++;
                    }

                    if (sourceStableFrames < RequiredSourceStableFrames)
                    {
                        yield return null;
                        continue;
                    }

                    sourceAttached = true;
                    first = TransitionMetrics.Begin(
                        presented,
                        middle.transform.position - source.transform.position);
                    firstMetricsStarted = true;
                    WriteMarker(MarkerPath(SourceMarkerSuffix), actor.NetworkId);
                    connectedOwnerMovementTested = true;
                    connectedOwnerMoveStart = localOwner.transform.position;
                    NetworkCiTrace.Log(
                        "fusion-ledge-transition-smoke",
                        "observer-source-ready",
                        actor.NetworkId,
                        0,
                        $"source='{HierarchyPath(source.transform)}' presented={presented:F3} " +
                        $"expectedSource={expectedSource:F3} expectedMiddle={first.Expected:F3}",
                        actor);
                }

                connectedOwnerInputHealthy &= IsConnectedOwnerInputHealthy(localOwner);
                if (connectedOwnerMovementTested)
                {
                    connectedOwnerMoveDistance = HorizontalDistance(
                        connectedOwnerMoveStart,
                        localOwner.transform.position);
                    if (connectedOwnerMoveDistance < RequiredConnectedOwnerMoveDistance)
                    {
                        DriveConnectedOwner(localOwner, Vector3.right);
                    }
                    else
                    {
                        StopConnectedOwnerInput();
                    }
                }

                if (firstMetricsStarted && !firstTransitionComplete)
                {
                    middleAttached |= ReferenceEquals(stance?.Traverse, middle);
                    first.Capture(presented, middleAttached);

                    if (Elapsed >= nextTraceAt)
                    {
                        nextTraceAt = Elapsed + 0.25f;
                        NetworkCiTrace.Log(
                            "fusion-ledge-transition-smoke",
                            "observer-transition-sample",
                            actor.NetworkId,
                            0,
                            $"index=1 presented={presented:F3} {first.ToTrace()}",
                            actor);
                    }

                    if (first.IsConverged)
                    {
                        if (!first.IsSmooth)
                        {
                            FinishObserverFailure(
                                "observer-first-transition-not-smooth",
                                $"The {TransitionOwnerLabel} proxy reached the middle ledge " +
                                "without enough monotonic intermediate presentation samples, " +
                                "or required a teleport/reverse correction.",
                                actor,
                                sourceAttached,
                                middleAttached,
                                topAttached,
                                first,
                                second,
                                connectedOwnerMovementTested,
                                connectedOwnerInputHealthy,
                                connectedOwnerMoveDistance);
                            yield break;
                        }

                        firstTransitionComplete = true;
                        WriteMarker(MarkerPath(MiddleMarkerSuffix), actor.NetworkId);
                        second = TransitionMetrics.Begin(
                            presented,
                            top.transform.position - middle.transform.position);
                        secondMetricsStarted = true;
                        NetworkCiTrace.Log(
                            "fusion-ledge-transition-smoke",
                            "observer-transition-complete",
                            actor.NetworkId,
                            0,
                            $"index=1 {first.ToTrace()}",
                            actor);
                    }
                }
                else if (secondMetricsStarted)
                {
                    topAttached |= ReferenceEquals(stance?.Traverse, top);
                    second.Capture(presented, topAttached);

                    if (Elapsed >= nextTraceAt)
                    {
                        nextTraceAt = Elapsed + 0.25f;
                        NetworkCiTrace.Log(
                            "fusion-ledge-transition-smoke",
                            "observer-transition-sample",
                            actor.NetworkId,
                            0,
                            $"index=2 presented={presented:F3} {second.ToTrace()}",
                            actor);
                    }

                    if (second.IsConverged)
                    {
                        bool passed =
                            second.IsSmooth &&
                            connectedOwnerMovementTested &&
                            connectedOwnerInputHealthy &&
                            connectedOwnerMoveDistance >= RequiredConnectedOwnerMoveDistance;
                        if (!passed)
                        {
                            FinishObserverFailure(
                                "observer-second-transition-not-smooth",
                                $"The second {TransitionOwnerLabel}-proxy transition was not " +
                                "smooth or the observer's authenticated local owner lost " +
                                "independent movement.",
                                actor,
                                sourceAttached,
                                middleAttached,
                                topAttached,
                                first,
                                second,
                                connectedOwnerMovementTested,
                                connectedOwnerInputHealthy,
                                connectedOwnerMoveDistance);
                            yield break;
                        }

                        NetworkCiTrace.Log(
                            "fusion-ledge-transition-smoke",
                            "observer-transition-complete",
                            actor.NetworkId,
                            0,
                            $"index=2 {second.ToTrace()} connectedMove={connectedOwnerMoveDistance:F3}",
                            actor);
                        WriteMarker(MarkerPath(CompleteMarkerSuffix), actor.NetworkId);
                        // Keep this process alive long enough for the transition owner to consume
                        // the final coordination marker and write its own result before this peer
                        // leaves.
                        yield return new WaitForSecondsRealtime(ObserverCompletionGraceSeconds);
                        WriteMarker(
                            MarkerPath(ObserverFinishedMarkerSuffix),
                            actor.NetworkId);
                        Finish(
                            true,
                            IsSharedTransition
                                ? "observer-shared-owner-ledge-transitions-smooth-local-owner-healthy"
                                : "observer-host-ledge-transitions-smooth-local-owner-healthy",
                            $"Observer saw both {TransitionOwnerLabel} ledge changes through " +
                            "multiple monotonic transport-pose samples without a teleport " +
                            "boundary, while its authenticated local owner remained movable.",
                            actor,
                            true,
                            true,
                            true,
                            2,
                            first.IntermediateSamples,
                            second.IntermediateSamples,
                            first.SampleCount + second.SampleCount,
                            first.ReverseCorrectionCount + second.ReverseCorrectionCount,
                            first.LargeTeleportCount + second.LargeTeleportCount,
                            first.Progress,
                            second.Progress,
                            first.FinalError,
                            second.FinalError,
                            first.ReverseDistance + second.ReverseDistance,
                            Mathf.Max(first.MaxReverseStep, second.MaxReverseStep),
                            Mathf.Max(first.MaxFrameStep, second.MaxFrameStep),
                            connectedOwnerMovementTested,
                            connectedOwnerInputHealthy,
                            connectedOwnerMoveDistance,
                            presented);
                        yield break;
                    }
                }

                yield return null;
            }

            FinishObserverFailure(
                !sourceAttached
                    ? "observer-no-source-shell"
                    : !middleAttached
                        ? "observer-no-middle-shell"
                        : !topAttached
                            ? "observer-no-top-shell"
                            : "observer-no-final-convergence",
                $"Timed out proving smooth {TransitionOwnerLabel} ledge transitions on the " +
                "observer.",
                actor,
                sourceAttached,
                middleAttached,
                topAttached,
                first,
                second,
                connectedOwnerMovementTested,
                connectedOwnerInputHealthy,
                connectedOwnerMoveDistance);
        }

        private void FinishObserverFailure(
            string phase,
            string message,
            NetworkCharacter actor,
            bool sourceAttached,
            bool middleAttached,
            bool topAttached,
            TransitionMetrics first,
            TransitionMetrics second,
            bool connectedOwnerMovementTested,
            bool connectedOwnerInputHealthy,
            float connectedOwnerMoveDistance)
        {
            Finish(
                false,
                phase,
                message,
                actor,
                sourceAttached,
                middleAttached,
                topAttached,
                (middleAttached ? 1 : 0) + (topAttached ? 1 : 0),
                first.IntermediateSamples,
                second.IntermediateSamples,
                first.SampleCount + second.SampleCount,
                first.ReverseCorrectionCount + second.ReverseCorrectionCount,
                first.LargeTeleportCount + second.LargeTeleportCount,
                first.Progress,
                second.Progress,
                first.FinalError,
                second.FinalError,
                first.ReverseDistance + second.ReverseDistance,
                Mathf.Max(first.MaxReverseStep, second.MaxReverseStep),
                Mathf.Max(first.MaxFrameStep, second.MaxFrameStep),
                connectedOwnerMovementTested,
                connectedOwnerInputHealthy,
                connectedOwnerMoveDistance,
                actor != null ? GetPresentedPosition(actor) : default);
        }

        private static NetworkCharacter FindLocalPlayer()
        {
            return UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                .FirstOrDefault(character =>
                    character != null && character.IsPlayerOwnedActor &&
                    character.IsOwnerInstance && character.NetworkId != 0);
        }

        private static NetworkCharacter FindRemotePlayer()
        {
            return UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                .FirstOrDefault(character =>
                    character != null && character.IsPlayerOwnedActor &&
                    !character.IsOwnerInstance && character.HasAuthenticatedPlayerOwner &&
                    character.NetworkId != 0);
        }

        private static TraverseInteractive FindExactFixture(Vector3 localPosition)
        {
            return UnityObjectSearch.FindAll<TraverseInteractive>(FindObjectsInactive.Exclude)
                .Where(interactive =>
                {
                    if (interactive == null || interactive.MotionInteractive == null ||
                        !string.Equals(
                            interactive.MotionInteractive.name,
                            MotionName,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    Transform root = FindFixtureRoot(interactive.transform);
                    return root != null &&
                           Vector3.Distance(root.localPosition, localPosition) <=
                           FixtureTolerance;
                })
                .OrderBy(interactive => HierarchyPath(interactive.transform), StringComparer.Ordinal)
                .FirstOrDefault();
        }

        private static Transform FindFixtureRoot(Transform value)
        {
            for (Transform current = value; current != null; current = current.parent)
            {
                if (string.Equals(current.name, FixtureRootName, StringComparison.Ordinal))
                {
                    return current;
                }
            }

            return null;
        }

        private static string HierarchyPath(Transform value)
        {
            if (value == null) return string.Empty;
            string path = value.name;
            for (Transform current = value.parent; current != null; current = current.parent)
            {
                path = current.name + "/" + path;
            }

            return path;
        }

        private static TraversalStance GetStance(Character character)
        {
            return character?.Combat?.RequestStance<TraversalStance>();
        }

        private static bool IsInteractiveTransitionActive(TraversalStance stance)
        {
            return stance != null &&
                   s_InInteractiveTransitionProperty?.GetValue(stance) is bool active &&
                   active;
        }

        private static Vector3 CalculateCharacterRootAtFixture(
            Character character,
            TraverseInteractive interactive)
        {
            float localZ = Mathf.Lerp(interactive.PositionA, interactive.PositionB, 0.5f);
            Vector3 targetAnchor = interactive.Transform.TransformPoint(new Vector3(0f, 0f, localZ));
            Vector3 currentAnchor = interactive.MotionInteractive.CharacterPosition(character);
            return character.transform.position + (targetAnchor - currentAnchor);
        }

        private static Vector3 ToDriverPosition(Character character, Vector3 rootPosition)
        {
            float halfHeight = character != null ? character.Motion.Height * 0.5f : 0f;
            return rootPosition + Vector3.down * halfHeight;
        }

        private static Vector3 FixtureAxis(
            TraverseInteractive source,
            TraverseInteractive target)
        {
            if (source == null || target == null) return Vector3.up;
            Vector3 delta = target.transform.position - source.transform.position;
            return delta.sqrMagnitude > 0.0001f ? delta.normalized : Vector3.up;
        }

        private static Vector3 GetPresentedPosition(NetworkCharacter actor)
        {
            if (actor == null) return default;
            FusionNativeNetworkCharacterMotor motor =
                actor.GetComponent<FusionNativeNetworkCharacterMotor>();
            if (motor != null &&
                motor.TryGetCommittedRemotePresentationPosition(out Vector3 committedPosition))
            {
                return committedPosition;
            }

            Transform target = motor?.ActiveRemotePresentationTarget;
            return target != null ? target.position : actor.transform.position;
        }

        private void DriveConnectedOwner(NetworkCharacter actor, Vector3 worldDirection)
        {
            if (actor?.Character?.Player is not UnitPlayerDirectionalNetwork player) return;

            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude <= 0.0001f) return;

            if (m_InputCamera == null)
            {
                var owner = new GameObject("Fusion Ledge Transition Smoke Input Camera");
                owner.transform.SetParent(transform, false);
                m_InputCamera = owner.transform;
                player.SetCamera(m_InputCamera);
            }

            m_InputCamera.rotation = Quaternion.LookRotation(worldDirection.normalized, Vector3.up);
            player.InjectInput(Vector2.up);
        }

        private void StopConnectedOwnerInput()
        {
            NetworkCharacter actor = FindLocalPlayer();
            if (actor?.Character?.Player is UnitPlayerDirectionalNetwork player)
            {
                player.InjectInput(Vector2.zero);
            }
        }

        private static bool IsConnectedOwnerInputHealthy(NetworkCharacter actor)
        {
            return actor != null &&
                   actor.IsOwnerInstance &&
                   actor.Character != null &&
                   actor.Character.IsPlayer &&
                   actor.Character.Player != null &&
                   actor.Character.Player.IsControllable &&
                   ShortcutPlayer.Instance == actor.gameObject;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        private string MarkerPath(string suffix)
        {
            return string.IsNullOrWhiteSpace(m_ReadyPath) ? string.Empty : m_ReadyPath + suffix;
        }

        private static bool IsMarkerPresent(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(Path.GetFullPath(path));
        }

        private static bool IsMarkerForActor(string path, uint actorNetworkId)
        {
            if (actorNetworkId == 0 || !IsMarkerPresent(path)) return false;

            try
            {
                string value = File.ReadAllText(Path.GetFullPath(path)).Trim();
                return uint.TryParse(value, out uint markerActorId) &&
                       markerActorId == actorNetworkId;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool WriteMarker(string path, uint actorNetworkId)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                string fullPath = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, actorNetworkId.ToString());
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    "[GC2 Fusion Ledge Transition Smoke] Could not write coordination marker: " +
                    exception.Message);
                return false;
            }
        }

        private void Finish(
            bool passed,
            string phase,
            string message,
            NetworkCharacter actor = null,
            bool sourceAttached = false,
            bool middleAttached = false,
            bool topAttached = false,
            int transitionCount = 0,
            int firstIntermediateSamples = 0,
            int secondIntermediateSamples = 0,
            int totalSampleCount = 0,
            int reverseCorrectionCount = 0,
            int largeTeleportCount = 0,
            float firstProgress = 0f,
            float secondProgress = 0f,
            float firstFinalError = 0f,
            float secondFinalError = 0f,
            float reverseDistance = 0f,
            float maxReverseStep = 0f,
            float maxFrameStep = 0f,
            bool connectedOwnerMovementTested = false,
            bool connectedOwnerInputHealthy = false,
            float connectedOwnerMoveDistance = 0f,
            Vector3 finalPosition = default)
        {
            if (m_Finished) return;
            m_Finished = true;
            StopConnectedOwnerInput();

            var result = new Result
            {
                passed = passed,
                role = m_Role,
                phase = phase,
                message = message,
                actorNetworkId = actor != null ? actor.NetworkId : 0,
                sourceAttached = sourceAttached,
                middleAttached = middleAttached,
                topAttached = topAttached,
                transitionCount = transitionCount,
                firstIntermediateSamples = firstIntermediateSamples,
                secondIntermediateSamples = secondIntermediateSamples,
                totalSampleCount = totalSampleCount,
                reverseCorrectionCount = reverseCorrectionCount,
                largeTeleportCount = largeTeleportCount,
                firstProgress = firstProgress,
                secondProgress = secondProgress,
                firstFinalError = firstFinalError,
                secondFinalError = secondFinalError,
                reverseDistance = reverseDistance,
                maxReverseStep = maxReverseStep,
                maxFrameStep = maxFrameStep,
                connectedOwnerMovementTested = connectedOwnerMovementTested,
                connectedOwnerInputHealthy = connectedOwnerInputHealthy,
                connectedOwnerMoveDistance = connectedOwnerMoveDistance,
                setupTeleportAttempts = m_SetupTeleportAttempts,
                setupTeleportResolved = m_SetupTeleportResolved,
                finalPosition = finalPosition,
                elapsedSeconds = Elapsed
            };

            if (!string.IsNullOrWhiteSpace(m_ResultPath))
            {
                string fullPath = Path.GetFullPath(m_ResultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, JsonUtility.ToJson(result, true));
            }

            Debug.Log(
                $"[GC2 Fusion Ledge Transition Smoke] passed={passed} role={m_Role} " +
                $"phase={phase} actor={result.actorNetworkId} transitions={transitionCount} " +
                $"source={sourceAttached} middle={middleAttached} top={topAttached} " +
                $"intermediate={firstIntermediateSamples}/{secondIntermediateSamples} " +
                $"progress={firstProgress:F3}/{secondProgress:F3} " +
                $"finalError={firstFinalError:F3}/{secondFinalError:F3} " +
                $"reverse={reverseDistance:F3} maxReverse={maxReverseStep:F3} " +
                $"maxStep={maxFrameStep:F3} reverseCorrections={reverseCorrectionCount} " +
                $"largeTeleports={largeTeleportCount} connectedMove={connectedOwnerMoveDistance:F3} " +
                $"connectedInputHealthy={connectedOwnerInputHealthy} message='{message}'");
            Application.Quit(passed ? 0 : 1);
        }

        private float Elapsed => Time.realtimeSinceStartup - m_StartedAt;

        private static bool TryGetArgument(string name, out string value)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
            {
                if (!string.Equals(arguments[i], name, StringComparison.Ordinal)) continue;
                value = arguments[i + 1] ?? string.Empty;
                return true;
            }

            value = string.Empty;
            return false;
        }

        private static int GetIntArgument(string name, int fallback)
        {
            return TryGetArgument(name, out string value) && int.TryParse(value, out int parsed)
                ? parsed
                : fallback;
        }
    }
}
#endif
