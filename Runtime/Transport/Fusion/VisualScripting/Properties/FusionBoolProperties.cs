using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Has Active Session")]
    [Category("Network/Fusion/Session/Has Active Session")]
    [Description("True while the resolved Photon Fusion session is active")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Active", "Running")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetBoolFusionHasActiveSession : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.TryGetActiveSession(
                args.Self,
                out _);
        }

        public override string String => "Fusion Has Active Session";
    }

    [Title("Fusion Connection Is Relayed")]
    [Category("Network/Fusion/Connection/Is Relayed")]
    [Description("True when the active Fusion connection is using Photon Relay")]
    [Keywords("Network", "Fusion", "Photon", "Connection", "Relayed", "Relay")]
    [Image(typeof(IconSphereOutline), ColorTheme.Type.Blue, typeof(OverlayTick))]
    [Serializable]
    public sealed class GetBoolFusionConnectionIsRelayed : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.IsConnectionRelayed(args.Self);
        }

        public override string String => "Fusion Connection Is Relayed";
    }

    [Title("Fusion Is Shared Master Client")]
    [Category("Network/Fusion/Authority/Is Shared Master Client")]
    [Description("True when this peer is the current Master Client in a Fusion Shared session")]
    [Keywords("Network", "Fusion", "Photon", "Shared", "Master", "Authority")]
    [Image(typeof(IconCrown), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetBoolFusionIsSharedMasterClient : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.IsSharedMasterClient(args.Self);
        }

        public override string String => "Fusion Is Shared Master Client";
    }

    [Title("Fusion Object Is Admitted")]
    [Category("Network/Fusion/Authority/Object Is Admitted")]
    [Description("True when the target identity passed the Fusion authority-spawn admission boundary")]
    [Keywords("Network", "Fusion", "Photon", "Object", "Identity", "Admission", "Authority")]
    [Image(typeof(IconShieldSolid), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class GetBoolFusionObjectIsAdmitted : PropertyTypeGetBool
    {
        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.IsObjectAdmitted(m_Target, args);
        }

        public override string String => $"Fusion Object Is Admitted: {m_Target}";
    }

    [Title("Fusion Is Local Logical Owner")]
    [Category("Network/Fusion/Authority/Is Local Logical Owner")]
    [Description("True when the local peer is the target identity's gameplay owner")]
    [Keywords("Network", "Fusion", "Photon", "Logical", "Owner", "Local", "Authority")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Blue, typeof(OverlayTick))]
    [Serializable]
    public sealed class GetBoolFusionIsLocalLogicalOwner : PropertyTypeGetBool
    {
        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.IsLocalLogicalOwner(m_Target, args);
        }

        public override string String => $"Fusion Is Local Logical Owner: {m_Target}";
    }

    [Title("Fusion Last Authority Is Local Authority")]
    [Category("Network/Fusion/Authority/Last Observation Is Local Authority")]
    [Description("The local-authority value carried by the latest Fusion authority observation")]
    [Keywords("Network", "Fusion", "Photon", "Authority", "Latest", "Event", "Payload")]
    [Image(typeof(IconCrown), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetBoolFusionLastAuthorityIsLocalAuthority : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.HasLastAuthorityObservation &&
                   bridge.LastAuthorityObservation.IsAuthority;
        }

        public override string String => "Fusion Last Authority Is Local Authority";
    }

    [Title("Fusion Last Stop Was Requested")]
    [Category("Network/Fusion/Session/Last Stop Was Requested")]
    [Description("True when the latest observed Fusion session stop was explicitly requested")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Stop", "Requested", "Payload")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetBoolFusionLastStopWasRequested : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(
                       args.Self,
                       out FusionSessionBootstrap bootstrap) &&
                   bootstrap.HasLastStop &&
                   bootstrap.LastStop.WasRequested;
        }

        public override string String => "Fusion Last Stop Was Requested";
    }
}
