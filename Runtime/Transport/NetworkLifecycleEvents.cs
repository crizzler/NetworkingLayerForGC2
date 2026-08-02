using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Transport-neutral role exposed to gameplay and visual scripting.
    /// Fusion Shared masters are reported as <see cref="Server"/> because they are the
    /// logical GC2 gameplay authority; this deliberately does not expose native transport
    /// State Authority or Input Authority.
    /// </summary>
    public enum NetworkTransportRole
    {
        Offline = 0,
        Client = 1,
        Server = 2,
        Host = 3
    }

    public enum NetworkLifecycleEventType
    {
        None = 0,
        SessionStarted = 1,
        SessionStopped = 2,
        ClientConnected = 3,
        ClientDisconnected = 4,
        LogicalAuthorityChanged = 5,
        LocalPlayerReady = 6,
        LocalPlayerLost = 7
    }

    /// <summary>
    /// Best-effort, transport-neutral lifecycle notifications. These events are observed
    /// from <see cref="NetworkTransportBridge.LateUpdate"/>, after native transport
    /// callbacks have returned. Each listener is isolated so visual-scripting failures
    /// can never alter transport readiness, authority promotion, or shutdown behavior.
    /// </summary>
    public static class NetworkLifecycleEvents
    {
        public static event Action<NetworkTransportBridge> SessionStarted;
        public static event Action<NetworkTransportBridge> SessionStopped;
        public static event Action<NetworkTransportBridge, uint> ClientConnected;
        public static event Action<NetworkTransportBridge, uint> ClientDisconnected;
        public static event Action<NetworkTransportBridge, bool, uint> LogicalAuthorityChanged;
        public static event Action<NetworkTransportBridge, GameObject> LocalPlayerReady;
        public static event Action<NetworkTransportBridge, GameObject> LocalPlayerLost;

        /// <summary>
        /// Stable context for the most recently dispatched lifecycle notification. GC2
        /// Property nodes expose these values to the Trigger currently being executed.
        /// </summary>
        public static NetworkLifecycleEventType LastEventType { get; private set; }
        public static NetworkTransportBridge LastSource { get; private set; }
        public static uint LastClientId { get; private set; } = NetworkTransportBridge.InvalidClientId;
        public static bool HasLastClientId => NetworkTransportBridge.IsValidClientId(LastClientId);
        public static bool LastLogicalAuthority { get; private set; }
        public static uint LastAuthorityEpoch { get; private set; }
        public static GameObject LastLocalPlayer { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            SessionStarted = null;
            SessionStopped = null;
            ClientConnected = null;
            ClientDisconnected = null;
            LogicalAuthorityChanged = null;
            LocalPlayerReady = null;
            LocalPlayerLost = null;

            LastEventType = NetworkLifecycleEventType.None;
            LastSource = null;
            LastClientId = NetworkTransportBridge.InvalidClientId;
            LastLogicalAuthority = false;
            LastAuthorityEpoch = 0;
            LastLocalPlayer = null;
        }

        internal static void RaiseSessionStarted(NetworkTransportBridge bridge)
        {
            SetContext(NetworkLifecycleEventType.SessionStarted, bridge);
            InvokeSafely(SessionStarted, bridge, bridge);
        }

        internal static void RaiseSessionStopped(NetworkTransportBridge bridge)
        {
            SetContext(NetworkLifecycleEventType.SessionStopped, bridge);
            InvokeSafely(SessionStopped, bridge, bridge);
        }

        internal static void RaiseClientConnected(NetworkTransportBridge bridge, uint clientId)
        {
            SetContext(NetworkLifecycleEventType.ClientConnected, bridge);
            LastClientId = clientId;
            InvokeSafely(ClientConnected, bridge, clientId, bridge);
        }

        internal static void RaiseClientDisconnected(NetworkTransportBridge bridge, uint clientId)
        {
            SetContext(NetworkLifecycleEventType.ClientDisconnected, bridge);
            LastClientId = clientId;
            InvokeSafely(ClientDisconnected, bridge, clientId, bridge);
        }

        internal static void RaiseLogicalAuthorityChanged(
            NetworkTransportBridge bridge,
            bool isAuthority,
            uint epoch)
        {
            SetContext(NetworkLifecycleEventType.LogicalAuthorityChanged, bridge);
            LastLogicalAuthority = isAuthority;
            LastAuthorityEpoch = epoch;
            InvokeSafely(LogicalAuthorityChanged, bridge, isAuthority, epoch, bridge);
        }

        internal static void RaiseLocalPlayerReady(
            NetworkTransportBridge bridge,
            GameObject player)
        {
            SetContext(NetworkLifecycleEventType.LocalPlayerReady, bridge);
            LastLocalPlayer = player;
            InvokeSafely(LocalPlayerReady, bridge, player, bridge);
        }

        internal static void RaiseLocalPlayerLost(
            NetworkTransportBridge bridge,
            GameObject player)
        {
            SetContext(NetworkLifecycleEventType.LocalPlayerLost, bridge);
            LastLocalPlayer = player;
            InvokeSafely(LocalPlayerLost, bridge, player, bridge);
        }

        private static void SetContext(
            NetworkLifecycleEventType eventType,
            NetworkTransportBridge bridge)
        {
            LastEventType = eventType;
            LastSource = bridge;
        }

        private static void InvokeSafely<T>(Action<T> listeners, T value, UnityEngine.Object context)
        {
            if (listeners == null) return;

            Delegate[] invocationList = listeners.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T>)invocationList[i]).Invoke(value);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, context);
                }
            }
        }

        private static void InvokeSafely<T1, T2>(
            Action<T1, T2> listeners,
            T1 value1,
            T2 value2,
            UnityEngine.Object context)
        {
            if (listeners == null) return;

            Delegate[] invocationList = listeners.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T1, T2>)invocationList[i]).Invoke(value1, value2);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, context);
                }
            }
        }

        private static void InvokeSafely<T1, T2, T3>(
            Action<T1, T2, T3> listeners,
            T1 value1,
            T2 value2,
            T3 value3,
            UnityEngine.Object context)
        {
            if (listeners == null) return;

            Delegate[] invocationList = listeners.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T1, T2, T3>)invocationList[i]).Invoke(value1, value2, value3);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, context);
                }
            }
        }
    }
}
