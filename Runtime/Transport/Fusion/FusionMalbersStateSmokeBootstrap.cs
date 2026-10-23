using System;
using System.Collections;
using System.IO;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Opt-in standalone CLI bootstrap that proves native Malbers state parity between a
    /// Fusion authority and a connected observer. Dormant unless the smoke scenario is
    /// <c>malbers-state</c>.
    /// </summary>
    internal sealed class FusionMalbersStateSmokeBootstrap : MonoBehaviour
    {
        private const float ObserverHandshakeSeconds = 60f;

        private string m_Role;
        private string m_ReadyPath;
        private float m_StartedAt;
        private float m_NextDiagnosticAt;
        private FusionSessionBootstrap m_SessionBootstrap;
        private FusionTransportBridge m_Bridge;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!NetworkEnemyShooterSmokeUtility.TryGetArgument(
                    "--gc2-network-smoke-scenario",
                    out string scenario) ||
                !string.Equals(scenario, "malbers-state", StringComparison.Ordinal))
            {
                return;
            }

            if (!NetworkEnemyShooterSmokeUtility.TryGetArgument(
                    "--gc2-network-smoke-role",
                    out string role) ||
                !role.StartsWith("fusion-", StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("Fusion Malbers State Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionMalbersStateSmokeBootstrap>();
        }

        private async void Start()
        {
            m_StartedAt = Time.realtimeSinceStartup;
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-ready",
                out m_ReadyPath);
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-session",
                out string session);
            if (string.IsNullOrWhiteSpace(session)) session = "GC2-Malbers-Smoke";
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-region",
                out string region);
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
                Fail("start", "FusionSessionBootstrap is missing from the Malbers smoke scene.");
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

            Debug.Log($"[MalbersSmoke] session started role={m_Role}");
            m_SessionBootstrap = bootstrap;
            m_Bridge = UnityObjectSearch.FindAny<FusionTransportBridge>();
            StartCoroutine(Monitor());
        }

        /// <summary>
        /// Describes the runner the bridge is bound to. A session can be alive while the bridge
        /// reports an idle runner, which blocks the transport-neutral character registry.
        /// </summary>
        private string DescribeRunner()
        {
            if (m_Bridge == null) return "fusionBridge=none";

            NetworkRunner runner = m_Bridge.Runner;
            return $"fusionBridge={m_Bridge.name} usable={m_Bridge.IsRunning} " +
                   $"sessionRunning={m_SessionBootstrap != null && m_SessionBootstrap.IsRunning} " +
                   $"runner={(runner != null ? runner.name : "none")} " +
                   $"runnerRunning={runner != null && runner.IsRunning} " +
                   $"runnerShutdown={runner != null && runner.IsShutdown} " +
                   $"server={runner != null && runner.IsServer} " +
                   $"player={runner != null && runner.IsPlayer} " +
                   $"mode={(runner != null ? runner.GameMode.ToString() : "none")}";
        }

        private IEnumerator Monitor()
        {
            bool authority = m_Role is "fusion-host" or "fusion-shared-master";
            int expectedHumans = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-expected-humans",
                2);
            int timeout = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-timeout",
                150);
            float holdSeconds = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-minimum-duration",
                authority ? 12 : 6);

            int actorNetworkId = 0;
            int targetState = 0;
            int observedState = 0;
            int initialState = 0;
            int sentBeforeForce = 0;
            bool hasInitialState = false;
            bool forced = false;
            bool changed = false;
            bool humansReady = false;
            bool observerReady = false;
            float humansReadyAt = 0f;
            float changedAt = 0f;
            float forcedAt = 0f;
            string phase = "joined";

            while (Time.realtimeSinceStartup - m_StartedAt < timeout)
            {
                int humans = NetworkMalbersSmokeUtility.CountAuthenticatedHumans();
                if (authority && !humansReady && humans >= expectedHumans)
                {
                    humansReady = true;
                    humansReadyAt = Time.realtimeSinceStartup;
                    Debug.Log($"[MalbersSmoke] humans ready={humans}");
                }

                if (!NetworkMalbersSmokeUtility.TryGetServerNpcState(
                        out NetworkCharacter character,
                        out int liveActorId,
                        out int liveState))
                {
                    if (Time.realtimeSinceStartup >= m_NextDiagnosticAt)
                    {
                        m_NextDiagnosticAt = Time.realtimeSinceStartup + 5f;
                        Debug.Log(
                            "[MalbersSmoke] no authoritative Malbers NPC yet: " +
                            DescribeRunner() + " || " +
                            NetworkMalbersSmokeUtility.DescribeCharacters());
                    }

                    yield return new WaitForSecondsRealtime(0.25f);
                    continue;
                }

                actorNetworkId = liveActorId;

                if (authority)
                {
                    if (!observerReady)
                    {
                        if (IsReadyMarkerPresent())
                        {
                            observerReady = true;
                            Debug.Log("[MalbersSmoke] observer ready marker detected");
                        }
                        else if (Time.realtimeSinceStartup - humansReadyAt >=
                                 ObserverHandshakeSeconds)
                        {
                            observerReady = true;
                            Debug.Log("[MalbersSmoke] observer handshake timed out; forcing anyway");
                        }
                    }

                    if (!forced && observerReady &&
                        Time.realtimeSinceStartup - humansReadyAt >= 1f)
                    {
                        NetworkMalbersSmokeUtility.TryGetSnapshotCounters(
                            out _,
                            out sentBeforeForce,
                            out _,
                            out _);
                        if (!NetworkMalbersSmokeUtility.TryForceNextState(
                                character,
                                liveState,
                                out targetState))
                        {
                            Fail("force", "Could not force a Malbers state on the server NPC.");
                            yield break;
                        }

                        forced = true;
                        forcedAt = Time.realtimeSinceStartup;
                        phase = "forced";
                        Debug.Log(
                            $"[MalbersSmoke] authority forced actor={actorNetworkId} " +
                            $"from={liveState} state={targetState}");
                    }

                    if (forced &&
                        NetworkMalbersSmokeUtility.TryGetSnapshotCounters(
                            out int lastSent,
                            out int sentCount,
                            out _,
                            out _) &&
                        sentCount > sentBeforeForce &&
                        lastSent == targetState)
                    {
                        changed = true;
                        observedState = targetState;
                        phase = "authority-observed";
                    }

                    if (changed && Time.realtimeSinceStartup - forcedAt >= holdSeconds)
                    {
                        Finish(
                            0,
                            phase,
                            humans,
                            actorNetworkId,
                            targetState,
                            observedState,
                            changed,
                            "Authority forced the native Malbers state and sent it.");
                        yield break;
                    }
                }
                else
                {
                    if (NetworkMalbersSmokeUtility.TryGetAppliedSnapshot(
                            actorNetworkId,
                            out int appliedActor,
                            out int appliedState,
                            out int appliedCount))
                    {
                        if (!hasInitialState)
                        {
                            hasInitialState = true;
                            initialState = appliedState;
                            observedState = appliedState;
                            actorNetworkId = appliedActor;
                            WriteReadyMarker(appliedActor);
                            Debug.Log(
                                $"[MalbersSmoke] observer initial actor={appliedActor} " +
                                $"state={appliedState}");
                        }
                        else if (appliedState != observedState)
                        {
                            changed = true;
                            changedAt = Time.realtimeSinceStartup;
                            actorNetworkId = appliedActor;
                            observedState = appliedState;
                            phase = "observer-applied";
                            Debug.Log(
                                $"[MalbersSmoke] observer applied actor={appliedActor} " +
                                $"from={initialState} state={observedState} " +
                                $"snapshots={appliedCount}");
                        }
                    }

                    if (changed &&
                        Time.realtimeSinceStartup - changedAt >= holdSeconds &&
                        Time.realtimeSinceStartup - m_StartedAt >= holdSeconds + 1f)
                    {
                        Finish(
                            0,
                            phase,
                            humans,
                            actorNetworkId,
                            targetState,
                            observedState,
                            changed,
                            "Observer received the authoritative native Malbers state.");
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            Finish(
                1,
                phase,
                NetworkMalbersSmokeUtility.CountAuthenticatedHumans(),
                actorNetworkId,
                targetState,
                observedState,
                changed,
                "Malbers state smoke timed out.");
        }

        /// <summary>
        /// Writes the observer handshake marker. The authority waits for it before forcing a
        /// state, which guarantees the observed change is a live transition rather than the
        /// spawn-state snapshot the joining client would have received anyway.
        /// </summary>
        private void WriteReadyMarker(int actorNetworkId)
        {
            if (string.IsNullOrWhiteSpace(m_ReadyPath)) return;

            try
            {
                string fullPath = Path.GetFullPath(m_ReadyPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
                File.WriteAllText(fullPath, actorNetworkId.ToString());
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[MalbersSmoke] could not write the ready marker: " + exception.Message);
            }
        }

        private bool IsReadyMarkerPresent()
        {
            if (string.IsNullOrWhiteSpace(m_ReadyPath)) return false;
            try
            {
                return File.Exists(Path.GetFullPath(m_ReadyPath));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Finish(
            int exitCode,
            string phase,
            int humans,
            int actorNetworkId,
            int authorityState,
            int observedState,
            bool changed,
            string message)
        {
            var result = new NetworkEnemyShooterSmokeUtility.Result
            {
                role = m_Role,
                phase = phase,
                humans = humans,
                serverNpcs = 1,
                elapsedSeconds = Time.realtimeSinceStartup - m_StartedAt,
                malbersActorNetworkId = actorNetworkId,
                malbersAuthorityState = authorityState,
                malbersObservedState = observedState,
                malbersStateChanged = changed,
                message = message
            };
            NetworkEnemyShooterSmokeUtility.Finish(result, exitCode);
        }

        private void Fail(string phase, string message)
        {
            var result = new NetworkEnemyShooterSmokeUtility.Result
            {
                role = m_Role,
                phase = phase,
                elapsedSeconds = Time.realtimeSinceStartup - m_StartedAt,
                message = message
            };
            NetworkEnemyShooterSmokeUtility.Finish(result, 1);
        }
    }
}
