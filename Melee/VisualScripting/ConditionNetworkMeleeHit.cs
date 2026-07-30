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
    /// Legacy GC2 Condition that intercepts melee hits for network processing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// New projects use the required AttackSkill source patch and do not need to add this
    /// condition. It remains supported so upgraded Skill assets continue to route one request.
    /// </para>
    /// <para>
    /// Networked hits always return false so GC2 cannot execute Skill.OnHit gameplay locally.
    /// Optimistic playback, when enabled, is presentation-only.
    /// </para>
    /// </remarks>
    [Title("Network Melee Hit")]
    [Description("Intercepts melee hit and routes through server-authoritative network")]

    [Category("Network/Melee/Network Melee Hit")]

    [Parameter("Apply Optimistic Effects", "If true, local client sees hit effects immediately before server confirmation")]

    [Keywords("Network", "Melee", "Combat", "Hit", "Server", "Authoritative")]

    [Image(typeof(IconMeleeSword), ColorTheme.Type.Blue)]
    [Serializable]
    public class ConditionNetworkMeleeHit : Condition
    {
        // MEMBERS: -------------------------------------------------------------------------------

        private static float s_NextMissingControllerWarningTime;

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
                NetworkCharacter networkCharacter = args.Self.Get<NetworkCharacter>();
                if (networkCharacter == null)
                {
                    // Genuinely non-networked characters retain native GC2 behavior.
                    return true;
                }

                if (Time.unscaledTime >= s_NextMissingControllerWarningTime)
                {
                    s_NextMissingControllerWarningTime = Time.unscaledTime + 10f;
                    Debug.LogWarning(
                        $"[ConditionNetworkMeleeHit] Suppressed a hit for network character " +
                        $"'{networkCharacter.name}' because NetworkMeleeController is missing or not ready.");
                }

                return false;
            }

            // Get hit position and direction.
            // Note: The actual hit point comes from the Striker, but we don't have access here
            // so we use the target's position as a fallback. The direction must match GC2's
            // target-local ReactionInput direction, otherwise vertical skills such as
            // UpperSlash cannot select FromBottom reactions like Sword@HitToAir.
            Vector3 hitPoint = args.Target.transform.position;

            // Get the current skill being used (via reflection or cached reference)
            Skill currentSkill = GetCurrentSkill(args.Self.Get<Character>());
            Vector3 direction = attackerController.ResolveStrikeDirectionForTarget(
                args.Target,
                currentSkill
            );

            attackerController.OptimisticEffects = m_OptimisticEffects;

            // Intercept the hit
            bool shouldProcessLocally = attackerController.InterceptHit(
                args.Target,
                hitPoint,
                direction,
                currentSkill
            );

            // The controller returns false for every successfully routed networked hit. Keep the
            // local variable for compatibility with projects that subclass the controller.
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
