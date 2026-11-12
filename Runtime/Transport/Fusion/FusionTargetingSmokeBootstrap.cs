#if UNITY_EDITOR || DEBUG
using System;
using System.Collections;
using System.IO;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Opt-in standalone CLI bootstrap that proves native Targeting state parity between a
    /// Fusion authority and a connected observer. Dormant unless the smoke scenario is
    /// <c>targeting</c>.
    /// </summary>
    internal sealed class FusionTargetingSmokeBootstrap : MonoBehaviour
    {
        private string m_Role;
        private string m_ReadyPath;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (!NetworkEnemyShooterSmokeUtility.TryGetArgument(
                    "--gc2-network-smoke-scenario",
                    out string scenario) ||
                !string.Equals(scenario, "targeting", StringComparison.Ordinal))
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

            var owner = new GameObject("Fusion Targeting State Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionTargetingSmokeBootstrap>();
        }

        private async void Start()
        {
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-ready",
                out m_ReadyPath);
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-session",
                out string session);
            if (string.IsNullOrWhiteSpace(session)) session = "GC2-Targeting-Smoke";
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
                Fail("start", "FusionSessionBootstrap is missing from the Targeting smoke scene.");
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

            Debug.Log($"[TargetingSmoke] session started role={m_Role}");
            StartCoroutine(NetworkTargetingSmokeProbe.Run(m_Role, m_ReadyPath));
        }

        private void Fail(string phase, string message)
        {
            NetworkTargetingSmokeProbe.Fail(m_Role, phase + ": " + message);
        }
    }
}
#endif
