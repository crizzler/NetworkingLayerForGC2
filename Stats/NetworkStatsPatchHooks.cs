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
            return
                HasPublicStaticField(
                    typeof(RuntimeStatData),
                    "NetworkBaseValidator",
                    typeof(Func<RuntimeStatData, double, bool>)) &&
                HasPublicStaticField(
                    typeof(RuntimeStatData),
                    "NetworkAddModifierValidator",
                    typeof(Func<RuntimeStatData, ModifierType, double, bool>)) &&
                HasPublicStaticField(
                    typeof(RuntimeStatData),
                    "NetworkRemoveModifierValidator",
                    typeof(Func<RuntimeStatData, ModifierType, double, bool>)) &&
                HasPublicStaticField(
                    typeof(RuntimeStatData),
                    "NetworkClearModifiersValidator",
                    typeof(Func<RuntimeStatData, bool>)) &&
                HasPublicStaticField(
                    typeof(RuntimeAttributeData),
                    "NetworkValueValidator",
                    typeof(Func<RuntimeAttributeData, double, bool>)) &&
                HasPublicStaticProperty(typeof(RuntimeStatData), "IsNetworkingActive", typeof(bool)) &&
                HasPublicStaticProperty(typeof(RuntimeAttributeData), "IsNetworkingActive", typeof(bool)) &&
                HasInstanceMethod(typeof(RuntimeStatData), "SetBaseDirect", typeof(double)) &&
                HasInstanceMethod(typeof(RuntimeStatData), "AddModifierDirect", typeof(ModifierType), typeof(double)) &&
                HasInstanceMethod(typeof(RuntimeStatData), "RemoveModifierDirect", typeof(ModifierType), typeof(double)) &&
                HasInstanceMethod(typeof(RuntimeStatData), "ClearModifiersDirect") &&
                HasInstanceMethod(typeof(RuntimeAttributeData), "SetValueDirect", typeof(double));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsStatsPatched())
            {
                UnityEngine.Debug.LogWarning("[NetworkStatsPatchHooks] Stats runtime patch markers were not detected. Falling back to interception mode.");
                return;
            }

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
            if (field == null)
            {
                UnityEngine.Debug.LogWarning($"[NetworkStatsPatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
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
