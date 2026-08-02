using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Context-first resolution for optional runtime observers. Scene fallback succeeds only
    /// when exactly one candidate exists, so a multi-runner project never selects arbitrarily.
    /// </summary>
    public static class FusionLifecycleResolver
    {
        public static bool TryResolveBootstrap(
            GameObject context,
            out FusionSessionBootstrap bootstrap)
        {
            return TryResolve(context, out bootstrap);
        }

        public static bool TryResolveBridge(
            GameObject context,
            out FusionTransportBridge bridge)
        {
            return TryResolve(context, out bridge);
        }

        public static bool TryResolvePlayerSpawner(
            GameObject context,
            out FusionPlayerSpawner spawner)
        {
            return TryResolve(context, out spawner);
        }

        private static bool TryResolve<T>(GameObject context, out T component)
            where T : Component
        {
            component = null;
            if (context != null)
            {
                component = context.GetComponent<T>();
                if (component == null) component = context.GetComponentInParent<T>();
                if (component == null) component = context.GetComponentInChildren<T>(true);
                if (component != null) return true;
            }

            T[] candidates = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            if (candidates.Length != 1) return false;
            component = candidates[0];
            return component != null;
        }
    }
}
