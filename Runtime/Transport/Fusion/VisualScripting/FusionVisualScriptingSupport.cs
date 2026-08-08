using System;
using System.Collections;
using System.Threading.Tasks;
using Fusion;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Shared, read-only resolution helpers for the Fusion GC2 Inspector entries.
    /// Resolution stays context-first so an entry cannot silently bind to the wrong runner.
    /// </summary>
    internal static class FusionVisualScriptingSupport
    {
        public static bool TryResolveBootstrap(
            GameObject context,
            out FusionSessionBootstrap bootstrap)
        {
            return FusionLifecycleResolver.TryResolveBootstrap(context, out bootstrap);
        }

        public static bool TryResolveBridge(
            GameObject context,
            out FusionTransportBridge bridge)
        {
            return FusionLifecycleResolver.TryResolveBridge(context, out bridge);
        }

        public static bool TryGetActiveSession(
            GameObject context,
            out FusionSessionSnapshot session)
        {
            session = default;
            return TryResolveBridge(context, out FusionTransportBridge bridge) &&
                   bridge.TryGetActiveSession(out session);
        }

        public static bool ActiveSessionNameEquals(
            GameObject context,
            string expectedSessionName)
        {
            if (string.IsNullOrWhiteSpace(expectedSessionName) ||
                !TryGetActiveSession(context, out FusionSessionSnapshot session))
            {
                return false;
            }

            return string.Equals(
                session.SessionName,
                expectedSessionName.Trim(),
                StringComparison.Ordinal);
        }

        public static FusionNetworkIdentity ResolveIdentity(
            PropertyGetGameObject target,
            Args args)
        {
            GameObject gameObject = target?.Get(args);
            if (gameObject == null) return null;

            FusionNetworkIdentity identity =
                gameObject.GetComponent<FusionNetworkIdentity>();
            if (identity != null) return identity;

            identity = gameObject.GetComponentInParent<FusionNetworkIdentity>();
            return identity != null
                ? identity
                : gameObject.GetComponentInChildren<FusionNetworkIdentity>(true);
        }

        public static bool IsSharedMasterClient(GameObject context)
        {
            if (!TryResolveBridge(context, out FusionTransportBridge bridge))
            {
                return false;
            }

            NetworkRunner runner = bridge.Runner;
            return runner != null &&
                   runner.IsRunning &&
                   !runner.IsShutdown &&
                   runner.GameMode == GameMode.Shared &&
                   runner.IsSharedModeMasterClient;
        }

        public static bool IsConnectionRelayed(GameObject context)
        {
            return TryResolveBridge(context, out FusionTransportBridge bridge) &&
                   bridge.TryGetConnectionDiagnostics(
                       out FusionConnectionDiagnostics diagnostics) &&
                   diagnostics.IsRelayed;
        }

        public static bool IsObjectAdmitted(
            PropertyGetGameObject target,
            Args args)
        {
            FusionNetworkIdentity identity = ResolveIdentity(target, args);
            if (identity == null || !identity.TransportAdmitted || identity.Runner == null)
            {
                return false;
            }

            return FusionAuthoritySpawnRegistry.TryGet(identity.Runner, out var registry) &&
                   registry.IsAdmitted(identity);
        }

        public static bool IsLocalLogicalOwner(
            PropertyGetGameObject target,
            Args args)
        {
            FusionNetworkIdentity identity = ResolveIdentity(target, args);
            return identity != null &&
                   identity.Runner != null &&
                   identity.Runner.IsRunning &&
                   !identity.Runner.IsShutdown &&
                   identity.IsOwnedBy(identity.Runner.LocalPlayer);
        }

        /// <summary>
        /// Defers GC2 execution by one Unity frame so instructions never re-enter a
        /// native Fusion callback. Event payload state must be stored before calling this.
        /// </summary>
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
                    $"[Fusion Visual Scripting] {eventName} could not start its GC2 Trigger.",
                    trigger);
                Debug.LogException(exception, trigger);
                yield break;
            }

            Observe(
                execution,
                self != null ? self : trigger.gameObject,
                eventName);
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

        private static async void Observe(
            Task task,
            GameObject context,
            string operation)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // Session cancellation is a normal outcome when Shutdown is requested.
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Fusion Visual Scripting] {operation} failed.", context);
                Debug.LogException(exception, context);
            }
        }

        public static void LogMissingBootstrap(GameObject context)
        {
            Debug.LogError(
                "[Fusion Visual Scripting] No unambiguous FusionSessionBootstrap " +
                "could be resolved from this context.",
                context);
        }
    }
}
