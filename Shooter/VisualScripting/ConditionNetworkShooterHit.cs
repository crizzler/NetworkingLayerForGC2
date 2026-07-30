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
    /// Legacy fallback for Shooter assets that have not yet removed their old "Can Hit" network
    /// condition. The required source patch now intercepts every hit before native side effects.
    /// </para>
    /// <para>
    /// <b>Returns:</b>
    /// - Active cancellable validator: true for every role; the later OnHit validator owns routing
    /// - Unpatched local client fallback: false; optimism is presentation-only
    /// - Unpatched remote client fallback: false (receives broadcasts instead)
    /// </para>
    /// </remarks>
    [Title("Network Shooter Hit")]
    [Description("Intercepts shooter hit and routes through server-authoritative network")]

    [Category("Network/Shooter/Network Shooter Hit")]

    [Parameter("Apply Optimistic Effects", "If true, local client sees hit effects immediately before server confirmation")]

    [Keywords("Network", "Shooter", "Combat", "Hit", "Server", "Authoritative", "Projectile", "Raycast")]

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

            // ShooterWeapon.CanHit evaluates this legacy condition before ShooterWeapon.OnHit,
            // which is where the cancellable source hook runs. When the current source patch is
            // present this condition must therefore be a pass-through: trying to queue here would
            // either reject the hit before the validator can run or queue it a second time. The
            // validator owns interception for patched projects; this condition only remains an
            // operational fallback for projects that have not applied that patch yet.
            NetworkShooterPatchHooks patchHooks = NetworkShooterPatchHooks.Instance;
            if (ShouldDeferToCancellablePatch(
                    patchHooks != null && patchHooks.IsHitValidatorActive))
            {
                return true;
            }

            // Get hit data from ShooterWeapon.LastShotData if available
            ShotData shotData = ShooterWeapon.LastShotData;
            GameObject hitTarget = args.Target != null ? args.Target : shotData.Target;
            if (hitTarget == null) return false;

            // The new cancellable source hook already queued this server-native hit. Let the
            // original GC2 call continue exactly once without entering InterceptHit again.
            if (shooterController.TryConsumeServerNativeHitContinuation(hitTarget)) return true;

            Vector3 hitPoint = hitTarget.transform.position;
            Vector3 hitNormal = Vector3.up;
            float distance = Vector3.Distance(args.Self.transform.position, hitPoint);

            // Try to get more accurate data from ShotData
            ShooterWeapon weapon = GetCurrentWeapon(args.Self.Get<Character>());
            if (weapon != null)
            {
                if (shotData.Source != null)
                {
                    hitPoint = shotData.HitPoint;
                    distance = shotData.Distance;
                    if (shotData.ShootDirection.sqrMagnitude > 0.0001f)
                    {
                        hitNormal = -shotData.ShootDirection.normalized;
                    }
                }
            }

            // Intercept the hit
            bool shouldProcessLocally = shooterController.InterceptHit(
                hitTarget,
                hitPoint,
                hitNormal,
                distance,
                weapon,
                0 // Pierce index
            );

            return shouldProcessLocally;
        }

        internal static bool ShouldDeferToCancellablePatch(bool cancellablePatchActive)
        {
            return cancellablePatchActive;
        }

        // HELPER METHODS: ------------------------------------------------------------------------

        private ShooterWeapon GetCurrentWeapon(Character character)
        {
            if (character == null) return null;

            var shooterStance = character.Combat.RequestStance<ShooterStance>();
            if (shooterStance == null) return null;

            // Try to find equipped shooter weapon
            foreach (var equipped in character.Combat.Weapons)
            {
                if (equipped?.Asset is ShooterWeapon shooterWeapon)
                {
                    return shooterWeapon;
                }
            }

            return null;
        }
    }
}
#endif
