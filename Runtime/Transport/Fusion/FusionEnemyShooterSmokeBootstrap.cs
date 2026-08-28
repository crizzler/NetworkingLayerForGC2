using System;
using System.Collections;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>Opt-in standalone CLI bootstrap for Fusion enemy-demo smoke jobs.</summary>
    internal sealed class FusionEnemyShooterSmokeBootstrap : MonoBehaviour
    {
        private string m_Role;
        private float m_StartedAt;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (NetworkEnemyShooterSmokeUtility.TryGetArgument(
                    "--gc2-network-smoke-scenario",
                    out string scenario) &&
                !string.Equals(scenario, "enemy-shooter", StringComparison.Ordinal))
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

            var owner = new GameObject("Fusion Enemy Shooter Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionEnemyShooterSmokeBootstrap>();
        }

        private async void Start()
        {
            m_StartedAt = Time.realtimeSinceStartup;
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-session",
                out string session);
            if (string.IsNullOrWhiteSpace(session)) session = "GC2-Network-Smoke";
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
                Fail("start", "FusionSessionBootstrap is missing from the smoke scene.");
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

            Debug.Log($"[GC2 Network Smoke] Session started role={m_Role}.");
            StartCoroutine(Monitor());
        }

        private IEnumerator Monitor()
        {
            bool authority = m_Role is "fusion-host" or "fusion-shared-master";
            int expectedHumans = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-expected-humans",
                2);
            int timeout = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-timeout",
                120);
            bool expectDisconnect = authority &&
                NetworkEnemyShooterSmokeUtility.HasFlag(
                    "--gc2-network-smoke-expect-disconnect");
            bool requireCombat = authority &&
                NetworkEnemyShooterSmokeUtility.HasFlag(
                    "--gc2-network-smoke-require-combat");
            int minimumObservationSeconds = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-minimum-duration",
                authority ? 0 : 8);
            string phase = "joined";

            while (Time.realtimeSinceStartup - m_StartedAt < timeout)
            {
                int phaseHumans = phase == "joined"
                    ? expectedHumans
                    : Math.Max(0, expectedHumans - 1);
                if (NetworkEnemyShooterSmokeUtility.TryCapture(
                        m_Role,
                        phaseHumans,
                        authority,
                        phase == "joined" && requireCombat,
                        phase,
                        Time.realtimeSinceStartup - m_StartedAt,
                        out NetworkEnemyShooterSmokeUtility.Result result))
                {
                    if (!authority &&
                        Time.realtimeSinceStartup - m_StartedAt < minimumObservationSeconds)
                    {
                        yield return new WaitForSecondsRealtime(0.25f);
                        continue;
                    }

                    if (phase == "joined" && expectDisconnect)
                    {
                        phase = "disconnect-replacement";
                        Debug.Log(
                            "[GC2 Network Smoke] Initial membership ready; waiting for " +
                            "disconnect replacement.");
                    }
                    else
                    {
                        NetworkEnemyShooterSmokeUtility.Finish(result, 0);
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(0.25f);
            }

            NetworkEnemyShooterSmokeUtility.TryCapture(
                m_Role,
                phase == "joined" ? expectedHumans : Math.Max(0, expectedHumans - 1),
                authority,
                phase == "joined" && requireCombat,
                phase,
                Time.realtimeSinceStartup - m_StartedAt,
                out NetworkEnemyShooterSmokeUtility.Result timeoutResult);
            timeoutResult.message = "Smoke validation timed out. Last state: " + timeoutResult.message;
            NetworkEnemyShooterSmokeUtility.Finish(timeoutResult, 1);
        }

        private void Fail(string phase, string message)
        {
            NetworkEnemyShooterSmokeUtility.Finish(
                new NetworkEnemyShooterSmokeUtility.Result
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
