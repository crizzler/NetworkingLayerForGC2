#if GC2_INVENTORY
using System;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Runtime installer for Inventory patch delegates. Enables patched-mode validation on server.
    /// </summary>
    public class NetworkInventoryPatchHooks : NetworkSingleton<NetworkInventoryPatchHooks>
    {
        private bool m_IsServer;
        private bool m_Installed;

        public bool IsPatchActive => m_Installed && IsInventoryPatched();

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

        public static bool IsInventoryPatched()
        {
            return
                HasPublicStaticField(
                    typeof(TBagContent),
                    "NetworkAddValidator",
                    typeof(Func<TBagContent, RuntimeItem, Vector2Int, bool, bool>)) &&
                HasPublicStaticField(
                    typeof(TBagContent),
                    "NetworkRemoveValidator",
                    typeof(Func<TBagContent, RuntimeItem, bool>)) &&
                HasPublicStaticField(
                    typeof(TBagContent),
                    "NetworkMoveValidator",
                    typeof(Func<TBagContent, Vector2Int, Vector2Int, bool, bool>)) &&
                HasPublicStaticField(
                    typeof(TBagContent),
                    "NetworkDropValidator",
                    typeof(Func<TBagContent, RuntimeItem, Vector3, bool>)) &&
                HasPublicStaticField(
                    typeof(TBagContent),
                    "NetworkUseValidator",
                    typeof(Func<TBagContent, RuntimeItem, bool>)) &&
                HasPublicStaticField(
                    typeof(BagWealth),
                    "NetworkAddValidator",
                    typeof(Func<BagWealth, IdString, int, bool>)) &&
                HasPublicStaticField(
                    typeof(BagWealth),
                    "NetworkSetValidator",
                    typeof(Func<BagWealth, IdString, int, bool>)) &&
                HasPublicStaticProperty(typeof(TBagContent), "IsNetworkingActive", typeof(bool)) &&
                HasPublicStaticProperty(typeof(BagWealth), "IsNetworkingActive", typeof(bool)) &&
                HasInstanceMethod(typeof(TBagContent), "UseDirect", typeof(RuntimeItem)) &&
                HasInstanceMethod(typeof(TBagContent), "DropDirect", typeof(RuntimeItem), typeof(Vector3)) &&
                HasInstanceMethod(typeof(BagWealth), "SetDirect", typeof(IdString), typeof(int)) &&
                HasInstanceMethod(typeof(BagWealth), "AddDirect", typeof(IdString), typeof(int));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsInventoryPatched())
            {
                Debug.LogWarning("[NetworkInventoryPatchHooks] Inventory runtime patch markers were not detected. Falling back to interception mode.");
                return;
            }

            SetStaticField(typeof(TBagContent), "NetworkAddValidator", new Func<TBagContent, RuntimeItem, Vector2Int, bool, bool>(ValidateAdd));
            SetStaticField(typeof(TBagContent), "NetworkRemoveValidator", new Func<TBagContent, RuntimeItem, bool>(ValidateRemove));
            SetStaticField(typeof(TBagContent), "NetworkMoveValidator", new Func<TBagContent, Vector2Int, Vector2Int, bool, bool>(ValidateMove));
            SetStaticField(typeof(TBagContent), "NetworkDropValidator", new Func<TBagContent, RuntimeItem, Vector3, bool>(ValidateDrop));
            SetStaticField(typeof(TBagContent), "NetworkUseValidator", new Func<TBagContent, RuntimeItem, bool>(ValidateUse));

            SetStaticField(typeof(BagWealth), "NetworkAddValidator", new Func<BagWealth, IdString, int, bool>(ValidateWealthAdd));
            SetStaticField(typeof(BagWealth), "NetworkSetValidator", new Func<BagWealth, IdString, int, bool>(ValidateWealthSet));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(TBagContent), "NetworkAddValidator", null);
            SetStaticField(typeof(TBagContent), "NetworkRemoveValidator", null);
            SetStaticField(typeof(TBagContent), "NetworkMoveValidator", null);
            SetStaticField(typeof(TBagContent), "NetworkDropValidator", null);
            SetStaticField(typeof(TBagContent), "NetworkUseValidator", null);

            SetStaticField(typeof(BagWealth), "NetworkAddValidator", null);
            SetStaticField(typeof(BagWealth), "NetworkSetValidator", null);

            m_Installed = false;
        }

        private bool ValidateAdd(TBagContent _, RuntimeItem __, Vector2Int ___, bool ____) => m_IsServer;
        private bool ValidateRemove(TBagContent _, RuntimeItem __) => m_IsServer;
        private bool ValidateMove(TBagContent _, Vector2Int __, Vector2Int ___, bool ____) => m_IsServer;
        private bool ValidateDrop(TBagContent _, RuntimeItem __, Vector3 ___) => m_IsServer;
        private bool ValidateUse(TBagContent _, RuntimeItem __) => m_IsServer;
        private bool ValidateWealthAdd(BagWealth _, IdString __, int ___) => m_IsServer;
        private bool ValidateWealthSet(BagWealth _, IdString __, int ___) => m_IsServer;

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            if (field == null)
            {
                Debug.LogWarning($"[NetworkInventoryPatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
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
