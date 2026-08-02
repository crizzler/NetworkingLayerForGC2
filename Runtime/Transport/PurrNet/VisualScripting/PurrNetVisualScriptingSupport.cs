using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using GameCreator.Runtime.VisualScripting;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public enum PurrNetConnectionSide
    {
        Server = 0,
        Client = 1
    }

    /// <summary>
    /// Context-first resolution, asynchronous waiting, and deferred Trigger dispatch for
    /// the PurrNet GC2 Inspector Instructions, Conditions, Events, and Properties.
    /// </summary>
    internal static class PurrNetVisualScriptingSupport
    {
        private const double DefaultTimeoutSeconds = 30d;

        public static bool TryResolveNetworkManager(
            GameObject context,
            out NetworkManager manager)
        {
            manager = ResolveInHierarchy<NetworkManager>(context);
            if (manager != null) return true;

            PurrNetTransportBridge bridge = ResolveInHierarchy<PurrNetTransportBridge>(context);
            if (bridge != null && bridge.ActiveNetworkManager != null)
            {
                manager = bridge.ActiveNetworkManager;
                return true;
            }

            if (NetworkManager.main != null)
            {
                manager = NetworkManager.main;
                return true;
            }

            return TryResolveUniqueSceneComponent(out manager);
        }

        public static bool TryResolveSteamLobbyNetwork(
            GameObject context,
            out PurrNetSteamLobbyNetwork lobbyNetwork)
        {
            lobbyNetwork = ResolveInHierarchy<PurrNetSteamLobbyNetwork>(context);
            return lobbyNetwork != null || TryResolveUniqueSceneComponent(out lobbyNetwork);
        }

        public static bool TryConfigureEndpoint(
            NetworkManager manager,
            string address,
            int port,
            out string error)
        {
            error = string.Empty;
            string trimmedAddress = address?.Trim();
            bool setAddress = !string.IsNullOrWhiteSpace(trimmedAddress);
            bool setPort = port != 0;
            if (!setAddress && !setPort) return true;

            if (manager == null || manager.transport == null)
            {
                error = "The resolved PurrNet NetworkManager has no transport.";
                return false;
            }

            if (setPort && (port < 1 || port > ushort.MaxValue))
            {
                error = $"PurrNet port '{port}' must be between 1 and {ushort.MaxValue}.";
                return false;
            }

            object transport = manager.transport;
            Type transportType = transport.GetType();
            try
            {
                if (setAddress &&
                    !TrySetPublicMember(transportType, transport, "address", trimmedAddress))
                {
                    error = $"PurrNet transport '{transportType.FullName}' has no writable address member.";
                    return false;
                }

                if (setPort &&
                    !TrySetPublicMember(transportType, transport, "serverPort", port))
                {
                    error = $"PurrNet transport '{transportType.FullName}' has no writable serverPort member.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = $"Could not configure PurrNet transport '{transportType.FullName}': {exception.Message}";
                return false;
            }

            return true;
        }

        public static Task StartAndWaitAsync(
            NetworkManager manager,
            Action start,
            bool requireServer,
            bool requireClient,
            double timeoutSeconds)
        {
            return StartAndWaitInternalAsync(
                manager,
                start,
                requireServer,
                requireClient,
                NormalizeTimeout(timeoutSeconds));
        }

        private static async Task StartAndWaitInternalAsync(
            NetworkManager manager,
            Action start,
            bool requireServer,
            bool requireClient,
            double timeoutSeconds)
        {
            if (manager == null) throw new InvalidOperationException("No PurrNet NetworkManager is available.");

            bool serverActivated = manager.serverState != ConnectionState.Disconnected;
            bool clientActivated = manager.clientState != ConnectionState.Disconnected;
            bool serverFailed = false;
            bool clientFailed = false;

            void OnServerState(ConnectionState state)
            {
                if (state == ConnectionState.Disconnected)
                {
                    if (serverActivated) serverFailed = true;
                }
                else
                {
                    serverActivated = true;
                }
            }

            void OnClientState(ConnectionState state)
            {
                if (state == ConnectionState.Disconnected)
                {
                    if (clientActivated) clientFailed = true;
                }
                else
                {
                    clientActivated = true;
                }
            }

            manager.onServerConnectionState += OnServerState;
            manager.onClientConnectionState += OnClientState;
            try
            {
                start();
                double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;

                while (true)
                {
                    if (manager == null)
                    {
                        throw new InvalidOperationException(
                            "The PurrNet NetworkManager was destroyed while the session was starting.");
                    }

                    bool serverReady = !requireServer ||
                                       manager.serverState == ConnectionState.Connected;
                    bool clientReady = !requireClient ||
                                       manager.clientState == ConnectionState.Connected;
                    if (serverReady && clientReady) return;

                    if ((requireServer && serverFailed) || (requireClient && clientFailed))
                    {
                        throw new InvalidOperationException(
                            $"PurrNet could not complete the requested session start " +
                            $"(server {manager.serverState}, client {manager.clientState}).");
                    }

                    if (Time.realtimeSinceStartupAsDouble >= deadline)
                    {
                        throw new TimeoutException(
                            $"PurrNet session start timed out after {timeoutSeconds:0.##} seconds " +
                            $"(server {manager.serverState}, client {manager.clientState}).");
                    }

                    await Task.Yield();
                }
            }
            finally
            {
                if (manager != null)
                {
                    manager.onServerConnectionState -= OnServerState;
                    manager.onClientConnectionState -= OnClientState;
                }
            }
        }

        public static Task StopAndWaitAsync(
            NetworkManager manager,
            Action stop,
            bool requireServerStopped,
            bool requireClientStopped,
            double timeoutSeconds)
        {
            return StopAndWaitInternalAsync(
                manager,
                stop,
                requireServerStopped,
                requireClientStopped,
                NormalizeTimeout(timeoutSeconds));
        }

        private static async Task StopAndWaitInternalAsync(
            NetworkManager manager,
            Action stop,
            bool requireServerStopped,
            bool requireClientStopped,
            double timeoutSeconds)
        {
            if (manager == null) return;

            stop();
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (manager != null)
            {
                bool serverStopped = !requireServerStopped ||
                                     manager.serverState == ConnectionState.Disconnected;
                bool clientStopped = !requireClientStopped ||
                                     manager.clientState == ConnectionState.Disconnected;
                if (serverStopped && clientStopped) return;

                if (Time.realtimeSinceStartupAsDouble >= deadline)
                {
                    throw new TimeoutException(
                        $"PurrNet shutdown timed out after {timeoutSeconds:0.##} seconds " +
                        $"(server {manager.serverState}, client {manager.clientState}).");
                }

                await Task.Yield();
            }
        }

        public static Task RunSteamOperationAsync(
            PurrNetSteamLobbyNetwork lobbyNetwork,
            Action operation,
            Func<PurrNetSteamLobbySessionState, bool> completed,
            double timeoutSeconds,
            string operationName)
        {
            return RunSteamOperationInternalAsync(
                lobbyNetwork,
                operation,
                completed,
                NormalizeTimeout(timeoutSeconds),
                operationName);
        }

        private static async Task RunSteamOperationInternalAsync(
            PurrNetSteamLobbyNetwork lobbyNetwork,
            Action operation,
            Func<PurrNetSteamLobbySessionState, bool> completed,
            double timeoutSeconds,
            string operationName)
        {
            if (lobbyNetwork == null)
            {
                throw new InvalidOperationException("No PurrNet Steam Lobby Network is available.");
            }

            operation();
            double deadline = Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            while (lobbyNetwork != null)
            {
                PurrNetSteamLobbySessionState state = lobbyNetwork.State;
                if (completed(state)) return;

                if (state == PurrNetSteamLobbySessionState.Error)
                {
                    string detail = string.IsNullOrWhiteSpace(lobbyNetwork.LastError)
                        ? lobbyNetwork.StatusMessage
                        : lobbyNetwork.LastError;
                    throw new InvalidOperationException(
                        $"PurrNet Steam lobby {operationName} failed: {detail}");
                }

                if (Time.realtimeSinceStartupAsDouble >= deadline)
                {
                    throw new TimeoutException(
                        $"PurrNet Steam lobby {operationName} timed out after " +
                        $"{timeoutSeconds:0.##} seconds (state {state}).");
                }

                await Task.Yield();
            }

            throw new InvalidOperationException(
                $"The PurrNet Steam Lobby Network was destroyed during {operationName}.");
        }

        public static void DispatchNextFrame(
            Trigger trigger,
            GameObject self,
            string eventName)
        {
            if (trigger == null || !trigger.isActiveAndEnabled) return;
            trigger.StartCoroutine(ExecuteNextFrame(trigger, self, eventName));
        }

        private static IEnumerator ExecuteNextFrame(
            Trigger trigger,
            GameObject self,
            string eventName)
        {
            yield return null;
            if (trigger == null || !trigger.isActiveAndEnabled) yield break;

            Task execution;
            try
            {
                execution = trigger.Execute(self);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[PurrNet Visual Scripting] {eventName} could not start its GC2 Trigger.",
                    trigger);
                Debug.LogException(exception, trigger);
                yield break;
            }

            Observe(execution, self != null ? self : trigger.gameObject, eventName);
        }

        public static Task CompleteOrObserve(
            Task task,
            bool waitUntilComplete,
            GameObject context,
            string operation)
        {
            if (task == null) return Task.CompletedTask;
            if (waitUntilComplete) return task;
            Observe(task, context, operation);
            return Task.CompletedTask;
        }

        private static async void Observe(Task task, GameObject context, string operation)
        {
            try
            {
                await task;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[PurrNet Visual Scripting] {operation} failed.", context);
                Debug.LogException(exception, context);
            }
        }

        public static void LogMissingManager(GameObject context)
        {
            Debug.LogError(
                "[PurrNet Visual Scripting] No PurrNet NetworkManager could be resolved " +
                "from this context.",
                context);
        }

        public static void LogMissingSteamLobbyNetwork(GameObject context)
        {
            Debug.LogError(
                "[PurrNet Visual Scripting] No unambiguous PurrNetSteamLobbyNetwork " +
                "could be resolved from this context.",
                context);
        }

        private static T ResolveInHierarchy<T>(GameObject context) where T : Component
        {
            if (context == null) return null;
            T component = context.GetComponent<T>();
            if (component == null) component = context.GetComponentInParent<T>();
            if (component == null) component = context.GetComponentInChildren<T>(true);
            return component;
        }

        private static bool TryResolveUniqueSceneComponent<T>(out T component)
            where T : Component
        {
            component = null;
            T[] candidates = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (candidates.Length != 1) return false;
            component = candidates[0];
            return component != null;
        }

        private static double NormalizeTimeout(double timeoutSeconds)
        {
            return double.IsNaN(timeoutSeconds) ||
                   double.IsInfinity(timeoutSeconds) ||
                   timeoutSeconds <= 0d
                ? DefaultTimeoutSeconds
                : timeoutSeconds;
        }

        private static bool TrySetPublicMember(
            Type type,
            object target,
            string memberName,
            object value)
        {
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;
            PropertyInfo property = type.GetProperty(memberName, Flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, ConvertValue(value, property.PropertyType));
                return true;
            }

            FieldInfo field = type.GetField(memberName, Flags);
            if (field == null || field.IsInitOnly) return false;
            field.SetValue(target, ConvertValue(value, field.FieldType));
            return true;
        }

        private static object ConvertValue(object value, Type destinationType)
        {
            Type targetType = Nullable.GetUnderlyingType(destinationType) ?? destinationType;
            if (value == null || targetType.IsInstanceOfType(value)) return value;
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
    }
}
