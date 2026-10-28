using System;
using System.Collections;
using System.Collections.Generic;
using Fusion;
using Fusion.Photon.Realtime;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Host/client smoke harness for the Fusion Core demo on Android or a desktop peer. It
    /// is created only when the player is launched with gc2-network-smoke-role, starts a
    /// Host/Client session, and verifies that each peer sees both local and remote player
    /// characters.
    /// </summary>
    internal sealed class FusionCoreNetworkSmokeBootstrap : MonoBehaviour
    {
        private string m_Role;
        private string m_Session;
        private float m_StartedAt;
        private int m_ExpectedHumans;
        private int m_TimeoutSeconds;
        private int m_LastHumans;
        private int m_LastLocal;
        private int m_LastRemote;
        private bool m_LastServer;
        private bool m_LastClient;
        private string m_LastActorSignature = string.Empty;

        private readonly List<NetworkCharacter> m_Characters = new(16);
        private static bool s_Created;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateWhenRequested()
        {
            if (s_Created)
            {
                return;
            }

            if (NetworkSmokeArguments.TryGetArgument(
                    "--gc2-network-smoke-scenario",
                    out string scenario) &&
                !string.Equals(scenario, "core", StringComparison.Ordinal))
            {
                return;
            }

            if (!NetworkSmokeArguments.TryGetArgument(
                    "--gc2-network-smoke-role",
                    out string role) ||
                !role.StartsWith("fusion-", StringComparison.Ordinal))
            {
                return;
            }

            s_Created = true;

            var owner = new GameObject("Fusion Android Network Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<FusionCoreNetworkSmokeBootstrap>();
        }

        private async void Start()
        {
            m_StartedAt = Time.realtimeSinceStartup;
            NetworkSmokeArguments.TryGetArgument("--gc2-network-smoke-role", out m_Role);
            NetworkSmokeArguments.TryGetArgument("--gc2-network-smoke-session", out m_Session);
            if (string.IsNullOrWhiteSpace(m_Session)) m_Session = "GC2-Android-Smoke";
            m_ExpectedHumans = NetworkSmokeArguments.GetIntArgument(
                "--gc2-network-smoke-expected-humans",
                2);
            m_TimeoutSeconds = NetworkSmokeArguments.GetIntArgument(
                "--gc2-network-smoke-timeout",
                120);

            string appId = string.Empty;
            NetworkSmokeArguments.TryGetArgument(
                "--gc2-network-smoke-photon-app-id",
                out appId);
            if (string.IsNullOrWhiteSpace(appId))
            {
                appId = Environment.GetEnvironmentVariable(
                    "GC2_NETWORK_SMOKE_PHOTON_APP_ID") ?? string.Empty;
            }
            if (!string.IsNullOrWhiteSpace(appId) &&
                PhotonAppSettings.Global?.AppSettings != null)
            {
                PhotonAppSettings.Global.AppSettings.AppIdFusion = appId.Trim();
            }

            if (!HasLiveAppId())
            {
                Finish(
                    false,
                    "Photon Fusion App Id is empty. Pass gc2-network-smoke-photon-app-id " +
                    "as an Android intent extra or configure the project's PhotonAppSettings.");
                return;
            }

            FusionSessionBootstrap bootstrap = UnityObjectSearch.FindAny<FusionSessionBootstrap>();
            if (bootstrap == null)
            {
                Finish(false, "FusionSessionBootstrap is missing from the smoke scene.");
                return;
            }

            NetworkSmokeArguments.TryGetArgument(
                "--gc2-network-smoke-region",
                out string region);
            var options = new FusionSessionStartOptions(m_Session, region);

            try
            {
                StartGameResult result = m_Role switch
                {
                    "fusion-host" => await bootstrap.StartHostAsync(options),
                    "fusion-client" => await bootstrap.JoinHostAsync(options),
                    "fusion-shared-master" =>
                        await bootstrap.CreateSharedWithExactSessionNameAsync(options),
                    "fusion-shared-client" => await bootstrap.JoinSharedAsync(options),
                    _ => default
                };

                if (!result.Ok)
                {
                    Finish(
                        false,
                        $"Fusion start failed: {result.ShutdownReason} {result.ErrorMessage}");
                    return;
                }
            }
            catch (Exception exception)
            {
                Finish(
                    false,
                    exception.GetType().Name + ": " + exception.Message);
                return;
            }

            Debug.Log(
                $"[GC2 Network Sync Smoke] session started role={m_Role} " +
                $"session={m_Session}");
            StartCoroutine(Monitor());
        }

        private IEnumerator Monitor()
        {
            bool authority = m_Role is "fusion-host" or "fusion-shared-master";

            while (Time.realtimeSinceStartup - m_StartedAt < m_TimeoutSeconds)
            {
                NetworkTransportBridge bridge = ResolveRunningBridge();
                if (bridge != null && bridge.IsRunning)
                {
                    m_LastServer = bridge.IsServer;
                    m_LastClient = bridge.IsClient;
                    bridge.CopyRegisteredCharacters(m_Characters);
                    CountActors();

                    bool transportRoleReady = authority
                        ? m_LastServer
                        : m_LastClient;
                    bool replicatedBothPeers =
                        m_LastHumans >= m_ExpectedHumans &&
                        m_LastLocal >= 1 &&
                        m_LastRemote >= 1;
                    if (transportRoleReady && replicatedBothPeers)
                    {
                        // Keep the host alive briefly so the joining peer can observe the
                        // same replicated actor set before this process quits.
                        yield return new WaitForSecondsRealtime(2f);
                        Finish(
                            true,
                            "Both peers are running and local/remote player " +
                            "characters are registered.");
                        yield break;
                    }
                }

                yield return new WaitForSecondsRealtime(0.5f);
            }

            Finish(
                false,
                $"Timed out after {m_TimeoutSeconds}s. " +
                $"Server={m_LastServer} Client={m_LastClient} " +
                $"humans={m_LastHumans} local={m_LastLocal} remote={m_LastRemote}.");
        }

        private void CountActors()
        {
            m_LastHumans = 0;
            m_LastLocal = 0;
            m_LastRemote = 0;
            string signature = string.Empty;

            for (int i = 0; i < m_Characters.Count; i++)
            {
                NetworkCharacter character = m_Characters[i];
                if (character == null)
                {
                    continue;
                }

                signature += $"{character.NetworkId}:" +
                             $"{character.IsPlayerOwnedActor}:" +
                             $"{character.HasAuthenticatedPlayerOwner}:" +
                             $"{character.IsLocalPlayer}:" +
                             $"{character.IsRemotePlayer}:" +
                             $"{character.isActiveAndEnabled}; ";
                if (!character.IsPlayerOwnedActor ||
                    !character.HasAuthenticatedPlayerOwner)
                {
                    continue;
                }

                m_LastHumans++;
                if (character.IsLocalPlayer) m_LastLocal++;
                if (character.IsRemotePlayer) m_LastRemote++;
            }

            if (!string.Equals(signature, m_LastActorSignature, StringComparison.Ordinal))
            {
                m_LastActorSignature = signature;
                Debug.Log(
                    $"[GC2 Network Sync Smoke] actors role={m_Role} " +
                    $"count={m_Characters.Count} signature={signature}");
            }
        }

        private static NetworkTransportBridge ResolveRunningBridge()
        {
            NetworkTransportBridge active = NetworkTransportBridge.Active;
            if (active != null && active.IsRunning) return active;

            NetworkTransportBridge[] bridges =
                UnityObjectSearch.FindAll<NetworkTransportBridge>(
                    FindObjectsInactive.Exclude);
            for (int i = 0; i < bridges.Length; i++)
            {
                if (bridges[i] != null && bridges[i].IsRunning) return bridges[i];
            }

            return null;
        }

        private static bool HasLiveAppId()
        {
            return PhotonAppSettings.TryGetGlobal(out PhotonAppSettings settings) &&
                   settings?.AppSettings != null &&
                   !string.IsNullOrWhiteSpace(settings.AppSettings.AppIdFusion);
        }

        private void Finish(bool passed, string message)
        {
            string marker =
                $"[GC2 Network Sync Smoke] role={m_Role} passed={passed.ToString().ToLowerInvariant()} " +
                $"session={m_Session} server={m_LastServer} client={m_LastClient} " +
                $"humans={m_LastHumans} local={m_LastLocal} remote={m_LastRemote}: {message}";
            if (passed) Debug.Log(marker);
            else Debug.LogError(marker);
            Application.Quit(passed ? 0 : 1);
        }
    }
}
