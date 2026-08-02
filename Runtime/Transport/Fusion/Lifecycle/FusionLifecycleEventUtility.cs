using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Observer callbacks are never part of transport correctness. Invoke every listener,
    /// isolate failures, and leave critical authority/readiness events to their existing paths.
    /// </summary>
    internal static class FusionLifecycleEventUtility
    {
        public static void InvokeBestEffort<T>(
            Action<T> handlers,
            T value,
            UnityEngine.Object context,
            string eventName)
        {
            if (handlers == null) return;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)callback)(value);
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"[FusionTransport] Observational event '{eventName}' listener failed.",
                        context);
                    Debug.LogException(exception, context);
                }
            }
        }
    }
}
