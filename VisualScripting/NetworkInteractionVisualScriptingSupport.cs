using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    internal readonly struct NetworkInteractionExecutionContext
    {
        public readonly NetworkInteractionEventType EventType;
        public readonly NetworkInteractionResponse Response;
        public readonly NetworkInteractionBroadcast Broadcast;
        public readonly GameObject Actor;
        public readonly GameObject Target;

        public NetworkInteractionExecutionContext(
            NetworkInteractionEventType eventType,
            in NetworkInteractionResponse response,
            in NetworkInteractionBroadcast broadcast,
            GameObject actor,
            GameObject target)
        {
            EventType = eventType;
            Response = response;
            Broadcast = broadcast;
            Actor = actor;
            Target = target;
        }

        public bool IsValid => EventType != NetworkInteractionEventType.None;
        public bool Approved => EventType != NetworkInteractionEventType.Rejected;
        public InteractionRejectReason RejectReason => Response.RejectReason;
        public InteractionType InteractionType => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.InteractionType : Response.InteractionType;
        public ushort RequestId => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.RequestId : Response.RequestId;
        public uint ActorNetworkId => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.ActorNetworkId : Response.ActorNetworkId;
        public uint CorrelationId => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.CorrelationId : Response.CorrelationId;
        public uint CharacterNetworkId => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.CharacterNetworkId : Response.CharacterNetworkId;
        public uint TargetNetworkId => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.TargetNetworkId : Response.TargetNetworkId;
        public int TargetHash => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.TargetHash : Response.TargetHash;
        public int ResultData => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.ResultData : Response.ResultData;
        public float ServerTime => EventType == NetworkInteractionEventType.Broadcast
            ? Broadcast.ServerTime : 0f;
    }

    internal static class NetworkInteractionVisualScriptingSupport
    {
        private sealed class Holder
        {
            public NetworkInteractionExecutionContext Value;
        }

        private sealed class Scope : IDisposable
        {
            private readonly Holder m_Previous;
            public Scope(Holder previous) => m_Previous = previous;
            public void Dispose() => s_Current.Value = m_Previous;
        }

        private static readonly AsyncLocal<Holder> s_Current = new();

        public static bool HasCurrent => s_Current.Value != null && s_Current.Value.Value.IsValid;
        public static NetworkInteractionExecutionContext Current =>
            s_Current.Value?.Value ?? default;
        public static NetworkInteractionExecutionContext Context
        {
            get
            {
                if (HasCurrent) return Current;
                NetworkInteractionResponse response = NetworkInteractionEvents.LastResponse;
                NetworkInteractionBroadcast broadcast = NetworkInteractionEvents.LastBroadcast;
                return new NetworkInteractionExecutionContext(
                    NetworkInteractionEvents.LastEventType,
                    in response,
                    in broadcast,
                    ResolveLastActorObject(),
                    ResolveLastTargetObject());
            }
        }

        public static bool TryResolveManager(out NetworkCoreManager manager)
        {
            manager = NetworkCoreManager.Instance;
            if (manager == null)
            {
                manager = UnityEngine.Object.FindFirstObjectByType<NetworkCoreManager>(
                    FindObjectsInactive.Include);
            }

            return manager != null && manager.IsInitialized;
        }

        public static bool TryResolveActor(
            PropertyGetGameObject property,
            Args args,
            out GameObject actorObject,
            out uint actorNetworkId)
        {
            actorObject = property.Get(args);
            actorNetworkId = 0;
            if (actorObject == null) return false;

            NetworkCharacter networkCharacter = actorObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                networkCharacter = actorObject.GetComponentInParent<NetworkCharacter>();
            }

            if (networkCharacter == null || networkCharacter.NetworkId == 0) return false;

            actorObject = networkCharacter.gameObject;
            actorNetworkId = networkCharacter.NetworkId;
            return true;
        }

        public static bool TryResolveTarget(
            NetworkCoreManager manager,
            PropertyGetGameObject property,
            Args args,
            out GameObject targetObject,
            out uint targetNetworkId,
            out int targetHash,
            out Vector3 interactionPosition)
        {
            targetObject = property.Get(args);
            targetNetworkId = 0;
            targetHash = 0;
            interactionPosition = default;
            if (targetObject == null) return false;

            IInteractive interactive = ResolveInteractive(targetObject);
            if (interactive?.Instance != null)
            {
                targetObject = interactive.Instance;
                interactionPosition = interactive.Position;
            }
            else
            {
                interactionPosition = targetObject.transform.position;
            }

            targetNetworkId = manager.GetNetworkIdForGameObject?.Invoke(targetObject) ?? 0;
            targetHash = targetNetworkId == 0
                ? NetworkCoreController.GetStableInteractionTargetHash(targetObject)
                : 0;
            return true;
        }

        public static bool Matches(
            InteractionType interactionType,
            PropertyGetGameObject targetProperty,
            GameObject self,
            in NetworkInteractionResponse response)
        {
            return response.InteractionType == interactionType &&
                   MatchesTarget(targetProperty, self, response.TargetNetworkId,
                       response.TargetHash);
        }

        public static bool Matches(
            InteractionType interactionType,
            PropertyGetGameObject targetProperty,
            GameObject self,
            in NetworkInteractionBroadcast broadcast)
        {
            return broadcast.InteractionType == interactionType &&
                   MatchesTarget(targetProperty, self, broadcast.TargetNetworkId,
                       broadcast.TargetHash);
        }

        private static bool MatchesTarget(
            PropertyGetGameObject targetProperty,
            GameObject self,
            uint targetNetworkId,
            int targetHash)
        {
            if (targetProperty == null) return false;
            GameObject expected = targetProperty.Get(self);
            if (expected == null) return false;
            NetworkCoreManager manager = NetworkCoreManager.Instance;
            if (targetNetworkId != 0 && manager?.GetNetworkIdForGameObject != null)
            {
                uint resolved = manager.GetNetworkIdForGameObject.Invoke(expected);
                if (resolved == targetNetworkId) return true;
            }

            return targetNetworkId == 0 && targetHash != 0 &&
                   NetworkCoreController.GetStableInteractionTargetHash(expected) == targetHash;
        }

        public static GameObject ResolveLastActorObject()
        {
            if (HasCurrent) return Current.Actor;
            if (NetworkInteractionEvents.LastActorObject != null)
            {
                return NetworkInteractionEvents.LastActorObject;
            }

            uint characterNetworkId = NetworkInteractionEvents.LastCharacterNetworkId != 0
                ? NetworkInteractionEvents.LastCharacterNetworkId
                : NetworkInteractionEvents.LastActorNetworkId;
            Character character = NetworkTransportBridge.Active?.ResolveCharacter(characterNetworkId);
            return character != null ? character.gameObject : null;
        }

        public static GameObject ResolveLastTargetObject()
        {
            if (HasCurrent) return Current.Target;
            if (NetworkInteractionEvents.LastTargetObject != null)
            {
                return NetworkInteractionEvents.LastTargetObject;
            }

            uint targetNetworkId = NetworkInteractionEvents.LastTargetNetworkId;
            int targetHash = NetworkInteractionEvents.LastTargetHash;

            Character character = NetworkTransportBridge.Active?.ResolveCharacter(targetNetworkId);
            if (character != null) return character.gameObject;

            NetworkCoreManager manager = NetworkCoreManager.Instance;
            if (manager == null)
            {
                manager = UnityEngine.Object.FindFirstObjectByType<NetworkCoreManager>(
                    FindObjectsInactive.Include);
            }

            InteractionTracker[] trackers = UnityEngine.Object.FindObjectsByType<InteractionTracker>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < trackers.Length; i++)
            {
                if (trackers[i] == null) continue;
                IInteractive interactive = trackers[i];
                GameObject candidate = interactive.Instance;
                if (candidate == null) continue;

                if (targetNetworkId != 0 && manager?.GetNetworkIdForGameObject != null &&
                    manager.GetNetworkIdForGameObject.Invoke(candidate) == targetNetworkId)
                {
                    return candidate;
                }

                if (targetNetworkId == 0 && targetHash != 0 &&
                    NetworkCoreController.GetStableInteractionTargetHash(candidate) == targetHash)
                {
                    return candidate;
                }
            }

            return null;
        }

        public static void DispatchNextFrame(
            Trigger trigger,
            GameObject self,
            NetworkInteractionResponse response,
            string eventName)
        {
            if (trigger == null || !trigger.isActiveAndEnabled) return;
            NetworkInteractionExecutionContext context = Capture(response);
            trigger.StartCoroutine(ExecuteNextFrame(trigger, self, context, eventName));
        }

        public static void DispatchNextFrame(
            Trigger trigger,
            GameObject self,
            NetworkInteractionBroadcast broadcast,
            string eventName)
        {
            if (trigger == null || !trigger.isActiveAndEnabled) return;
            NetworkInteractionExecutionContext context = Capture(broadcast);
            trigger.StartCoroutine(ExecuteNextFrame(trigger, self, context, eventName));
        }

        private static IEnumerator ExecuteNextFrame(
            Trigger trigger,
            GameObject self,
            NetworkInteractionExecutionContext context,
            string eventName)
        {
            yield return null;
            if (trigger == null || !trigger.isActiveAndEnabled) yield break;
            Observe(Execute(trigger, self, context, eventName), trigger, eventName);
        }

        private static async Task Execute(
            Trigger trigger,
            GameObject self,
            NetworkInteractionExecutionContext context,
            string eventName)
        {
            try
            {
                using (Push(context))
                {
                    await trigger.Execute(self);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Network Interaction] {eventName} could not start its GC2 Trigger.", trigger);
                Debug.LogException(exception, trigger);
            }
        }

        private static IDisposable Push(in NetworkInteractionExecutionContext context)
        {
            Holder previous = s_Current.Value;
            s_Current.Value = new Holder { Value = context };
            return new Scope(previous);
        }

        private static NetworkInteractionExecutionContext Capture(
            in NetworkInteractionResponse response)
        {
            NetworkInteractionBroadcast broadcast = default;
            return new NetworkInteractionExecutionContext(
                response.Approved
                    ? NetworkInteractionEventType.Approved
                    : NetworkInteractionEventType.Rejected,
                in response,
                in broadcast,
                ResolveLastActorObject(),
                ResolveLastTargetObject());
        }

        private static NetworkInteractionExecutionContext Capture(
            in NetworkInteractionBroadcast broadcast)
        {
            NetworkInteractionResponse response = default;
            return new NetworkInteractionExecutionContext(
                NetworkInteractionEventType.Broadcast,
                in response,
                in broadcast,
                ResolveLastActorObject(),
                ResolveLastTargetObject());
        }

        private static async void Observe(Task task, Trigger trigger, string eventName)
        {
            if (task == null) return;

            try
            {
                await task;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[Network Interaction] {eventName} Trigger failed.", trigger);
                Debug.LogException(exception, trigger);
            }
        }

        private static IInteractive ResolveInteractive(GameObject gameObject)
        {
            if (gameObject == null) return null;

            IInteractive interactive = gameObject.GetComponent(typeof(IInteractive)) as IInteractive;
            if (interactive != null) return interactive;

            interactive = gameObject.GetComponentInParent(typeof(IInteractive), true) as IInteractive;
            return interactive ??
                   gameObject.GetComponentInChildren(typeof(IInteractive), true) as IInteractive;
        }
    }
}
