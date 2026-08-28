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
    /// Opt-in two-peer Host regression harness for the Cover_Low to Cover_High path in the
    /// exact Fusion Climb demo. The harness is excluded from non-development players.
    /// </summary>
    internal sealed class FusionTraversalCoverSmokeBootstrap : MonoBehaviour
    {
        private const string Scenario = "fusion-traversal-cover";
        private const float RequiredCoverDistance = 0.5f;
        private const float RequiredOwnerMoveDistance = 0.35f;
        private const float RequiredOwnerJumpRise = 0.12f;
        private const float CoverAttachSettleSeconds = 0.35f;
        private const float HighCoverObservationSeconds = 0.75f;
        private const float ObserverSegmentSettleSeconds = 0.25f;
        private const float PositionSettleSeconds = 0.3f;
        private const float CoverConnectionGraceSeconds = 2f;
        // Keep the Host connected long enough for the observer to finish its post-detach local
        // input probe and write its JSON result.
        private const float ResultGraceSeconds = 3f;

        [Serializable]
        private struct Result
        {
            public bool passed;
            public string role;
            public string phase;
            public string message;
            public uint actorNetworkId;
            public bool sawLowCover;
            public bool sawHighCover;
            public bool detached;
            public float coverDistance;
            public float reverseDistance;
            public float maxReverseStep;
            public float maxFrameStep;
            public int sampleCount;
            public int correctionCount;
            public bool localMovementHealthy;
            public float localMoveDistance;
            public bool localJumpHealthy;
            public float localJumpRise;
            public string fatalTraversalLog;
            public float elapsedSeconds;
        }

        private string m_Role;
        private string m_ResultPath;
        private float m_StartedAt;
        private Keyboard m_SmokeKeyboard;
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

            var owner = new GameObject("Fusion Traversal Cover Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionTraversalCoverSmokeBootstrap>();
        }

        private void OnEnable()
        {
            Application.logMessageReceived += CaptureTraversalFailure;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= CaptureTraversalFailure;
            ReleaseCoverInput();
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
            if (string.IsNullOrWhiteSpace(session)) session = "GC2-Fusion-Traversal-Cover-Smoke";
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
                "fusion-cover-smoke",
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
            TraverseInteractive lowCover = null;
            TraverseInteractive highCover = null;
            bool requested = false;
            bool positioningRequested = false;
            bool positioningResolved = false;
            bool positioningAccepted = false;
            bool positioned = false;
            bool sawLow = false;
            bool sawHigh = false;
            Vector3 previous = default;
            float coverDistance = 0f;
            float highEnteredAt = -1f;
            float positionSettledAt = -1f;
            float lastLowAt = -1f;

            while (Elapsed < timeout)
            {
                if (TryFailFatal(actor, sawLow, sawHigh, false, coverDistance)) yield break;

                actor ??= FindLocalPlayer();
                remoteObserver ??= FindRemotePlayer();
                lowCover ??= FindCover("Cover_Low");
                highCover ??= FindCover("Cover_High");
                if (actor == null || remoteObserver == null || lowCover == null || highCover == null ||
                    !actor.IsOwnerInstance || !actor.Character.IsPlayer)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }

                if (!positioned)
                {
                    Vector3 target = CalculateCharacterRootStart(actor.Character, lowCover);
                    if (!positioningRequested)
                    {
                        UnitMotionNetworkController motion = actor.MotionController;
                        if (motion == null)
                        {
                            yield return null;
                            continue;
                        }

                        positioningRequested = true;
                        motion.RequestTeleport(
                            target,
                            float.NaN,
                            true,
                            result =>
                            {
                                positioningResolved = true;
                                positioningAccepted = result.approved;
                            });
                        NetworkCiTrace.Log(
                            "fusion-cover-smoke",
                            "host-position-cover",
                            actor.NetworkId,
                            0,
                            $"from={actor.transform.position:F3} target={target:F3}",
                            actor);
                    }

                    if (positioningResolved && !positioningAccepted)
                    {
                        Finish(
                            false,
                            "host-position-rejected",
                            "Authority rejected the smoke setup teleport to Cover_Low.",
                            actor);
                        yield break;
                    }

                    if (HorizontalDistance(actor.transform.position, target) > 0.15f ||
                        Mathf.Abs(actor.transform.position.y - target.y) > 0.25f)
                    {
                        yield return null;
                        continue;
                    }

                    if (positionSettledAt < 0f)
                    {
                        positionSettledAt = Elapsed;
                    }
                    if (Elapsed - positionSettledAt < PositionSettleSeconds)
                    {
                        yield return null;
                        continue;
                    }

                    positioned = true;
                }

                if (ShortcutPlayer.Instance != actor.gameObject)
                {
                    Finish(
                        false,
                        "host-shortcut",
                        "GC2 ShortcutPlayer does not resolve the authenticated Host owner.",
                        actor);
                    yield break;
                }

                if (!requested)
                {
                    requested = true;
                    previous = actor.transform.position;
                    NetworkCiTrace.Log(
                        "fusion-cover-smoke",
                        "host-request-low-cover",
                        actor.NetworkId,
                        0,
                        $"position={previous:F3} target='{lowCover.name}'",
                        actor);
                    // Use GC2's patched public entry path so the Host loopback request is
                    // authenticated and applied by the same authority route as a client request.
                    _ = lowCover.Enter(ShortcutPlayer.Instance.GetComponent<Character>(), default);
                }

                TraversalStance stance = GetStance(actor.Character);
                if (ReferenceEquals(stance?.Traverse, lowCover))
                {
                    if (!sawLow)
                    {
                        sawLow = true;
                        yield return new WaitForSecondsRealtime(CoverAttachSettleSeconds);
                        previous = actor.transform.position;
                        PressCoverInput();
                        NetworkCiTrace.Log(
                            "fusion-cover-smoke",
                            "host-low-cover-attached",
                            actor.NetworkId,
                            0,
                            $"position={actor.transform.position:F3}",
                            actor);
                    }

                    Vector3 current = actor.transform.position;
                    coverDistance += Vector3.Distance(previous, current);
                    previous = current;
                    lastLowAt = Elapsed;
                }
                else if (ReferenceEquals(stance?.Traverse, highCover))
                {
                    if (!sawHigh)
                    {
                        sawHigh = true;
                        highEnteredAt = Elapsed;
                        ReleaseCoverInput();
                        NetworkCiTrace.Log(
                            "fusion-cover-smoke",
                            "host-high-cover-attached",
                            actor.NetworkId,
                            0,
                            $"position={actor.transform.position:F3} distance={coverDistance:F3}",
                            actor);
                    }

                    if (Elapsed - highEnteredAt < HighCoverObservationSeconds)
                    {
                        yield return null;
                        continue;
                    }

                    if (TryFailFatal(actor, sawLow, sawHigh, false, coverDistance)) yield break;

                    stance.TryCancel(new Args(highCover.gameObject, actor.gameObject));
                    NetworkCiTrace.Log(
                        "fusion-cover-smoke",
                        "host-cover-cancel-requested",
                        actor.NetworkId,
                        0,
                        $"active='{stance.Traverse?.name ?? "none"}'",
                        actor);

                    while (Elapsed < timeout && GetStance(actor.Character)?.Traverse != null)
                    {
                        if (TryFailFatal(actor, sawLow, sawHigh, false, coverDistance)) yield break;
                        yield return null;
                    }

                    bool detached = GetStance(actor.Character)?.Traverse == null;
                    if (!detached)
                    {
                        Finish(
                            false,
                            "host-cover-no-detach",
                            "Host did not detach after the authoritative GC2 cover cancel.",
                            actor,
                            sawLow,
                            sawHigh,
                            false,
                            coverDistance);
                        yield break;
                    }

                    yield return RunLocalMovementAndJump(
                        actor,
                        timeout,
                        (moveDistance, jumpRise) =>
                        {
                            bool healthy =
                                moveDistance >= RequiredOwnerMoveDistance &&
                                jumpRise >= RequiredOwnerJumpRise;
                            if (healthy)
                            {
                                StartCoroutine(FinishAfterGrace(
                                    true,
                                    "host-cover-transition-detach-move-jump",
                                    "Fusion Host traversed Cover_Low to Cover_High, detached, and resumed normal movement and jumping.",
                                    actor,
                                    sawLow,
                                    sawHigh,
                                    coverDistance,
                                    moveDistance,
                                    jumpRise));
                            }
                            else
                            {
                                Finish(
                                    false,
                                    "host-post-cover-input-failed",
                                    "Host detached from cover but normal movement or jumping did not recover.",
                                    actor,
                                    sawLow,
                                    sawHigh,
                                    true,
                                    coverDistance,
                                    localMovementHealthy: moveDistance >= RequiredOwnerMoveDistance,
                                    localMoveDistance: moveDistance,
                                    localJumpHealthy: jumpRise >= RequiredOwnerJumpRise,
                                    localJumpRise: jumpRise);
                            }
                        });
                    yield break;
                }
                else if (sawLow && stance?.Traverse == null)
                {
                    ReleaseCoverInput();
                    if (lastLowAt >= 0f &&
                        Elapsed - lastLowAt <= CoverConnectionGraceSeconds)
                    {
                        // GC2 clears the previous stance before the authority has completed
                        // the authored ContinueB request. Do not mistake that bounded handoff
                        // for a user exit.
                        yield return null;
                        continue;
                    }
                    Finish(
                        false,
                        "host-left-low-cover",
                        "Host left Cover_Low before reaching its authored Cover_High continuation.",
                        actor,
                        sawLow,
                        sawHigh,
                        true,
                        coverDistance);
                    yield break;
                }

                yield return null;
            }

            ReleaseCoverInput();
            Finish(
                false,
                sawLow ? "host-no-high-cover" : "host-no-low-cover",
                "Timed out while traversing the exact Fusion demo's Cover_Low to Cover_High path.",
                actor,
                sawLow,
                sawHigh,
                GetStance(actor?.Character)?.Traverse == null,
                coverDistance);
        }

        private IEnumerator RunHostObserver()
        {
            int timeout = GetIntArgument("--gc2-network-smoke-timeout", 150);
            NetworkCharacter actor = null;
            NetworkCharacter localOwner = null;
            TraverseInteractive lowCover = null;
            TraverseInteractive highCover = null;
            Traverse lastTraverse = null;
            bool sawLow = false;
            bool sawHigh = false;
            bool detached = false;
            Vector3 previous = default;
            float segmentStartedAt = -1f;
            float coverDistance = 0f;
            float reverseDistance = 0f;
            float maxReverseStep = 0f;
            float maxFrameStep = 0f;
            int sampleCount = 0;
            int correctionCount = 0;

            while (Elapsed < timeout)
            {
                if (TryFailFatal(
                        actor,
                        sawLow,
                        sawHigh,
                        detached,
                        coverDistance,
                        reverseDistance,
                        maxReverseStep,
                        maxFrameStep,
                        sampleCount,
                        correctionCount))
                {
                    yield break;
                }

                actor ??= FindRemotePlayer();
                localOwner ??= FindLocalPlayer();
                lowCover ??= FindCover("Cover_Low");
                highCover ??= FindCover("Cover_High");
                if (actor == null || localOwner == null || lowCover == null || highCover == null)
                {
                    yield return new WaitForSecondsRealtime(0.1f);
                    continue;
                }

                TraversalStance stance = GetStance(actor.Character);
                Traverse active = stance?.Traverse;
                bool isLow = ReferenceEquals(active, lowCover);
                bool isHigh = ReferenceEquals(active, highCover);

                if (!sawLow && !isLow)
                {
                    yield return null;
                    continue;
                }

                if (isLow) sawLow = true;
                if (isHigh) sawHigh = true;

                Vector3 current = GetPresentedPosition(actor);
                if (!ReferenceEquals(active, lastTraverse))
                {
                    lastTraverse = active;
                    previous = current;
                    segmentStartedAt = Elapsed;
                    NetworkCiTrace.Log(
                        "fusion-cover-smoke",
                        active != null ? "observer-cover-segment" : "observer-cover-detached",
                        actor.NetworkId,
                        0,
                        $"active='{active?.name ?? "none"}' position={current:F3} " +
                        $"sawLow={sawLow} sawHigh={sawHigh}",
                        actor);
                }

                if ((isLow || isHigh) &&
                    Elapsed - segmentStartedAt >= ObserverSegmentSettleSeconds)
                {
                    Vector3 delta = current - previous;
                    float frameStep = delta.magnitude;
                    coverDistance += frameStep;
                    maxFrameStep = Mathf.Max(maxFrameStep, frameStep);

                    Vector3 expectedDirection = active.Transform.forward;
                    float signedProgress = Vector3.Dot(delta, expectedDirection);
                    if (signedProgress < 0f)
                    {
                        float reverseStep = -signedProgress;
                        reverseDistance += reverseStep;
                        maxReverseStep = Mathf.Max(maxReverseStep, reverseStep);
                        if (reverseStep > 0.01f) correctionCount++;
                    }

                    sampleCount++;
                }

                previous = current;

                if (sawHigh && active == null)
                {
                    detached = true;
                    bool smooth =
                        coverDistance >= RequiredCoverDistance &&
                        sampleCount >= 15 &&
                        reverseDistance <= 0.25f &&
                        maxReverseStep <= 0.1f &&
                        maxFrameStep <= 0.35f;
                    if (!smooth)
                    {
                        Finish(
                            false,
                            "observer-cover-stutter",
                            "Connected client observed material Cover traversal corrections or insufficient authoritative progress.",
                            actor,
                            sawLow,
                            sawHigh,
                            true,
                            coverDistance,
                            reverseDistance,
                            maxReverseStep,
                            maxFrameStep,
                            sampleCount,
                            correctionCount);
                        yield break;
                    }

                    yield return RunLocalMovementAndJump(
                        localOwner,
                        timeout,
                        (moveDistance, jumpRise) =>
                        {
                            bool movementHealthy = moveDistance >= RequiredOwnerMoveDistance;
                            bool jumpHealthy = jumpRise >= RequiredOwnerJumpRise;
                            if (movementHealthy && jumpHealthy)
                            {
                                StartCoroutine(FinishAfterGrace(
                                    true,
                                    "observer-cover-smooth-detach-local-input",
                                    "Connected client observed smooth Host cover traversal and detach while retaining normal local movement and jumping.",
                                    actor,
                                    sawLow,
                                    sawHigh,
                                    coverDistance,
                                    moveDistance,
                                    jumpRise,
                                    reverseDistance,
                                    maxReverseStep,
                                    maxFrameStep,
                                    sampleCount,
                                    correctionCount));
                            }
                            else
                            {
                                Finish(
                                    false,
                                    "observer-local-input-failed",
                                    "Connected owner did not retain normal movement or jumping after the Host detached from cover.",
                                    actor,
                                    sawLow,
                                    sawHigh,
                                    true,
                                    coverDistance,
                                    reverseDistance,
                                    maxReverseStep,
                                    maxFrameStep,
                                    sampleCount,
                                    correctionCount,
                                    movementHealthy,
                                    moveDistance,
                                    jumpHealthy,
                                    jumpRise);
                            }
                        });
                    yield break;
                }

                yield return null;
            }

            Finish(
                false,
                sawHigh ? "observer-no-detach" : sawLow ? "observer-no-high-cover" : "observer-no-low-cover",
                "Timed out while observing the Host Cover_Low to Cover_High transition and detach.",
                actor,
                sawLow,
                sawHigh,
                detached,
                coverDistance,
                reverseDistance,
                maxReverseStep,
                maxFrameStep,
                sampleCount,
                correctionCount);
        }

        private IEnumerator RunLocalMovementAndJump(
            NetworkCharacter actor,
            int timeout,
            Action<float, float> completed)
        {
            if (actor?.Character?.Player is not UnitPlayerDirectionalNetwork player ||
                !actor.IsOwnerInstance || !actor.Character.IsPlayer ||
                !actor.Character.Player.IsControllable || ShortcutPlayer.Instance != actor.gameObject)
            {
                completed?.Invoke(0f, 0f);
                yield break;
            }

            Vector3 start = actor.transform.position;
            float maxHeight = start.y;
            float testEndsAt = Mathf.Min(timeout, Elapsed + 5f);
            bool jumpSent = false;

            while (Elapsed < testEndsAt)
            {
                bool sendJump = !jumpSent;
                player.InjectInput(Vector2.up, sendJump);
                jumpSent = true;
                maxHeight = Mathf.Max(maxHeight, actor.transform.position.y);

                float moved = HorizontalDistance(start, actor.transform.position);
                float risen = Mathf.Max(0f, maxHeight - start.y);
                if (moved >= RequiredOwnerMoveDistance && risen >= RequiredOwnerJumpRise)
                {
                    player.InjectInput(Vector2.zero);
                    completed?.Invoke(moved, risen);
                    yield break;
                }

                if (!string.IsNullOrEmpty(m_FatalTraversalLog))
                {
                    player.InjectInput(Vector2.zero);
                    completed?.Invoke(moved, risen);
                    yield break;
                }

                yield return null;
            }

            player.InjectInput(Vector2.zero);
            completed?.Invoke(
                HorizontalDistance(start, actor.transform.position),
                Mathf.Max(0f, maxHeight - start.y));
        }

        private IEnumerator FinishAfterGrace(
            bool passed,
            string phase,
            string message,
            NetworkCharacter actor,
            bool sawLow,
            bool sawHigh,
            float coverDistance,
            float localMoveDistance,
            float localJumpRise,
            float reverseDistance = 0f,
            float maxReverseStep = 0f,
            float maxFrameStep = 0f,
            int sampleCount = 0,
            int correctionCount = 0)
        {
            yield return new WaitForSecondsRealtime(ResultGraceSeconds);
            Finish(
                passed,
                phase,
                message,
                actor,
                sawLow,
                sawHigh,
                true,
                coverDistance,
                reverseDistance,
                maxReverseStep,
                maxFrameStep,
                sampleCount,
                correctionCount,
                localMoveDistance >= RequiredOwnerMoveDistance,
                localMoveDistance,
                localJumpRise >= RequiredOwnerJumpRise,
                localJumpRise);
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

        private static TraverseInteractive FindCover(string name)
        {
            return UnityObjectSearch.FindAll<TraverseInteractive>(FindObjectsInactive.Exclude)
                .FirstOrDefault(interactive =>
                    interactive != null &&
                    string.Equals(interactive.name, name, StringComparison.Ordinal));
        }

        private static TraversalStance GetStance(Character character)
        {
            return character?.Combat?.RequestStance<TraversalStance>();
        }

        private static Vector3 CalculateCharacterRootStart(
            Character character,
            TraverseInteractive interactive)
        {
            Vector3 targetAnchor = interactive.CalculateStartPosition(character);
            Vector3 currentAnchor = interactive.MotionInteractive.CharacterPosition(character);
            return character.transform.position + (targetAnchor - currentAnchor);
        }

        private static Vector3 GetPresentedPosition(NetworkCharacter actor)
        {
            if (actor == null) return default;
            FusionNativeNetworkCharacterMotor motor =
                actor.GetComponent<FusionNativeNetworkCharacterMotor>();
            Transform target = motor?.ActiveRemotePresentationTarget;
            return target != null ? target.position : actor.transform.position;
        }

        private void PressCoverInput()
        {
            m_SmokeKeyboard ??= Keyboard.current ??
                InputSystem.AddDevice<Keyboard>("GC2 Fusion Traversal Cover Smoke Keyboard");
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState(Key.D));
        }

        private void ReleaseCoverInput()
        {
            if (m_SmokeKeyboard == null) return;
            InputSystem.QueueStateEvent(m_SmokeKeyboard, new KeyboardState());
        }

        private void CaptureTraversalFailure(
            string condition,
            string stackTrace,
            UnityEngine.LogType type)
        {
            if (m_Finished ||
                type != UnityEngine.LogType.Error &&
                type != UnityEngine.LogType.Exception)
            {
                return;
            }

            string combined = (condition ?? string.Empty) + "\n" + (stackTrace ?? string.Empty);
            if (combined.IndexOf("MissingReferenceException", StringComparison.OrdinalIgnoreCase) < 0 &&
                combined.IndexOf("collider2", StringComparison.OrdinalIgnoreCase) < 0 &&
                combined.IndexOf(
                    "authoritative traversal task failed",
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                return;
            }

            m_FatalTraversalLog = string.IsNullOrWhiteSpace(condition)
                ? type.ToString()
                : condition.Trim();
        }

        private bool TryFailFatal(
            NetworkCharacter actor,
            bool sawLow,
            bool sawHigh,
            bool detached,
            float coverDistance,
            float reverseDistance = 0f,
            float maxReverseStep = 0f,
            float maxFrameStep = 0f,
            int sampleCount = 0,
            int correctionCount = 0)
        {
            if (string.IsNullOrEmpty(m_FatalTraversalLog)) return false;

            Finish(
                false,
                "fatal-traversal-log",
                "The cover flow emitted a fatal Traversal/collider error.",
                actor,
                sawLow,
                sawHigh,
                detached,
                coverDistance,
                reverseDistance,
                maxReverseStep,
                maxFrameStep,
                sampleCount,
                correctionCount);
            return true;
        }

        private void Finish(
            bool passed,
            string phase,
            string message,
            NetworkCharacter actor = null,
            bool sawLowCover = false,
            bool sawHighCover = false,
            bool detached = false,
            float coverDistance = 0f,
            float reverseDistance = 0f,
            float maxReverseStep = 0f,
            float maxFrameStep = 0f,
            int sampleCount = 0,
            int correctionCount = 0,
            bool localMovementHealthy = false,
            float localMoveDistance = 0f,
            bool localJumpHealthy = false,
            float localJumpRise = 0f)
        {
            if (m_Finished) return;
            m_Finished = true;
            ReleaseCoverInput();

            if (!string.IsNullOrEmpty(m_FatalTraversalLog))
            {
                passed = false;
                phase = "fatal-traversal-log";
            }

            var result = new Result
            {
                passed = passed,
                role = m_Role,
                phase = phase,
                message = message,
                actorNetworkId = actor != null ? actor.NetworkId : 0,
                sawLowCover = sawLowCover,
                sawHighCover = sawHighCover,
                detached = detached,
                coverDistance = coverDistance,
                reverseDistance = reverseDistance,
                maxReverseStep = maxReverseStep,
                maxFrameStep = maxFrameStep,
                sampleCount = sampleCount,
                correctionCount = correctionCount,
                localMovementHealthy = localMovementHealthy,
                localMoveDistance = localMoveDistance,
                localJumpHealthy = localJumpHealthy,
                localJumpRise = localJumpRise,
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
                $"[GC2 Fusion Traversal Cover Smoke] passed={passed} role={m_Role} " +
                $"phase={phase} actor={result.actorNetworkId} low={sawLowCover} " +
                $"high={sawHighCover} detached={detached} distance={coverDistance:F3} " +
                $"reverse={reverseDistance:F3} maxReverse={maxReverseStep:F3} " +
                $"maxStep={maxFrameStep:F3} samples={sampleCount} " +
                $"corrections={correctionCount} localMove={localMoveDistance:F3} " +
                $"localJumpRise={localJumpRise:F3} fatal='{result.fatalTraversalLog}' " +
                $"message='{message}'");
            Application.Quit(passed ? 0 : 1);
        }

        private float Elapsed => Time.realtimeSinceStartup - m_StartedAt;

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            return Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
        }

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
