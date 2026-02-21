#if GC2_MELEE
using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// GC2 Condition that intercepts melee hits for network processing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add this condition to a Skill's "Can Hit" conditions list.
    /// When evaluated, it intercepts the hit and routes it through the NetworkMeleeController.
    /// </para>
    /// <para>
    /// <b>Returns:</b>
    /// - Server: Always true (server processes hits directly)
    /// - Local Client: True if optimistic effects enabled, false otherwise
    /// - Remote Client: Always false (receives broadcasts instead)
    /// </para>
    /// </remarks>
    [Title("Network Melee Hit")]
    [Description("Intercepts melee hit and routes through server-authoritative network")]

    [Category("Network/Melee/Network Melee Hit")]
    
    [Parameter("Apply Optimistic Effects", "If true, local client sees hit effects immediately before server confirmation")]

    [Keywords("Network", "Melee", "Combat", "Hit", "Server", "Authoritative", "Netcode")]
    
    [Image(typeof(IconMeleeSword), ColorTheme.Type.Blue)]
    [Serializable]
    public class ConditionNetworkMeleeHit : Condition
    {
        // MEMBERS: -------------------------------------------------------------------------------
        
        [SerializeField] 
        [Tooltip("If true, play hit effects locally before server confirms")]
        private bool m_OptimisticEffects = true;
        
        // PROPERTIES: ----------------------------------------------------------------------------
        
        protected override string Summary => m_OptimisticEffects 
            ? "Network hit (optimistic)" 
            : "Network hit (wait for server)";
        
        // RUN METHOD: ----------------------------------------------------------------------------

        protected override bool Run(Args args)
        {
            // args.Self is the attacker, args.Target is the hit target
            if (args.Self == null || args.Target == null) return false;
            
            // Get the attacker's NetworkMeleeController
            var attackerController = args.Self.Get<NetworkMeleeController>();
            if (attackerController == null)
            {
                // No network controller - allow normal GC2 processing
                return true;
            }
            
            // Get hit position and direction (approximate from target position)
            // Note: The actual hit point comes from the Striker, but we don't have access here
            // We use the target's position as a fallback
            Vector3 hitPoint = args.Target.transform.position;
            Vector3 direction = (args.Target.transform.position - args.Self.transform.position).normalized;
            
            // Get the current skill being used (via reflection or cached reference)
            Skill currentSkill = GetCurrentSkill(args.Self.Get<Character>());
            
            // Intercept the hit
            bool shouldProcessLocally = attackerController.InterceptHit(
                args.Target,
                hitPoint,
                direction,
                currentSkill
            );
            
            return shouldProcessLocally;
        }
        
        // HELPER METHODS: ------------------------------------------------------------------------
        
        private Skill GetCurrentSkill(Character character)
        {
            if (character == null) return null;
            
            // Try to get the current skill from MeleeStance
            var meleeStance = character.Combat.RequestStance<MeleeStance>();
            if (meleeStance == null) return null;
            
            // Use reflection to access the private Attacks field
            var attacksField = typeof(MeleeStance).GetField("m_Attacks", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (attacksField == null) return null;
            
            var attacks = attacksField.GetValue(meleeStance) as Attacks;
            return attacks?.ComboSkill;
        }
    }
}
#endif
