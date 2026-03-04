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
            return typeof(TBagContent).GetField("NetworkAddValidator", BindingFlags.Public | BindingFlags.Static) != null &&
                   typeof(BagWealth).GetField("NetworkAddValidator", BindingFlags.Public | BindingFlags.Static) != null;
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsInventoryPatched()) return;

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
            field?.SetValue(null, value);
        }
    }
}
#endif
