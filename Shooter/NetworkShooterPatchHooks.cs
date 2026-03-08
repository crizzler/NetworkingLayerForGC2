#if GC2_SHOOTER
using System;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Shooter;

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// Runtime installer for Shooter patch delegates. Enables patched-mode validation on server.
    /// </summary>
    public class NetworkShooterPatchHooks : NetworkSingleton<NetworkShooterPatchHooks>
    {
        private bool m_IsServer;
        private bool m_Installed;

        public bool IsPatchActive => m_Installed && IsShooterPatched();

        public void Initialize(bool isServer)
        {
            m_IsServer = isServer;
            if (m_IsServer) InstallHooks();
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
                HasPublicStaticProperty(typeof(ShooterStance), "IsNetworkingActive", typeof(bool)) &&
                HasPublicStaticProperty(typeof(WeaponData), "IsNetworkingActive", typeof(bool)) &&
                HasInstanceMethod(typeof(ShooterStance), "PullTriggerDirect", typeof(ShooterWeapon)) &&
                HasInstanceMethod(typeof(ShooterStance), "ReleaseTriggerDirect", typeof(ShooterWeapon)) &&
                HasInstanceMethod(typeof(ShooterStance), "ReloadDirect", typeof(ShooterWeapon)) &&
                HasInstanceMethod(typeof(WeaponData), "ShootDirect");
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsShooterPatched())
            {
                Debug.LogWarning("[NetworkShooterPatchHooks] Shooter runtime patch markers were not detected. Falling back to interception mode.");
                return;
            }

            SetStaticField(typeof(ShooterStance), "NetworkPullTriggerValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidatePullTrigger));
            SetStaticField(typeof(ShooterStance), "NetworkReleaseTriggerValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidateReleaseTrigger));
            SetStaticField(typeof(ShooterStance), "NetworkReloadValidator", new Func<ShooterStance, ShooterWeapon, bool>(ValidateReload));
            SetStaticField(typeof(WeaponData), "NetworkShootValidator", new Func<WeaponData, bool>(ValidateShoot));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(ShooterStance), "NetworkPullTriggerValidator", null);
            SetStaticField(typeof(ShooterStance), "NetworkReleaseTriggerValidator", null);
            SetStaticField(typeof(ShooterStance), "NetworkReloadValidator", null);
            SetStaticField(typeof(WeaponData), "NetworkShootValidator", null);

            m_Installed = false;
        }

        private bool ValidatePullTrigger(ShooterStance _, ShooterWeapon __) => m_IsServer;
        private bool ValidateReleaseTrigger(ShooterStance _, ShooterWeapon __) => m_IsServer;
        private bool ValidateReload(ShooterStance _, ShooterWeapon __) => m_IsServer;
        private bool ValidateShoot(WeaponData _) => m_IsServer;

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
