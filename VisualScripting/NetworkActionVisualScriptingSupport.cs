using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Serializable]
    public sealed class NetworkActionPropertyPayload
    {
        [SerializeField] private PropertyGetBool m_Boolean = new(false);
        [SerializeField] private PropertyGetDecimal m_Number = new(0d);
        [SerializeField] private PropertyGetString m_String = new("");
        [SerializeField] private PropertyGetPosition m_Vector3 = new(Vector3.zero);

        public NetworkActionPayload Get(NetworkActionDefinition definition, Args args)
        {
            if (definition == null) return NetworkActionPayload.None;
            return definition.PayloadType switch
            {
                NetworkActionPayloadType.Boolean =>
                    NetworkActionPayload.FromBoolean(m_Boolean.Get(args)),
                NetworkActionPayloadType.Number =>
                    NetworkActionPayload.FromNumber((float)m_Number.Get(args)),
                NetworkActionPayloadType.String =>
                    NetworkActionPayload.FromString(m_String.Get(args)),
                NetworkActionPayloadType.Vector3 =>
                    NetworkActionPayload.FromVector3(m_Vector3.Get(args)),
                _ => NetworkActionPayload.None
            };
        }
    }

    internal static class NetworkActionVisualScriptingSupport
    {
        public static bool TryResolve(
            PropertyGetGameObject actorProperty,
            PropertyGetGameObject targetProperty,
            Args args,
            out uint actorNetworkId,
            out NetworkActionEndpoint endpoint)
        {
            actorNetworkId = 0;
            endpoint = null;
            GameObject actor = actorProperty.Get(args);
            NetworkCharacter character = actor != null
                ? actor.GetComponent<NetworkCharacter>() ??
                  actor.GetComponentInParent<NetworkCharacter>() ??
                  actor.GetComponentInChildren<NetworkCharacter>(true)
                : null;
            if (character == null || character.NetworkId == 0) return false;

            GameObject target = targetProperty.Get(args);
            endpoint = target != null
                ? target.GetComponent<NetworkActionEndpoint>() ??
                  target.GetComponentInParent<NetworkActionEndpoint>() ??
                  target.GetComponentInChildren<NetworkActionEndpoint>(true)
                : null;
            if (endpoint == null || endpoint.NetworkId == 0) return false;
            actorNetworkId = character.NetworkId;
            return true;
        }

        public static bool Matches(
            NetworkActionDefinition definition,
            in NetworkActionExecutionContext context)
        {
            return definition != null &&
                   context.ActionHash == definition.ActionHash &&
                    string.Equals(context.ActionId, definition.ActionId,
                        StringComparison.Ordinal);
        }

        public static bool MatchesTarget(
            PropertyGetGameObject targetProperty,
            GameObject self,
            in NetworkActionExecutionContext context)
        {
            if (targetProperty == null) return false;
            GameObject resolved = targetProperty.Get(self);
            if (resolved == null || context.Target == null) return false;
            if (resolved == context.Target) return true;
            NetworkActionEndpoint expected =
                resolved.GetComponent<NetworkActionEndpoint>() ??
                resolved.GetComponentInParent<NetworkActionEndpoint>() ??
                resolved.GetComponentInChildren<NetworkActionEndpoint>(true);
            NetworkActionEndpoint actual =
                context.Target.GetComponent<NetworkActionEndpoint>() ??
                context.Target.GetComponentInParent<NetworkActionEndpoint>() ??
                context.Target.GetComponentInChildren<NetworkActionEndpoint>(true);
            return expected != null && actual != null && expected == actual;
        }

        public static async void ExecuteTrigger(
            GameCreator.Runtime.VisualScripting.Trigger trigger,
            GameObject self,
            NetworkActionExecutionContext context)
        {
            try
            {
                // Transport callbacks must finish before arbitrary designer-authored lists run.
                // The immutable context is captured by value and restored for this async flow.
                await Task.Yield();
                using (NetworkActionContext.Push(context))
                {
                    await trigger.Execute(context.Target != null ? context.Target : self);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, self);
            }
        }

        public static Task WaitForResponse(
            Task<NetworkActionResponse> response,
            float timeoutSeconds)
        {
            return timeoutSeconds <= 0f
                ? response
                : Task.WhenAny(response, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        }
    }
}
