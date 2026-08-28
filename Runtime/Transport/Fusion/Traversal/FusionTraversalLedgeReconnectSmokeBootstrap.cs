#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)
using System;
using System.Collections;
using System.IO;
using System.Linq;
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
    /// Opt-in first-connect/reconnect regression harness for the exact Ledge_Rail_Climb
    /// fixture in the Fusion Climb demo. It is excluded from non-development players.
    /// </summary>
    internal sealed class FusionTraversalLedgeReconnectSmokeBootstrap : MonoBehaviour
    {
        private const string Scenario = "fusion-traversal-ledge-reconnect";
        private const string LedgeRootName = "Ledge_Rail_Climb";
        private const string LedgeMotionName = "Motion_Ledge_Climb";

        private const float InitialClientShellGraceSeconds = 1.5f;
        private const float PositionSettleSeconds = 0.05f;
        private const float BlockerPreparationSettleSeconds = 0.5f;
        private const float HostStartLedgeFraction = 0.1f;
        private const float BlockerLedgeFraction = 0.55f;
        private const float SetupPositionTolerance = 0.2f;
        private const float HostStartLocalZTolerance = 0.2f;
        private const float MaximumBlockerDrift = 0.15f;
        private const float CapsuleClearanceTolerance = 0.08f;
        private const float BoundaryTolerance = 0.12f;
        private const float RequiredReplicatedLocalDirection = 0.2f;
        private const float RequiredIntentX = 0.65f;
        private const float MaximumSpeedXY = 0.25f;
        private const int RequiredStableEdgeSamples = 12;
        private const float RequiredDisconnectGapSeconds = 1f;
        private const float ReconnectObservationGraceSeconds = 4f;
        private const float HostReconnectCompletionGraceSeconds = 7f;
        private const float ExactLedgeFixtureTolerance = 0.05f;

        private static readonly Vector3 ExactLedgeFixtureLocalPosition =
            new Vector3(2.55f, 4f, 1.5f);

        private static readonly int SpeedXYHash = Animator.StringToHash("Speed-XY");
        private static readonly int IntentXHash = Animator.StringToHash("Intent-X");

        [Serializable]
        private struct Result
        {
            public bool passed;
            public string role;
            public string connectionPhase;
            public string phase;
            public string message;
            public uint actorNetworkId;
            public bool sawRemoteShell;
            public bool traversalControllerReady;
            public bool sawInactiveShell;
            public bool activeAtFirstActorSample;
            public int shellFramesBeforeActive;
            public bool liveObserverReadySignaled;
            public bool activeLedgeTraverse;
            public float localBoundaryZ;
            public float boundaryB;
            public bool atRightBoundary;
            public bool replicatedDirectionAvailable;
            public float replicatedLocalDirectionZ;
            public bool animatorParametersAvailable;
            public float intentX;
            public float speedXY;
            public string dominantClip;
            public int stableEdgeSamples;
            public bool sawFirstClient;
            public bool sawFirstDisconnect;
            public bool sawReconnectClient;
            public uint blockerActorNetworkId;
            public bool blockerPrepared;
            public bool blockerActiveLedgeTraverse;
            public float blockerStartLocalZ;
            public float blockerLocalZ;
            public float blockerDrift;
            public float combinedCharacterRadius;
            public float expectedHostStartLocalZ;
            public bool hostStartedLeftOfBlocker;
            public bool hostCrossedBlocker;
            public float hostClearancePastBlocker;
            public float elapsedSeconds;
        }

        private string m_Role;
        private string m_ConnectionPhase;
        private string m_ResultPath;
        private string m_ReadyPath;
        private float m_StartedAt;
        private Keyboard m_SmokeKeyboard;
        private Transform m_SmokeCamera;
        private bool m_Finished;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!TryGetArgument("--gc2-network-smoke-scenario", out string scenario) ||
                !string.Equals(scenario, Scenario, StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("Fusion Traversal Ledge Reconnect Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionTraversalLedgeReconnectSmokeBootstrap>();
        }

        private void OnDisable()
        {
            ReleaseRightInput();
        }

        private async void Start()
        {
            Application.SetStackTraceLogType(UnityEngine.LogType.Log, StackTraceLogType.None);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            m_StartedAt = Time.realtimeSinceStartup;

            TryGetArgument("--gc2-network-smoke-role", out m_Role);
            TryGetArgument("--gc2-network-smoke-connection-phase", out m_ConnectionPhase);
            TryGetArgument("--gc2-network-smoke-result", out m_ResultPath);
            TryGetArgument("--gc2-network-smoke-ready", out m_ReadyPath);
            TryGetArgument("--gc2-network-smoke-session", out string session);
            if (string.IsNullOrWhiteSpace(session))
            {
                session = "GC2-Fusion-Traversal-Ledge-Reconnect-Smoke";
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
                "fusion-ledge-reconnect-smoke",
                "session-started",
                0,
                0,
                $"role={m_Role} connectionPhase={m_ConnectionPhase} " +
                $"session='{session}' region='{region}'",
                this);

            StartCoroutine(m_Role == "fusion-host" ? RunHostOwner() : RunObserver());
        }

        private IEnumerator RunHostOwner()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            TraverseInteractive ledge = null;
            NetworkCharacter currentRemote = null;
            NetworkCharacter firstRemote = null;
            bool sawFirstClient = false;
            bool sawFirstDisconnect = false;
            bool sawReconnectClient = false;
            float firstClientSeenAt = -1f;
            float noClientSince = -1f;
            float reconnectSeenAt = -1f;
            bool teleportRequested = false;
            bool teleportResolved = false;
            bool teleportAccepted = false;
            float positionedAt = -1f;
            bool enterRequested = false;
            bool rightInputHeld = false;
            int stableEdgeSamples = 0;
            EdgeObservation observation = default;
            OverlapObservation overlap = default;

            while (Elapsed < timeout)
            {
                actor ??= FindLocalPlayer();
                ledge ??= FindExactLedge();
                currentRemote = FindRemotePlayer();
                bool firstObserverReady = IsReadyMarkerPresent();

                if (!sawFirstClient && currentRemote != null)
                {
                    sawFirstClient = true;
                    firstRemote = currentRemote;
                    firstClientSeenAt = Elapsed;
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "host-first-client-seen",
                        actor?.NetworkId ?? 0,
                        0,
                        $"remoteActor={currentRemote.NetworkId}",
                        this);
                }
                else if (sawFirstClient && !sawFirstDisconnect && currentRemote == null)
                {
                    if (noClientSince < 0f) noClientSince = Elapsed;
                    if (Elapsed - noClientSince >= RequiredDisconnectGapSeconds)
                    {
                        sawFirstDisconnect = true;
                        NetworkCiTrace.Log(
                            "fusion-ledge-reconnect-smoke",
                            "host-first-client-disconnected",
                            actor?.NetworkId ?? 0,
                            0,
                            "The first standalone client process left the Host session.",
                            this);
                    }
                }
                else if (sawFirstClient && !sawFirstDisconnect && currentRemote != null)
                {
                    noClientSince = -1f;
                }
                else if (sawFirstDisconnect && !sawReconnectClient && currentRemote != null)
                {
                    sawReconnectClient = true;
                    reconnectSeenAt = Elapsed;
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "host-reconnect-client-seen",
                        actor?.NetworkId ?? 0,
                        0,
                        $"remoteActor={currentRemote.NetworkId}",
                        this);
                }

                bool firstBlockerReady = false;
                if (!sawFirstDisconnect && actor != null && firstRemote != null && ledge != null &&
                    IsReadyMarkerPresent())
                {
                    TraversalStance blockerStance = GetStance(firstRemote.Character);
                    firstBlockerReady = ReferenceEquals(blockerStance?.Traverse, ledge);
                    if (firstBlockerReady)
                    {
                        overlap.BlockerPrepared = true;
                        UpdateOverlapObservation(
                            ref overlap,
                            actor,
                            firstRemote,
                            ledge);
                    }
                }

                if (actor == null || ledge == null || !sawFirstClient ||
                    !firstObserverReady ||
                    (!teleportRequested && !firstBlockerReady) ||
                    Elapsed - firstClientSeenAt < InitialClientShellGraceSeconds)
                {
                    yield return new WaitForSecondsRealtime(0.05f);
                    continue;
                }

                if (!teleportRequested)
                {
                    UnitMotionNetworkController motion = actor.MotionController;
                    if (motion == null)
                    {
                        yield return null;
                        continue;
                    }

                    float targetLocalZ = CalculateHostStartLocalZ(ledge);
                    Vector3 target = CalculateCharacterRootAtLedgeLocalZ(
                        actor.Character,
                        ledge,
                        targetLocalZ);
                    Vector3 driverTarget = ToDriverPosition(actor.Character, target);
                    teleportRequested = true;
                    motion.RequestTeleport(
                        driverTarget,
                        float.NaN,
                        true,
                        result =>
                        {
                            teleportResolved = true;
                            teleportAccepted = result.approved;
                            NetworkCiTrace.Log(
                                "fusion-ledge-reconnect-smoke",
                                "host-position-resolved",
                                actor.NetworkId,
                                0,
                                $"approved={result.approved} root={actor.transform.position:F3} " +
                                $"target={target:F3} driverTarget={driverTarget:F3}",
                                actor);
                        });
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "host-position-requested",
                        actor.NetworkId,
                        0,
                        $"target={target:F3} driverTarget={driverTarget:F3} " +
                        $"traverse='{HierarchyPath(ledge.transform)}'",
                        actor);
                }

                if (teleportResolved && !teleportAccepted)
                {
                    Finish(
                        false,
                        "host-position-rejected",
                        "Authority rejected the setup teleport to Ledge_Rail_Climb.",
                        actor,
                        observation,
                        stableEdgeSamples,
                        sawFirstClient,
                        sawFirstDisconnect,
                        sawReconnectClient);
                    yield break;
                }

                float setupLocalZ = CalculateHostStartLocalZ(ledge);
                Vector3 setupTarget = CalculateCharacterRootAtLedgeLocalZ(
                    actor.Character,
                    ledge,
                    setupLocalZ);
                if (!teleportResolved ||
                    (!enterRequested &&
                     Vector3.Distance(actor.transform.position, setupTarget) >
                     SetupPositionTolerance))
                {
                    yield return null;
                    continue;
                }

                if (positionedAt < 0f) positionedAt = Elapsed;
                if (Elapsed - positionedAt < PositionSettleSeconds)
                {
                    yield return null;
                    continue;
                }

                if (ShortcutPlayer.Instance != actor.gameObject)
                {
                    Finish(
                        false,
                        "host-shortcut",
                        "GC2 ShortcutPlayer does not resolve the authenticated Host owner.",
                        actor,
                        observation,
                        stableEdgeSamples,
                        sawFirstClient,
                        sawFirstDisconnect,
                        sawReconnectClient);
                    yield break;
                }

                if (!enterRequested)
                {
                    enterRequested = true;
                    _ = ledge.Enter(actor.Character, default);
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "host-enter-requested",
                        actor.NetworkId,
                        0,
                        $"traverse='{HierarchyPath(ledge.transform)}'",
                        actor);
                }

                TraversalStance stance = GetStance(actor.Character);
                if (!ReferenceEquals(stance?.Traverse, ledge))
                {
                    yield return null;
                    continue;
                }

                HoldRightInput(actor.Character);
                rightInputHeld = true;
                if (!sawFirstDisconnect && firstRemote != null)
                {
                    UpdateOverlapObservation(
                        ref overlap,
                        actor,
                        firstRemote,
                        ledge);
                }
                observation = ObserveEdge(actor, ledge);
                stableEdgeSamples = observation.IsRightEdgeSemantic
                    ? stableEdgeSamples + 1
                    : 0;

                if (stableEdgeSamples == RequiredStableEdgeSamples)
                {
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "host-right-edge-stable",
                        actor.NetworkId,
                        0,
                        observation.ToTrace(),
                        actor);
                }

                if (rightInputHeld && sawReconnectClient &&
                    stableEdgeSamples >= RequiredStableEdgeSamples &&
                    overlap.IsProven &&
                    Elapsed - reconnectSeenAt >= HostReconnectCompletionGraceSeconds)
                {
                    Finish(
                        true,
                        "host-held-right-edge-through-reconnect",
                        "Host crossed the first connected owner's occupied capsule on the exact Ledge_Rail_Climb, remained at Edge Right through disconnect, and was restored for a fresh reconnect.",
                        actor,
                        observation,
                        stableEdgeSamples,
                        sawFirstClient,
                        sawFirstDisconnect,
                        sawReconnectClient,
                        overlap: overlap);
                    yield break;
                }

                yield return null;
            }

            Finish(
                false,
                !sawFirstClient
                    ? "host-no-first-client"
                    : !IsReadyMarkerPresent()
                        ? "host-first-observer-not-ready"
                        : !observation.ActiveLedgeTraverse
                            ? "host-no-ledge-attach"
                            : !observation.AtRightBoundary
                                ? "host-no-right-boundary"
                                : !overlap.IsProven
                                    ? "host-did-not-cross-first-client"
                                    : !sawFirstDisconnect
                                        ? "host-no-first-disconnect"
                                        : !sawReconnectClient
                                            ? "host-no-reconnect"
                                            : "host-edge-semantic-failed",
                "Timed out proving the Host right-edge state across first connect and reconnect.",
                actor,
                observation,
                stableEdgeSamples,
                sawFirstClient,
                sawFirstDisconnect,
                sawReconnectClient,
                overlap: overlap);
        }

        private IEnumerator RunObserver()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            NetworkCharacter localOwner = null;
            TraverseInteractive ledge = null;
            FusionTransportBridge transport = null;
            bool sawRemoteShell = false;
            bool traversalControllerReady = false;
            bool sawInactiveShell = false;
            bool activeAtFirstActorSample = false;
            bool sampledFirstActor = false;
            int shellFramesBeforeActive = 0;
            int stableEdgeSamples = 0;
            float stableEdgeSince = -1f;
            bool liveObserverReadySignaled = false;
            bool blockerTeleportRequested = false;
            bool blockerTeleportResolved = false;
            bool blockerTeleportAccepted = false;
            bool blockerEnterRequested = false;
            float blockerPositionedAt = -1f;
            float blockerAttachedAt = -1f;
            EdgeObservation observation = default;
            OverlapObservation overlap = default;
            bool isFirstConnection = string.Equals(
                m_ConnectionPhase,
                "first",
                StringComparison.Ordinal);

            while (Elapsed < timeout)
            {
                actor ??= FindRemotePlayer();
                localOwner ??= FindLocalPlayer();
                ledge ??= FindExactLedge();
                transport ??= UnityObjectSearch.FindAny<FusionTransportBridge>();
                if (actor == null || localOwner == null || ledge == null)
                {
                    yield return new WaitForSecondsRealtime(0.05f);
                    continue;
                }

                sawRemoteShell = true;
                NetworkTraversalController controller =
                    actor.GetComponent<NetworkTraversalController>();
                traversalControllerReady |= controller != null &&
                                            controller.IsReadyForNetworkRouting;

                TraversalStance stance = GetStance(actor.Character);
                bool active = ReferenceEquals(stance?.Traverse, ledge);
                if (!sampledFirstActor)
                {
                    sampledFirstActor = true;
                    activeAtFirstActorSample = active;
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "observer-shell-created",
                        actor.NetworkId,
                        0,
                        $"connectionPhase={m_ConnectionPhase} active={active} " +
                        $"controllerReady={traversalControllerReady}",
                        actor);
                }

                if (!active)
                {
                    sawInactiveShell = true;
                    shellFramesBeforeActive++;

                    if (isFirstConnection && traversalControllerReady &&
                        !overlap.BlockerPrepared)
                    {
                        // GameplayReady is completed only after this client consumes the
                        // authority snapshot. Requests sent earlier are deliberately rejected
                        // by FusionTransportBridge, so do not let smoke setup race readiness.
                        if (transport == null || !transport.IsLocalGameplayReady)
                        {
                            stableEdgeSamples = 0;
                            yield return null;
                            continue;
                        }

                        if (ShortcutPlayer.Instance != localOwner.gameObject)
                        {
                            stableEdgeSamples = 0;
                            yield return null;
                            continue;
                        }

                        if (!blockerTeleportRequested)
                        {
                            UnitMotionNetworkController motion = localOwner.MotionController;
                            if (motion == null)
                            {
                                yield return null;
                                continue;
                            }

                            float targetLocalZ = CalculateBlockerLocalZ(ledge);
                            Vector3 target = CalculateCharacterRootAtLedgeLocalZ(
                                localOwner.Character,
                                ledge,
                                targetLocalZ);
                            Vector3 driverTarget = ToDriverPosition(
                                localOwner.Character,
                                target);
                            blockerTeleportRequested = true;
                            motion.RequestTeleport(
                                driverTarget,
                                float.NaN,
                                true,
                                result =>
                                {
                                    blockerTeleportResolved = true;
                                    blockerTeleportAccepted = result.approved;
                                    NetworkCiTrace.Log(
                                        "fusion-ledge-reconnect-smoke",
                                        "first-blocker-position-resolved",
                                        localOwner.NetworkId,
                                        0,
                                        $"approved={result.approved} " +
                                        $"root={localOwner.transform.position:F3} " +
                                        $"target={target:F3} driverTarget={driverTarget:F3}",
                                        localOwner);
                                });
                            NetworkCiTrace.Log(
                                "fusion-ledge-reconnect-smoke",
                                "first-blocker-position-requested",
                                localOwner.NetworkId,
                                0,
                                $"targetLocalZ={targetLocalZ:F3} target={target:F3} " +
                                $"driverTarget={driverTarget:F3} " +
                                $"traverse='{HierarchyPath(ledge.transform)}'",
                                localOwner);
                        }

                        if (blockerTeleportResolved && !blockerTeleportAccepted)
                        {
                            Finish(
                                false,
                                "first-blocker-position-rejected",
                                "Authority rejected the connected owner's setup teleport to Ledge_Rail_Climb.",
                                actor,
                                observation,
                                stableEdgeSamples,
                                sawRemoteShell: sawRemoteShell,
                                traversalControllerReady: traversalControllerReady,
                                sawInactiveShell: sawInactiveShell,
                                activeAtFirstActorSample: activeAtFirstActorSample,
                                shellFramesBeforeActive: shellFramesBeforeActive,
                                liveObserverReadySignaled: liveObserverReadySignaled,
                                overlap: overlap);
                            yield break;
                        }

                        float blockerTargetLocalZ = CalculateBlockerLocalZ(ledge);
                        float blockerLocalZ = GetLedgeLocalAnchorZ(
                            localOwner.Character,
                            ledge);
                        if (!blockerTeleportResolved ||
                            Mathf.Abs(blockerLocalZ - blockerTargetLocalZ) >
                            SetupPositionTolerance)
                        {
                            stableEdgeSamples = 0;
                            yield return null;
                            continue;
                        }

                        if (blockerPositionedAt < 0f) blockerPositionedAt = Elapsed;
                        if (Elapsed - blockerPositionedAt < PositionSettleSeconds)
                        {
                            stableEdgeSamples = 0;
                            yield return null;
                            continue;
                        }

                        if (!blockerEnterRequested)
                        {
                            blockerEnterRequested = true;
                            _ = ledge.Enter(localOwner.Character, default);
                            NetworkCiTrace.Log(
                                "fusion-ledge-reconnect-smoke",
                                "first-blocker-enter-requested",
                                localOwner.NetworkId,
                                0,
                                $"targetLocalZ={blockerTargetLocalZ:F3} " +
                                $"traverse='{HierarchyPath(ledge.transform)}'",
                                localOwner);
                        }

                        TraversalStance blockerStance = GetStance(localOwner.Character);
                        if (!ReferenceEquals(blockerStance?.Traverse, ledge))
                        {
                            stableEdgeSamples = 0;
                            yield return null;
                            continue;
                        }

                        if (blockerAttachedAt < 0f)
                        {
                            blockerAttachedAt = Elapsed;
                        }

                        UpdateOverlapObservation(
                            ref overlap,
                            actor,
                            localOwner,
                            ledge);

                        if (Elapsed - blockerAttachedAt < BlockerPreparationSettleSeconds)
                        {
                            stableEdgeSamples = 0;
                            yield return null;
                            continue;
                        }

                        overlap.BlockerStartCaptured = false;
                        overlap.BlockerDrift = 0f;
                        UpdateOverlapObservation(
                            ref overlap,
                            actor,
                            localOwner,
                            ledge);
                        overlap.BlockerPrepared = true;
                    }

                    if (isFirstConnection && traversalControllerReady &&
                        overlap.BlockerPrepared)
                    {
                        UpdateOverlapObservation(
                            ref overlap,
                            actor,
                            localOwner,
                            ledge);
                        if (!liveObserverReadySignaled)
                        {
                            liveObserverReadySignaled = TryWriteReadyMarker(actor.NetworkId);
                        }
                    }
                    stableEdgeSamples = 0;
                    yield return null;
                    continue;
                }

                if (isFirstConnection && !liveObserverReadySignaled)
                {
                    Finish(
                        false,
                        "first-observer-active-before-ready",
                        "The Host became active before the first observer proved its routing-ready inactive shell; this run cannot establish live-broadcast ordering.",
                        actor,
                        observation,
                        stableEdgeSamples,
                        sawRemoteShell: sawRemoteShell,
                        traversalControllerReady: traversalControllerReady,
                        sawInactiveShell: sawInactiveShell,
                        activeAtFirstActorSample: activeAtFirstActorSample,
                        shellFramesBeforeActive: shellFramesBeforeActive,
                        liveObserverReadySignaled: liveObserverReadySignaled);
                    yield break;
                }

                if (isFirstConnection)
                {
                    UpdateOverlapObservation(
                        ref overlap,
                        actor,
                        localOwner,
                        ledge);
                }

                observation = ObserveEdge(actor, ledge);
                if (observation.IsRightEdgeSemantic)
                {
                    stableEdgeSamples++;
                    if (stableEdgeSince < 0f) stableEdgeSince = Elapsed;
                }
                else
                {
                    stableEdgeSamples = 0;
                    stableEdgeSince = -1f;
                }

                bool reconnectObservationComplete =
                    !string.Equals(m_ConnectionPhase, "reconnect", StringComparison.Ordinal) ||
                    (stableEdgeSince >= 0f &&
                     Elapsed - stableEdgeSince >= ReconnectObservationGraceSeconds);
                bool firstOverlapComplete = !isFirstConnection || overlap.IsProven;

                if (stableEdgeSamples >= RequiredStableEdgeSamples &&
                    traversalControllerReady &&
                    firstOverlapComplete &&
                    reconnectObservationComplete)
                {
                    NetworkCiTrace.Log(
                        "fusion-ledge-reconnect-smoke",
                        "observer-right-edge-stable",
                        actor.NetworkId,
                        0,
                        $"connectionPhase={m_ConnectionPhase} " + observation.ToTrace() +
                        " " + overlap.ToTrace(),
                        actor);
                    Finish(
                        true,
                        string.Equals(m_ConnectionPhase, "reconnect", StringComparison.Ordinal)
                            ? "reconnect-observer-edge-right"
                            : "first-observer-edge-right",
                        isFirstConnection
                            ? "Connected owner remained attached as a physical blocker while the observed Host crossed completely through it and resolved Edge Right."
                            : "Connected observer resolved the active Ledge_Rail_Climb shell, the replicated held-right direction, and the Edge Right animator inputs.",
                        actor,
                        observation,
                        stableEdgeSamples,
                        sawRemoteShell: sawRemoteShell,
                        traversalControllerReady: traversalControllerReady,
                        sawInactiveShell: sawInactiveShell,
                        activeAtFirstActorSample: activeAtFirstActorSample,
                        shellFramesBeforeActive: shellFramesBeforeActive,
                        liveObserverReadySignaled: liveObserverReadySignaled,
                        overlap: overlap);
                    yield break;
                }

                yield return null;
            }

            Finish(
                false,
                !sawRemoteShell
                    ? "observer-no-remote-shell"
                    : !traversalControllerReady
                        ? "observer-controller-not-ready"
                        : !observation.ActiveLedgeTraverse
                            ? "observer-no-active-ledge"
                            : !observation.AtRightBoundary
                                ? "observer-no-right-boundary"
                                : isFirstConnection && !overlap.IsProven
                                    ? "observer-host-did-not-cross-local-owner"
                                    : !observation.ReplicatedDirectionAvailable
                                        ? "observer-no-replicated-direction"
                                        : !observation.AnimatorParametersAvailable
                                            ? "observer-no-animator-parameters"
                                            : "observer-edge-right-semantic-failed",
                "Timed out proving Edge Right presentation for the observed Host.",
                actor,
                observation,
                stableEdgeSamples,
                sawRemoteShell: sawRemoteShell,
                traversalControllerReady: traversalControllerReady,
                sawInactiveShell: sawInactiveShell,
                activeAtFirstActorSample: activeAtFirstActorSample,
                shellFramesBeforeActive: shellFramesBeforeActive,
                liveObserverReadySignaled: liveObserverReadySignaled,
                overlap: overlap);
        }

        private struct OverlapObservation
        {
            public uint BlockerActorNetworkId;
            public bool BlockerPrepared;
            public bool BlockerStartCaptured;
            public bool BlockerActiveLedgeTraverse;
            public float BlockerStartLocalZ;
            public float BlockerLocalZ;
            public float BlockerDrift;
            public float CombinedCharacterRadius;
            public float ExpectedHostStartLocalZ;
            public bool HostStartedLeftOfBlocker;
            public bool HostCrossedBlocker;
            public float HostClearancePastBlocker;

            public bool IsProven =>
                BlockerPrepared &&
                BlockerActiveLedgeTraverse &&
                BlockerDrift <= MaximumBlockerDrift &&
                HostStartedLeftOfBlocker &&
                HostCrossedBlocker;

            public string ToTrace()
            {
                return $"blocker={BlockerActorNetworkId} prepared={BlockerPrepared} " +
                       $"active={BlockerActiveLedgeTraverse} " +
                       $"startZ={BlockerStartLocalZ:F3} localZ={BlockerLocalZ:F3} " +
                       $"drift={BlockerDrift:F3} radii={CombinedCharacterRadius:F3} " +
                       $"expectedHostStartZ={ExpectedHostStartLocalZ:F3} " +
                       $"startedLeft={HostStartedLeftOfBlocker} " +
                       $"crossed={HostCrossedBlocker} " +
                       $"clearance={HostClearancePastBlocker:F3}";
            }
        }

        private readonly struct EdgeObservation
        {
            public readonly bool ActiveLedgeTraverse;
            public readonly float LocalBoundaryZ;
            public readonly float BoundaryB;
            public readonly bool AtRightBoundary;
            public readonly bool ReplicatedDirectionAvailable;
            public readonly float ReplicatedLocalDirectionZ;
            public readonly bool AnimatorParametersAvailable;
            public readonly float IntentX;
            public readonly float SpeedXY;
            public readonly string DominantClip;

            public EdgeObservation(
                bool activeLedgeTraverse,
                float localBoundaryZ,
                float boundaryB,
                bool atRightBoundary,
                bool replicatedDirectionAvailable,
                float replicatedLocalDirectionZ,
                bool animatorParametersAvailable,
                float intentX,
                float speedXY,
                string dominantClip)
            {
                ActiveLedgeTraverse = activeLedgeTraverse;
                LocalBoundaryZ = localBoundaryZ;
                BoundaryB = boundaryB;
                AtRightBoundary = atRightBoundary;
                ReplicatedDirectionAvailable = replicatedDirectionAvailable;
                ReplicatedLocalDirectionZ = replicatedLocalDirectionZ;
                AnimatorParametersAvailable = animatorParametersAvailable;
                IntentX = intentX;
                SpeedXY = speedXY;
                DominantClip = dominantClip ?? string.Empty;
            }

            public bool IsRightEdgeSemantic =>
                ActiveLedgeTraverse &&
                AtRightBoundary &&
                ReplicatedDirectionAvailable &&
                ReplicatedLocalDirectionZ >= RequiredReplicatedLocalDirection &&
                AnimatorParametersAvailable &&
                IntentX >= RequiredIntentX &&
                Mathf.Abs(SpeedXY) <= MaximumSpeedXY;

            public string ToTrace()
            {
                return $"active={ActiveLedgeTraverse} localZ={LocalBoundaryZ:F3} " +
                       $"boundaryB={BoundaryB:F3} atB={AtRightBoundary} " +
                       $"direction={ReplicatedDirectionAvailable}/" +
                       $"{ReplicatedLocalDirectionZ:F3} animator={AnimatorParametersAvailable} " +
                       $"intentX={IntentX:F3} speedXY={SpeedXY:F3} " +
                       $"baseAnimatorClip='{DominantClip}'";
            }
        }

        private static EdgeObservation ObserveEdge(
            NetworkCharacter actor,
            TraverseInteractive ledge)
        {
            if (actor?.Character == null || ledge == null)
            {
                return default;
            }

            TraversalStance stance = GetStance(actor.Character);
            bool active = ReferenceEquals(stance?.Traverse, ledge);
            Vector3 anchor = ledge.MotionInteractive.CharacterPosition(actor.Character);
            float localZ = ledge.Transform.InverseTransformPoint(anchor).z;
            bool atBoundary = localZ >= ledge.PositionB - BoundaryTolerance;

            Vector3 worldDirection = default;
            UnitMotionNetworkController motionController = actor.MotionController;
            bool hasDirection = motionController != null &&
                                motionController.TryGetTraversalPresentationDirection(
                                    out worldDirection);
            float localDirectionZ = hasDirection
                ? ledge.Transform.InverseTransformDirection(worldDirection).z
                : 0f;

            Animator animator = actor.Character.Animim?.Animator;
            bool hasAnimatorParameters = animator != null &&
                                         HasFloatParameter(animator, SpeedXYHash) &&
                                         HasFloatParameter(animator, IntentXHash);
            float intentX = hasAnimatorParameters ? animator.GetFloat(IntentXHash) : 0f;
            float speedXY = hasAnimatorParameters ? animator.GetFloat(SpeedXYHash) : 0f;
            string dominantClip = GetDominantBaseAnimatorClip(animator);

            return new EdgeObservation(
                active,
                localZ,
                ledge.PositionB,
                atBoundary,
                hasDirection,
                localDirectionZ,
                hasAnimatorParameters,
                intentX,
                speedXY,
                dominantClip);
        }

        private static bool HasFloatParameter(Animator animator, int hash)
        {
            if (animator == null) return false;
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Float)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetDominantBaseAnimatorClip(Animator animator)
        {
            if (animator == null || animator.layerCount == 0) return string.Empty;

            string dominantClip = string.Empty;
            float dominantWeight = -1f;
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                float layerWeight = layer == 0 ? 1f : animator.GetLayerWeight(layer);
                AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(layer);
                if (clips == null) continue;

                foreach (AnimatorClipInfo clipInfo in clips)
                {
                    if (clipInfo.clip == null) continue;
                    float effectiveWeight = clipInfo.weight * layerWeight;
                    string clipName = clipInfo.clip.name ?? string.Empty;
                    if (effectiveWeight > dominantWeight)
                    {
                        dominantWeight = effectiveWeight;
                        dominantClip = clipName;
                    }
                }
            }

            return dominantClip;
        }

        private void HoldRightInput(Character character)
        {
            if (character?.Player is not UnitPlayerDirectionalNetwork player) return;

            if (m_SmokeCamera == null)
            {
                var cameraOwner = new GameObject("Fusion Traversal Ledge Smoke Input Camera");
                cameraOwner.transform.SetParent(transform, false);
                m_SmokeCamera = cameraOwner.transform;
                Vector3 forward = character.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 0.0001f)
                {
                    // Freeze the synthetic view at the owner's pre-motion orientation. Updating
                    // it from the Traversal-controlled character rotation every frame creates a
                    // feedback loop that can turn the same held D input back along the rail.
                    m_SmokeCamera.rotation = Quaternion.LookRotation(
                        forward.normalized,
                        Vector3.up);
                }

                player.SetCamera(m_SmokeCamera);
            }

            m_SmokeKeyboard ??= Keyboard.current ??
                InputSystem.AddDevice<Keyboard>("GC2 Fusion Traversal Ledge Smoke Keyboard");
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState(Key.D));
            player.InjectInput(Vector2.right);
        }

        private void ReleaseRightInput()
        {
            if (m_SmokeKeyboard != null)
            {
                InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState());
            }

            NetworkCharacter actor = FindLocalPlayer();
            if (actor?.Character?.Player is UnitPlayerDirectionalNetwork player)
            {
                player.InjectInput(Vector2.zero);
            }
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

        private static TraverseInteractive FindExactLedge()
        {
            return UnityObjectSearch.FindAll<TraverseInteractive>(FindObjectsInactive.Exclude)
                .Where(interactive =>
                {
                    if (interactive == null || interactive.MotionInteractive == null ||
                        !string.Equals(
                            interactive.MotionInteractive.name,
                            LedgeMotionName,
                            StringComparison.Ordinal))
                    {
                        return false;
                    }

                    Transform fixtureRoot = FindAncestorNamed(
                        interactive.transform,
                        LedgeRootName);
                    return fixtureRoot != null &&
                           Vector3.Distance(
                               fixtureRoot.localPosition,
                               ExactLedgeFixtureLocalPosition) <= ExactLedgeFixtureTolerance;
                })
                .OrderBy(interactive => HierarchyPath(interactive.transform), StringComparer.Ordinal)
                .ThenBy(interactive => interactive.transform.position.x)
                .ThenBy(interactive => interactive.transform.position.y)
                .ThenBy(interactive => interactive.transform.position.z)
                .FirstOrDefault();
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

        private static float CalculateHostStartLocalZ(TraverseInteractive ledge)
        {
            return Mathf.Lerp(ledge.PositionA, ledge.PositionB, HostStartLedgeFraction);
        }

        private static float CalculateBlockerLocalZ(TraverseInteractive ledge)
        {
            return Mathf.Lerp(ledge.PositionA, ledge.PositionB, BlockerLedgeFraction);
        }

        private static float GetLedgeLocalAnchorZ(
            Character character,
            TraverseInteractive ledge)
        {
            Vector3 anchor = ledge.MotionInteractive.CharacterPosition(character);
            return ledge.Transform.InverseTransformPoint(anchor).z;
        }

        private static Vector3 CalculateCharacterRootAtLedgeLocalZ(
            Character character,
            TraverseInteractive ledge,
            float localZ)
        {
            Vector3 targetAnchor = ledge.Transform.TransformPoint(new Vector3(0f, 0f, localZ));
            Vector3 currentAnchor = ledge.MotionInteractive.CharacterPosition(character);
            return character.transform.position + (targetAnchor - currentAnchor);
        }

        private static void UpdateOverlapObservation(
            ref OverlapObservation observation,
            NetworkCharacter movingActor,
            NetworkCharacter blocker,
            TraverseInteractive ledge)
        {
            if (movingActor?.Character == null || blocker?.Character == null || ledge == null)
            {
                return;
            }

            observation.BlockerActorNetworkId = blocker.NetworkId;
            observation.BlockerActiveLedgeTraverse = ReferenceEquals(
                GetStance(blocker.Character)?.Traverse,
                ledge);
            observation.BlockerLocalZ = GetLedgeLocalAnchorZ(blocker.Character, ledge);
            if (!observation.BlockerStartCaptured &&
                observation.BlockerActiveLedgeTraverse)
            {
                observation.BlockerStartCaptured = true;
                observation.BlockerStartLocalZ = observation.BlockerLocalZ;
            }

            if (observation.BlockerStartCaptured)
            {
                observation.BlockerDrift = Mathf.Max(
                    observation.BlockerDrift,
                    Mathf.Abs(
                        observation.BlockerLocalZ -
                        observation.BlockerStartLocalZ));
            }

            observation.CombinedCharacterRadius =
                movingActor.Character.Motion.Radius + blocker.Character.Motion.Radius;
            observation.ExpectedHostStartLocalZ = CalculateHostStartLocalZ(ledge);

            float hostLocalZ = GetLedgeLocalAnchorZ(movingActor.Character, ledge);
            bool atExpectedStart =
                Mathf.Abs(hostLocalZ - observation.ExpectedHostStartLocalZ) <=
                HostStartLocalZTolerance &&
                hostLocalZ < observation.BlockerLocalZ;
            if (!observation.HostStartedLeftOfBlocker && atExpectedStart)
            {
                observation.HostStartedLeftOfBlocker = true;
                observation.HostClearancePastBlocker =
                    hostLocalZ - observation.BlockerLocalZ;
            }

            if (observation.HostStartedLeftOfBlocker)
            {
                observation.HostClearancePastBlocker = Mathf.Max(
                    observation.HostClearancePastBlocker,
                    hostLocalZ - observation.BlockerLocalZ);
                observation.HostCrossedBlocker |=
                    observation.HostClearancePastBlocker + CapsuleClearanceTolerance >=
                    observation.CombinedCharacterRadius;
            }
        }

        private static Vector3 ToDriverPosition(Character character, Vector3 rootPosition)
        {
            float halfHeight = character != null ? character.Motion.Height * 0.5f : 0f;
            return rootPosition + Vector3.down * halfHeight;
        }

        private void Finish(
            bool passed,
            string phase,
            string message,
            NetworkCharacter actor = null,
            EdgeObservation observation = default,
            int stableEdgeSamples = 0,
            bool sawFirstClient = false,
            bool sawFirstDisconnect = false,
            bool sawReconnectClient = false,
            bool sawRemoteShell = false,
            bool traversalControllerReady = false,
            bool sawInactiveShell = false,
            bool activeAtFirstActorSample = false,
            int shellFramesBeforeActive = 0,
            bool liveObserverReadySignaled = false,
            OverlapObservation overlap = default)
        {
            if (m_Finished) return;
            m_Finished = true;
            ReleaseRightInput();

            var result = new Result
            {
                passed = passed,
                role = m_Role,
                connectionPhase = m_ConnectionPhase,
                phase = phase,
                message = message,
                actorNetworkId = actor != null ? actor.NetworkId : 0,
                sawRemoteShell = sawRemoteShell,
                traversalControllerReady = traversalControllerReady,
                sawInactiveShell = sawInactiveShell,
                activeAtFirstActorSample = activeAtFirstActorSample,
                shellFramesBeforeActive = shellFramesBeforeActive,
                liveObserverReadySignaled = liveObserverReadySignaled,
                activeLedgeTraverse = observation.ActiveLedgeTraverse,
                localBoundaryZ = observation.LocalBoundaryZ,
                boundaryB = observation.BoundaryB,
                atRightBoundary = observation.AtRightBoundary,
                replicatedDirectionAvailable = observation.ReplicatedDirectionAvailable,
                replicatedLocalDirectionZ = observation.ReplicatedLocalDirectionZ,
                animatorParametersAvailable = observation.AnimatorParametersAvailable,
                intentX = observation.IntentX,
                speedXY = observation.SpeedXY,
                dominantClip = observation.DominantClip ?? string.Empty,
                stableEdgeSamples = stableEdgeSamples,
                sawFirstClient = sawFirstClient,
                sawFirstDisconnect = sawFirstDisconnect,
                sawReconnectClient = sawReconnectClient,
                blockerActorNetworkId = overlap.BlockerActorNetworkId,
                blockerPrepared = overlap.BlockerPrepared,
                blockerActiveLedgeTraverse = overlap.BlockerActiveLedgeTraverse,
                blockerStartLocalZ = overlap.BlockerStartLocalZ,
                blockerLocalZ = overlap.BlockerLocalZ,
                blockerDrift = overlap.BlockerDrift,
                combinedCharacterRadius = overlap.CombinedCharacterRadius,
                expectedHostStartLocalZ = overlap.ExpectedHostStartLocalZ,
                hostStartedLeftOfBlocker = overlap.HostStartedLeftOfBlocker,
                hostCrossedBlocker = overlap.HostCrossedBlocker,
                hostClearancePastBlocker = overlap.HostClearancePastBlocker,
                elapsedSeconds = Elapsed
            };

            if (!string.IsNullOrWhiteSpace(m_ResultPath))
            {
                string fullPath = Path.GetFullPath(m_ResultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, JsonUtility.ToJson(result, true));
            }

            Debug.Log(
                $"[GC2 Fusion Traversal Ledge Reconnect Smoke] passed={passed} " +
                $"role={m_Role} connectionPhase={m_ConnectionPhase} phase={phase} " +
                $"actor={result.actorNetworkId} shell={sawRemoteShell}/" +
                $"{traversalControllerReady} inactiveShell={sawInactiveShell} " +
                $"shellFrames={shellFramesBeforeActive} ready={liveObserverReadySignaled} " +
                $"{observation.ToTrace()} " +
                $"{overlap.ToTrace()} " +
                $"stableSamples={stableEdgeSamples} first={sawFirstClient} " +
                $"disconnected={sawFirstDisconnect} reconnect={sawReconnectClient} " +
                $"message='{message}'");
            Application.Quit(passed ? 0 : 1);
        }

        private bool IsReadyMarkerPresent()
        {
            return !string.IsNullOrWhiteSpace(m_ReadyPath) && File.Exists(m_ReadyPath);
        }

        private bool TryWriteReadyMarker(uint actorNetworkId)
        {
            if (string.IsNullOrWhiteSpace(m_ReadyPath)) return false;

            try
            {
                string fullPath = Path.GetFullPath(m_ReadyPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, actorNetworkId.ToString());
                NetworkCiTrace.Log(
                    "fusion-ledge-reconnect-smoke",
                    "first-observer-ready",
                    actorNetworkId,
                    0,
                    "The inactive remote shell is routing-ready and the connected owner is attached as the deterministic same-ledge blocker before Host traversal entry.",
                    this);
                return true;
            }
            catch (Exception exception)
            {
                NetworkCiTrace.Log(
                    "fusion-ledge-reconnect-smoke",
                    "first-observer-ready-failed",
                    actorNetworkId,
                    0,
                    exception.GetType().Name + ": " + exception.Message,
                    this);
                return false;
            }
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
