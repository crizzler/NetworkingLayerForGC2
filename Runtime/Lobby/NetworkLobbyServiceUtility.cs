using UnityEngine;

namespace Arawn.GameCreator2.Networking.Lobby
{
    /// <summary>
    /// Resolves a provider-neutral lobby service without introducing a dependency on
    /// Fusion, PurrNet, Steamworks, or any other provider SDK.
    /// </summary>
    public static class NetworkLobbyServiceUtility
    {
        public static INetworkLobbyService Resolve(
            MonoBehaviour preferred,
            GameObject context = null,
            bool searchScene = true)
        {
            if (preferred is INetworkLobbyService preferredService)
            {
                return preferredService;
            }

            INetworkLobbyService service = FindOn(context);
            if (service != null) return service;

            if (context != null)
            {
                Transform parent = context.transform.parent;
                while (parent != null)
                {
                    service = FindOn(parent.gameObject);
                    if (service != null) return service;
                    parent = parent.parent;
                }

                MonoBehaviour[] children = context.GetComponentsInChildren<MonoBehaviour>(true);
                service = FindIn(children);
                if (service != null) return service;
            }

            if (!searchScene) return null;

#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER || UNITY_6000
            MonoBehaviour[] behaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
#else
            MonoBehaviour[] behaviours = Object.FindObjectsOfType<MonoBehaviour>();
#endif
            return FindIn(behaviours);
        }

        public static INetworkLobbyService Resolve(
            GameObject context,
            bool searchScene = true)
        {
            return Resolve(null, context, searchScene);
        }

        public static MonoBehaviour FindBehaviour(
            GameObject context = null,
            bool searchScene = true)
        {
            INetworkLobbyService service = Resolve(null, context, searchScene);
            return service as MonoBehaviour;
        }

        public static bool HasCapability(
            INetworkLobbyService service,
            NetworkLobbyCapabilities capability)
        {
            return service != null &&
                   (service.Capabilities & capability) == capability;
        }

        public static bool IsBusy(NetworkLobbyState state)
        {
            return state == NetworkLobbyState.Initializing ||
                   state == NetworkLobbyState.Browsing ||
                   state == NetworkLobbyState.Creating ||
                   state == NetworkLobbyState.Joining ||
                   state == NetworkLobbyState.Leaving;
        }

        private static INetworkLobbyService FindOn(GameObject gameObject)
        {
            if (gameObject == null) return null;
            return FindIn(gameObject.GetComponents<MonoBehaviour>());
        }

        private static INetworkLobbyService FindIn(MonoBehaviour[] behaviours)
        {
            if (behaviours == null) return null;

            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null && behaviours[i] is INetworkLobbyService service)
                {
                    return service;
                }
            }

            return null;
        }
    }
}
