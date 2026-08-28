using System;
using System.Collections;
using System.Reflection;
using Arawn.GameCreator2.Networking.Melee;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.PurrNet
{
    /// <summary>Opt-in standalone CLI bootstrap for the PurrNet Free Flow Combat smoke.</summary>
    internal sealed class PurrNetFreeFlowCombatSmokeBootstrap : MonoBehaviour
    {
        private string m_Role;
        private float m_StartedAt;
        private global::Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetTransportBridge
            m_Bridge;
        private bool m_ManagerDiagnosticLogged;

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
                !role.StartsWith("purrnet-", StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("PurrNet Free Flow Combat Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<PurrNetFreeFlowCombatSmokeBootstrap>();
        }

        private void Start()
        {
            m_StartedAt = Time.realtimeSinceStartup;
            NetworkFreeFlowCombatSmokeUtility.EnableSemanticAttackDiagnostics();
            NetworkFreeFlowCombatSmokeUtility.BeginSemanticObservation();
            NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);

            NetworkManager manager = NetworkManager.main ??
                UnityObjectSearch.FindAny<NetworkManager>();
            if (manager == null)
            {
                Fail("start", "PurrNet NetworkManager is missing from the Free Flow scene.");
                return;
            }

            m_Bridge = UnityObjectSearch.FindAny<
                global::Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetTransportBridge>(
                FindObjectsInactive.Include);
            if (m_Bridge == null)
            {
                Fail("start", "PurrNetTransportBridge is missing from the Free Flow scene.");
                return;
            }

            if (m_Role is not ("purrnet-host" or "purrnet-server" or "purrnet-client"))
            {
                Fail("start", $"Unsupported PurrNet Free Flow smoke role '{m_Role}'.");
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
                }

                // Some PurrNet versions raise their client-start callback one tick later. Keep
                // the bridge pinned to the exact manager selected by this smoke process.
                m_Bridge.ConfigureNetworkManager(manager);
            }
            catch (Exception exception)
            {
                Fail("start", exception.GetType().Name + ": " + exception.Message);
                return;
            }

            Debug.Log($"[GC2 Free Flow Smoke] PurrNet session requested role={m_Role}.");
            StartCoroutine(Monitor());
        }

        private void ConfigureTransport(GenericTransport transport)
        {
            if (transport == null) return;
            NetworkFreeFlowCombatSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-address",
                out string address);
            int port = NetworkFreeFlowCombatSmokeUtility.GetIntArgument(
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
            bool requirePresentation = m_Role != "purrnet-server";
            int expectedHumans = NetworkFreeFlowCombatSmokeUtility.GetIntArgument(
                "--gc2-network-smoke-expected-humans",
                m_Role == "purrnet-server" ? 1 : 2);
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
                RefreshBridgeManager();
                float elapsedSeconds = Time.realtimeSinceStartup - m_StartedAt;
                float deadline = foundationReadyAt >= 0f
                    ? foundationReadyAt + timeout
                    : timeout;
                if (elapsedSeconds >= deadline) break;

                bool foundationReady = NetworkFreeFlowCombatSmokeUtility.TryCaptureFoundation(
                    m_Role,
                    expectedHumans,
                    authority,
                    requirePresentation,
                    "foundation",
                    elapsedSeconds,
                    transportEvidence: null,
                    out NetworkFreeFlowCombatSmokeUtility.Result foundationResult);
                if (foundationReady)
                {
                    // Retain the last complete setup snapshot so peer teardown cannot hide the
                    // gameplay condition that actually prevented the strict smoke from passing.
                    lastFoundationResult = foundationResult;
                    if (foundationReadyAt < 0f) foundationReadyAt = elapsedSeconds;
                    NetworkFreeFlowCombatSmokeUtility.ArmAssignedLocalPlayerCounter();
                }

                bool fullReady = NetworkFreeFlowCombatSmokeUtility.TryCapture(
                    m_Role,
                    expectedHumans,
                    authority,
                    requirePresentation,
                    "joined",
                    elapsedSeconds,
                    transportEvidence: null,
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
                    requirePresentation,
                    "timeout",
                    Time.realtimeSinceStartup - m_StartedAt,
                    transportEvidence: null,
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
                "PurrNet Free Flow smoke timed out. Strict state: " + strictMessage +
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

        private void RefreshBridgeManager()
        {
            if (m_Bridge == null) return;

            NetworkManager running = NetworkManager.main;
            if (running == null || (!running.isServer && !running.isClient))
            {
                NetworkManager[] managers = UnityObjectSearch.FindAll<NetworkManager>(
                    FindObjectsInactive.Include);
                for (int i = 0; i < managers.Length; i++)
                {
                    NetworkManager candidate = managers[i];
                    if (candidate == null || (!candidate.isServer && !candidate.isClient))
                        continue;
                    running = candidate;
                    break;
                }
            }

            if (running != null &&
                (running.isServer || running.isClient) &&
                (!ReferenceEquals(m_Bridge.ActiveNetworkManager, running) ||
                 !m_Bridge.IsRunning))
            {
                m_Bridge.ConfigureNetworkManager(running);
            }

            if (m_ManagerDiagnosticLogged ||
                Time.realtimeSinceStartup - m_StartedAt < 2f)
            {
                return;
            }

            m_ManagerDiagnosticLogged = true;
            NetworkManager bound = m_Bridge.ActiveNetworkManager;
            Debug.Log(
                "[GC2 Free Flow Smoke] PurrNet manager binding: " +
                $"bound={(bound != null ? bound.GetEntityId().ToString() : "none")}, " +
                $"bridgeServer={m_Bridge.IsServer}, bridgeClient={m_Bridge.IsClient}, " +
                $"bridgeHost={m_Bridge.IsHost}.");
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
