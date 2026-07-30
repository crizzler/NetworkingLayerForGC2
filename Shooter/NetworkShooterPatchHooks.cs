#if GC2_SHOOTER
using System;
using System.Reflection;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;
using GameCreator.Runtime.Shooter;

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// Runtime installer for Shooter patch delegates. Enables patched-mode validation on server.
    /// </summary>
    public class NetworkShooterPatchHooks : NetworkSingleton<NetworkShooterPatchHooks>
    {
        private bool m_Installed;
        private bool m_FullShooterHooksInstalled;
        private bool m_SightHookInstalled;
        private bool m_WarnedMissingFullShooterPatch;
        private bool m_WarnedMissingSightPatch;
        private float m_NextAimResolverDiagnosticTime;
        private float m_NextHitRoutingInvariantTime;
        [SerializeField] private bool m_LogDiagnostics;

        public bool IsHitValidatorActive =>
            m_FullShooterHooksInstalled && IsShooterPatched();

        public bool IsPatchActive =>
            IsHitValidatorActive ||
            (m_SightHookInstalled && IsSightInstructionsPatched());

        private void LogDiagnostics(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.Log($"[NetworkShooterPatchHooks] {message}", this);
        }

        private void LogDiagnosticsWarning(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.LogWarning($"[NetworkShooterPatchHooks] {message}", this);
        }

        private bool ShouldLogDiagnostic(ref float nextTime, float interval = 1f)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return false;

            float now = Time.unscaledTime;
            if (now < nextTime) return false;

            nextTime = now + Mathf.Max(0.1f, interval);
            return true;
        }

        public void Initialize(bool isNetworkingActive)
        {
            LogDiagnostics(
                $"initialize networkingActive={isNetworkingActive} installed={m_Installed} " +
                $"shooterPatched={IsShooterPatched()} sightPatched={IsSightInstructionsPatched()}");

            if (isNetworkingActive) InstallHooks();
            else UninstallHooks();
        }

        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }

        public static bool IsShooterPatched()
        {
            return
                HasPublicStaticField(
                    typeof(ShooterStance),
                    "NetworkPullTriggerValidator",
                    typeof(Func<ShooterStance, ShooterWeapon, bool>)) &&
                HasPublicStaticField(
                    typeof(ShooterStance),
                    "NetworkReleaseTriggerValidator",
                    typeof(Func<ShooterStance, ShooterWeapon, bool>)) &&
                HasPublicStaticField(
                    typeof(ShooterStance),
                    "NetworkReloadValidator",
                    typeof(Func<ShooterStance, ShooterWeapon, bool>)) &&
                HasPublicStaticField(
                    typeof(WeaponData),
                    "NetworkShootValidator",
                    typeof(Func<WeaponData, bool>)) &&
                HasPublicStaticField(
                    typeof(WeaponData),
                    "NetworkShotFired",
                    typeof(Action<WeaponData, Vector3, Vector3, float>)) &&
                HasPublicStaticField(
                    typeof(ShooterWeapon),
                    "NetworkOnHitValidator",
                    typeof(Func<ShotData, Args, bool>)) &&
                HasPublicStaticField(
                    typeof(ShooterWeapon),
                    "NetworkShotFiredExact",
                    typeof(Action<ShotData>)) &&
                HasPublicStaticField(
                    typeof(ShooterWeapon),
                    "NetworkOnHitResolved",
                    typeof(Action<ShotData, Args, BlockType, ReactionOutput, float>)) &&
                HasPublicStaticProperty(typeof(ShooterStance), "IsNetworkingActive", typeof(bool)) &&
                HasPublicStaticProperty(typeof(WeaponData), "IsNetworkingActive", typeof(bool)) &&
                HasInstanceMethod(typeof(ShooterStance), "PullTriggerDirect", typeof(ShooterWeapon)) &&
                HasInstanceMethod(typeof(ShooterStance), "ReleaseTriggerDirect", typeof(ShooterWeapon)) &&
                HasInstanceMethod(typeof(ShooterStance), "ReloadDirect", typeof(ShooterWeapon)) &&
                HasInstanceMethod(typeof(WeaponData), "ShootDirect");
        }

        public static bool IsSightInstructionsPatched()
        {
            return HasPublicStaticField(
                typeof(Sight),
                "NetworkSightInstructionsValidator",
                typeof(Func<Character, bool>));
        }

        private void InstallHooks()
        {
            bool fullShooterPatched = IsShooterPatched();
            bool sightPatched = IsSightInstructionsPatched();
            bool installedAny = false;

            if (!m_FullShooterHooksInstalled && !fullShooterPatched && !m_WarnedMissingFullShooterPatch)
            {
                m_WarnedMissingFullShooterPatch = true;
                Debug.LogWarning(
                    "[NetworkShooterPatchHooks] Shooter runtime patch markers were not detected. " +
                    "NetworkShooterController.InterceptShot will only run if Shooter assets explicitly call " +
                    "the network interception path; vanilla GC2 Shooter fire/reload animations and particles " +
                    "will stay local unless a transport bridge or patched Shooter runtime forwards them. " +
                    "The legacy NetworkHitDetected-only patch is unsafe because it cannot cancel client gameplay; " +
                    "reapply the required Shooter patch in the setup wizard.");
            }

            if (!m_FullShooterHooksInstalled && fullShooterPatched)
            {
                SetStaticField(typeof(ShooterStance), "NetworkPullTriggerValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidatePullTrigger));
                SetStaticField(typeof(ShooterStance), "NetworkReleaseTriggerValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidateReleaseTrigger));
                SetStaticField(typeof(ShooterStance), "NetworkReloadValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidateReload));
                SetStaticField(typeof(WeaponData), "NetworkShootValidator", new Func<WeaponData, bool>(ValidateShoot));
                // The legacy pre-projectile callback does not include GC2's final spread.
                // Leave it unwired and use ShooterWeapon.OnShoot's exact ShotData instead.
                SetStaticField(typeof(WeaponData), "NetworkShotFired", null);
                SetStaticField(typeof(ShooterWeapon), "NetworkShotFiredExact", new Action<ShotData>(NotifyShotFiredExact));
                SetStaticField(typeof(ShooterWeapon), "NetworkOnHitValidator", new Func<ShotData, Args, bool>(ValidateHit));
                SetStaticField(
                    typeof(ShooterWeapon),
                    "NetworkOnHitResolved",
                    new Action<ShotData, Args, BlockType, ReactionOutput, float>(NotifyHitResolved));
                if (HasPublicStaticField(typeof(Aim), "NetworkAimPointResolver", typeof(Func<Args, Aim, Vector3?>)))
                {
                    SetStaticField(typeof(Aim), "NetworkAimPointResolver", new Func<Args, Aim, Vector3?>(ResolveAimPoint));
                }
                else
                {
                    LogDiagnosticsWarning(
                        "Shooter Aim.NetworkAimPointResolver hook was not detected. " +
                        "Shot and hit hooks are active, but remote shooter aim will use local GC2 aim rules.");
                }

                m_FullShooterHooksInstalled = true;
                installedAny = true;
            }

            if (!m_SightHookInstalled && sightPatched)
            {
                SetStaticField(typeof(Sight), "NetworkSightInstructionsValidator", new Func<Character, bool>(AllowSightInstructions));
                m_SightHookInstalled = true;
                installedAny = true;
            }
            else if (!m_SightHookInstalled && !m_WarnedMissingSightPatch)
            {
                m_WarnedMissingSightPatch = true;
                LogDiagnosticsWarning(
                    "Shooter Sight.NetworkSightInstructionsValidator hook was not detected. " +
                    "Remote shooter sight transitions may execute local-only Sight OnEnter/OnExit instructions. " +
                    "Run the setup wizard or Game Creator > Networking Layer > Patches > Shooter Sight > Patch.");
            }

            m_Installed = m_FullShooterHooksInstalled || m_SightHookInstalled;
            if (installedAny)
            {
                LogDiagnostics(
                    $"installed Shooter runtime delegates full={m_FullShooterHooksInstalled} sight={m_SightHookInstalled}");
            }
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            if (m_FullShooterHooksInstalled)
            {
                SetStaticField(typeof(ShooterStance), "NetworkPullTriggerValidator", null);
                SetStaticField(typeof(ShooterStance), "NetworkReleaseTriggerValidator", null);
                SetStaticField(typeof(ShooterStance), "NetworkReloadValidator", null);
                SetStaticField(typeof(WeaponData), "NetworkShootValidator", null);
                SetStaticField(typeof(WeaponData), "NetworkShotFired", null);
                SetStaticField(typeof(ShooterWeapon), "NetworkShotFiredExact", null);
                SetStaticField(typeof(ShooterWeapon), "NetworkOnHitValidator", null);
                SetStaticField(typeof(ShooterWeapon), "NetworkOnHitResolved", null);
                if (HasPublicStaticField(typeof(Aim), "NetworkAimPointResolver", typeof(Func<Args, Aim, Vector3?>)))
                {
                    SetStaticField(typeof(Aim), "NetworkAimPointResolver", null);
                }
            }

            if (m_SightHookInstalled)
            {
                SetStaticField(typeof(Sight), "NetworkSightInstructionsValidator", null);
            }

            m_FullShooterHooksInstalled = false;
            m_SightHookInstalled = false;
            m_WarnedMissingFullShooterPatch = false;
            m_WarnedMissingSightPatch = false;
            m_Installed = false;
            LogDiagnostics("uninstalled Shooter runtime delegates");
        }

        private bool ValidatePullTrigger(ShooterStance _, ShooterWeapon __) => true;
        private bool ValidateReleaseTrigger(ShooterStance _, ShooterWeapon __) => true;
        private bool ValidateReload(ShooterStance stance, ShooterWeapon weapon)
        {
            if (stance?.Character == null || weapon == null) return true;

            NetworkShooterController controller = stance.Character.GetComponent<NetworkShooterController>();
            if (controller == null) return true;

            return controller.IsLocalClient
                ? controller.RequestReload()
                : true;
        }
        private bool ValidateShoot(WeaponData _) => true;

        private bool AllowSightInstructions(Character character)
        {
            if (character == null) return true;

            NetworkShooterController controller = character.GetComponent<NetworkShooterController>();
            return controller == null || controller.IsLocalClient;
        }

        private Vector3? ResolveAimPoint(Args args, Aim _)
        {
            Character character = args.Self != null ? args.Self.Get<Character>() : null;
            NetworkShooterController controller = character != null
                ? character.GetComponent<NetworkShooterController>()
                : null;

            if (controller != null && controller.TryGetNetworkAimPoint(out Vector3 point))
            {
                if (ShouldLogDiagnostic(ref m_NextAimResolverDiagnosticTime))
                {
                    LogDiagnostics($"aim resolver using network point character={character.name} point={point}");
                }

                return point;
            }

            if (ShouldLogDiagnostic(ref m_NextAimResolverDiagnosticTime))
            {
                LogDiagnostics(
                    $"aim resolver had no network point character={(character != null ? character.name : "null")} " +
                    $"controller={(controller != null)}");
            }

            return null;
        }

        private void NotifyShotFired(WeaponData data, Vector3 muzzlePosition, Vector3 shotDirection, float chargeRatio)
        {
            if (data?.Character == null)
            {
                LogDiagnosticsWarning(
                    $"shot hook skipped because WeaponData/Character is missing weapon={(data?.Weapon != null ? data.Weapon.name : "null")}");
                return;
            }

            var controller = data.Character.GetComponent<NetworkShooterController>();
            LogDiagnostics(
                $"shot hook weapon={(data.Weapon != null ? data.Weapon.name : "null")} " +
                $"hash={(data.Weapon != null ? data.Weapon.Id.Hash : 0)} character={data.Character.name} " +
                $"controller={(controller != null)} muzzle={muzzlePosition} dir={shotDirection} charge={chargeRatio:F2}");

            if (controller == null)
            {
                LogDiagnosticsWarning($"shot hook could not find NetworkShooterController on {data.Character.name}");
                return;
            }

            controller.NotifyShotFired(muzzlePosition, shotDirection, data.Weapon, chargeRatio);
        }

        private void NotifyShotFiredExact(ShotData data)
        {
            if (data.Source == null || data.Weapon == null)
            {
                NetworkShooterDebug.LogPhysics(
                    "ExactShotHook",
                    $"dropped source={(data.Source != null)} weapon={(data.Weapon != null)}");
                return;
            }

            NetworkShooterController controller =
                data.Source.GetComponent<NetworkShooterController>();
            NetworkShooterDebug.LogPhysics(
                "ExactShotHook",
                $"source={data.Source.name} controller={(controller != null)} " +
                $"weapon={data.Weapon.name} hash={data.Weapon.Id.Hash} " +
                $"origin={data.ShootPosition} exactDirection={data.ShootDirection} " +
                $"charge={data.ChargeRatio:F2}",
                data.Source);

            if (controller == null)
            {
                LogDiagnosticsWarning(
                    $"exact shot hook could not find NetworkShooterController on {data.Source.name}");
                return;
            }

            controller.NotifyShotFired(
                data.ShootPosition,
                data.ShootDirection,
                data.Weapon,
                data.ChargeRatio);
        }

        private bool ValidateHit(ShotData data, Args _)
        {
            if (data.Source == null)
            {
                LogDiagnosticsWarning(
                    $"hit hook skipped source=null " +
                    $"target={(data.Target != null ? data.Target.name : "null")} " +
                    $"weapon={(data.Weapon != null ? data.Weapon.name : "null")}");
                return true;
            }

            var controller = data.Source.GetComponent<NetworkShooterController>();
            Collider directCollider = data.Target != null
                ? data.Target.GetComponent<Collider>()
                : null;
            Rigidbody directRigidbody = data.Target != null
                ? data.Target.GetComponent<Rigidbody>()
                : null;
            Rigidbody resolvedRigidbody = directCollider != null && directCollider.attachedRigidbody != null
                ? directCollider.attachedRigidbody
                : data.Target != null ? data.Target.GetComponentInParent<Rigidbody>() : null;
            NetworkShooterDebug.LogPhysics(
                "NativeHitHook",
                $"source={data.Source.name} controller={(controller != null)} " +
                $"target={(data.Target != null ? data.Target.name : "null")} " +
                $"weapon={(data.Weapon != null ? data.Weapon.name : "null")} " +
                $"hash={(data.Weapon != null ? data.Weapon.Id.Hash : 0)} " +
                $"forceEnabled={(data.Weapon != null && data.Weapon.Fire.ForceEnabled)} " +
                $"force={(data.Weapon != null ? data.Weapon.Fire.Force : 0f):F2} " +
                $"point={data.HitPoint} direction={data.ShootDirection} distance={data.Distance:F2} " +
                $"collider={(directCollider != null ? directCollider.name : "null")} " +
                $"directRb={(directRigidbody != null ? directRigidbody.name : "null")} " +
                $"resolvedRb={(resolvedRigidbody != null ? resolvedRigidbody.name : "null")} " +
                $"kinematic={(resolvedRigidbody != null && resolvedRigidbody.isKinematic)}",
                data.Source);
            LogDiagnostics(
                $"hit hook source={data.Source.name} target={(data.Target != null ? data.Target.name : "null")} " +
                $"weapon={(data.Weapon != null ? data.Weapon.name : "null")} " +
                $"hash={(data.Weapon != null ? data.Weapon.Id.Hash : 0)} controller={(controller != null)} " +
                $"point={data.HitPoint} distance={data.Distance:F2}");

            if (controller == null)
            {
                NetworkCharacter networkCharacter = data.Source.GetComponent<NetworkCharacter>();
                if (networkCharacter != null)
                {
                    LogHitRoutingInvariantWarning(
                        $"suppressed Shooter hit from network character '{data.Source.name}' " +
                        $"(networkId={networkCharacter.NetworkId}) because it has no NetworkShooterController. " +
                        "Add/configure the Shooter controller before gameplay starts.");
                    return false;
                }

                LogDiagnosticsWarning($"hit hook could not find NetworkShooterController on {data.Source.name}");
                return true;
            }

            return controller.ValidatePatchedHit(data);
        }

        private void NotifyHitResolved(
            ShotData data,
            Args _,
            BlockType blockType,
            ReactionOutput reactionOutput,
            float reactionPower)
        {
            if (data.Source == null) return;
            NetworkShooterController controller = data.Source.GetComponent<NetworkShooterController>();
            controller?.NotifyPatchedHitResolved(
                data,
                blockType,
                reactionOutput,
                reactionPower);
        }

        private void LogHitRoutingInvariantWarning(string message)
        {
            const float WarningInterval = 5f;
            float now = Time.unscaledTime;
            if (now < m_NextHitRoutingInvariantTime) return;

            m_NextHitRoutingInvariantTime = now + WarningInterval;
            Debug.LogWarning($"[NetworkShooterPatchHooks] {message}", this);
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field == null)
            {
                Debug.LogWarning($"[NetworkShooterPatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
                return;
            }

            field.SetValue(null, value);
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }

        private static bool HasPublicStaticProperty(Type type, string propertyName, Type expectedPropertyType)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            return property != null && expectedPropertyType.IsAssignableFrom(property.PropertyType);
        }

        private static bool HasInstanceMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                parameterTypes,
                null);

            return method != null;
        }
    }
}
#endif
