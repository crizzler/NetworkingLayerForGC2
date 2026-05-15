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
        [SerializeField] private bool m_LogDiagnostics = true;

        public bool IsPatchActive =>
            (m_FullShooterHooksInstalled && IsShooterPatched()) ||
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
                    "NetworkHitDetected",
                    typeof(Action<ShotData, Args>)) &&
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
                    "will stay local unless a transport bridge or patched Shooter runtime forwards them.");
            }

            if (!m_FullShooterHooksInstalled && fullShooterPatched)
            {
                SetStaticField(typeof(ShooterStance), "NetworkPullTriggerValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidatePullTrigger));
                SetStaticField(typeof(ShooterStance), "NetworkReleaseTriggerValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidateReleaseTrigger));
                SetStaticField(typeof(ShooterStance), "NetworkReloadValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidateReload));
                SetStaticField(typeof(WeaponData), "NetworkShootValidator", new Func<WeaponData, bool>(ValidateShoot));
                SetStaticField(typeof(WeaponData), "NetworkShotFired", new Action<WeaponData, Vector3, Vector3, float>(NotifyShotFired));
                SetStaticField(typeof(ShooterWeapon), "NetworkHitDetected", new Action<ShotData, Args>(NotifyHitDetected));
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
                SetStaticField(typeof(ShooterWeapon), "NetworkHitDetected", null);
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

        private void NotifyHitDetected(ShotData data, Args _)
        {
            if (data.Source == null || data.Target == null)
            {
                LogDiagnosticsWarning(
                    $"hit hook skipped source={(data.Source != null ? data.Source.name : "null")} " +
                    $"target={(data.Target != null ? data.Target.name : "null")} " +
                    $"weapon={(data.Weapon != null ? data.Weapon.name : "null")}");
                return;
            }

            var controller = data.Source.GetComponent<NetworkShooterController>();
            LogDiagnostics(
                $"hit hook source={data.Source.name} target={data.Target.name} " +
                $"weapon={(data.Weapon != null ? data.Weapon.name : "null")} " +
                $"hash={(data.Weapon != null ? data.Weapon.Id.Hash : 0)} controller={(controller != null)} " +
                $"point={data.HitPoint} distance={data.Distance:F2}");

            if (controller == null)
            {
                LogDiagnosticsWarning($"hit hook could not find NetworkShooterController on {data.Source.name}");
                return;
            }

            controller.NotifyHitDetected(data);
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
