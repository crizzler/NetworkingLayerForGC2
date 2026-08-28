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
    /// Opt-in two-peer Host/Shared regression harness for the real Fusion Climb demo. It is
    /// inert unless the dedicated CI scenario argument is supplied.
    /// </summary>
    internal sealed class FusionTraversalClimbSmokeBootstrap : MonoBehaviour
    {
        private const string Scenario = "fusion-traversal-climb";
        private const float RequiredClimbDistance = 0.2f;
        private const float RequiredHostClimbDistance = 1f;
        private const float RequiredObserverClimbDistance = 0.5f;
        private const float RequiredHostFallDistance = 0.75f;
        private const float RequiredObserverFallDistance = 0.5f;
        private const float RequiredConnectedOwnerMoveDistance = 0.35f;
        private const float AttachSettleSeconds = 1f;
        private const float ConnectedOwnerIdleProbeSeconds = 0.25f;
        private const float SharedAuthorityResultGraceSeconds = 2f;
        private const float HostResultGraceSeconds = 2f;
        private const float ObserverResultGraceSeconds = 1f;

        [Serializable]
        private struct Result
        {
            public bool passed;
            public string role;
            public string phase;
            public string message;
            public uint actorNetworkId;
            public bool attached;
            public float climbedDistance;
            public float reverseDistance;
            public float maxReverseStep;
            public float maxFrameStep;
            public bool detached;
            public float fallenDistance;
            public float fallReverseDistance;
            public float maxFallReverseStep;
            public float maxFallFrameStep;
            public int fallSampleCount;
            public int fallCorrectionCount;
            public float idleOwnerDrift;
            public bool connectedOwnerMovementTested;
            public bool connectedOwnerInputHealthy;
            public float connectedOwnerMoveDistance;
            public int sampleCount;
            public int correctionCount;
            public float elapsedSeconds;
        }

        private string m_Role;
        private string m_ResultPath;
        private float m_StartedAt;
        private Transform m_SmokeCamera;
        private Keyboard m_SmokeKeyboard;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!TryGetArgument("--gc2-network-smoke-scenario", out string scenario) ||
                !string.Equals(scenario, Scenario, StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("Fusion Traversal Climb Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionTraversalClimbSmokeBootstrap>();
        }

        private async void Start()
        {
            Application.SetStackTraceLogType(UnityEngine.LogType.Log, StackTraceLogType.None);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            m_StartedAt = Time.realtimeSinceStartup;
            TryGetArgument("--gc2-network-smoke-role", out m_Role);
            TryGetArgument("--gc2-network-smoke-result", out m_ResultPath);
            TryGetArgument("--gc2-network-smoke-session", out string session);
            if (string.IsNullOrWhiteSpace(session)) session = "GC2-Fusion-Traversal-Climb-Smoke";
            TryGetArgument("--gc2-network-smoke-region", out string region);
            var startOptions = new FusionSessionStartOptions(session, region);

            string appId = Environment.GetEnvironmentVariable(
                "GC2_NETWORK_SMOKE_PHOTON_APP_ID");
            if (!string.IsNullOrWhiteSpace(appId))
            {
                PhotonAppSettings settings = PhotonAppSettings.Global;
                if (settings != null) settings.AppSettings.AppIdFusion = appId.Trim();
            }

            FusionSessionBootstrap bootstrap = UnityObjectSearch.FindAny<FusionSessionBootstrap>();
            if (bootstrap == null)
            {
                Finish(false, "start", "FusionSessionBootstrap is missing.", null, false, 0f);
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
                        $"Fusion start failed: {startResult.ShutdownReason} {startResult.ErrorMessage}",
                        null,
                        false,
                        0f);
                    return;
                }
            }
            catch (Exception exception)
            {
                Finish(false, "start", exception.GetType().Name + ": " + exception.Message,
                    null, false, 0f);
                return;
            }

            NetworkCiTrace.Log(
                "fusion-climb-smoke",
                "session-started",
                0,
                0,
                $"role={m_Role} session='{session}' region='{region}'",
                this);
            IEnumerator scenario = m_Role switch
            {
                "fusion-host" => RunLocalOwner(hostTopology: true),
                "fusion-client" => RunHostObserver(),
                "fusion-shared-master" => RunAuthority(),
                _ => RunLocalOwner(hostTopology: false)
            };
            StartCoroutine(scenario);
        }

        private IEnumerator RunAuthority()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 120);
            NetworkCharacter actor = null;
            TraverseInteractive interactive = null;
            Vector3 climbStart = default;
            bool attached = false;
            float attachedAt = -1f;

            while (Elapsed < timeout)
            {
                actor ??= FindAuthorityRemotePlayer();
                interactive ??= FindClimbInteractive();
                if (actor == null || interactive == null)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }

                TraversalStance stance = GetStance(actor.Character);
                attached = ReferenceEquals(stance?.Traverse, interactive);
                if (!attached)
                {
                    // In Shared mode the owning peer authors its pose. Moving the master's
                    // remote replica here would be immediately overwritten by the owner's next
                    // input state and would make this test exercise the wrong authority path.
                    climbStart = actor.transform.position;
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }

                if (attachedAt < 0f)
                {
                    attachedAt = Elapsed;
                    climbStart = actor.transform.position;
                }

                if (Elapsed - attachedAt < AttachSettleSeconds)
                {
                    climbStart = actor.transform.position;
                    yield return null;
                    continue;
                }

                float climbed = Mathf.Max(0f, actor.transform.position.y - climbStart.y);
                if (climbed >= RequiredClimbDistance)
                {
                    NetworkCiTrace.Log(
                        "fusion-climb-smoke",
                        "authority-observed-climb",
                        actor.NetworkId,
                        0,
                        $"distance={climbed:F3} active='{stance.Traverse.name}'",
                        actor);
                    yield return new WaitForSecondsRealtime(
                        SharedAuthorityResultGraceSeconds);
                    Finish(true, "authority-observed-climb",
                        "Shared master observed the joining owner's authoritative climb.",
                        actor, true, climbed);
                    yield break;
                }

                yield return null;
            }

            float finalDistance = actor != null
                ? Mathf.Max(0f, actor.transform.position.y - climbStart.y)
                : 0f;
            Finish(false, attached ? "authority-no-movement" : "authority-no-attach",
                "Timed out waiting for the joining owner's climb on Shared authority.",
                actor, attached, finalDistance);
        }

        private IEnumerator RunLocalOwner(bool hostTopology)
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 120);
            NetworkCharacter actor = null;
            NetworkCharacter remoteObserver = null;
            TraverseInteractive interactive = null;
            bool requested = false;
            bool attached = false;
            bool inputPressed = false;
            Vector3 climbStart = default;
            float attachedAt = -1f;

            while (Elapsed < timeout)
            {
                actor ??= FindLocalPlayer();
                interactive ??= FindClimbInteractive();
                if (actor == null || interactive == null ||
                    !actor.IsOwnerInstance || !actor.Character.IsPlayer)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }

                if (hostTopology)
                {
                    remoteObserver ??= FindRemotePlayer();
                    if (remoteObserver == null)
                    {
                        yield return new WaitForSecondsRealtime(0.1f);
                        continue;
                    }
                }

                Vector3 target = CalculateCharacterRootStart(actor.Character, interactive);
                if (!inputPressed)
                {
                    if (!PressPrimaryClimbInput(actor.Character))
                    {
                        Finish(false, "owner-input", "Could not press GC2 Primary Motion input.",
                            actor, false, 0f);
                        yield break;
                    }

                    inputPressed = true;
                }

                if (!requested && HorizontalDistance(actor.transform.position, target) > 1.25f)
                {
                    AimSmokeCamera(actor.Character, target - actor.transform.position);
                    yield return null;
                    continue;
                }

                if (!requested)
                {
                    NetworkTraversalController controller =
                        actor.GetComponent<NetworkTraversalController>();
                    if (controller == null)
                    {
                        Finish(false, "owner-request", "Local player has no traversal controller.",
                            actor, false, 0f);
                        yield break;
                    }

                    if (ShortcutPlayer.Instance != actor.gameObject)
                    {
                        Finish(false, "owner-shortcut",
                            "GC2 ShortcutPlayer does not resolve the authenticated local owner.",
                            actor, false, 0f);
                        yield break;
                    }

                    requested = true;
                    climbStart = actor.transform.position;
                    NetworkCiTrace.Log(
                        "fusion-climb-smoke",
                        "owner-request-climb",
                        actor.NetworkId,
                        0,
                        $"start={climbStart:F3} target='{interactive.name}'",
                        actor);
                    // Follow the real GC2 instruction/patch path. The patched Enter validator
                    // must route this to the controller instead of applying it locally.
                    _ = interactive.Enter(
                        ShortcutPlayer.Instance.GetComponent<Character>(),
                        default);
                }

                TraversalStance stance = GetStance(actor.Character);
                if (ReferenceEquals(stance?.Traverse, interactive))
                {
                    if (!attached)
                    {
                        attached = true;
                        attachedAt = Elapsed;
                        climbStart = actor.transform.position;
                        NetworkCiTrace.Log(
                            "fusion-climb-smoke",
                            "owner-attached",
                            actor.NetworkId,
                            0,
                            $"position={climbStart:F3}",
                            actor);
                    }

                    if (Elapsed - attachedAt < AttachSettleSeconds)
                    {
                        climbStart = actor.transform.position;
                        yield return null;
                        continue;
                    }

                    float climbed = Mathf.Max(0f, actor.transform.position.y - climbStart.y);
                    float requiredDistance = hostTopology
                        ? RequiredHostClimbDistance
                        : RequiredClimbDistance;
                    if (climbed >= requiredDistance)
                    {
                        NetworkCiTrace.Log(
                            "fusion-climb-smoke",
                            "owner-climbed",
                            actor.NetworkId,
                            0,
                            $"distance={climbed:F3} position={actor.transform.position:F3}",
                            actor);
                        if (!hostTopology)
                        {
                            Finish(true, "owner-climbed",
                                "Joining Shared owner attached and moved through GC2 MotionInteractive.",
                                actor, true, climbed);
                            yield break;
                        }

                        ReleasePrimaryClimbInput();
                        NetworkTraversalController controller =
                            actor.GetComponent<NetworkTraversalController>();
                        stance.TryCancel(new Args(interactive.gameObject, actor.gameObject));
                        float detachRequestedAt = Elapsed;
                        Vector3 fallStart = actor.transform.position;
                        NetworkCiTrace.Log(
                            "fusion-climb-smoke",
                            "host-owner-detach-requested",
                            actor.NetworkId,
                            0,
                            $"position={fallStart:F3} distance={climbed:F3} " +
                            $"controllerReady={controller != null}",
                            actor);

                        while (Elapsed < timeout)
                        {
                            stance = GetStance(actor.Character);
                            bool detached = stance?.Traverse == null;
                            float fallen = Mathf.Max(0f, fallStart.y - actor.transform.position.y);
                            if (detached && fallen >= RequiredHostFallDistance)
                            {
                                NetworkCiTrace.Log(
                                    "fusion-climb-smoke",
                                    "host-owner-fell",
                                    actor.NetworkId,
                                    0,
                                    $"start={fallStart:F3} current={actor.transform.position:F3} " +
                                    $"distance={fallen:F3} " +
                                    $"detachLatency={Elapsed - detachRequestedAt:F3}",
                                    actor);
                                yield return new WaitForSecondsRealtime(HostResultGraceSeconds);
                                Finish(
                                    true,
                                    "host-owner-climbed-detached-fell",
                                    "Fusion Host owner climbed, exited through GC2's authoritative cancel route, and fell under normal movement simulation.",
                                    actor,
                                    false,
                                    climbed,
                                    detached: true,
                                    fallenDistance: fallen);
                                yield break;
                            }

                            yield return null;
                        }

                        Finish(
                            false,
                            "host-owner-no-fall",
                            "Fusion Host owner climbed but did not complete the authoritative detach-and-fall handoff.",
                            actor,
                            stance?.Traverse != null,
                            climbed,
                            detached: stance?.Traverse == null,
                            fallenDistance: Mathf.Max(
                                0f,
                                fallStart.y - actor.transform.position.y));
                        yield break;
                    }
                }

                yield return null;
            }

            float finalDistance = actor != null && attached
                ? Mathf.Max(0f, actor.transform.position.y - climbStart.y)
                : 0f;
            Finish(false,
                !requested ? "owner-not-positioned" : attached ? "owner-no-movement" : "owner-no-attach",
                hostTopology
                    ? "Timed out waiting for the Fusion Host owner to attach and climb locally."
                    : "Timed out waiting for the joining Shared owner to attach and climb locally.",
                actor, attached, finalDistance);
        }

        private IEnumerator RunHostObserver()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 120);
            NetworkCharacter actor = null;
            NetworkCharacter localOwner = null;
            TraverseInteractive interactive = null;
            bool attached = false;
            float attachedAt = -1f;
            Vector3 climbStart = default;
            Vector3 previous = default;
            Vector3 idleOwnerStart = default;
            float reverseDistance = 0f;
            float maxReverseStep = 0f;
            float maxFrameStep = 0f;
            float idleOwnerDrift = 0f;
            float connectedOwnerMoveDistance = 0f;
            bool connectedOwnerMovementTested = false;
            bool connectedOwnerInputHealthy = true;
            Vector3 connectedOwnerMoveStart = default;
            float nextTraceAt = 0f;
            int sampleCount = 0;
            int correctionCount = 0;
            bool climbQualified = false;
            bool detached = false;
            Vector3 fallStart = default;
            float fallReverseDistance = 0f;
            float maxFallReverseStep = 0f;
            float maxFallFrameStep = 0f;
            int fallSampleCount = 0;
            int fallCorrectionCount = 0;
            float maxObservedClimbDistance = 0f;

            while (Elapsed < timeout)
            {
                if (actor == null)
                {
                    if (attachedAt >= 0f)
                    {
                        Finish(
                            false,
                            "observer-host-disconnected",
                            "The observed Host actor despawned before the climb-and-fall result was complete.",
                            null,
                            attached,
                            maxObservedClimbDistance,
                            reverseDistance,
                            maxReverseStep,
                            maxFrameStep,
                            idleOwnerDrift,
                            sampleCount,
                            correctionCount,
                            connectedOwnerMovementTested,
                            connectedOwnerInputHealthy,
                            connectedOwnerMoveDistance,
                            detached,
                            0f,
                            fallReverseDistance,
                            maxFallReverseStep,
                            maxFallFrameStep,
                            fallSampleCount,
                            fallCorrectionCount);
                        yield break;
                    }

                    actor = FindRemotePlayer();
                }

                if (localOwner == null) localOwner = FindLocalPlayer();
                interactive ??= FindClimbInteractive();
                if (actor == null || localOwner == null || interactive == null)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }

                TraversalStance stance = GetStance(actor.Character);
                attached = ReferenceEquals(stance?.Traverse, interactive);
                if (!attached && attachedAt < 0f)
                {
                    yield return null;
                    continue;
                }

                if (attachedAt < 0f)
                {
                    attachedAt = Elapsed;
                    climbStart = GetPresentedPosition(actor);
                    previous = climbStart;
                    idleOwnerStart = localOwner.transform.position;
                    NetworkCiTrace.Log(
                        "fusion-climb-smoke",
                        "observer-host-attached",
                        actor.NetworkId,
                        0,
                        $"remote={climbStart:F3} localOwner={idleOwnerStart:F3}",
                        actor);
                }

                if (Elapsed - attachedAt < AttachSettleSeconds)
                {
                    climbStart = GetPresentedPosition(actor);
                    previous = climbStart;
                    idleOwnerStart = localOwner.transform.position;
                    yield return null;
                    continue;
                }

                Vector3 current = GetPresentedPosition(actor);
                Vector3 frameDelta = current - previous;
                float frameStep = frameDelta.magnitude;
                if (attached && frameDelta.y < 0f)
                {
                    float reverseStep = -frameDelta.y;
                    reverseDistance += reverseStep;
                    maxReverseStep = Mathf.Max(maxReverseStep, reverseStep);
                    if (reverseStep > 0.01f)
                    {
                        correctionCount++;
                        NetworkCiTrace.Log(
                            "fusion-climb-smoke",
                            "observer-host-reverse-correction",
                            actor.NetworkId,
                            0,
                            $"previous={previous:F4} current={current:F4} " +
                            $"delta={frameDelta:F4} reverseStep={reverseStep:F4} " +
                            $"reverseTotal={reverseDistance:F4} sample={sampleCount}",
                            actor);
                    }
                }

                if (attached && frameStep > 0.1f)
                {
                    NetworkCiTrace.Log(
                        "fusion-climb-smoke",
                        "observer-host-large-frame-step",
                        actor.NetworkId,
                        0,
                        $"previous={previous:F4} current={current:F4} delta={frameDelta:F4} " +
                        $"step={frameStep:F4} sample={sampleCount}",
                        actor);
                }

                if (attached)
                {
                    maxFrameStep = Mathf.Max(maxFrameStep, frameStep);
                }
                if (!connectedOwnerMovementTested)
                {
                    idleOwnerDrift = Mathf.Max(
                        idleOwnerDrift,
                        Vector3.Distance(idleOwnerStart, localOwner.transform.position));
                }

                if (Elapsed - attachedAt >=
                    AttachSettleSeconds + ConnectedOwnerIdleProbeSeconds)
                {
                    connectedOwnerInputHealthy &=
                        localOwner.Character != null &&
                        localOwner.Character.IsPlayer &&
                        localOwner.Character.Player != null &&
                        localOwner.Character.Player.IsControllable &&
                        ShortcutPlayer.Instance == localOwner.gameObject;

                    if (!connectedOwnerMovementTested)
                    {
                        connectedOwnerMovementTested = true;
                        connectedOwnerMoveStart = localOwner.transform.position;
                        NetworkCiTrace.Log(
                            "fusion-climb-smoke",
                            "connected-owner-move-start",
                            localOwner.NetworkId,
                            0,
                            $"position={connectedOwnerMoveStart:F3} " +
                            $"isPlayer={localOwner.Character?.IsPlayer ?? false} " +
                            $"controllable={localOwner.Character?.Player?.IsControllable ?? false} " +
                            $"shortcut={ShortcutPlayer.Instance?.name ?? "<none>"}",
                            localOwner);
                    }

                    if (localOwner.Character?.Player is UnitPlayerDirectionalNetwork player &&
                        connectedOwnerMoveDistance < RequiredConnectedOwnerMoveDistance)
                    {
                        AimSmokeCamera(localOwner.Character, Vector3.right);
                        player.InjectInput(Vector2.up);
                    }

                    connectedOwnerMoveDistance = HorizontalDistance(
                        connectedOwnerMoveStart,
                        localOwner.transform.position);
                }
                if (!attached && !detached)
                {
                    detached = true;
                    fallStart = current;
                    previous = current;
                    frameDelta = Vector3.zero;
                    frameStep = 0f;
                    NetworkCiTrace.Log(
                        "fusion-climb-smoke",
                        "observer-host-detached",
                        actor.NetworkId,
                        0,
                        $"remote={current:F3} climbQualified={climbQualified} " +
                        $"connectedInputHealthy={connectedOwnerInputHealthy}",
                        actor);
                }
                else if (detached)
                {
                    maxFallFrameStep = Mathf.Max(maxFallFrameStep, frameStep);
                    if (frameDelta.y > 0f)
                    {
                        fallReverseDistance += frameDelta.y;
                        maxFallReverseStep = Mathf.Max(maxFallReverseStep, frameDelta.y);
                        if (frameDelta.y > 0.01f)
                        {
                            fallCorrectionCount++;
                            NetworkCiTrace.Log(
                                "fusion-climb-smoke",
                                "observer-host-fall-reverse-correction",
                                actor.NetworkId,
                                0,
                                $"previous={previous:F4} current={current:F4} " +
                                $"delta={frameDelta:F4} reverseStep={frameDelta.y:F4} " +
                                $"reverseTotal={fallReverseDistance:F4} " +
                                $"sample={fallSampleCount}",
                                actor);
                        }
                    }

                    if (frameStep > 0.1f)
                    {
                        NetworkCiTrace.Log(
                            "fusion-climb-smoke",
                            "observer-host-fall-large-frame-step",
                            actor.NetworkId,
                            0,
                            $"previous={previous:F4} current={current:F4} " +
                            $"delta={frameDelta:F4} step={frameStep:F4} " +
                            $"sample={fallSampleCount}",
                            actor);
                    }

                    fallSampleCount++;
                }

                previous = current;
                sampleCount++;

                float climbed = Mathf.Max(0f, current.y - climbStart.y);
                maxObservedClimbDistance = Mathf.Max(maxObservedClimbDistance, climbed);
                float fallen = detached
                    ? Mathf.Max(0f, fallStart.y - current.y)
                    : 0f;
                if (Elapsed >= nextTraceAt)
                {
                    nextTraceAt = Elapsed + 0.25f;
                    FusionNativeNetworkCharacterMotor motor =
                        actor.GetComponent<FusionNativeNetworkCharacterMotor>();
                    string visual = motor?.ActiveRemotePresentationTarget != null
                        ? motor.ActiveRemotePresentationTarget.position.ToString("F3")
                        : "<none>";
                    NetworkCiTrace.Log(
                        "fusion-climb-smoke",
                        "observer-host-climb-sample",
                        actor.NetworkId,
                        0,
                        $"presented={current:F3} root={actor.transform.position:F3} " +
                        $"visual={visual} delta={frameDelta:F4} " +
                        $"climbed={climbed:F3} reverse={reverseDistance:F3} " +
                        $"maxReverse={maxReverseStep:F3} maxStep={maxFrameStep:F3} " +
                        $"corrections={correctionCount} idleOwnerDrift={idleOwnerDrift:F3} " +
                        $"connectedMove={connectedOwnerMoveDistance:F3} " +
                        $"connectedInputHealthy={connectedOwnerInputHealthy} " +
                        $"detached={detached} fallen={fallen:F3} " +
                        $"fallReverse={fallReverseDistance:F3} " +
                        $"maxFallReverse={maxFallReverseStep:F3} " +
                        $"maxFallStep={maxFallFrameStep:F3}",
                        actor);
                }

                if (maxObservedClimbDistance >= RequiredObserverClimbDistance &&
                    sampleCount >= 20 &&
                    connectedOwnerMovementTested &&
                    connectedOwnerMoveDistance >= RequiredConnectedOwnerMoveDistance)
                {
                    climbQualified =
                        reverseDistance <= 0.15f &&
                        maxReverseStep <= 0.075f &&
                        maxFrameStep <= 0.3f &&
                        idleOwnerDrift <= 0.25f &&
                        connectedOwnerInputHealthy;
                }

                if (detached && fallen >= RequiredObserverFallDistance &&
                    fallSampleCount >= 10)
                {
                    bool smooth =
                        climbQualified &&
                        fallReverseDistance <= 0.15f &&
                        maxFallReverseStep <= 0.075f &&
                        maxFallFrameStep <= 0.3f &&
                        connectedOwnerInputHealthy;
                    if (smooth)
                    {
                        // Keep the observer connected until the Host writes its own fall result.
                        yield return new WaitForSecondsRealtime(ObserverResultGraceSeconds);
                    }

                    Finish(
                        smooth,
                        smooth
                            ? "observer-host-climb-fall-smooth"
                            : "observer-host-climb-fall-stutter",
                        smooth
                            ? "Connected client observed the Host climb, detach, and fall without material reverse corrections while retaining authenticated local movement input."
                            : "Connected client detected Host climb/fall presentation corrections or lost authenticated local movement input.",
                        actor,
                        false,
                        maxObservedClimbDistance,
                        reverseDistance,
                        maxReverseStep,
                        maxFrameStep,
                        idleOwnerDrift,
                        sampleCount,
                        correctionCount,
                        connectedOwnerMovementTested,
                        connectedOwnerInputHealthy,
                        connectedOwnerMoveDistance,
                        true,
                        fallen,
                        fallReverseDistance,
                        maxFallReverseStep,
                        maxFallFrameStep,
                        fallSampleCount,
                        fallCorrectionCount);
                    yield break;
                }

                yield return null;
            }

            float finalDistance = maxObservedClimbDistance;
            Finish(
                false,
                attached ? "observer-host-no-progress" : "observer-host-no-attach",
                "Timed out while observing the Host climb or proving simultaneous connected-owner movement.",
                actor,
                attached,
                finalDistance,
                reverseDistance,
                maxReverseStep,
                maxFrameStep,
                idleOwnerDrift,
                sampleCount,
                correctionCount,
                connectedOwnerMovementTested,
                connectedOwnerInputHealthy,
                connectedOwnerMoveDistance,
                detached,
                detached && actor != null
                    ? Mathf.Max(0f, fallStart.y - GetPresentedPosition(actor).y)
                    : 0f,
                fallReverseDistance,
                maxFallReverseStep,
                maxFallFrameStep,
                fallSampleCount,
                fallCorrectionCount);
        }

        private static NetworkCharacter FindLocalPlayer()
        {
            return UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                .FirstOrDefault(character =>
                    character != null && character.IsPlayerOwnedActor &&
                    character.IsOwnerInstance && character.NetworkId != 0);
        }

        private static NetworkCharacter FindAuthorityRemotePlayer()
        {
            return UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                .FirstOrDefault(character =>
                    character != null && character.IsPlayerOwnedActor &&
                    character.IsServerInstance && !character.IsOwnerInstance &&
                    character.HasAuthenticatedPlayerOwner && character.NetworkId != 0);
        }

        private static NetworkCharacter FindRemotePlayer()
        {
            return UnityObjectSearch.FindAll<NetworkCharacter>(FindObjectsInactive.Exclude)
                .FirstOrDefault(character =>
                    character != null && character.IsPlayerOwnedActor &&
                    !character.IsOwnerInstance &&
                    character.HasAuthenticatedPlayerOwner && character.NetworkId != 0);
        }

        private static TraverseInteractive FindClimbInteractive()
        {
            return UnityObjectSearch.FindAll<TraverseInteractive>(FindObjectsInactive.Exclude)
                .FirstOrDefault(interactive =>
                    interactive != null &&
                    (interactive.name.IndexOf(
                         "Traverse_Climb",
                         StringComparison.OrdinalIgnoreCase) >= 0 ||
                     interactive.transform.parent != null &&
                     interactive.transform.parent.name.IndexOf(
                         "Free_Climb",
                         StringComparison.OrdinalIgnoreCase) >= 0));
        }

        private static TraversalStance GetStance(Character character)
        {
            return character?.Combat?.RequestStance<TraversalStance>();
        }

        private static Vector3 CalculateCharacterRootStart(
            Character character,
            TraverseInteractive interactive)
        {
            Vector3 target = interactive.CalculateStartPosition(character);
            float height = character.Motion.Height;
            return target + (interactive.MotionInteractive.Anchor switch
            {
                Anchor.Crown => Vector3.down * height,
                Anchor.Center => Vector3.down * (height * 0.5f),
                Anchor.Feet => Vector3.zero,
                _ => Vector3.zero
            });
        }

        private void AimSmokeCamera(Character character, Vector3 worldDirection)
        {
            if (character?.Player is not UnitPlayerDirectionalNetwork player) return;

            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude <= 0.0001f) return;

            if (m_SmokeCamera == null)
            {
                var owner = new GameObject("Fusion Traversal Smoke Input Camera");
                owner.transform.SetParent(transform, false);
                m_SmokeCamera = owner.transform;
                player.SetCamera(m_SmokeCamera);
            }

            m_SmokeCamera.rotation = Quaternion.LookRotation(worldDirection.normalized, Vector3.up);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

        private static Vector3 GetPresentedPosition(NetworkCharacter actor)
        {
            if (actor == null) return default;
            FusionNativeNetworkCharacterMotor motor =
                actor.GetComponent<FusionNativeNetworkCharacterMotor>();
            Transform target = motor?.ActiveRemotePresentationTarget;
            return target != null ? target.position : actor.transform.position;
        }

        private bool PressPrimaryClimbInput(Character character)
        {
            if (character?.Player is not UnitPlayerDirectionalNetwork)
            {
                return false;
            }

            m_SmokeKeyboard ??= Keyboard.current ??
                InputSystem.AddDevice<Keyboard>("GC2 Fusion Traversal Smoke Keyboard");
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState(Key.W));
            return true;
        }

        private void ReleasePrimaryClimbInput()
        {
            if (m_SmokeKeyboard == null) return;
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState());
        }

        private void Finish(
            bool passed,
            string phase,
            string message,
            NetworkCharacter actor,
            bool attached,
            float climbedDistance,
            float reverseDistance = 0f,
            float maxReverseStep = 0f,
            float maxFrameStep = 0f,
            float idleOwnerDrift = 0f,
            int sampleCount = 0,
            int correctionCount = 0,
            bool connectedOwnerMovementTested = false,
            bool connectedOwnerInputHealthy = false,
            float connectedOwnerMoveDistance = 0f,
            bool detached = false,
            float fallenDistance = 0f,
            float fallReverseDistance = 0f,
            float maxFallReverseStep = 0f,
            float maxFallFrameStep = 0f,
            int fallSampleCount = 0,
            int fallCorrectionCount = 0)
        {
            var result = new Result
            {
                passed = passed,
                role = m_Role,
                phase = phase,
                message = message,
                actorNetworkId = actor != null ? actor.NetworkId : 0,
                attached = attached,
                climbedDistance = climbedDistance,
                reverseDistance = reverseDistance,
                maxReverseStep = maxReverseStep,
                maxFrameStep = maxFrameStep,
                detached = detached,
                fallenDistance = fallenDistance,
                fallReverseDistance = fallReverseDistance,
                maxFallReverseStep = maxFallReverseStep,
                maxFallFrameStep = maxFallFrameStep,
                fallSampleCount = fallSampleCount,
                fallCorrectionCount = fallCorrectionCount,
                idleOwnerDrift = idleOwnerDrift,
                connectedOwnerMovementTested = connectedOwnerMovementTested,
                connectedOwnerInputHealthy = connectedOwnerInputHealthy,
                connectedOwnerMoveDistance = connectedOwnerMoveDistance,
                sampleCount = sampleCount,
                correctionCount = correctionCount,
                elapsedSeconds = Elapsed
            };

            if (!string.IsNullOrWhiteSpace(m_ResultPath))
            {
                string fullPath = Path.GetFullPath(m_ResultPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, JsonUtility.ToJson(result, true));
            }

            Debug.Log(
                $"[GC2 Fusion Traversal Smoke] passed={passed} role={m_Role} phase={phase} " +
                $"actor={result.actorNetworkId} attached={attached} climbed={climbedDistance:F3} " +
                $"reverse={reverseDistance:F3} maxReverse={maxReverseStep:F3} " +
                $"maxStep={maxFrameStep:F3} idleOwnerDrift={idleOwnerDrift:F3} " +
                $"detached={detached} fallen={fallenDistance:F3} " +
                $"fallReverse={fallReverseDistance:F3} " +
                $"maxFallReverse={maxFallReverseStep:F3} " +
                $"maxFallStep={maxFallFrameStep:F3} " +
                $"connectedMoveTested={connectedOwnerMovementTested} " +
                $"connectedInputHealthy={connectedOwnerInputHealthy} " +
                $"connectedMove={connectedOwnerMoveDistance:F3} " +
                $"samples={sampleCount} corrections={correctionCount} " +
                $"fallSamples={fallSampleCount} fallCorrections={fallCorrectionCount} " +
                $"message='{message}'");
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
