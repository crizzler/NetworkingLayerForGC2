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
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.Fusion
{
    /// <summary>
    /// Opt-in built-versus-built Fusion Host regression harness for the authored
    /// Free_Climb -> PullUp path in the Climb demo. It is excluded from release players.
    /// </summary>
    internal sealed class FusionTraversalPullUpSmokeBootstrap : MonoBehaviour
    {
        private const string Scenario = "fusion-traversal-pullup";
        private const string FreeClimbRootName = "Free_Climb";
        private const string PullUpName = "PullUp";
        private const string PullUpMotionName = "Motion_PullUp";

        private const int AttemptsRequired = 3;
        private const int RequiredTrajectorySamples = 10;

        // Starting only 0.35 m below B places the two-metre character root inside the
        // PullUp platform's y=8..9 collider before Traversal has installed its ignores.
        // Start below the platform underside, then climb through the normal authored loop.
        private const float StartOffsetFromPositionB = 1.8f;
        private const float BoundaryOffsetFromPositionB = 0.05f;
        private const float AttachSettleSeconds = 0.25f;
        private const float BetweenAttemptsSeconds = 0.6f;
        private const float InitialPeerSettleSeconds = 1f;
        private const float FinalPoseSettleSeconds = 0.35f;
        private const float ResultGraceSeconds = 3f;
        private const float AttemptAcknowledgementTimeoutSeconds = 6f;

        private const float MaximumHostPreEnterDrop = 0.2f;
        private const float MaximumHostPreEnterReverseStep = 0.12f;
        private const float MaximumHostBoundaryAlignmentError = 0.2f;
        private const float MaximumHostEntryRollback = 0.2f;
        private const float MaximumHostReverseDistance = 0.15f;
        private const float MaximumHostReverseStep = 0.1f;
        private const float MaximumHostRollbackFromHighWater = 0.2f;
        private const float MaximumHostFrameStep = 0.35f;
        private const float MaximumHostLateralDeviation = 0.35f;
        private const float MaximumHostEndpointError = 0.3f;

        private const float MaximumObserverPreEnterDrop = 0.25f;
        private const float MaximumObserverPreEnterReverseStep = 0.15f;
        private const float MaximumObserverEntryRollback = 0.25f;
        private const float MaximumObserverReverseDistance = 0.2f;
        private const float MaximumObserverReverseStep = 0.12f;
        private const float MaximumObserverRollbackFromHighWater = 0.25f;
        private const float MaximumObserverFrameStep = 0.4f;
        private const float MaximumObserverLateralDeviation = 0.4f;
        private const float MaximumObserverEndpointError = 0.35f;

        private const float GroundTeleportMargin = 0.5f;
        private const float AirLaunchMargin = 0.25f;
        private const float MaximumEntryWarpInflation = 0.25f;
        private const float RequiredConnectedOwnerMoveDistance = 0.35f;

        private const float ExactFixtureTolerance = 0.1f;
        private static readonly Vector3 ExactFreeClimbRootPosition = new(-5f, 0f, 7f);
        private static readonly Vector3 ExactPullUpRootPosition = new(-5f, 8f, 7f);
        private static readonly PropertyInfo s_TraversalStanceRelativePositionProperty =
            typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        [Serializable]
        private struct Result
        {
            public bool passed;
            public string role;
            public string phase;
            public string message;
            public uint actorNetworkId;
            public bool fixtureResolved;
            public int attemptsRequired;
            public int attemptsCompleted;
            public Vector3 boundaryPosition;
            public Vector3 expectedBoundaryPosition;
            public float boundaryAlignmentError;
            public Vector3 pullUpEntryPosition;
            public Vector3 linkWarpPosition;
            public Vector3 expectedEndPosition;
            public Vector3 finalPosition;
            public bool landingSurfaceResolved;
            public Vector3 landingBoundsMin;
            public Vector3 landingBoundsMax;
            public float minimumPreEnterY;
            public float preEnterDrop;
            public float maxPreEnterReverseStep;
            public int preEnterSampleCount;
            public float entryRollback;
            public float nominalWarpDistance;
            public float entryWarpDistance;
            public float minimumY;
            public float maximumY;
            public int groundTeleportCount;
            public int airLaunchCount;
            public float reverseDistance;
            public float maxReverseStep;
            public float maxRollbackFromHighWater;
            public float maxFrameStep;
            public float maxLateralDeviation;
            public float finalTargetError;
            public int sampleCount;
            public int linkEnterCount;
            public int linkExitCount;
            public int stanceLinkEntryCount;
            public int stanceLinkExitCount;
            public int maxLinkOccupancy;
            public int duplicateTransitionCount;
            public bool connectedOwnerMovementTested;
            public bool connectedOwnerInputHealthy;
            public float connectedOwnerMoveDistance;
            public bool connectedOwnerTraversalMovementTested;
            public float connectedOwnerTraversalMoveDistance;
            public string fatalTraversalLog;
            public float elapsedSeconds;
        }

        private sealed class AttemptMetrics
        {
            public int Attempt;
            public bool Passed;
            public string FailurePhase = string.Empty;
            public string FailureMessage = string.Empty;
            public bool BoundaryCaptured;
            public bool LinkEntered;
            public bool LinkExited;
            public Vector3 BoundaryPosition;
            public Vector3 ExpectedBoundaryPosition;
            public float BoundaryAlignmentError;
            public Vector3 PullUpEntryPosition;
            public Vector3 LinkWarpPosition;
            public Vector3 ExpectedEndPosition;
            public Vector3 FinalPosition;
            public bool LandingSurfaceResolved;
            public Bounds LandingBounds;
            public float LandingRootY;
            public float MinimumPreEnterY;
            public float PreEnterDrop;
            public float MaxPreEnterReverseStep;
            public int PreEnterSampleCount;
            public float EntryRollback;
            public float NominalWarpDistance;
            public float EntryWarpDistance;
            public float MinimumY;
            public float MaximumY;
            public int GroundTeleportCount;
            public int AirLaunchCount;
            public float ReverseDistance;
            public float MaxReverseStep;
            public float MaxRollbackFromHighWater;
            public float MaxFrameStep;
            public float MaxLateralDeviation;
            public float FinalTargetError;
            public int SampleCount;
            public int LinkEnterCount;
            public int LinkExitCount;
            public int StanceLinkEntryCount;
            public int StanceLinkExitCount;
            public int MaxLinkOccupancy;
            public int DuplicateTransitionCount;

            public Vector3 LastPreEnterPosition;
            public Vector3 LastTrajectoryPosition;
            public float HighWaterProgress;

            public void Fail(string phase, string message)
            {
                Passed = false;
                FailurePhase = phase;
                FailureMessage = message;
            }
        }

        private sealed class AggregateMetrics
        {
            public int AttemptsCompleted;
            public Vector3 BoundaryPosition;
            public Vector3 ExpectedBoundaryPosition;
            public float BoundaryAlignmentError;
            public Vector3 PullUpEntryPosition;
            public Vector3 LinkWarpPosition;
            public Vector3 ExpectedEndPosition;
            public Vector3 FinalPosition;
            public bool LandingSurfaceResolved;
            public Bounds LandingBounds;
            public float MinimumPreEnterY;
            public float PreEnterDrop;
            public float MaxPreEnterReverseStep;
            public int PreEnterSampleCount;
            public float EntryRollback;
            public float NominalWarpDistance;
            public float EntryWarpDistance;
            public float MinimumY;
            public float MaximumY;
            public int GroundTeleportCount;
            public int AirLaunchCount;
            public float ReverseDistance;
            public float MaxReverseStep;
            public float MaxRollbackFromHighWater;
            public float MaxFrameStep;
            public float MaxLateralDeviation;
            public float FinalTargetError;
            public int SampleCount;
            public int LinkEnterCount;
            public int LinkExitCount;
            public int StanceLinkEntryCount;
            public int StanceLinkExitCount;
            public int MaxLinkOccupancy;
            public int DuplicateTransitionCount;

            public void Merge(AttemptMetrics attempt)
            {
                bool first = AttemptsCompleted == 0;
                AttemptsCompleted++;
                BoundaryPosition = attempt.BoundaryPosition;
                ExpectedBoundaryPosition = attempt.ExpectedBoundaryPosition;
                BoundaryAlignmentError = Mathf.Max(
                    BoundaryAlignmentError,
                    attempt.BoundaryAlignmentError);
                PullUpEntryPosition = attempt.PullUpEntryPosition;
                LinkWarpPosition = attempt.LinkWarpPosition;
                ExpectedEndPosition = attempt.ExpectedEndPosition;
                FinalPosition = attempt.FinalPosition;
                LandingSurfaceResolved = attempt.LandingSurfaceResolved;
                LandingBounds = attempt.LandingBounds;
                MinimumPreEnterY = first
                    ? attempt.MinimumPreEnterY
                    : Mathf.Min(MinimumPreEnterY, attempt.MinimumPreEnterY);
                PreEnterDrop = Mathf.Max(PreEnterDrop, attempt.PreEnterDrop);
                MaxPreEnterReverseStep = Mathf.Max(
                    MaxPreEnterReverseStep,
                    attempt.MaxPreEnterReverseStep);
                PreEnterSampleCount += attempt.PreEnterSampleCount;
                EntryRollback = Mathf.Max(EntryRollback, attempt.EntryRollback);
                NominalWarpDistance = Mathf.Max(
                    NominalWarpDistance,
                    attempt.NominalWarpDistance);
                EntryWarpDistance = Mathf.Max(EntryWarpDistance, attempt.EntryWarpDistance);
                MinimumY = first ? attempt.MinimumY : Mathf.Min(MinimumY, attempt.MinimumY);
                MaximumY = first ? attempt.MaximumY : Mathf.Max(MaximumY, attempt.MaximumY);
                GroundTeleportCount += attempt.GroundTeleportCount;
                AirLaunchCount += attempt.AirLaunchCount;
                ReverseDistance += attempt.ReverseDistance;
                MaxReverseStep = Mathf.Max(MaxReverseStep, attempt.MaxReverseStep);
                MaxRollbackFromHighWater = Mathf.Max(
                    MaxRollbackFromHighWater,
                    attempt.MaxRollbackFromHighWater);
                MaxFrameStep = Mathf.Max(MaxFrameStep, attempt.MaxFrameStep);
                MaxLateralDeviation = Mathf.Max(
                    MaxLateralDeviation,
                    attempt.MaxLateralDeviation);
                FinalTargetError = Mathf.Max(FinalTargetError, attempt.FinalTargetError);
                SampleCount += attempt.SampleCount;
                LinkEnterCount += attempt.LinkEnterCount;
                LinkExitCount += attempt.LinkExitCount;
                StanceLinkEntryCount += attempt.StanceLinkEntryCount;
                StanceLinkExitCount += attempt.StanceLinkExitCount;
                MaxLinkOccupancy = Mathf.Max(MaxLinkOccupancy, attempt.MaxLinkOccupancy);
                DuplicateTransitionCount += attempt.DuplicateTransitionCount;
            }
        }

        private sealed class OwnerHealthProbe
        {
            public bool Started;
            public bool MovementTested;
            public bool InputHealthy = true;
            public Vector3 StartPosition;
            public float MoveDistance;
            public bool TraversalStressStarted;
            public bool TraversalMovementTested;
            public Vector3 TraversalStartPosition;
            public float TraversalMoveDistance;
        }

        private string m_Role;
        private string m_ResultPath;
        private string m_ReadyPath;
        private float m_StartedAt;
        private Keyboard m_SmokeKeyboard;
        private Transform m_SmokeCamera;
        private string m_FatalTraversalLog;
        private bool m_Finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!TryGetArgument("--gc2-network-smoke-scenario", out string scenario) ||
                !string.Equals(scenario, Scenario, StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("Fusion Traversal PullUp Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionTraversalPullUpSmokeBootstrap>();
        }

        private void OnEnable()
        {
            Application.logMessageReceived += CaptureTraversalFailure;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= CaptureTraversalFailure;
            ReleaseUpInput();
            ReleaseConnectedOwnerInput();
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
                session = "GC2-Fusion-Traversal-PullUp-Smoke";
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
                "fusion-pullup-smoke",
                "session-started",
                0,
                0,
                $"role={m_Role} session='{session}' region='{region}'",
                this);
            StartCoroutine(m_Role == "fusion-host" ? RunHostOwner() : RunHostObserver());
        }

        private IEnumerator RunHostOwner()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            NetworkCharacter remoteObserver = null;
            FusionTransportBridge transport = null;
            TraverseInteractive freeClimb = null;
            TraverseLink pullUp = null;
            float peersReadyAt = -1f;

            while (Elapsed < timeout)
            {
                actor ??= FindLocalPlayer();
                remoteObserver ??= FindRemotePlayer();
                transport ??= UnityObjectSearch.FindAny<FusionTransportBridge>();
                if (freeClimb == null || pullUp == null)
                {
                    TryFindExactFixture(out freeClimb, out pullUp);
                }

                bool ready = actor != null && remoteObserver != null &&
                             transport != null && transport.IsLocalGameplayReady &&
                             freeClimb != null && pullUp != null &&
                             IsReadyMarkerPresent();
                if (!ready)
                {
                    peersReadyAt = -1f;
                    yield return new WaitForSecondsRealtime(0.05f);
                    continue;
                }

                if (peersReadyAt < 0f) peersReadyAt = Elapsed;
                if (Elapsed - peersReadyAt < InitialPeerSettleSeconds)
                {
                    yield return null;
                    continue;
                }

                break;
            }

            if (actor == null || remoteObserver == null || freeClimb == null || pullUp == null)
            {
                Finish(
                    false,
                    "host-fixture-or-peer-missing",
                    "Timed out resolving both built peers and the exact Free_Climb -> PullUp fixture.",
                    actor,
                    fixtureResolved: freeClimb != null && pullUp != null);
                yield break;
            }

            var aggregate = new AggregateMetrics();
            for (int attemptIndex = 1; attemptIndex <= AttemptsRequired; attemptIndex++)
            {
                if (TryFailFatal(actor, aggregate, true)) yield break;

                AttemptMetrics attempt = null;
                yield return RunHostAttempt(
                    actor,
                    freeClimb,
                    pullUp,
                    attemptIndex,
                    timeout,
                    value => attempt = value);

                if (attempt == null)
                {
                    Finish(
                        false,
                        "host-pullup-attempt-missing",
                        "The Host PullUp attempt did not produce trajectory metrics.",
                        actor,
                        true,
                        aggregate);
                    yield break;
                }

                aggregate.Merge(attempt);
                LogAttemptResult(actor, attempt, observer: false);
                if (!attempt.Passed)
                {
                    Finish(
                        false,
                        attempt.FailurePhase,
                        attempt.FailureMessage,
                        actor,
                        true,
                        aggregate);
                    yield break;
                }

                float acknowledgementDeadline = Mathf.Min(
                    timeout,
                    Elapsed + AttemptAcknowledgementTimeoutSeconds);
                while (Elapsed < acknowledgementDeadline &&
                       !IsAttemptAcknowledgementPresent(attemptIndex))
                {
                    if (TryFailFatal(actor, aggregate, true)) yield break;
                    yield return new WaitForSecondsRealtime(0.02f);
                }

                if (!IsAttemptAcknowledgementPresent(attemptIndex))
                {
                    Finish(
                        false,
                        "host-pullup-observer-ack-timeout",
                        $"Connected observer did not acknowledge PullUp attempt {attemptIndex}.",
                        actor,
                        true,
                        aggregate);
                    yield break;
                }

                if (attemptIndex < AttemptsRequired)
                {
                    yield return new WaitForSecondsRealtime(BetweenAttemptsSeconds);
                }
            }

            yield return new WaitForSecondsRealtime(ResultGraceSeconds);
            Finish(
                true,
                "host-pullup-trajectory-stable",
                "Fusion Host completed three deterministic Free_Climb to PullUp transitions without rollback, launch, teleport, snap-back, or duplicate transitions.",
                actor,
                true,
                aggregate);
        }

        private IEnumerator RunHostAttempt(
            NetworkCharacter actor,
            TraverseInteractive freeClimb,
            TraverseLink pullUp,
            int attemptIndex,
            int timeout,
            Action<AttemptMetrics> completed)
        {
            var metrics = new AttemptMetrics { Attempt = attemptIndex };
            TraversalStance stance = GetStance(actor.Character);

            float inactiveDeadline = Mathf.Min(timeout, Elapsed + 4f);
            while (Elapsed < inactiveDeadline && stance?.Traverse != null)
            {
                stance = GetStance(actor.Character);
                yield return null;
            }

            if (stance?.Traverse != null)
            {
                metrics.Fail(
                    "host-pullup-reset-active",
                    $"Attempt {attemptIndex} began while another traversal remained active.");
                completed(metrics);
                yield break;
            }

            UnitMotionNetworkController motion = actor.MotionController;
            if (motion == null)
            {
                metrics.Fail(
                    "host-pullup-motion-controller-missing",
                    "The Host owner has no UnitMotionNetworkController.");
                completed(metrics);
                yield break;
            }

            float startLocalZ = freeClimb.PositionB - StartOffsetFromPositionB;
            Vector3 targetRoot = CalculateCharacterRootAtInteractiveLocalZ(
                actor.Character,
                freeClimb,
                startLocalZ);
            Vector3 driverTarget = ToDriverPosition(actor.Character, targetRoot);
            bool teleportResolved = false;
            bool teleportAccepted = false;
            motion.RequestTeleport(
                driverTarget,
                float.NaN,
                true,
                result =>
                {
                    teleportResolved = true;
                    teleportAccepted = result.approved;
                });
            NetworkCiTrace.Log(
                "fusion-pullup-smoke",
                "pullup-position-requested",
                actor.NetworkId,
                0,
                $"attempt={attemptIndex} targetLocalZ={startLocalZ:F3} " +
                $"targetRoot={targetRoot:F3} driverTarget={driverTarget:F3}",
                actor);

            float positionDeadline = Mathf.Min(timeout, Elapsed + 5f);
            bool positioned = false;
            while (Elapsed < positionDeadline)
            {
                if (teleportResolved && !teleportAccepted)
                {
                    metrics.Fail(
                        "host-pullup-position-rejected",
                        $"Authority rejected the setup teleport for PullUp attempt {attemptIndex}.");
                    completed(metrics);
                    yield break;
                }

                if (teleportResolved &&
                    Vector3.Distance(actor.transform.position, targetRoot) <= 0.2f)
                {
                    // This authored point is deliberately mid-wall and therefore airborne
                    // until Free Climb installs its stance. Enter immediately after the
                    // accepted network teleport instead of waiting for gravity to "settle" it.
                    positioned = true;
                    break;
                }

                yield return null;
            }

            if (!teleportResolved || !teleportAccepted || !positioned)
            {
                metrics.Fail(
                    "host-pullup-position-timeout",
                    $"Host did not settle at the authored Free_Climb B setup for attempt {attemptIndex}.");
                completed(metrics);
                yield break;
            }

            if (ShortcutPlayer.Instance != actor.gameObject ||
                actor.Character == null || !actor.Character.IsPlayer || !actor.IsOwnerInstance)
            {
                metrics.Fail(
                    "host-pullup-owner-invalid",
                    "The authenticated Fusion Host owner or GC2 ShortcutPlayer was not healthy.");
                completed(metrics);
                yield break;
            }

            Action linkEntered = () => metrics.LinkEnterCount++;
            Action linkExited = () => metrics.LinkExitCount++;
            pullUp.EventCharacterEnter += linkEntered;
            pullUp.EventCharacterExit += linkExited;
            bool attemptCompleted = false;
            void CompleteAttempt()
            {
                if (attemptCompleted) return;
                attemptCompleted = true;
                pullUp.EventCharacterEnter -= linkEntered;
                pullUp.EventCharacterExit -= linkExited;
                ReleaseUpInput();
                completed(metrics);
            }

            _ = freeClimb.Enter(ShortcutPlayer.Instance.GetComponent<Character>(), default);
            NetworkCiTrace.Log(
                "fusion-pullup-smoke",
                "pullup-free-climb-enter-requested",
                actor.NetworkId,
                0,
                $"attempt={attemptIndex} traverse='{HierarchyPath(freeClimb.transform)}'",
                actor);

            float attachDeadline = Mathf.Min(timeout, Elapsed + 5f);
            while (Elapsed < attachDeadline &&
                   !ReferenceEquals(GetStance(actor.Character)?.Traverse, freeClimb))
            {
                if (TryMarkAttemptFatal(metrics, attemptIndex))
                {
                    CompleteAttempt();
                    yield break;
                }

                yield return null;
            }

            if (!ReferenceEquals(GetStance(actor.Character)?.Traverse, freeClimb))
            {
                metrics.Fail(
                    "host-pullup-free-climb-no-attach",
                    $"Host did not attach to Free_Climb for PullUp attempt {attemptIndex}.");
                CompleteAttempt();
                yield break;
            }

            yield return new WaitForSecondsRealtime(AttachSettleSeconds);
            HoldUpInput(actor.Character);

            Traverse previousTraverse = freeClimb;
            float transitionDeadline = Mathf.Min(timeout, Elapsed + 8f);
            while (Elapsed < transitionDeadline)
            {
                if (TryMarkAttemptFatal(metrics, attemptIndex))
                {
                    CompleteAttempt();
                    yield break;
                }

                stance = GetStance(actor.Character);
                Traverse active = stance?.Traverse;
                Vector3 current = actor.transform.position;

                if (!metrics.BoundaryCaptured && ReferenceEquals(active, freeClimb) &&
                    TryGetStanceRelativePosition(stance, out Vector3 relativePosition))
                {
                    if (relativePosition.z >=
                        freeClimb.PositionB - BoundaryOffsetFromPositionB)
                    {
                        CaptureBoundary(metrics, current);
                        metrics.ExpectedBoundaryPosition =
                            CalculateCharacterRootAtInteractiveLocalZ(
                                actor.Character,
                                freeClimb,
                                freeClimb.PositionB);
                        metrics.BoundaryAlignmentError = Vector3.Distance(
                            current,
                            metrics.ExpectedBoundaryPosition);
                        NetworkCiTrace.Log(
                            "fusion-pullup-smoke",
                            "pullup-boundary-reached",
                            actor.NetworkId,
                            0,
                            $"attempt={attemptIndex} relative={relativePosition:F4} " +
                            $"position={current:F4} positionB={freeClimb.PositionB:F3} " +
                            $"expected={metrics.ExpectedBoundaryPosition:F4} " +
                            $"error={metrics.BoundaryAlignmentError:F4}",
                            actor);
                    }
                }

                if (metrics.BoundaryCaptured && !metrics.LinkEntered)
                {
                    SamplePreEnter(metrics, current);
                }

                if (ReferenceEquals(active, pullUp) && !ReferenceEquals(previousTraverse, pullUp))
                {
                    metrics.StanceLinkEntryCount++;
                }
                if (!ReferenceEquals(active, pullUp) && ReferenceEquals(previousTraverse, pullUp))
                {
                    metrics.StanceLinkExitCount++;
                }

                if (ReferenceEquals(active, pullUp) && !metrics.LinkEntered)
                {
                    if (!metrics.BoundaryCaptured)
                    {
                        metrics.Fail(
                            "host-pullup-boundary-not-observed",
                            $"PullUp attempt {attemptIndex} entered before the authored Free_Climb B boundary was sampled.");
                        CompleteAttempt();
                        yield break;
                    }

                    BeginLinkTrajectory(metrics, actor.Character, pullUp, current);
                    ReleaseUpInput();
                    NetworkCiTrace.Log(
                        "fusion-pullup-smoke",
                        "pullup-link-enter",
                        actor.NetworkId,
                        0,
                        $"attempt={attemptIndex} boundary={metrics.BoundaryPosition:F4} " +
                        $"entry={current:F4} expected={metrics.ExpectedEndPosition:F4} " +
                        $"entryRollback={metrics.EntryRollback:F4} " +
                        $"entryWarp={metrics.EntryWarpDistance:F4} " +
                        $"nominalWarp={metrics.NominalWarpDistance:F4}",
                        actor);
                }

                if (metrics.LinkEntered)
                {
                    SampleTrajectory(metrics, current, pullUp);
                    if (!ReferenceEquals(active, pullUp))
                    {
                        metrics.LinkExited = true;
                        NetworkCiTrace.Log(
                            "fusion-pullup-smoke",
                            "pullup-link-exit",
                            actor.NetworkId,
                            0,
                            $"attempt={attemptIndex} position={current:F4} " +
                            $"samples={metrics.SampleCount} enters={metrics.LinkEnterCount} " +
                            $"exits={metrics.LinkExitCount}",
                            actor);
                        break;
                    }
                }

                previousTraverse = active;
                yield return null;
            }

            if (!metrics.LinkEntered || !metrics.LinkExited)
            {
                metrics.Fail(
                    !metrics.LinkEntered
                        ? "host-pullup-link-no-enter"
                        : "host-pullup-link-no-exit",
                    $"Host did not complete the authored PullUp link in attempt {attemptIndex}.");
                CompleteAttempt();
                yield break;
            }

            float finalSettleEndsAt = Mathf.Min(timeout, Elapsed + FinalPoseSettleSeconds);
            while (Elapsed < finalSettleEndsAt)
            {
                if (TryMarkAttemptFatal(metrics, attemptIndex))
                {
                    CompleteAttempt();
                    yield break;
                }

                metrics.FinalPosition = actor.transform.position;
                SampleTrajectory(metrics, metrics.FinalPosition, pullUp);
                yield return null;
            }

            CaptureFinalLanding(metrics, actor.transform.position);
            ValidateAttempt(metrics, observer: false);
            CompleteAttempt();
        }

        private IEnumerator RunHostObserver()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            NetworkCharacter localOwner = null;
            FusionTransportBridge transport = null;
            TraverseInteractive freeClimb = null;
            TraverseLink pullUp = null;
            var aggregate = new AggregateMetrics();
            var ownerProbe = new OwnerHealthProbe();

            while (Elapsed < timeout)
            {
                actor ??= FindRemotePlayer();
                localOwner ??= FindLocalPlayer();
                transport ??= UnityObjectSearch.FindAny<FusionTransportBridge>();
                if (freeClimb == null || pullUp == null)
                {
                    TryFindExactFixture(out freeClimb, out pullUp);
                }

                if (actor != null && localOwner != null && transport != null &&
                    transport.IsLocalGameplayReady && freeClimb != null && pullUp != null)
                {
                    if (!WriteReadyMarker())
                    {
                        Finish(
                            false,
                            "observer-ready-marker-failed",
                            "Connected PullUp observer could not signal readiness to the Host.",
                            actor,
                            true);
                        yield break;
                    }

                    break;
                }

                yield return new WaitForSecondsRealtime(0.05f);
            }

            if (actor == null || localOwner == null || freeClimb == null || pullUp == null)
            {
                Finish(
                    false,
                    "observer-fixture-or-peer-missing",
                    "Timed out resolving both built peers and the exact Free_Climb -> PullUp fixture.",
                    actor,
                    fixtureResolved: freeClimb != null && pullUp != null);
                yield break;
            }

            for (int attemptIndex = 1; attemptIndex <= AttemptsRequired; attemptIndex++)
            {
                if (TryFailFatal(actor, aggregate, true, ownerProbe)) yield break;

                AttemptMetrics attempt = null;
                yield return RunObserverAttempt(
                    actor,
                    localOwner,
                    freeClimb,
                    pullUp,
                    ownerProbe,
                    attemptIndex,
                    timeout,
                    value => attempt = value);

                if (attempt == null)
                {
                    Finish(
                        false,
                        "observer-pullup-attempt-missing",
                        "The observer PullUp attempt did not produce trajectory metrics.",
                        actor,
                        true,
                        aggregate,
                        ownerProbe);
                    yield break;
                }

                aggregate.Merge(attempt);
                LogAttemptResult(actor, attempt, observer: true);
                if (!attempt.Passed)
                {
                    Finish(
                        false,
                        attempt.FailurePhase,
                        attempt.FailureMessage,
                        actor,
                        true,
                        aggregate,
                        ownerProbe);
                    yield break;
                }

                if (!WriteAttemptAcknowledgement(attemptIndex))
                {
                    Finish(
                        false,
                        "observer-pullup-ack-write-failed",
                        $"Connected observer could not acknowledge PullUp attempt {attemptIndex}.",
                        actor,
                        true,
                        aggregate,
                        ownerProbe);
                    yield break;
                }
            }

            ReleaseConnectedOwnerInput();
            bool ownerHealthy = ownerProbe.MovementTested && ownerProbe.InputHealthy &&
                                ownerProbe.MoveDistance >= RequiredConnectedOwnerMoveDistance &&
                                ownerProbe.TraversalMovementTested &&
                                ownerProbe.TraversalMoveDistance >=
                                RequiredConnectedOwnerMoveDistance;
            Finish(
                ownerHealthy,
                ownerHealthy
                    ? "observer-host-pullup-stable-local-owner-healthy"
                    : "observer-host-pullup-local-owner-unhealthy",
                ownerHealthy
                    ? "Connected client observed three stable Host PullUp trajectories while retaining authenticated local movement control."
                    : "Host PullUp completed, but the connected authenticated owner did not retain normal local movement control.",
                actor,
                true,
                aggregate,
                ownerProbe);
        }

        private IEnumerator RunObserverAttempt(
            NetworkCharacter actor,
            NetworkCharacter localOwner,
            TraverseInteractive freeClimb,
            TraverseLink pullUp,
            OwnerHealthProbe ownerProbe,
            int attemptIndex,
            int timeout,
            Action<AttemptMetrics> completed)
        {
            var metrics = new AttemptMetrics { Attempt = attemptIndex };
            Action linkEntered = () => metrics.LinkEnterCount++;
            Action linkExited = () => metrics.LinkExitCount++;
            pullUp.EventCharacterEnter += linkEntered;
            pullUp.EventCharacterExit += linkExited;
            bool attemptCompleted = false;
            void CompleteAttempt()
            {
                if (attemptCompleted) return;
                attemptCompleted = true;
                pullUp.EventCharacterEnter -= linkEntered;
                pullUp.EventCharacterExit -= linkExited;
                completed(metrics);
            }

            Traverse previousTraverse = GetStance(actor.Character)?.Traverse;
            bool observedFreeClimb = false;
            Vector3 lastFreeClimbPosition = default;
            float transitionDeadline = Mathf.Min(timeout, Elapsed + 12f);
            while (Elapsed < transitionDeadline)
            {
                if (TryMarkAttemptFatal(metrics, attemptIndex))
                {
                    CompleteAttempt();
                    yield break;
                }

                if (actor == null || !actor.isActiveAndEnabled)
                {
                    metrics.Fail(
                        "observer-host-pullup-disconnected",
                        "The observed Fusion Host actor despawned during PullUp.");
                    CompleteAttempt();
                    yield break;
                }

                TraversalStance stance = GetStance(actor.Character);
                Traverse active = stance?.Traverse;
                Vector3 current = GetPresentedPosition(actor);
                UpdateConnectedOwnerProbe(
                    localOwner,
                    ownerProbe,
                    ReferenceEquals(active, freeClimb) || ReferenceEquals(active, pullUp));

                if (ReferenceEquals(active, freeClimb))
                {
                    observedFreeClimb = true;
                    lastFreeClimbPosition = current;
                }

                if (!metrics.BoundaryCaptured && ReferenceEquals(active, freeClimb) &&
                    TryGetStanceRelativePosition(stance, out Vector3 relativePosition))
                {
                    if (relativePosition.z >=
                        freeClimb.PositionB - BoundaryOffsetFromPositionB)
                    {
                        CaptureBoundary(metrics, current);
                        NetworkCiTrace.Log(
                            "fusion-pullup-smoke",
                            "pullup-boundary-reached",
                            actor.NetworkId,
                            0,
                            $"observer=True attempt={attemptIndex} " +
                            $"relative={relativePosition:F4} " +
                            $"presented={current:F4} root={actor.transform.position:F4}",
                            actor);
                    }
                }

                if (metrics.BoundaryCaptured && !metrics.LinkEntered)
                {
                    SamplePreEnter(metrics, current);
                }

                if (ReferenceEquals(active, pullUp) && !ReferenceEquals(previousTraverse, pullUp))
                {
                    metrics.StanceLinkEntryCount++;
                }
                if (!ReferenceEquals(active, pullUp) && ReferenceEquals(previousTraverse, pullUp))
                {
                    metrics.StanceLinkExitCount++;
                }

                if (ReferenceEquals(active, pullUp) && !metrics.LinkEntered)
                {
                    if (!metrics.BoundaryCaptured)
                    {
                        // Fusion may coalesce the final Free_Climb relative-position snapshot
                        // with the authoritative PullUp state. The Host still proves the exact
                        // PositionB boundary; the observer starts its presentation trajectory
                        // from the last Free_Climb pose it actually rendered.
                        if (!observedFreeClimb)
                        {
                            metrics.Fail(
                                "observer-host-pullup-free-climb-not-observed",
                                $"Observer received PullUp attempt {attemptIndex} without " +
                                "first rendering that attempt's Free_Climb state.");
                            CompleteAttempt();
                            yield break;
                        }

                        CaptureBoundary(metrics, lastFreeClimbPosition);
                        SamplePreEnter(metrics, current);
                        NetworkCiTrace.Log(
                            "fusion-pullup-smoke",
                            "pullup-boundary-coalesced",
                            actor.NetworkId,
                            0,
                            $"observer=True attempt={attemptIndex} " +
                            $"observedFreeClimb={observedFreeClimb} " +
                            $"boundary={metrics.BoundaryPosition:F4} entry={current:F4}",
                            actor);
                    }

                    BeginLinkTrajectory(metrics, actor.Character, pullUp, current);
                    NetworkCiTrace.Log(
                        "fusion-pullup-smoke",
                        "pullup-link-enter",
                        actor.NetworkId,
                        0,
                        $"observer=True attempt={attemptIndex} " +
                        $"boundary={metrics.BoundaryPosition:F4} entry={current:F4} " +
                        $"expected={metrics.ExpectedEndPosition:F4} " +
                        $"entryRollback={metrics.EntryRollback:F4}",
                        actor);
                }

                if (metrics.LinkEntered)
                {
                    SampleTrajectory(metrics, current, pullUp);
                    if (!ReferenceEquals(active, pullUp))
                    {
                        metrics.LinkExited = true;
                        NetworkCiTrace.Log(
                            "fusion-pullup-smoke",
                            "pullup-link-exit",
                            actor.NetworkId,
                            0,
                            $"observer=True attempt={attemptIndex} presented={current:F4} " +
                            $"samples={metrics.SampleCount}",
                            actor);
                        break;
                    }
                }

                previousTraverse = active;
                yield return null;
            }

            if (!metrics.LinkEntered || !metrics.LinkExited)
            {
                metrics.Fail(
                    !metrics.LinkEntered
                        ? "observer-host-pullup-link-no-enter"
                        : "observer-host-pullup-link-no-exit",
                    $"Observer did not receive the complete Host PullUp cycle for attempt {attemptIndex}.");
                CompleteAttempt();
                yield break;
            }

            float finalSettleEndsAt = Mathf.Min(timeout, Elapsed + FinalPoseSettleSeconds);
            while (Elapsed < finalSettleEndsAt)
            {
                UpdateConnectedOwnerProbe(localOwner, ownerProbe, traversalStressActive: true);
                metrics.FinalPosition = GetPresentedPosition(actor);
                SampleTrajectory(metrics, metrics.FinalPosition, pullUp);
                yield return null;
            }

            CaptureFinalLanding(metrics, GetPresentedPosition(actor));
            ValidateAttempt(metrics, observer: true);
            CompleteAttempt();
        }

        private static void CaptureBoundary(AttemptMetrics metrics, Vector3 position)
        {
            metrics.BoundaryCaptured = true;
            metrics.BoundaryPosition = position;
            metrics.MinimumPreEnterY = position.y;
            metrics.LastPreEnterPosition = position;
            metrics.PreEnterSampleCount = 1;
        }

        private static void SamplePreEnter(AttemptMetrics metrics, Vector3 position)
        {
            if (!metrics.BoundaryCaptured) return;
            float reverseStep = Mathf.Max(0f, metrics.LastPreEnterPosition.y - position.y);
            metrics.MinimumPreEnterY = Mathf.Min(metrics.MinimumPreEnterY, position.y);
            metrics.PreEnterDrop = Mathf.Max(
                metrics.PreEnterDrop,
                metrics.BoundaryPosition.y - metrics.MinimumPreEnterY);
            metrics.MaxPreEnterReverseStep = Mathf.Max(
                metrics.MaxPreEnterReverseStep,
                reverseStep);
            metrics.LastPreEnterPosition = position;
            metrics.PreEnterSampleCount++;
        }

        private static void BeginLinkTrajectory(
            AttemptMetrics metrics,
            Character character,
            TraverseLink pullUp,
            Vector3 position)
        {
            metrics.LinkEntered = true;
            metrics.PullUpEntryPosition = position;
            metrics.LinkWarpPosition = CalculateExpectedLinkRoot(character, pullUp);
            metrics.LandingSurfaceResolved = TryCalculateLandingBounds(
                pullUp,
                out Bounds landingBounds);
            metrics.LandingBounds = landingBounds;
            metrics.LandingRootY = metrics.LandingSurfaceResolved
                ? landingBounds.max.y + character.Motion.Height * 0.5f +
                  character.Driver.SkinWidth
                : metrics.LinkWarpPosition.y;
            metrics.ExpectedEndPosition = metrics.LandingSurfaceResolved
                ? new Vector3(
                    Mathf.Clamp(
                        metrics.LinkWarpPosition.x,
                        landingBounds.min.x,
                        landingBounds.max.x),
                    metrics.LandingRootY,
                    Mathf.Clamp(
                        metrics.LinkWarpPosition.z,
                        landingBounds.min.z,
                        landingBounds.max.z))
                : metrics.LinkWarpPosition;
            metrics.EntryRollback = Mathf.Max(0f, metrics.BoundaryPosition.y - position.y);
            metrics.NominalWarpDistance = Vector3.Distance(
                metrics.BoundaryPosition,
                metrics.LinkWarpPosition);
            metrics.EntryWarpDistance = Vector3.Distance(position, metrics.LinkWarpPosition);
            metrics.MinimumY = Mathf.Min(metrics.BoundaryPosition.y, position.y);
            metrics.MaximumY = Mathf.Max(metrics.BoundaryPosition.y, position.y);
            metrics.LastTrajectoryPosition = metrics.BoundaryPosition;
            metrics.HighWaterProgress = 0f;
        }

        private static void SampleTrajectory(
            AttemptMetrics metrics,
            Vector3 position,
            TraverseLink pullUp)
        {
            // PullUp is not a straight line to the initial warp point. The authored clip
            // aligns there during its first 8.33%, then contributes about 1.5 m of upward
            // root motion and lands on the platform. Vertical high-water tracking catches
            // rollback without classifying that intended root motion as lateral error.
            float progress = position.y - metrics.BoundaryPosition.y;
            float previousProgress =
                metrics.LastTrajectoryPosition.y - metrics.BoundaryPosition.y;
            float reverseStep = Mathf.Max(0f, previousProgress - progress);
            metrics.ReverseDistance += reverseStep;
            metrics.MaxReverseStep = Mathf.Max(metrics.MaxReverseStep, reverseStep);
            metrics.HighWaterProgress = Mathf.Max(metrics.HighWaterProgress, progress);
            metrics.MaxRollbackFromHighWater = Mathf.Max(
                metrics.MaxRollbackFromHighWater,
                metrics.HighWaterProgress - progress);
            metrics.MaxFrameStep = Mathf.Max(
                metrics.MaxFrameStep,
                Vector3.Distance(metrics.LastTrajectoryPosition, position));

            metrics.MaxLateralDeviation = Mathf.Max(
                metrics.MaxLateralDeviation,
                DistanceOutsideLandingCorridor(metrics, position));

            metrics.MinimumY = Mathf.Min(metrics.MinimumY, position.y);
            metrics.MaximumY = Mathf.Max(metrics.MaximumY, position.y);
            float lowerBound = Mathf.Min(
                metrics.BoundaryPosition.y,
                metrics.ExpectedEndPosition.y) - GroundTeleportMargin;
            float upperBound = metrics.LandingRootY + AirLaunchMargin;
            if (position.y < lowerBound) metrics.GroundTeleportCount++;
            if (position.y > upperBound) metrics.AirLaunchCount++;
            metrics.MaxLinkOccupancy = Mathf.Max(
                metrics.MaxLinkOccupancy,
                pullUp.CharactersUsingCount);
            metrics.LastTrajectoryPosition = position;
            metrics.SampleCount++;
        }

        private static void ValidateAttempt(AttemptMetrics metrics, bool observer)
        {
            metrics.DuplicateTransitionCount =
                Mathf.Max(0, metrics.LinkEnterCount - 1) +
                Mathf.Max(0, metrics.LinkExitCount - 1) +
                Mathf.Max(0, metrics.StanceLinkEntryCount - 1) +
                Mathf.Max(0, metrics.StanceLinkExitCount - 1) +
                Mathf.Max(0, metrics.MaxLinkOccupancy - 1);

            bool transitionCountsValid =
                metrics.StanceLinkEntryCount == 1 &&
                metrics.StanceLinkExitCount == 1 &&
                metrics.LinkEnterCount <= 1 &&
                metrics.LinkExitCount <= 1 &&
                (observer || metrics.LinkEnterCount == 1 && metrics.LinkExitCount == 1);
            if (!transitionCountsValid || metrics.DuplicateTransitionCount != 0)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-duplicate-transition"
                        : "host-pullup-duplicate-transition",
                    $"PullUp attempt {metrics.Attempt} had invalid transition counts: " +
                    $"events={metrics.LinkEnterCount}/{metrics.LinkExitCount}, " +
                    $"stance={metrics.StanceLinkEntryCount}/{metrics.StanceLinkExitCount}, " +
                    $"occupancy={metrics.MaxLinkOccupancy}.");
                return;
            }

            if (!metrics.LandingSurfaceResolved)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-landing-surface-missing"
                        : "host-pullup-landing-surface-missing",
                    $"PullUp attempt {metrics.Attempt} could not resolve the authored platform colliders.");
                return;
            }

            if (!observer &&
                metrics.BoundaryAlignmentError > MaximumHostBoundaryAlignmentError)
            {
                metrics.Fail(
                    "host-pullup-boundary-misaligned",
                    $"PullUp attempt {metrics.Attempt} reached GC2 PositionB without " +
                    $"the authoritative root: error={metrics.BoundaryAlignmentError:F3}, " +
                    $"actual={metrics.BoundaryPosition:F3}, " +
                    $"expected={metrics.ExpectedBoundaryPosition:F3}.");
                return;
            }

            float maxPreEnterDrop = observer
                ? MaximumObserverPreEnterDrop
                : MaximumHostPreEnterDrop;
            float maxPreEnterStep = observer
                ? MaximumObserverPreEnterReverseStep
                : MaximumHostPreEnterReverseStep;
            float maxEntryRollback = observer
                ? MaximumObserverEntryRollback
                : MaximumHostEntryRollback;
            if (metrics.PreEnterDrop > maxPreEnterDrop ||
                metrics.MaxPreEnterReverseStep > maxPreEnterStep ||
                metrics.EntryRollback > maxEntryRollback ||
                metrics.EntryWarpDistance >
                metrics.NominalWarpDistance + MaximumEntryWarpInflation)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-pre-enter-rollback"
                        : "host-pullup-pre-enter-rollback",
                    $"PullUp attempt {metrics.Attempt} corrected downward before link entry: " +
                    $"drop={metrics.PreEnterDrop:F3}, step={metrics.MaxPreEnterReverseStep:F3}, " +
                    $"entryRollback={metrics.EntryRollback:F3}, " +
                    $"warp={metrics.EntryWarpDistance:F3}/{metrics.NominalWarpDistance:F3}.");
                return;
            }

            if (metrics.GroundTeleportCount != 0)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-ground-teleport"
                        : "host-pullup-ground-teleport",
                    $"PullUp attempt {metrics.Attempt} dropped below its authored vertical envelope.");
                return;
            }

            if (metrics.AirLaunchCount != 0)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-air-launch"
                        : "host-pullup-air-launch",
                    $"PullUp attempt {metrics.Attempt} rose above its authored vertical envelope.");
                return;
            }

            float maxReverseDistance = observer
                ? MaximumObserverReverseDistance
                : MaximumHostReverseDistance;
            float maxReverseStep = observer
                ? MaximumObserverReverseStep
                : MaximumHostReverseStep;
            float maxRollback = observer
                ? MaximumObserverRollbackFromHighWater
                : MaximumHostRollbackFromHighWater;
            float maxFrameStep = observer
                ? MaximumObserverFrameStep
                : MaximumHostFrameStep;
            float maxLateral = observer
                ? MaximumObserverLateralDeviation
                : MaximumHostLateralDeviation;
            if (metrics.ReverseDistance > maxReverseDistance ||
                metrics.MaxReverseStep > maxReverseStep ||
                metrics.MaxRollbackFromHighWater > maxRollback ||
                metrics.MaxFrameStep > maxFrameStep ||
                metrics.MaxLateralDeviation > maxLateral)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-snap-back"
                        : "host-pullup-snap-back",
                    $"PullUp attempt {metrics.Attempt} left its smooth authored trajectory: " +
                    $"reverse={metrics.ReverseDistance:F3}, " +
                    $"maxReverse={metrics.MaxReverseStep:F3}, " +
                    $"rollback={metrics.MaxRollbackFromHighWater:F3}, " +
                    $"frame={metrics.MaxFrameStep:F3}, lateral={metrics.MaxLateralDeviation:F3}.");
                return;
            }

            float maxEndpointError = observer
                ? MaximumObserverEndpointError
                : MaximumHostEndpointError;
            if (metrics.SampleCount < RequiredTrajectorySamples ||
                metrics.FinalTargetError > maxEndpointError)
            {
                metrics.Fail(
                    observer
                        ? "observer-host-pullup-endpoint-miss"
                        : "host-pullup-endpoint-miss",
                    $"PullUp attempt {metrics.Attempt} did not settle at its authored endpoint: " +
                    $"samples={metrics.SampleCount}, error={metrics.FinalTargetError:F3}.");
                return;
            }

            metrics.Passed = true;
        }

        private void UpdateConnectedOwnerProbe(
            NetworkCharacter localOwner,
            OwnerHealthProbe probe,
            bool traversalStressActive)
        {
            if (localOwner?.Character == null) return;
            bool healthy = localOwner.IsOwnerInstance && localOwner.IsPlayerOwnedActor &&
                           localOwner.Character.IsPlayer &&
                           localOwner.Character.Player != null &&
                           localOwner.Character.Player.IsControllable &&
                           ShortcutPlayer.Instance == localOwner.gameObject;
            probe.InputHealthy &= healthy;
            if (!probe.Started)
            {
                probe.Started = true;
                probe.StartPosition = localOwner.transform.position;
                NetworkCiTrace.Log(
                    "fusion-pullup-smoke",
                    "connected-owner-move-start",
                    localOwner.NetworkId,
                    0,
                    $"position={probe.StartPosition:F3} healthy={healthy}",
                    localOwner);
            }

            if (traversalStressActive && !probe.TraversalStressStarted)
            {
                probe.TraversalStressStarted = true;
                probe.TraversalStartPosition = localOwner.transform.position;
                NetworkCiTrace.Log(
                    "fusion-pullup-smoke",
                    "connected-owner-traversal-move-start",
                    localOwner.NetworkId,
                    0,
                    $"position={probe.TraversalStartPosition:F3} healthy={healthy}",
                    localOwner);
            }

            probe.MoveDistance = HorizontalDistance(
                probe.StartPosition,
                localOwner.transform.position);
            if (probe.TraversalStressStarted)
            {
                probe.TraversalMoveDistance = HorizontalDistance(
                    probe.TraversalStartPosition,
                    localOwner.transform.position);
            }

            if (localOwner.Character.Player is UnitPlayerDirectionalNetwork player)
            {
                bool needsBaselineMovement =
                    probe.MoveDistance < RequiredConnectedOwnerMoveDistance;
                bool needsTraversalMovement = probe.TraversalStressStarted &&
                    probe.TraversalMoveDistance < RequiredConnectedOwnerMoveDistance;
                if (needsBaselineMovement || needsTraversalMovement)
                {
                    AimSmokeCamera(localOwner.Character, Vector3.right);
                    player.InjectInput(Vector2.up);
                    probe.MovementTested |= needsBaselineMovement;
                    probe.TraversalMovementTested |= needsTraversalMovement;
                }
                else
                {
                    player.InjectInput(Vector2.zero);
                }
            }
        }

        private void AimSmokeCamera(Character character, Vector3 worldDirection)
        {
            if (character?.Player is not UnitPlayerDirectionalNetwork player) return;
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude <= 0.0001f) return;

            if (m_SmokeCamera == null)
            {
                var owner = new GameObject("Fusion Traversal PullUp Smoke Input Camera");
                owner.transform.SetParent(transform, false);
                m_SmokeCamera = owner.transform;
                player.SetCamera(m_SmokeCamera);
            }

            m_SmokeCamera.rotation = Quaternion.LookRotation(worldDirection.normalized, Vector3.up);
        }

        private void HoldUpInput(Character character)
        {
            if (character?.Player is not UnitPlayerDirectionalNetwork) return;
            m_SmokeKeyboard ??= Keyboard.current ??
                InputSystem.AddDevice<Keyboard>("GC2 Fusion Traversal PullUp Smoke Keyboard");
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState(Key.W));
        }

        private void ReleaseUpInput()
        {
            if (m_SmokeKeyboard == null) return;
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState());
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

        private static bool TryFindExactFixture(
            out TraverseInteractive freeClimb,
            out TraverseLink pullUp)
        {
            freeClimb = null;
            pullUp = null;
            foreach (TraverseInteractive candidate in
                     UnityObjectSearch.FindAll<TraverseInteractive>(FindObjectsInactive.Exclude)
                         .Where(value => value != null)
                         .OrderBy(value => HierarchyPath(value.transform), StringComparer.Ordinal))
            {
                Transform root = FindAncestorNamed(candidate.transform, FreeClimbRootName);
                if (root == null ||
                    Vector3.Distance(root.position, ExactFreeClimbRootPosition) >
                    ExactFixtureTolerance ||
                    candidate.ContinueB is not TraverseLink candidatePullUp ||
                    !string.Equals(candidatePullUp.name, PullUpName, StringComparison.Ordinal) ||
                    Vector3.Distance(candidatePullUp.transform.position, ExactPullUpRootPosition) >
                    ExactFixtureTolerance ||
                    candidatePullUp.MotionLink == null ||
                    !string.Equals(
                        candidatePullUp.MotionLink.name,
                        PullUpMotionName,
                        StringComparison.Ordinal) ||
                    candidatePullUp.Type is not TraverseLinkTypeWarpToTarget)
                {
                    continue;
                }

                freeClimb = candidate;
                pullUp = candidatePullUp;
                return true;
            }

            return false;
        }

        private static Transform FindAncestorNamed(Transform value, string name)
        {
            for (Transform current = value; current != null; current = current.parent)
            {
                if (string.Equals(current.name, name, StringComparison.Ordinal)) return current;
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

        private static bool TryGetStanceRelativePosition(
            TraversalStance stance,
            out Vector3 relativePosition)
        {
            if (stance != null &&
                s_TraversalStanceRelativePositionProperty?.GetValue(stance) is Vector3 value)
            {
                relativePosition = value;
                return true;
            }

            relativePosition = default;
            return false;
        }

        private static Vector3 CalculateCharacterRootAtInteractiveLocalZ(
            Character character,
            TraverseInteractive interactive,
            float localZ)
        {
            Vector3 targetAnchor = interactive.Transform.TransformPoint(
                new Vector3(0f, 0f, localZ));
            Vector3 anchorOffset = interactive.MotionInteractive.CharacterPosition(character) -
                                   character.transform.position;
            return targetAnchor - anchorOffset;
        }

        private static Vector3 CalculateExpectedLinkRoot(
            Character character,
            TraverseLink pullUp)
        {
            TraverseLinkData data = pullUp.Type.ToTraverseLinkData(character, pullUp);
            Vector3 targetAnchor = pullUp.Transform.TransformPoint(data.positionB);
            Vector3 anchorOffset = pullUp.MotionLink.CharacterPosition(character) -
                                   character.transform.position;
            return targetAnchor - anchorOffset;
        }

        private static bool TryCalculateLandingBounds(
            TraverseLink pullUp,
            out Bounds landingBounds)
        {
            landingBounds = default;
            if (pullUp == null) return false;

            bool found = false;
            foreach (Collider collider in pullUp.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || !collider.enabled || collider.isTrigger) continue;
                if (!found)
                {
                    landingBounds = collider.bounds;
                    found = true;
                }
                else
                {
                    landingBounds.Encapsulate(collider.bounds);
                }
            }

            return found;
        }

        private static float DistanceOutsideLandingCorridor(
            AttemptMetrics metrics,
            Vector3 position)
        {
            if (!metrics.LandingSurfaceResolved) return 0f;

            float minimumX = Mathf.Min(
                metrics.BoundaryPosition.x,
                metrics.LinkWarpPosition.x,
                metrics.LandingBounds.min.x);
            float maximumX = Mathf.Max(
                metrics.BoundaryPosition.x,
                metrics.LinkWarpPosition.x,
                metrics.LandingBounds.max.x);
            float minimumZ = Mathf.Min(
                metrics.BoundaryPosition.z,
                metrics.LinkWarpPosition.z,
                metrics.LandingBounds.min.z);
            float maximumZ = Mathf.Max(
                metrics.BoundaryPosition.z,
                metrics.LinkWarpPosition.z,
                metrics.LandingBounds.max.z);

            float outsideX = position.x < minimumX
                ? minimumX - position.x
                : position.x > maximumX
                    ? position.x - maximumX
                    : 0f;
            float outsideZ = position.z < minimumZ
                ? minimumZ - position.z
                : position.z > maximumZ
                    ? position.z - maximumZ
                    : 0f;
            return new Vector2(outsideX, outsideZ).magnitude;
        }

        private static void CaptureFinalLanding(
            AttemptMetrics metrics,
            Vector3 finalPosition)
        {
            metrics.FinalPosition = finalPosition;
            if (!metrics.LandingSurfaceResolved)
            {
                metrics.FinalTargetError = float.MaxValue;
                return;
            }

            metrics.ExpectedEndPosition = new Vector3(
                Mathf.Clamp(
                    finalPosition.x,
                    metrics.LandingBounds.min.x,
                    metrics.LandingBounds.max.x),
                metrics.LandingRootY,
                Mathf.Clamp(
                    finalPosition.z,
                    metrics.LandingBounds.min.z,
                    metrics.LandingBounds.max.z));
            metrics.FinalTargetError = Vector3.Distance(
                finalPosition,
                metrics.ExpectedEndPosition);
        }

        private static Vector3 ToDriverPosition(Character character, Vector3 rootPosition)
        {
            float halfHeight = character != null ? character.Motion.Height * 0.5f : 0f;
            return rootPosition + Vector3.down * halfHeight;
        }

        private static Vector3 GetPresentedPosition(NetworkCharacter actor)
        {
            if (actor == null) return default;
            FusionNativeNetworkCharacterMotor motor =
                actor.GetComponent<FusionNativeNetworkCharacterMotor>();
            Transform target = motor?.ActiveRemotePresentationTarget;
            return target != null ? target.position : actor.transform.position;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        private void ReleaseConnectedOwnerInput()
        {
            NetworkCharacter actor = FindLocalPlayer();
            if (actor?.Character?.Player is UnitPlayerDirectionalNetwork player)
            {
                player.InjectInput(Vector2.zero);
            }
        }

        private bool WriteReadyMarker()
        {
            if (string.IsNullOrWhiteSpace(m_ReadyPath)) return false;
            try
            {
                string fullPath = Path.GetFullPath(m_ReadyPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, "pullup-observer-ready");
                NetworkCiTrace.Log(
                    "fusion-pullup-smoke",
                    "observer-ready",
                    0,
                    0,
                    $"marker='{fullPath}'",
                    this);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[GC2 Fusion Traversal PullUp Smoke] Could not write ready marker: " +
                    exception.Message);
                return false;
            }
        }

        private bool IsReadyMarkerPresent()
        {
            return !string.IsNullOrWhiteSpace(m_ReadyPath) &&
                   File.Exists(Path.GetFullPath(m_ReadyPath));
        }

        private bool WriteAttemptAcknowledgement(int attemptIndex)
        {
            if (string.IsNullOrWhiteSpace(m_ReadyPath)) return false;
            try
            {
                string fullPath = AttemptAcknowledgementPath(attemptIndex);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, "pullup-observer-attempt-complete");
                NetworkCiTrace.Log(
                    "fusion-pullup-smoke",
                    "observer-attempt-acknowledged",
                    0,
                    0,
                    $"attempt={attemptIndex} marker='{fullPath}'",
                    this);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[GC2 Fusion Traversal PullUp Smoke] Could not acknowledge " +
                    $"attempt {attemptIndex}: {exception.Message}");
                return false;
            }
        }

        private bool IsAttemptAcknowledgementPresent(int attemptIndex)
        {
            return !string.IsNullOrWhiteSpace(m_ReadyPath) &&
                   File.Exists(AttemptAcknowledgementPath(attemptIndex));
        }

        private string AttemptAcknowledgementPath(int attemptIndex)
        {
            return Path.GetFullPath($"{m_ReadyPath}.attempt-{attemptIndex}.ack");
        }

        private void CaptureTraversalFailure(
            string condition,
            string stackTrace,
            UnityEngine.LogType type)
        {
            if (m_Finished ||
                type != UnityEngine.LogType.Error && type != UnityEngine.LogType.Exception)
            {
                return;
            }

            string combined = (condition ?? string.Empty) + "\n" + (stackTrace ?? string.Empty);
            bool traversalFailure =
                combined.IndexOf(
                    "authoritative traversal task failed",
                    StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("MissingReferenceException", StringComparison.OrdinalIgnoreCase) >= 0 ||
                combined.IndexOf("NetworkTraversal", StringComparison.OrdinalIgnoreCase) >= 0 &&
                combined.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!traversalFailure) return;
            m_FatalTraversalLog = string.IsNullOrWhiteSpace(condition)
                ? type.ToString()
                : condition.Trim();
        }

        private bool TryMarkAttemptFatal(AttemptMetrics metrics, int attemptIndex)
        {
            if (string.IsNullOrEmpty(m_FatalTraversalLog)) return false;
            metrics.Fail(
                "fatal-traversal-log",
                $"PullUp attempt {attemptIndex} emitted a fatal Traversal error: " +
                m_FatalTraversalLog);
            return true;
        }

        private bool TryFailFatal(
            NetworkCharacter actor,
            AggregateMetrics aggregate,
            bool fixtureResolved,
            OwnerHealthProbe ownerProbe = null)
        {
            if (string.IsNullOrEmpty(m_FatalTraversalLog)) return false;
            Finish(
                false,
                "fatal-traversal-log",
                "The PullUp flow emitted a fatal Traversal error.",
                actor,
                fixtureResolved,
                aggregate,
                ownerProbe);
            return true;
        }

        private static void LogAttemptResult(
            NetworkCharacter actor,
            AttemptMetrics attempt,
            bool observer)
        {
            NetworkCiTrace.Log(
                "fusion-pullup-smoke",
                "pullup-attempt-result",
                actor?.NetworkId ?? 0,
                0,
                $"observer={observer} attempt={attempt.Attempt} passed={attempt.Passed} " +
                $"phase='{attempt.FailurePhase}' preDrop={attempt.PreEnterDrop:F4} " +
                $"entryRollback={attempt.EntryRollback:F4} minY={attempt.MinimumY:F4} " +
                $"maxY={attempt.MaximumY:F4} ground={attempt.GroundTeleportCount} " +
                $"air={attempt.AirLaunchCount} reverse={attempt.ReverseDistance:F4} " +
                $"rollback={attempt.MaxRollbackFromHighWater:F4} " +
                $"maxStep={attempt.MaxFrameStep:F4} endpoint={attempt.FinalTargetError:F4} " +
                $"events={attempt.LinkEnterCount}/{attempt.LinkExitCount} " +
                $"stance={attempt.StanceLinkEntryCount}/{attempt.StanceLinkExitCount}",
                actor);
        }

        private void Finish(
            bool passed,
            string phase,
            string message,
            NetworkCharacter actor = null,
            bool fixtureResolved = false,
            AggregateMetrics metrics = null,
            OwnerHealthProbe ownerProbe = null)
        {
            if (m_Finished) return;
            m_Finished = true;
            ReleaseUpInput();
            ReleaseConnectedOwnerInput();

            if (!string.IsNullOrEmpty(m_FatalTraversalLog))
            {
                passed = false;
                phase = "fatal-traversal-log";
            }

            metrics ??= new AggregateMetrics();
            ownerProbe ??= new OwnerHealthProbe();
            var result = new Result
            {
                passed = passed,
                role = m_Role,
                phase = phase,
                message = message,
                actorNetworkId = actor != null ? actor.NetworkId : 0,
                fixtureResolved = fixtureResolved,
                attemptsRequired = AttemptsRequired,
                attemptsCompleted = metrics.AttemptsCompleted,
                boundaryPosition = metrics.BoundaryPosition,
                expectedBoundaryPosition = metrics.ExpectedBoundaryPosition,
                boundaryAlignmentError = metrics.BoundaryAlignmentError,
                pullUpEntryPosition = metrics.PullUpEntryPosition,
                linkWarpPosition = metrics.LinkWarpPosition,
                expectedEndPosition = metrics.ExpectedEndPosition,
                finalPosition = metrics.FinalPosition,
                landingSurfaceResolved = metrics.LandingSurfaceResolved,
                landingBoundsMin = metrics.LandingBounds.min,
                landingBoundsMax = metrics.LandingBounds.max,
                minimumPreEnterY = metrics.MinimumPreEnterY,
                preEnterDrop = metrics.PreEnterDrop,
                maxPreEnterReverseStep = metrics.MaxPreEnterReverseStep,
                preEnterSampleCount = metrics.PreEnterSampleCount,
                entryRollback = metrics.EntryRollback,
                nominalWarpDistance = metrics.NominalWarpDistance,
                entryWarpDistance = metrics.EntryWarpDistance,
                minimumY = metrics.MinimumY,
                maximumY = metrics.MaximumY,
                groundTeleportCount = metrics.GroundTeleportCount,
                airLaunchCount = metrics.AirLaunchCount,
                reverseDistance = metrics.ReverseDistance,
                maxReverseStep = metrics.MaxReverseStep,
                maxRollbackFromHighWater = metrics.MaxRollbackFromHighWater,
                maxFrameStep = metrics.MaxFrameStep,
                maxLateralDeviation = metrics.MaxLateralDeviation,
                finalTargetError = metrics.FinalTargetError,
                sampleCount = metrics.SampleCount,
                linkEnterCount = metrics.LinkEnterCount,
                linkExitCount = metrics.LinkExitCount,
                stanceLinkEntryCount = metrics.StanceLinkEntryCount,
                stanceLinkExitCount = metrics.StanceLinkExitCount,
                maxLinkOccupancy = metrics.MaxLinkOccupancy,
                duplicateTransitionCount = metrics.DuplicateTransitionCount,
                connectedOwnerMovementTested = ownerProbe.MovementTested,
                connectedOwnerInputHealthy = ownerProbe.InputHealthy,
                connectedOwnerMoveDistance = ownerProbe.MoveDistance,
                connectedOwnerTraversalMovementTested =
                    ownerProbe.TraversalMovementTested,
                connectedOwnerTraversalMoveDistance = ownerProbe.TraversalMoveDistance,
                fatalTraversalLog = m_FatalTraversalLog ?? string.Empty,
                elapsedSeconds = Elapsed
            };

            if (!string.IsNullOrWhiteSpace(m_ResultPath))
            {
                string fullPath = Path.GetFullPath(m_ResultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, JsonUtility.ToJson(result, true));
            }

            Debug.Log(
                $"[GC2 Fusion Traversal PullUp Smoke] passed={passed} role={m_Role} " +
                $"phase={phase} actor={result.actorNetworkId} " +
                $"attempts={result.attemptsCompleted}/{AttemptsRequired} " +
                $"preDrop={result.preEnterDrop:F3} entryRollback={result.entryRollback:F3} " +
                $"ground={result.groundTeleportCount} air={result.airLaunchCount} " +
                $"reverse={result.reverseDistance:F3} " +
                $"rollback={result.maxRollbackFromHighWater:F3} " +
                $"maxStep={result.maxFrameStep:F3} endpoint={result.finalTargetError:F3} " +
                $"duplicates={result.duplicateTransitionCount} " +
                $"connectedInputHealthy={result.connectedOwnerInputHealthy} " +
                $"connectedMove={result.connectedOwnerMoveDistance:F3} " +
                $"fatal='{result.fatalTraversalLog}' message='{message}'");
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
