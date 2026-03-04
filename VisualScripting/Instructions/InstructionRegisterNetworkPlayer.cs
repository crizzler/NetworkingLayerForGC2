using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// GC2 Instruction that registers a player with GC2's
    /// <see cref="ShortcutPlayer"/> system. This enables GC2 nodes
    /// like "Get Player" to find the local player.
    /// </summary>
    /// <remarks>
    /// Typically called from a trigger that fires after the local player
    /// spawns and <see cref="NetworkCharacter"/> finishes initialization.
    /// </remarks>
    [Title("Register Network Player")]
    [Description("Registers a networked player as GC2's ShortcutPlayer so 'Get Player' nodes work")]

    [Category("Network/Player/Register Network Player")]

    [Parameter("Target", "The local player's GameObject to register")]

    [Keywords("Network", "Player", "Register", "Shortcut", "Local", "GC2")]

    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    public class InstructionRegisterNetworkPlayer : Instruction
    {
        // MEMBERS: -------------------------------------------------------------------------------

        [SerializeField]
        [Tooltip("The local player's GameObject to register with ShortcutPlayer")]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        // PROPERTIES: ----------------------------------------------------------------------------

        public override string Title => "Register Network Player";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override Task Run(Args args)
        {
            GameObject target = m_Target.Get(args);
            if (target == null) target = args.Self;
            if (target == null) return DefaultResult;

            ShortcutPlayer.Change(target);

            return DefaultResult;
        }
    }
}
