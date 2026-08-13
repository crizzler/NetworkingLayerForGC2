using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Network Damage Poise")]
    [Description("Requests the server to apply poise damage to a Network Character")]
    [Category("Network/Core/Combat/Damage Poise")]
    [Parameter("Character", "The initialized Network Character whose poise is damaged")]
    [Parameter("Damage", "The amount of poise damage requested")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Combat", "Poise", "Stagger")]
    [Image(typeof(IconHeartBeat), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionNetworkCoreDamagePoise : TInstructionNetworkCoreRequest
    {
        [SerializeField] private PropertyGetDecimal m_Damage = new PropertyGetDecimal(1f);

        public override string Title => $"Network Damage {m_Character} Poise by {m_Damage}";

        protected override Task Run(Args args)
        {
            float damage = (float)m_Damage.Get(args);

            return SendRequest<NetworkPoiseResponse>(
                args,
                nameof(InstructionNetworkCoreDamagePoise),
                NetworkCoreRequestChannel.Poise,
                NetworkCoreVisualScriptingOperation.DamagePoise,
                (manager, characterNetworkId, callback) =>
                    manager.RequestPoiseDamage(characterNetworkId, damage, callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Reset Poise")]
    [Description("Requests the server to reset a Network Character's poise to its maximum")]
    [Category("Network/Core/Combat/Reset Poise")]
    [Parameter("Character", "The initialized Network Character whose poise is reset")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Combat", "Poise", "Reset", "Recover")]
    [Image(typeof(IconHeartBeat), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkCoreResetPoise : TInstructionNetworkCoreRequest
    {
        public override string Title => $"Network Reset Poise on {m_Character}";

        protected override Task Run(Args args)
        {
            return SendRequest<NetworkPoiseResponse>(
                args,
                nameof(InstructionNetworkCoreResetPoise),
                NetworkCoreRequestChannel.Poise,
                NetworkCoreVisualScriptingOperation.ResetPoise,
                (manager, characterNetworkId, callback) =>
                    manager.RequestPoiseReset(characterNetworkId, callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }
}
