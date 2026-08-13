using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkCoreRequestChannel
    {
        Ragdoll,
        Prop,
        Invincibility,
        Poise,
        Busy
    }

    [Serializable]
    public abstract class TInstructionNetworkCoreRequest : Instruction
    {
        [SerializeField]
        protected PropertyGetGameObject m_Character = GetGameObjectLocalNetworkPlayer.Create();

        [SerializeField]
        protected bool m_WaitForResponse = true;

        protected Task SendRequest<TResponse>(
            Args args,
            string instructionName,
            NetworkCoreRequestChannel channel,
            NetworkCoreVisualScriptingOperation operation,
            Action<NetworkCoreManager, uint, Action<TResponse>> sender,
            Action<NetworkCoreVisualScriptingOperation, uint, TResponse> recorder)
        {
            if (!NetworkCoreInstructionUtility.TryResolveRequest(
                    m_Character,
                    args,
                    instructionName,
                    channel,
                    out NetworkCoreManager manager,
                    out uint characterNetworkId))
            {
                return DefaultResult;
            }

            NetworkCoreVisualScriptingContext.BeginRequest(operation, characterNetworkId);
            Action<TResponse> callback = NetworkCoreInstructionUtility.CreateCallback<TResponse>(
                m_WaitForResponse,
                response => recorder(operation, characterNetworkId, response),
                out Task completion);

            sender(manager, characterNetworkId, callback);
            return completion;
        }
    }

    internal static class NetworkCoreInstructionUtility
    {
        internal static bool TryResolveRequest(
            PropertyGetGameObject characterProperty,
            Args args,
            string instructionName,
            NetworkCoreRequestChannel channel,
            out NetworkCoreManager manager,
            out uint characterNetworkId)
        {
            manager = null;
            characterNetworkId = 0;

            GameObject characterObject = characterProperty.Get(args);
            if (characterObject == null)
            {
                Warn(instructionName, "Character GameObject resolved to null.");
                return false;
            }

            NetworkCharacter networkCharacter = characterObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                networkCharacter = characterObject.GetComponentInParent<NetworkCharacter>();
            }

            if (networkCharacter == null || networkCharacter.NetworkId == 0)
            {
                Warn(instructionName,
                    $"'{characterObject.name}' has no initialized NetworkCharacter.");
                return false;
            }

            manager = NetworkCoreManager.Instance;
            if (manager == null)
            {
                Warn(instructionName, "NetworkCoreManager is missing.");
                return false;
            }

            if (!manager.IsInitialized || manager.CoreController == null)
            {
                Warn(instructionName, "NetworkCoreManager is not initialized.");
                manager = null;
                return false;
            }

            if (!manager.CoreController.IsClient)
            {
                Warn(instructionName,
                    "The Core request path requires a client or host endpoint.");
                manager = null;
                return false;
            }

            if (!HasSendRoute(manager, channel))
            {
                Warn(instructionName,
                    $"The active transport has no {channel} Core request route.");
                manager = null;
                return false;
            }

            characterNetworkId = networkCharacter.NetworkId;
            return true;
        }

        internal static Action<TResponse> CreateCallback<TResponse>(
            bool waitForResponse,
            Action<TResponse> recorder,
            out Task completion)
        {
            TaskCompletionSource<bool> source = waitForResponse
                ? new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
                : null;

            completion = source != null ? source.Task : Task.CompletedTask;
            return response =>
            {
                try
                {
                    recorder(response);
                }
                finally
                {
                    source?.TrySetResult(true);
                }
            };
        }

        private static bool HasSendRoute(
            NetworkCoreManager manager,
            NetworkCoreRequestChannel channel)
        {
            return channel switch
            {
                NetworkCoreRequestChannel.Ragdoll =>
                    manager.SendRagdollRequestToServer != null,
                NetworkCoreRequestChannel.Prop =>
                    manager.SendPropRequestToServer != null,
                NetworkCoreRequestChannel.Invincibility =>
                    manager.SendInvincibilityRequestToServer != null,
                NetworkCoreRequestChannel.Poise =>
                    manager.SendPoiseRequestToServer != null,
                NetworkCoreRequestChannel.Busy =>
                    manager.SendBusyRequestToServer != null,
                _ => false
            };
        }

        private static void Warn(string instructionName, string message)
        {
            Debug.LogWarning($"[{instructionName}] {message}");
        }
    }
}
