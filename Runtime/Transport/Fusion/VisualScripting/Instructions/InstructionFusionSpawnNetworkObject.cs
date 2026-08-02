using System;
using System.Threading.Tasks;
using Fusion;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    public enum FusionSpawnLogicalOwnerMode
    {
        None = 0,
        LocalClient = 1,
        ClientId = 2
    }

    [Version(1, 0, 0)]

    [Title("Spawn Fusion Network Object")]
    [Description(
        "Spawns a transport-managed Fusion Network Object through the logical-authority " +
        "spawn registry")]

    [Category("Network/Fusion/Object/Spawn Network Object")]

    [Parameter("Context", "A Game Object associated with the Fusion scene setup")]
    [Parameter("Prefab", "A prefab Game Object containing a Network Object and Fusion Network Identity")]
    [Parameter("Position", "The world-space position of the spawned object")]
    [Parameter("Rotation", "The world-space rotation of the spawned object")]
    [Parameter("Logical Owner", "Optionally assign no owner, the local client, or a validated client ID")]
    [Parameter("Client ID", "The connected Fusion client ID used by the Client ID owner mode")]
    [Parameter("Dont Destroy On Load", "Keep the spawned object across scene loads on every peer")]
    [Parameter("Save", "Optional value where the spawned Game Object is stored")]

    [Keywords(
        "Network", "Fusion", "Photon", "Spawn", "Create", "Prefab", "Object",
        "Logical", "Owner", "Authority")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Green, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionFusionSpawnNetworkObject : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private PropertyGetGameObject m_Prefab = GetGameObjectNone.Create();

        [SerializeField]
        private PropertyGetPosition m_Position = GetPositionVector3.Create(Vector3.zero);

        [SerializeField]
        private PropertyGetRotation m_Rotation = GetRotationIdentity.Create;

        [SerializeField]
        private FusionSpawnLogicalOwnerMode m_LogicalOwner =
            FusionSpawnLogicalOwnerMode.None;

        [SerializeField]
        private PropertyGetDecimal m_ClientId = new PropertyGetDecimal(0d);

        [SerializeField]
        private bool m_DontDestroyOnLoad;

        [SerializeField]
        private PropertySetGameObject m_Save = SetGameObjectNone.Create;

        public override string Title => $"Spawn Fusion Network Object: {m_Prefab}";

        protected override Task Run(Args args)
        {
            GameObject context = m_Context.Get(args) ?? args.Self;
            if (!FusionVisualScriptingSupport.TryResolveBridge(
                    context,
                    out FusionTransportBridge bridge))
            {
                LogError(
                    context,
                    "No unambiguous Fusion Transport Bridge could be resolved from this context.");
                return DefaultResult;
            }

            NetworkRunner runner = bridge.Runner;
            if (runner == null || !runner.IsRunning || !bridge.IsServer)
            {
                LogWarning(
                    context,
                    "Spawn Network Object can only run on the active Fusion logical authority.");
                return DefaultResult;
            }

            if (!FusionAuthoritySpawnRegistry.TryGet(
                    runner,
                    out FusionAuthoritySpawnRegistry registry))
            {
                LogError(
                    context,
                    "The active Fusion runner has no bound Authority Spawn Registry.");
                return DefaultResult;
            }

            GameObject prefabGameObject = m_Prefab.Get(args);
            NetworkObject prefab = prefabGameObject != null
                ? prefabGameObject.GetComponent<NetworkObject>()
                : null;
            if (prefab == null)
            {
                LogError(
                    prefabGameObject != null ? prefabGameObject : context,
                    "The selected prefab must contain a Fusion Network Object on its root.");
                return DefaultResult;
            }

            if (prefab.GetComponent<FusionNetworkIdentity>() == null)
            {
                LogError(
                    prefabGameObject,
                    "The selected prefab must contain a Fusion Network Identity on its root.");
                return DefaultResult;
            }

            if (!TryResolveLogicalOwner(
                    bridge,
                    args,
                    out PlayerRef logicalOwner,
                    out string ownerError))
            {
                LogError(context, ownerError);
                return DefaultResult;
            }

            NetworkSpawnFlags flags = m_DontDestroyOnLoad
                ? NetworkSpawnFlags.DontDestroyOnLoad
                : default;
            NetworkObject spawned = registry.Spawn(
                prefab,
                m_Position.Get(args),
                m_Rotation.Get(args),
                logicalOwner,
                null,
                flags);
            if (spawned == null)
            {
                LogWarning(
                    context,
                    $"The Fusion Authority Spawn Registry did not spawn '{prefab.name}'.");
                return DefaultResult;
            }

            m_Save.Set(spawned.gameObject, args);
            return DefaultResult;
        }

        private bool TryResolveLogicalOwner(
            FusionTransportBridge bridge,
            Args args,
            out PlayerRef owner,
            out string error)
        {
            owner = PlayerRef.None;
            error = null;

            switch (m_LogicalOwner)
            {
                case FusionSpawnLogicalOwnerMode.None:
                    return true;

                case FusionSpawnLogicalOwnerMode.LocalClient:
                    if (!bridge.TryGetLocalClientId(out uint localClientId) ||
                        !bridge.TryGetPlayerRef(localClientId, out owner))
                    {
                        error = "The active Fusion runner has no valid local client to assign as logical owner.";
                        return false;
                    }
                    return true;

                case FusionSpawnLogicalOwnerMode.ClientId:
                    double rawClientId = m_ClientId.Get(args);
                    if (double.IsNaN(rawClientId) ||
                        double.IsInfinity(rawClientId) ||
                        rawClientId < 1d ||
                        rawClientId > int.MaxValue ||
                        rawClientId != Math.Truncate(rawClientId))
                    {
                        error = $"'{rawClientId}' is not a valid Fusion client ID.";
                        return false;
                    }

                    uint clientId = (uint)rawClientId;
                    if (!bridge.TryGetPlayerRef(clientId, out owner))
                    {
                        error = $"Fusion client ID '{clientId}' is not connected to the active runner.";
                        return false;
                    }
                    return true;

                default:
                    error = $"Unsupported logical owner mode '{m_LogicalOwner}'.";
                    return false;
            }
        }

        private static void LogError(GameObject context, string message)
        {
            Debug.LogError($"[Fusion Visual Scripting] {message}", context);
        }

        private static void LogWarning(GameObject context, string message)
        {
            Debug.LogWarning($"[Fusion Visual Scripting] {message}", context);
        }
    }
}
