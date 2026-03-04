#if GC2_SHOOTER
using System;
using System.Reflection;
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
            return typeof(ShooterStance).GetField("NetworkPullTriggerValidator", BindingFlags.Public | BindingFlags.Static) != null &&
                   typeof(WeaponData).GetField("NetworkShootValidator", BindingFlags.Public | BindingFlags.Static) != null;
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsShooterPatched()) return;

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
            field?.SetValue(null, value);
        }
    }
}
#endif
