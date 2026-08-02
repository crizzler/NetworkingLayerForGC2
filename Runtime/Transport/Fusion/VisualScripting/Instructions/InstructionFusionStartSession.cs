using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    public enum FusionSessionStartOperation
    {
        StartHost = 0,
        JoinHost = 1,
        CreateShared = 2,
        JoinShared = 3
    }

    public enum FusionSessionRegionSource
    {
        BootstrapDefault = 0,
        BestRegion = 1,
        Custom = 2
    }

    public enum FusionSessionRelayPolicy
    {
        BootstrapDefault = 0,
        ForcePhotonRelay = 1,
        AllowNatPunchthrough = 2
    }

    [Version(1, 1, 0)]

    [Title("Start Fusion Session")]
    [Description("Starts or joins a supported Photon Fusion session through the configured session bootstrap")]

    [Category("Network/Fusion/Session/Start Session")]

    [Parameter("Context", "A Game Object associated with the Fusion scene setup")]
    [Parameter("Operation", "Start Host, Join Host, Create Shared, or Join Shared")]
    [Parameter("Session Name", "The Photon Fusion session name. Blank uses the bootstrap default")]
    [Parameter("Region Source", "Use the bootstrap region, automatic best-region selection, or a custom region")]
    [Parameter("Custom Region", "The Photon region code used when Region Source is Custom")]
    [Parameter("Relay Policy", "Use the bootstrap policy, force Photon Relay, or allow NAT punch-through")]
    [Parameter("Wait Until Complete", "Wait for Fusion to finish starting or joining before continuing")]

    [Keywords(
        "Network", "Fusion", "Photon", "Session", "Host", "Shared", "Join", "Start",
        "Region", "Relay", "NAT")]
    [Image(typeof(IconChip), ColorTheme.Type.Green, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionFusionStartSession : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private FusionSessionStartOperation m_Operation = FusionSessionStartOperation.StartHost;

        [SerializeField]
        private PropertyGetString m_SessionName = new PropertyGetString(string.Empty);

        [SerializeField]
        private FusionSessionRegionSource m_RegionSource =
            FusionSessionRegionSource.BootstrapDefault;

        [SerializeField]
        private PropertyGetString m_CustomRegion = new PropertyGetString(string.Empty);

        [SerializeField]
        private FusionSessionRelayPolicy m_RelayPolicy =
            FusionSessionRelayPolicy.BootstrapDefault;

        [SerializeField]
        private bool m_WaitUntilComplete = true;

        public override string Title => $"Fusion {GetOperationTitle(m_Operation)}: {m_SessionName}";

        protected override Task Run(Args args)
        {
            GameObject context = m_Context.Get(args) ?? args.Self;
            if (!FusionVisualScriptingSupport.TryResolveBootstrap(context, out var bootstrap))
            {
                FusionVisualScriptingSupport.LogMissingBootstrap(context);
                return DefaultResult;
            }

            string sessionName = m_SessionName.Get(args);
            if (string.IsNullOrWhiteSpace(sessionName))
            {
                sessionName = bootstrap.DefaultSessionName;
            }

            if (!TryResolveRegion(bootstrap, args, context, out string region))
            {
                return DefaultResult;
            }

            bool forcePhotonRelay = ResolveForcePhotonRelay(bootstrap);
            var options = new FusionSessionStartOptions(
                sessionName,
                region,
                null,
                forcePhotonRelay);

            Task startTask;
            switch (m_Operation)
            {
                case FusionSessionStartOperation.StartHost:
                    startTask = bootstrap.StartHostAsync(options);
                    break;

                case FusionSessionStartOperation.JoinHost:
                    startTask = bootstrap.JoinHostAsync(options);
                    break;

                case FusionSessionStartOperation.CreateShared:
                    startTask = bootstrap.CreateSharedAsync(options);
                    break;

                case FusionSessionStartOperation.JoinShared:
                    startTask = bootstrap.JoinSharedAsync(options);
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            return FusionVisualScriptingSupport.CompleteOrObserve(
                startTask,
                m_WaitUntilComplete,
                context,
                GetOperationTitle(m_Operation));
        }

        private bool TryResolveRegion(
            FusionSessionBootstrap bootstrap,
            Args args,
            GameObject context,
            out string region)
        {
            switch (m_RegionSource)
            {
                case FusionSessionRegionSource.BootstrapDefault:
                    region = bootstrap.Region;
                    return true;

                case FusionSessionRegionSource.BestRegion:
                    region = string.Empty;
                    return true;

                case FusionSessionRegionSource.Custom:
                    region = m_CustomRegion?.Get(args);
                    if (!string.IsNullOrWhiteSpace(region)) return true;

                    Debug.LogError(
                        "[Fusion Visual Scripting] A non-empty Photon region code is " +
                        "required when Region Source is Custom.",
                        context);
                    return false;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private bool ResolveForcePhotonRelay(FusionSessionBootstrap bootstrap)
        {
            return m_RelayPolicy switch
            {
                FusionSessionRelayPolicy.BootstrapDefault => bootstrap.ForcePhotonRelay,
                FusionSessionRelayPolicy.ForcePhotonRelay => true,
                FusionSessionRelayPolicy.AllowNatPunchthrough => false,
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        private static string GetOperationTitle(FusionSessionStartOperation operation)
        {
            return operation switch
            {
                FusionSessionStartOperation.StartHost => "Start Host",
                FusionSessionStartOperation.JoinHost => "Join Host",
                FusionSessionStartOperation.CreateShared => "Create Shared",
                FusionSessionStartOperation.JoinShared => "Join Shared",
                _ => operation.ToString()
            };
        }
    }
}
