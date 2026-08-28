using System;
using System.Collections;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Melee;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.Fusion
{
    /// <summary>Opt-in standalone CLI bootstrap for the Fusion Free Flow Combat smoke.</summary>
    internal sealed class FusionFreeFlowCombatSmokeBootstrap : MonoBehaviour
    {
        private readonly List<NetworkCharacter> m_Characters = new(16);

        private string m_Role;
        private float m_StartedAt;
        private global::Arawn.GameCreator2.Networking.Transport.Fusion.FusionTransportBridge
            m_Bridge;

        private bool m_LocalReadyCaptured;
        private int m_LocalReadySpawned;
        private int m_LocalReadyAdmitted;
        private bool m_RemoteReadyCaptured;
        private int m_RemoteReadySpawned;
        private int m_RemoteReadyAdmitted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                    "--gc2-network-smoke-scenario",
                    out string scenario) ||
                !string.Equals(scenario, "freeflow-combat", StringComparison.Ordinal))
            {
                return;
            }

            if (!NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                    "--gc2-network-smoke-role",
                    out string role) ||
                !role.StartsWith("fusion-", StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("Fusion Free Flow Combat Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionFreeFlowCombatSmokeBootstrap>();
        }

        private async void Start()
        {
            m_StartedAt = Time.realtimeSinceStartup;
            NetworkFreeFlowCombatSmokeUtility.EnableSemanticAttackDiagnostics();
            NetworkFreeFlowCombatSmokeUtility.BeginSemanticObservation();
            NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);
            NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-session",
                out string session);
            if (string.IsNullOrWhiteSpace(session)) session = "GC2-FreeFlow-Smoke";
            NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-region",
                out string region);
            var startOptions = new global::Arawn.GameCreator2.Networking.Transport.Fusion
                .FusionSessionStartOptions(session, region);

            string appId = Environment.GetEnvironmentVariable(
                "GC2_NETWORK_SMOKE_PHOTON_APP_ID");
            if (!string.IsNullOrWhiteSpace(appId))
            {
                PhotonAppSettings settings = PhotonAppSettings.Global;
                if (settings != null) settings.AppSettings.AppIdFusion = appId.Trim();
            }

            var bootstrap = UnityObjectSearch.FindAny<
                global::Arawn.GameCreator2.Networking.Transport.Fusion.FusionSessionBootstrap>();
            if (bootstrap == null)
            {
                Fail("start", "FusionSessionBootstrap is missing from the Free Flow scene.");
                return;
            }

            if (m_Role is not ("fusion-host" or "fusion-client" or
                "fusion-shared-master" or "fusion-shared-client"))
            {
                Fail("start", $"Unsupported Fusion Free Flow smoke role '{m_Role}'.");
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
                    Fail(
                        "start",
                        $"Fusion start failed: {startResult.ShutdownReason} " +
                        startResult.ErrorMessage);
                    return;
                }
            }
            catch (Exception exception)
            {
                Fail("start", exception.GetType().Name + ": " + exception.Message);
                return;
            }

            m_Bridge = ResolveBridge();
            Debug.Log($"[GC2 Free Flow Smoke] Fusion session started role={m_Role}.");
            StartCoroutine(Monitor());
        }

        private IEnumerator Monitor()
        {
            bool authority = m_Role is "fusion-host" or "fusion-shared-master";
            int expectedHumans = NetworkFreeFlowCombatSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-expected-humans",
                2);
            int timeout = NetworkFreeFlowCombatSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-timeout",
                120);
            int minimumDuration = NetworkFreeFlowCombatSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-minimum-duration",
                authority ? 0 : 5);

            NetworkFreeFlowCombatSmokeUtility.Result lastStrictResult = null;
            NetworkFreeFlowCombatSmokeUtility.Result lastGameplayFailureResult = null;
            NetworkFreeFlowCombatSmokeUtility.Result lastFoundationResult = null;
            NetworkFreeFlowCombatSmokeUtility.Result lastFullReadyResult = null;
            NetworkFreeFlowCombatSmokeUtility.Result frozenObserverSemanticResult = null;
            bool semanticAttackIssued = false;
            bool semanticAttackObserved = false;
            bool semanticCounterObserved = false;
            bool observerMarkerSignaled = false;
            string lastSemanticMessage = string.Empty;
            float foundationReadyAt = -1f;
            while (true)
            {
                m_Bridge ??= ResolveBridge();
                float elapsedSeconds = Time.realtimeSinceStartup - m_StartedAt;
                float deadline = foundationReadyAt >= 0f
                    ? foundationReadyAt + timeout
                    : timeout;
                if (elapsedSeconds >= deadline) break;

                NetworkFreeFlowCombatSmokeUtility.TransportEvidence evidence =
                    CaptureAdmissionEvidence(authority, expectedHumans);
                bool foundationReady = NetworkFreeFlowCombatSmokeUtility.TryCaptureFoundation(
                    m_Role,
                    expectedHumans,
                    authority,
                    requireNpcPresentation: true,
                    "foundation",
                    elapsedSeconds,
                    evidence,
                    out NetworkFreeFlowCombatSmokeUtility.Result foundationResult);
                if (foundationReady)
                {
                    // Keep the last structurally complete snapshot. A peer shutting down after a
                    // semantic failure must not replace it with a misleading bridge-not-running
                    // teardown result.
                    lastFoundationResult = foundationResult;
                    if (foundationReadyAt < 0f) foundationReadyAt = elapsedSeconds;
                    NetworkFreeFlowCombatSmokeUtility.ArmAssignedLocalPlayerCounter();
                }

                bool fullReady = NetworkFreeFlowCombatSmokeUtility.TryCapture(
                    m_Role,
                    expectedHumans,
                    authority,
                    requireNpcPresentation: true,
                    "joined",
                    elapsedSeconds,
                    evidence,
                    out NetworkFreeFlowCombatSmokeUtility.Result strictResult);
                lastStrictResult = strictResult;
                if (fullReady)
                {
                    lastFullReadyResult = strictResult;
                }
                else if (foundationReady &&
                         (authority || !semanticAttackIssued ||
                          lastGameplayFailureResult == null))
                {
                    // Freeze the observer's last pre-Attack strict failure. Attack/MotionWarp can
                    // intentionally change candidates and UI, and that secondary state must not
                    // overwrite the gameplay condition which originally blocked full readiness.
                    lastGameplayFailureResult = strictResult;
                }

                bool minimumDurationElapsed =
                    elapsedSeconds >= minimumDuration;
                if (authority && minimumDurationElapsed)
                {
                    bool markerReady =
                        NetworkFreeFlowCombatSmokeUtility.IsObserverReadySignaled();
                    NetworkFreeFlowCombatSmokeUtility.Result semanticResult =
                        lastFullReadyResult ?? lastFoundationResult ?? strictResult;
                    if (markerReady && semanticResult != null)
                    {
                        NetworkFreeFlowCombatSmokeUtility.FreezeAssignedLocalPlayerCounter();
                        bool attackValid = NetworkFreeFlowCombatSmokeUtility
                            .TryValidateAuthorityPlayerTargetedAttack(
                                semanticResult,
                                out string attackMessage);
                        bool counterValid = NetworkFreeFlowCombatSmokeUtility
                            .TryValidateAuthorityPlayerCounter(
                                semanticResult,
                                out string counterMessage);
                        lastSemanticMessage = attackValid && counterValid
                            ? counterMessage
                            : !attackValid
                                ? attackMessage
                                : counterMessage;

                        if (attackValid && counterValid && lastFullReadyResult != null)
                        {
                            lastFullReadyResult.phase = "semantic-actions-authority";
                            lastFullReadyResult.message = lastSemanticMessage;
                            NetworkFreeFlowCombatSmokeUtility.SignalAuthorityValidated(
                                m_Role,
                                lastFullReadyResult);
                            NetworkFreeFlowCombatSmokeUtility.Finish(lastFullReadyResult, 0);
                            yield break;
                        }
                    }
                    else
                    {
                        lastSemanticMessage =
                            "Authority is ready; waiting for the observer's real targeted " +
                            "Free Flow attack and canonical counter.";
                    }
                }

                if (minimumDurationElapsed && !authority && lastFoundationResult != null)
                {
                    if (observerMarkerSignaled)
                    {
                        if (NetworkFreeFlowCombatSmokeUtility
                            .IsAuthorityValidationSignaled())
                        {
                            NetworkFreeFlowCombatSmokeUtility.Finish(
                                frozenObserverSemanticResult,
                                0);
                            yield break;
                        }

                        lastSemanticMessage =
                            "The exact Attack and canonical Counter passed; waiting for " +
                            "authority to acknowledge the frozen tuples while the player " +
                            "remains registered.";
                        yield return new WaitForSecondsRealtime(0.25f);
                        continue;
                    }

                    if (!semanticAttackIssued)
                    {
                        semanticAttackIssued = NetworkFreeFlowCombatSmokeUtility
                            .TryBeginLocalPlayerTargetedAttack(out lastSemanticMessage);
                    }

                    NetworkFreeFlowCombatSmokeUtility.Result semanticResult =
                        lastFullReadyResult ?? lastFoundationResult;
                    semanticResult.elapsedSeconds = elapsedSeconds;
                    string attackMessage = lastSemanticMessage;
                    if (semanticAttackIssued)
                    {
                        bool attackObservedNow = NetworkFreeFlowCombatSmokeUtility
                            .TryValidateLocalPlayerTargetedAttack(
                                semanticResult,
                                out attackMessage);
                        semanticAttackObserved |= attackObservedNow;
                    }
                    bool counterObservedNow = NetworkFreeFlowCombatSmokeUtility
                        .TryValidateObservedCanonicalCounter(
                            semanticResult,
                            out string counterMessage);
                    semanticCounterObserved |= counterObservedNow;
                    lastSemanticMessage = !semanticAttackObserved
                        ? attackMessage
                        : counterMessage;

                    if (semanticAttackObserved && semanticCounterObserved)
                    {
                        semanticResult.phase = "semantic-actions-observer";
                        semanticResult.message = lastSemanticMessage;
                        if (lastFullReadyResult != null)
                        {
                            if (!observerMarkerSignaled)
                            {
                                NetworkFreeFlowCombatSmokeUtility.SignalObserverReady(
                                    m_Role,
                                    semanticResult);
                                frozenObserverSemanticResult = semanticResult;
                                observerMarkerSignaled = true;
                            }

                            if (NetworkFreeFlowCombatSmokeUtility
                                .IsAuthorityValidationSignaled())
                            {
                                NetworkFreeFlowCombatSmokeUtility.Finish(
                                    frozenObserverSemanticResult,
                                    0);
                                yield break;
                            }

                            lastSemanticMessage =
                                "The exact Attack and canonical Counter passed; waiting for " +
                                "authority to acknowledge the same tuples while the player " +
                                "remains registered.";
                        }
                        else
                        {
                            lastSemanticMessage =
                                "The exact Attack and canonical Counter passed; waiting for the " +
                                "strict authored-motion, facing, presentation, and UI contract.";
                        }
                    }
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            if (lastStrictResult == null)
            {
                NetworkFreeFlowCombatSmokeUtility.TryCapture(
                    m_Role,
                    expectedHumans,
                    authority,
                    requireNpcPresentation: true,
                    "timeout",
                    Time.realtimeSinceStartup - m_StartedAt,
                    CaptureAdmissionEvidence(authority, expectedHumans),
                    out lastStrictResult);
            }

            NetworkFreeFlowCombatSmokeUtility.Result timeoutResult =
                frozenObserverSemanticResult ??
                lastFullReadyResult ??
                lastGameplayFailureResult ??
                lastStrictResult ??
                lastFoundationResult;
            timeoutResult ??= new NetworkFreeFlowCombatSmokeUtility.Result
            {
                role = m_Role,
                phase = "timeout"
            };
            string strictMessage = timeoutResult.message;
            timeoutResult.phase = "timeout";
            timeoutResult.message =
                "Fusion Free Flow smoke timed out. Strict state: " + strictMessage +
                (string.IsNullOrWhiteSpace(lastSemanticMessage)
                    ? string.Empty
                    : " Semantic state: " + lastSemanticMessage);
            NetworkFreeFlowCombatSmokeUtility.Finish(timeoutResult, 1);
        }

        private void Update()
        {
            NetworkFreeFlowCombatSmokeUtility.BeginSemanticObservation();
            NetworkFreeFlowCombatSmokeUtility.ObserveNpcMotionAndFacing();
        }

        private NetworkFreeFlowCombatSmokeUtility.TransportEvidence CaptureAdmissionEvidence(
            bool authority,
            int expectedHumans)
        {
            var evidence = new NetworkFreeFlowCombatSmokeUtility.TransportEvidence
            {
                admissionRequired = true,
                remoteGameplayReadyRequired = authority && expectedHumans > 1
            };

            if (m_Bridge == null || !m_Bridge.IsRunning)
            {
                evidence.diagnostic = "FusionTransportBridge is not running.";
                return evidence;
            }

            CountAdmission(out int spawned, out int admitted, out string diagnostic);
            evidence.currentlySpawned = spawned;
            evidence.currentlyAdmitted = admitted;

            if (!m_LocalReadyCaptured && m_Bridge.IsLocalGameplayReady)
            {
                m_LocalReadyCaptured = true;
                m_LocalReadySpawned = spawned;
                m_LocalReadyAdmitted = admitted;
                Debug.Log(
                    "[GC2 Free Flow Smoke] Fusion first local gameplay readiness " +
                    $"captured spawned={spawned} admitted={admitted}.");
            }

            if (!m_RemoteReadyCaptured && evidence.remoteGameplayReadyRequired)
            {
                bool hasLocalId = m_Bridge.TryGetLocalClientId(out uint localClientId);
                foreach (uint clientId in m_Bridge.ConnectedClientIds)
                {
                    if (hasLocalId && clientId == localClientId) continue;
                    if (!m_Bridge.IsClientReady(clientId)) continue;
                    m_RemoteReadyCaptured = true;
                    m_RemoteReadySpawned = spawned;
                    m_RemoteReadyAdmitted = admitted;
                    Debug.Log(
                        "[GC2 Free Flow Smoke] Fusion first remote gameplay readiness " +
                        $"captured client={clientId} spawned={spawned} admitted={admitted}.");
                    break;
                }
            }

            evidence.localGameplayReadyObserved = m_LocalReadyCaptured;
            evidence.remoteGameplayReadyObserved = m_RemoteReadyCaptured;
            evidence.spawnedAtFirstLocalReady = m_LocalReadySpawned;
            evidence.admittedAtFirstLocalReady = m_LocalReadyAdmitted;
            evidence.spawnedAtFirstRemoteReady = m_RemoteReadySpawned;
            evidence.admittedAtFirstRemoteReady = m_RemoteReadyAdmitted;
            evidence.diagnostic = diagnostic;
            return evidence;
        }

        private void CountAdmission(
            out int spawned,
            out int admitted,
            out string diagnostic)
        {
            spawned = 0;
            admitted = 0;
            bool registryReady =
                global::Arawn.GameCreator2.Networking.Transport.Fusion
                    .FusionAuthoritySpawnRegistry.TryGet(
                        m_Bridge != null ? m_Bridge.Runner : null,
                        out var registry);

            m_Characters.Clear();
            m_Bridge?.CopyRegisteredCharacters(m_Characters);
            int explicitNpcs = 0;
            for (int i = 0; i < m_Characters.Count; i++)
            {
                NetworkCharacter character = m_Characters[i];
                if (character == null ||
                    character.ActorType != NetworkCharacterActorType.NPC ||
                    !character.IsServerAuthoritativeNPC)
                {
                    continue;
                }

                explicitNpcs++;
                var identity = character.GetComponent<
                    global::Arawn.GameCreator2.Networking.Transport.Fusion
                        .FusionNetworkIdentity>() ??
                    character.GetComponentInParent<
                        global::Arawn.GameCreator2.Networking.Transport.Fusion
                            .FusionNetworkIdentity>();
                if (identity == null || !identity.IsSpawned) continue;
                spawned++;
                if (registryReady &&
                    identity.TransportAdmitted &&
                    identity.HasAuthorityAdmission &&
                    registry.IsAdmitted(identity))
                {
                    admitted++;
                }
            }

            diagnostic =
                $"registeredNpcs={explicitNpcs}, registryReady={registryReady}, " +
                $"registryAdmitted={(registryReady ? registry.AdmittedCount : 0)}, " +
                $"epoch={(m_Bridge != null ? m_Bridge.AuthorityEpoch : 0)}";
        }

        private static global::Arawn.GameCreator2.Networking.Transport.Fusion
            .FusionTransportBridge ResolveBridge()
        {
            return NetworkTransportBridge.Active as
                       global::Arawn.GameCreator2.Networking.Transport.Fusion
                           .FusionTransportBridge ??
                   UnityObjectSearch.FindAny<
                       global::Arawn.GameCreator2.Networking.Transport.Fusion
                           .FusionTransportBridge>(FindObjectsInactive.Include);
        }

        private void Fail(string phase, string message)
        {
            NetworkFreeFlowCombatSmokeUtility.Finish(
                new NetworkFreeFlowCombatSmokeUtility.Result
                {
                    role = m_Role,
                    phase = phase,
                    message = message,
                    elapsedSeconds = Time.realtimeSinceStartup - m_StartedAt
                },
                1);
        }
    }
}
