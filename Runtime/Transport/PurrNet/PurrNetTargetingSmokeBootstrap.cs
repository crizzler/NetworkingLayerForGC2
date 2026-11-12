#if UNITY_EDITOR || DEBUG
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Opt-in standalone CLI bootstrap that proves native Targeting state parity between a
    /// PurrNet authority and a connected observer. Dormant unless the smoke scenario is
    /// <c>targeting</c>.
    /// </summary>
    internal sealed class PurrNetTargetingSmokeBootstrap : MonoBehaviour
    {
        private string m_Role;
        private string m_ReadyPath;
        private PurrNetTransportBridge m_Bridge;

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
                !role.StartsWith("purrnet-", StringComparison.Ordinal))
            {
                return;
            }

            var owner = new GameObject("PurrNet Targeting State Smoke Bootstrap");
            DontDestroyOnLoad(owner);
            owner.AddComponent<PurrNetTargetingSmokeBootstrap>();
        }

        private void Start()
        {
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-role",
                out m_Role);
            NetworkEnemyShooterSmokeUtility.TryGetArgument(
                "--gc2-network-smoke-ready",
                out m_ReadyPath);

            NetworkManager manager = NetworkManager.main ?? UnityObjectSearch.FindAny<NetworkManager>();
            if (manager == null)
            {
                Fail("start", "PurrNet NetworkManager is missing from the Targeting smoke scene.");
                return;
            }

            m_Bridge = UnityObjectSearch.FindAny<PurrNetTransportBridge>(
                FindObjectsInactive.Include);
            if (m_Bridge == null)
            {
                Fail("start", "PurrNetTransportBridge is missing from the Targeting smoke scene.");
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

                m_Bridge.ConfigureNetworkManager(manager);
            }
            catch (Exception exception)
            {
                Fail("start", exception.GetType().Name + ": " + exception.Message);
                return;
            }

            Debug.Log($"[TargetingSmoke] session start requested role={m_Role}");
            StartCoroutine(NetworkTargetingSmokeProbe.Run(m_Role, m_ReadyPath));
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

        private void Fail(string phase, string message)
        {
            NetworkTargetingSmokeProbe.Fail(m_Role, phase + ": " + message);
        }
    }
}
#endif
