using System;
using System.Threading.Tasks;
using Fusion;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Version(1, 0, 0)]

    [Title("Despawn Fusion Network Object")]
    [Description(
        "Despawns a transport-managed Fusion Network Object through the logical-authority " +
        "spawn registry")]

    [Category("Network/Fusion/Object/Despawn Network Object")]

    [Parameter("Context", "A Game Object associated with the Fusion scene setup")]
    [Parameter("Target", "The Game Object containing or belonging to a Fusion Network Identity")]

    [Keywords("Network", "Fusion", "Photon", "Despawn", "Destroy", "Object", "Authority")]
    [Image(typeof(IconCubeOutline), ColorTheme.Type.Red, typeof(OverlayMinus))]
    [Serializable]
    public sealed class InstructionFusionDespawnNetworkObject : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        public override string Title => $"Despawn Fusion Network Object: {m_Target}";

        protected override Task Run(Args args)
        {
            FusionNetworkIdentity identity =
                FusionVisualScriptingSupport.ResolveIdentity(m_Target, args);
            GameObject context = m_Context.Get(args) ?? identity?.gameObject ?? args.Self;
            if (identity == null || identity.NetworkObject == null ||
                !identity.NetworkObject.IsValid || !identity.NetworkObject.Id.IsValid)
            {
                LogError(
                    context,
                    "The target does not resolve to a spawned Fusion Network Identity.");
                return DefaultResult;
            }

            if (!FusionVisualScriptingSupport.TryResolveBridge(
                    context,
                    out FusionTransportBridge bridge))
            {
                LogError(
                    context,
                    "No unambiguous Fusion Transport Bridge could be resolved from this context.");
                return DefaultResult;
            }

            if (bridge.Runner == null || !bridge.Runner.IsRunning || !bridge.IsServer)
            {
                LogWarning(
                    context,
                    "Despawn Network Object can only run on the active Fusion logical authority.");
                return DefaultResult;
            }

            if (identity.Runner != bridge.Runner)
            {
                LogError(
                    context,
                    "The target identity belongs to a different Fusion runner than the resolved bridge.");
                return DefaultResult;
            }

            if (!FusionAuthoritySpawnRegistry.TryGet(
                    bridge.Runner,
                    out FusionAuthoritySpawnRegistry registry))
            {
                LogError(
                    context,
                    "The active Fusion runner has no bound Authority Spawn Registry.");
                return DefaultResult;
            }

            NetworkId networkId = identity.NetworkObject.Id;
            if (!registry.Despawn(networkId))
            {
                LogWarning(
                    identity.gameObject,
                    $"The Fusion Authority Spawn Registry did not despawn Network ID '{networkId.Raw}'.");
            }

            return DefaultResult;
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
