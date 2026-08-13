using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Network Attach Prop")]
    [Description("Requests the server to attach a registered prop prefab to a Network Character")]
    [Category("Network/Core/Props/Attach Prop")]
    [Parameter("Character", "The initialized Network Character that receives the prop")]
    [Parameter("Prop ID", "The ID of a prefab registered on Network Core Manager")]
    [Parameter("Bone Name", "The target bone name, or empty to attach to the Character root")]
    [Parameter("Local Position", "The prop's local position offset")]
    [Parameter("Local Rotation", "The prop's local rotation offset")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Prop", "Attach", "Equip")]
    [Image(typeof(IconTennis), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class InstructionNetworkCoreAttachProp : TInstructionNetworkCoreRequest
    {
        [SerializeField] private PropertyGetString m_PropId = new PropertyGetString(string.Empty);
        [SerializeField] private PropertyGetString m_BoneName = new PropertyGetString(string.Empty);
        [SerializeField] private PropertyGetPosition m_LocalPosition = new PropertyGetPosition(Vector3.zero);
        [SerializeField] private PropertyGetRotation m_LocalRotation = new PropertyGetRotation(Quaternion.identity);

        public override string Title => $"Network Attach {m_PropId} on {m_Character}";

        protected override Task Run(Args args)
        {
            string propId = m_PropId.Get(args);
            if (string.IsNullOrWhiteSpace(propId))
            {
                Debug.LogWarning($"[{nameof(InstructionNetworkCoreAttachProp)}] Prop ID is empty.");
                return DefaultResult;
            }

            string boneName = m_BoneName.Get(args);
            Vector3 localPosition = m_LocalPosition.Get(args);
            Quaternion localRotation = m_LocalRotation.Get(args);

            return SendRequest<NetworkPropResponse>(
                args,
                nameof(InstructionNetworkCoreAttachProp),
                NetworkCoreRequestChannel.Prop,
                NetworkCoreVisualScriptingOperation.AttachProp,
                (manager, characterNetworkId, callback) =>
                    manager.RequestAttachProp(
                        characterNetworkId,
                        propId,
                        boneName,
                        localPosition,
                        localRotation,
                        callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Detach Prop")]
    [Description("Requests the server to detach a registered prop type from a Network Character")]
    [Category("Network/Core/Props/Detach Prop")]
    [Parameter("Character", "The initialized Network Character that owns the prop")]
    [Parameter("Prop ID", "The registered prop ID to detach")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Prop", "Detach", "Unequip")]
    [Image(typeof(IconTennis), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionNetworkCoreDetachProp : TInstructionNetworkCoreRequest
    {
        [SerializeField] private PropertyGetString m_PropId = new PropertyGetString(string.Empty);

        public override string Title => $"Network Detach {m_PropId} from {m_Character}";

        protected override Task Run(Args args)
        {
            string propId = m_PropId.Get(args);
            if (string.IsNullOrWhiteSpace(propId))
            {
                Debug.LogWarning($"[{nameof(InstructionNetworkCoreDetachProp)}] Prop ID is empty.");
                return DefaultResult;
            }

            return SendRequest<NetworkPropResponse>(
                args,
                nameof(InstructionNetworkCoreDetachProp),
                NetworkCoreRequestChannel.Prop,
                NetworkCoreVisualScriptingOperation.DetachProp,
                (manager, characterNetworkId, callback) =>
                    manager.RequestDetachProp(characterNetworkId, propId, callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Detach Prop Instance")]
    [Description("Requests the server to detach one exact server-assigned prop instance")]
    [Category("Network/Core/Props/Detach Prop Instance")]
    [Parameter("Character", "The initialized Network Character that owns the prop")]
    [Parameter("Prop Instance ID", "The server-assigned ID returned by an approved attach request")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Prop", "Detach", "Instance")]
    [Image(typeof(IconTennis), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionNetworkCoreDetachPropInstance : TInstructionNetworkCoreRequest
    {
        [SerializeField] private PropertyGetInteger m_PropInstanceId = new PropertyGetInteger(0);

        public override string Title => $"Network Detach Prop Instance {m_PropInstanceId} from {m_Character}";

        protected override Task Run(Args args)
        {
            int propInstanceId = Mathf.FloorToInt((float)m_PropInstanceId.Get(args));
            if (propInstanceId <= 0)
            {
                Debug.LogWarning(
                    $"[{nameof(InstructionNetworkCoreDetachPropInstance)}] " +
                    "Prop Instance ID must be greater than zero.");
                return DefaultResult;
            }

            return SendRequest<NetworkPropResponse>(
                args,
                nameof(InstructionNetworkCoreDetachPropInstance),
                NetworkCoreRequestChannel.Prop,
                NetworkCoreVisualScriptingOperation.DetachPropInstance,
                (manager, characterNetworkId, callback) =>
                    manager.RequestDetachPropInstance(
                        characterNetworkId,
                        propInstanceId,
                        callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Detach All Props")]
    [Description("Requests the server to detach all network-managed props from a Network Character")]
    [Category("Network/Core/Props/Detach All Props")]
    [Parameter("Character", "The initialized Network Character whose props are detached")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Prop", "Detach", "Clear", "All")]
    [Image(typeof(IconTennis), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionNetworkCoreDetachAllProps : TInstructionNetworkCoreRequest
    {
        public override string Title => $"Network Detach All Props from {m_Character}";

        protected override Task Run(Args args)
        {
            return SendRequest<NetworkPropResponse>(
                args,
                nameof(InstructionNetworkCoreDetachAllProps),
                NetworkCoreRequestChannel.Prop,
                NetworkCoreVisualScriptingOperation.DetachAllProps,
                (manager, characterNetworkId, callback) =>
                    manager.RequestDetachAllProps(characterNetworkId, callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }
}
