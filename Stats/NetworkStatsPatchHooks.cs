#if GC2_STATS
using GameCreator.Runtime.Stats;
using System;
using System.Reflection;

namespace Arawn.GameCreator2.Networking.Stats
{
    /// <summary>
    /// Runtime installer for Stats patch delegates. Enables patched-mode validation on server.
    /// </summary>
    public class NetworkStatsPatchHooks : NetworkSingleton<NetworkStatsPatchHooks>
    {
        private bool m_IsServer;
        private bool m_Installed;

        public bool IsPatchActive => m_Installed && IsStatsPatched();

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

        public static bool IsStatsPatched()
        {
            return typeof(RuntimeStatData).GetField("NetworkBaseValidator", BindingFlags.Public | BindingFlags.Static) != null &&
                   typeof(RuntimeAttributeData).GetField("NetworkValueValidator", BindingFlags.Public | BindingFlags.Static) != null;
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsStatsPatched()) return;

            SetStaticField(typeof(RuntimeStatData), "NetworkBaseValidator", new Func<RuntimeStatData, double, bool>(ValidateBase));
            SetStaticField(typeof(RuntimeStatData), "NetworkAddModifierValidator", new Func<RuntimeStatData, ModifierType, double, bool>(ValidateAddModifier));
            SetStaticField(typeof(RuntimeStatData), "NetworkRemoveModifierValidator", new Func<RuntimeStatData, ModifierType, double, bool>(ValidateRemoveModifier));
            SetStaticField(typeof(RuntimeStatData), "NetworkClearModifiersValidator", new Func<RuntimeStatData, bool>(ValidateClearModifiers));
            SetStaticField(typeof(RuntimeAttributeData), "NetworkValueValidator", new Func<RuntimeAttributeData, double, bool>(ValidateAttributeValue));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(RuntimeStatData), "NetworkBaseValidator", null);
            SetStaticField(typeof(RuntimeStatData), "NetworkAddModifierValidator", null);
            SetStaticField(typeof(RuntimeStatData), "NetworkRemoveModifierValidator", null);
            SetStaticField(typeof(RuntimeStatData), "NetworkClearModifiersValidator", null);
            SetStaticField(typeof(RuntimeAttributeData), "NetworkValueValidator", null);

            m_Installed = false;
        }

        private bool ValidateBase(RuntimeStatData _, double __) => m_IsServer;
        private bool ValidateAddModifier(RuntimeStatData _, ModifierType __, double ___) => m_IsServer;
        private bool ValidateRemoveModifier(RuntimeStatData _, ModifierType __, double ___) => m_IsServer;
        private bool ValidateClearModifiers(RuntimeStatData _) => m_IsServer;
        private bool ValidateAttributeValue(RuntimeAttributeData _, double __) => m_IsServer;

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            field?.SetValue(null, value);
        }
    }
}
#endif
