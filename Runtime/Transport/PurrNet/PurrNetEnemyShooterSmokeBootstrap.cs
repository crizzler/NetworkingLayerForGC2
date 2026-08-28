using System;
using System.Collections;
using System.Reflection;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>Opt-in standalone CLI bootstrap for PurrNet enemy-demo smoke jobs.</summary>
    internal sealed class PurrNetEnemyShooterSmokeBootstrap : MonoBehaviour
    {
        private string m_Role;
        private float m_StartedAt;
        private PurrNetTransportBridge m_Bridge;
        private bool m_ManagerDiagnosticLogged;

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
                !role.StartsWith("purrnet-", StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("PurrNet Enemy Shooter Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<PurrNetEnemyShooterSmokeBootstrap>();
        }

        private void Start()
        {
            m_StartedAt = Time.realtimeSinceStartup;
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);

            NetworkManager manager = NetworkManager.main ?? UnityObjectSearch.FindAny<NetworkManager>();
            if (manager == null)
            {
                Fail("start", "PurrNet NetworkManager is missing from the smoke scene.");
                return;
            }

            m_Bridge = UnityObjectSearch.FindAny<PurrNetTransportBridge>(
                FindObjectsInactive.Include);
            if (m_Bridge == null)
            {
                Fail("start", "PurrNetTransportBridge is missing from the smoke scene.");
                return;
            }

            m_Bridge.ConfigureNetworkManager(manager);
            ConfigureTransport(manager.transport);
            try
            {
                switch (m_Role)
                {
                    case "purrnet-host": manager.StartHost(); break;
                    case "purrnet-server": manager.StartServer(); break;
                    case "purrnet-client": manager.StartClient(); break;
                    default:
                        Fail("start", $"Unsupported PurrNet smoke role '{m_Role}'.");
                        return;
                }

                // Reassert the explicit session manager after StartClient/StartServer. This also
                // covers PurrNet versions whose client-start callback is raised later in a tick.
                m_Bridge.ConfigureNetworkManager(manager);
            }
            catch (Exception exception)
            {
                Fail("start", exception.GetType().Name + ": " + exception.Message);
                return;
            }

            Debug.Log($"[GC2 Network Smoke] Session start requested role={m_Role}.");
            StartCoroutine(Monitor());
        }

        private void ConfigureTransport(GenericTransport transport)
        {
            if (transport == null) return;
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-address",
                out string address);
            int port = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-port",
                5000);
            Type type = transport.GetType();
            PropertyInfo addressProperty = type.GetProperty(
                "address",
                BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo portProperty = type.GetProperty(
                "serverPort",
                BindingFlags.Instance | BindingFlags.Public);
            if (addressProperty?.CanWrite == true && !string.IsNullOrWhiteSpace(address))
                addressProperty.SetValue(transport, address);
            if (portProperty?.CanWrite == true)
                portProperty.SetValue(transport, (ushort)Mathf.Clamp(port, 1, ushort.MaxValue));
        }

        private IEnumerator Monitor()
        {
            bool authority = m_Role is "purrnet-host" or "purrnet-server";
            int expectedHumans = NetworkEnemyShooterSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-expected-humans",
                m_Role == "purrnet-server" ? 1 : 2);
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
                RefreshBridgeManager();
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

        private void RefreshBridgeManager()
        {
            if (m_Bridge == null) return;

            NetworkManager running = NetworkManager.main;
            if (running == null || (!running.isServer && !running.isClient))
            {
                NetworkManager[] managers = UnityObjectSearch.FindAll<NetworkManager>(FindObjectsInactive.Include);
                for (int i = 0; i < managers.Length; i++)
                {
                    NetworkManager candidate = managers[i];
                    if (candidate == null || (!candidate.isServer && !candidate.isClient))
                        continue;
                    running = candidate;
                    break;
                }
            }

            if (running != null && (running.isServer || running.isClient) &&
                (!ReferenceEquals(m_Bridge.ActiveNetworkManager, running) ||
                 !m_Bridge.IsRunning))
            {
                m_Bridge.ConfigureNetworkManager(running);
            }

            if (!m_ManagerDiagnosticLogged &&
                Time.realtimeSinceStartup - m_StartedAt >= 2f)
            {
                m_ManagerDiagnosticLogged = true;
                NetworkManager bound = m_Bridge.ActiveNetworkManager;
                NetworkManager main = NetworkManager.main;
                NetworkManager[] managers = UnityObjectSearch.FindAll<NetworkManager>(FindObjectsInactive.Include);
                string managerStates = string.Empty;
                for (int i = 0; i < managers.Length; i++)
                {
                    NetworkManager candidate = managers[i];
                    if (candidate == null) continue;
                    if (managerStates.Length > 0) managerStates += "; ";
                    managerStates +=
                        $"{candidate.name}#{candidate.GetEntityId()} " +
                        $"server={candidate.isServer} client={candidate.isClient}";
                }

                Debug.Log(
                    "[GC2 Network Smoke] PurrNet manager binding: " +
                    $"main={(main != null ? main.GetEntityId().ToString() : "none")}, " +
                    $"bound={(bound != null ? bound.GetEntityId().ToString() : "none")}, " +
                    $"bridgeServer={m_Bridge.IsServer}, bridgeClient={m_Bridge.IsClient}, " +
                    $"instances=[{managerStates}].");
            }
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
