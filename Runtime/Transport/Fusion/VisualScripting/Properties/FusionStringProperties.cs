using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Session Name")]
    [Category("Network/Fusion/Session/Session Name")]
    [Description("The active Photon Fusion session name")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Name", "Code")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionSessionName : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryGetActiveSession(
                args.Self,
                out FusionSessionSnapshot session)
                ? session.SessionName
                : string.Empty;
        }

        public override string String => "Fusion Session Name";
    }

    [Title("Fusion Session Region")]
    [Category("Network/Fusion/Session/Region")]
    [Description("The Photon Cloud region used by the active Fusion session")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Region", "Cloud")]
    [Image(typeof(IconSphereOutline), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionSessionRegion : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryGetActiveSession(
                args.Self,
                out FusionSessionSnapshot session)
                ? session.Region
                : string.Empty;
        }

        public override string String => "Fusion Session Region";
    }

    [Title("Fusion Session Mode")]
    [Category("Network/Fusion/Session/Game Mode")]
    [Description("The native Photon Fusion game mode of the active session")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Mode", "Host", "Client", "Shared")]
    [Image(typeof(IconChip), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionSessionMode : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryGetActiveSession(
                args.Self,
                out FusionSessionSnapshot session)
                ? session.GameMode.ToString()
                : string.Empty;
        }

        public override string String => "Fusion Session Mode";
    }

    [Title("Fusion Session Lifecycle State")]
    [Category("Network/Fusion/Session/Lifecycle State")]
    [Description("The current lifecycle state of the resolved Fusion session bootstrap")]
    [Keywords("Network", "Fusion", "Photon", "Session", "State", "Starting", "Running", "Stopping")]
    [Image(typeof(IconCircleOutline), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionSessionLifecycleState : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(
                args.Self,
                out FusionSessionBootstrap bootstrap)
                ? bootstrap.SessionLifecycleState.ToString()
                : FusionSessionLifecycleState.Offline.ToString();
        }

        public override string String => "Fusion Session Lifecycle State";
    }

    [Title("Fusion Connection Type")]
    [Category("Network/Fusion/Connection/Connection Type")]
    [Description("Whether the active Fusion Host/Client connection is direct or using Photon Relay")]
    [Keywords("Network", "Fusion", "Photon", "Connection", "Direct", "Relayed", "Relay")]
    [Image(typeof(IconSphereOutline), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionConnectionType : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.TryGetConnectionDiagnostics(
                       out FusionConnectionDiagnostics diagnostics)
                ? diagnostics.ConnectionType.ToString()
                : string.Empty;
        }

        public override string String => "Fusion Connection Type";
    }

    [Title("Fusion NAT Type")]
    [Category("Network/Fusion/Connection/NAT Type")]
    [Description("The NAT type Photon Fusion discovered for the local peer")]
    [Keywords("Network", "Fusion", "Photon", "Connection", "NAT", "STUN")]
    [Image(typeof(IconSphereOutline), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionNATType : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.TryGetConnectionDiagnostics(
                       out FusionConnectionDiagnostics diagnostics)
                ? diagnostics.NATType.ToString()
                : string.Empty;
        }

        public override string String => "Fusion NAT Type";
    }

    [Title("Fusion Authenticated User ID")]
    [Category("Network/Fusion/Connection/Authenticated User ID")]
    [Description("The Photon user ID assigned to the authenticated local Fusion peer")]
    [Keywords("Network", "Fusion", "Photon", "Authentication", "User", "ID", "Steam")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringFusionAuthenticatedUserId : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.TryGetConnectionDiagnostics(
                       out FusionConnectionDiagnostics diagnostics)
                ? diagnostics.AuthenticatedUserId
                : string.Empty;
        }

        public override string String => "Fusion Authenticated User ID";
    }

    [Title("Fusion Last Start Failure")]
    [Category("Network/Fusion/Session/Last Start Failure")]
    [Description("The most recent Fusion session start failure message or shutdown reason")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Start", "Failure", "Error", "Reason")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetStringFusionLastStartFailure : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            if (!FusionVisualScriptingSupport.TryResolveBootstrap(
                    args.Self,
                    out FusionSessionBootstrap bootstrap) ||
                !bootstrap.HasLastStartFailure)
            {
                return string.Empty;
            }

            FusionSessionFailureInfo failure = bootstrap.LastStartFailure;
            if (!string.IsNullOrWhiteSpace(failure.ErrorMessage))
            {
                return failure.ErrorMessage;
            }

            return failure.WasCancelled
                ? "Cancelled"
                : failure.ShutdownReason.ToString();
        }

        public override string String => "Fusion Last Start Failure";
    }

    [Title("Fusion Last Shutdown Reason")]
    [Category("Network/Fusion/Runner/Last Shutdown Reason")]
    [Description("The most recent Photon Fusion runner shutdown reason")]
    [Keywords("Network", "Fusion", "Photon", "Runner", "Shutdown", "Disconnect", "Reason")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetStringFusionLastShutdownReason : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.HasLastRunnerShutdown
                ? bridge.LastRunnerShutdown.Reason.ToString()
                : string.Empty;
        }

        public override string String => "Fusion Last Shutdown Reason";
    }

    [Title("Fusion Last Session Stop Origin")]
    [Category("Network/Fusion/Session/Last Stop Origin")]
    [Description("The origin carried by the latest observed Fusion session stop")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Stop", "Origin", "Payload")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetStringFusionLastSessionStopOrigin : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(
                       args.Self,
                       out FusionSessionBootstrap bootstrap) &&
                   bootstrap.HasLastStop
                ? bootstrap.LastStop.Origin.ToString()
                : string.Empty;
        }

        public override string String => "Fusion Last Session Stop Origin";
    }

    [Title("Fusion Last Session Stop Reason")]
    [Category("Network/Fusion/Session/Last Stop Reason")]
    [Description("The shutdown reason carried by the latest observed Fusion session stop")]
    [Keywords("Network", "Fusion", "Photon", "Session", "Stop", "Shutdown", "Reason", "Payload")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetStringFusionLastSessionStopReason : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(
                       args.Self,
                       out FusionSessionBootstrap bootstrap) &&
                   bootstrap.HasLastStop
                ? bootstrap.LastStop.ShutdownReason.ToString()
                : string.Empty;
        }

        public override string String => "Fusion Last Session Stop Reason";
    }

    [Title("Fusion Last Local Scene Name")]
    [Category("Network/Fusion/Scene/Last Local Scene Name")]
    [Description("The scene name carried by the latest Fusion local-scene observation")]
    [Keywords("Network", "Fusion", "Photon", "Scene", "Name", "Ready", "Payload")]
    [Image(typeof(IconUnity), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetStringFusionLastLocalSceneName : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBridge(
                       args.Self,
                       out FusionTransportBridge bridge) &&
                   bridge.HasLastLocalSceneObservation
                ? bridge.LastLocalSceneObservation.SceneName
                : string.Empty;
        }

        public override string String => "Fusion Last Local Scene Name";
    }
}
