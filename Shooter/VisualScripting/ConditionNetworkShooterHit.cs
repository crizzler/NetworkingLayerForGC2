#if GC2_SHOOTER
using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using GameCreator.Runtime.Shooter;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// GC2 Condition that intercepts shooter hits for network processing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add this condition to a ShooterWeapon's "Can Hit" conditions list.
    /// When evaluated, it intercepts the hit and routes it through the NetworkShooterController.
    /// </para>
    /// <para>
    /// <b>Returns:</b>
    /// - Server: Always true (server processes hits directly)
    /// - Local Client: True if optimistic effects enabled, false otherwise
    /// - Remote Client: Always false (receives broadcasts instead)
    /// </para>
    /// </remarks>
    [Title("Network Shooter Hit")]
    [Description("Intercepts shooter hit and routes through server-authoritative network")]

    [Category("Network/Shooter/Network Shooter Hit")]
    
    [Parameter("Apply Optimistic Effects", "If true, local client sees hit effects immediately before server confirmation")]

    [Keywords("Network", "Shooter", "Combat", "Hit", "Server", "Authoritative", "Netcode", "Projectile", "Raycast")]
    
    [Image(typeof(IconBullsEye), ColorTheme.Type.Blue)]
    [Serializable]
    public class ConditionNetworkShooterHit : Condition
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
            // args.Self is the shooter, args.Target is the hit target
            if (args.Self == null) return false;
            
            // Get the shooter's NetworkShooterController
            var shooterController = args.Self.Get<NetworkShooterController>();
            if (shooterController == null)
            {
                // No network controller - allow normal GC2 processing
                return true;
            }
            
            // If no target, this is an environment hit - allow for effects
            if (args.Target == null)
            {
                return shooterController.IsServer || m_OptimisticEffects;
            }
            
            // Get hit data from ShooterWeapon.LastShotData if available
            Vector3 hitPoint = args.Target.transform.position;
            Vector3 hitNormal = Vector3.up;
            float distance = Vector3.Distance(args.Self.transform.position, hitPoint);
            
            // Try to get more accurate data from ShotData
            ShooterWeapon weapon = GetCurrentWeapon(args.Self.Get<Character>());
            if (weapon != null && weapon.LastShotData != null)
            {
                hitPoint = weapon.LastShotData.HitPoint;
                distance = weapon.LastShotData.Distance;
            }
            
            // Intercept the hit
            bool shouldProcessLocally = shooterController.InterceptHit(
                args.Target,
                hitPoint,
                hitNormal,
                distance,
                weapon,
                0 // Pierce index
            );
            
            return shouldProcessLocally;
        }
        
        // HELPER METHODS: ------------------------------------------------------------------------
        
        private ShooterWeapon GetCurrentWeapon(Character character)
        {
            if (character == null) return null;
            
            var shooterStance = character.Combat.RequestStance<ShooterStance>();
            if (shooterStance == null) return null;
            
            // Try to find equipped shooter weapon
            foreach (var slot in character.Combat.Slots)
            {
                if (slot.Weapon is ShooterWeapon shooterWeapon)
                {
                    return shooterWeapon;
                }
            }
            
            return null;
        }
    }
}
#endif
