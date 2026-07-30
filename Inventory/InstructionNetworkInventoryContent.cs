#if GC2_INVENTORY
using System;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    [Version(1, 0, 0)]
    [Title("Network Add Item")]
    [Description("Requests that the server creates and adds an Item to a networked Bag")]
    [Category("Network/Inventory/Bags/Add Item")]
    [Parameter("Item", "The type of item the server is requested to create")]
    [Parameter("Bag", "The networked Bag receiving the item")]
    [Keywords("Bag", "Inventory", "Give", "Server", "Authority")]
    [Image(typeof(IconItem), ColorTheme.Type.Green, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkInventoryAddItem : Instruction
    {
        [SerializeField] private PropertyGetItem m_Item = new PropertyGetItem();
        [SerializeField] private PropertyGetGameObject m_Bag = GetGameObjectPlayer.Create();
        [SerializeField] private bool m_AllowStack = true;
        [SerializeField] private InventoryModificationSource m_Source = InventoryModificationSource.Direct;

        [NonSerialized] private float m_NextWarningTime;

        public override string Title => $"Network Add {this.m_Item} to {this.m_Bag}";

        protected override Task Run(Args args)
        {
            Item item = this.m_Item.Get(args);
            Bag bag = this.m_Bag.Get<Bag>(args);
            if (item == null || bag == null) return DefaultResult;

            if (!TryGetReadyController(bag, out NetworkInventoryController controller))
            {
                ApplyOfflineFallback(bag, item);
                return DefaultResult;
            }

            controller.RequestAddItem(
                item,
                TBagContent.INVALID,
                m_AllowStack,
                m_Source);

            return DefaultResult;
        }

        private bool TryGetReadyController(Bag bag, out NetworkInventoryController controller)
        {
            controller = bag.GetComponent<NetworkInventoryController>();
            NetworkCharacter networkCharacter = bag.GetComponent<NetworkCharacter>();

            if (networkCharacter == null)
            {
                return controller != null && controller.NetworkId != 0 &&
                       (controller.IsServer || controller.IsLocalClient);
            }

            if (!networkCharacter.IsOwnerInstance) return false;
            if (controller != null && controller.NetworkId != 0 &&
                (controller.IsServer || controller.IsLocalClient))
            {
                return true;
            }

            WarnUnavailableRoute(bag, networkCharacter, controller);
            return false;
        }

        private void ApplyOfflineFallback(Bag bag, Item item)
        {
            if (bag.GetComponent<NetworkCharacter>() != null ||
                bag.GetComponent<NetworkInventoryController>() != null)
            {
                return;
            }

            bag.Content.AddType(item, m_AllowStack);
        }

        private void WarnUnavailableRoute(
            Bag bag,
            NetworkCharacter networkCharacter,
            NetworkInventoryController controller)
        {
            if (UnityEngine.Time.unscaledTime < m_NextWarningTime) return;
            m_NextWarningTime = UnityEngine.Time.unscaledTime + 5f;

            Debug.LogWarning(
                $"[NetworkInventory] Add Item for '{bag.name}' was not sent because its " +
                $"authoritative inventory route is not ready (networkId={networkCharacter.NetworkId}, " +
                $"controller={(controller != null)}, server={controller?.IsServer ?? false}, " +
                $"local={controller?.IsLocalClient ?? false}).",
                bag);
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Remove Item")]
    [Description("Requests that the server removes one matching Item from a networked Bag")]
    [Category("Network/Inventory/Bags/Remove Item")]
    [Parameter("Item", "The parent type of item the server is requested to remove")]
    [Parameter("Bag", "The networked Bag losing the item")]
    [Keywords("Bag", "Inventory", "Remove", "Server", "Authority")]
    [Image(typeof(IconItem), ColorTheme.Type.Green, typeof(OverlayMinus))]
    [Serializable]
    public sealed class InstructionNetworkInventoryRemoveItem : Instruction
    {
        [SerializeField] private PropertyGetItem m_Item = new PropertyGetItem();
        [SerializeField] private PropertyGetGameObject m_Bag = GetGameObjectPlayer.Create();
        [SerializeField] private InventoryModificationSource m_Source = InventoryModificationSource.Direct;

        [NonSerialized] private float m_NextWarningTime;

        public override string Title => $"Network Remove {this.m_Item} from {this.m_Bag}";

        protected override Task Run(Args args)
        {
            Item item = this.m_Item.Get(args);
            Bag bag = this.m_Bag.Get<Bag>(args);
            if (item == null || bag == null) return DefaultResult;

            RuntimeItem runtimeItem = bag.Content.FindRuntimeItem(item);
            if (runtimeItem == null) return DefaultResult;

            NetworkInventoryController controller = bag.GetComponent<NetworkInventoryController>();
            NetworkCharacter networkCharacter = bag.GetComponent<NetworkCharacter>();

            if (networkCharacter == null && controller == null)
            {
                bag.Content.Remove(runtimeItem);
                return DefaultResult;
            }

            if (networkCharacter != null && !networkCharacter.IsOwnerInstance)
            {
                return DefaultResult;
            }

            if (controller == null || controller.NetworkId == 0 ||
                (!controller.IsServer && !controller.IsLocalClient))
            {
                WarnUnavailableRoute(bag, networkCharacter, controller);
                return DefaultResult;
            }

            controller.RequestRemoveItem(runtimeItem, m_Source);
            return DefaultResult;
        }

        private void WarnUnavailableRoute(
            Bag bag,
            NetworkCharacter networkCharacter,
            NetworkInventoryController controller)
        {
            if (UnityEngine.Time.unscaledTime < m_NextWarningTime) return;
            m_NextWarningTime = UnityEngine.Time.unscaledTime + 5f;

            Debug.LogWarning(
                $"[NetworkInventory] Remove Item for '{bag.name}' was not sent because its " +
                $"authoritative inventory route is not ready (networkId={networkCharacter?.NetworkId ?? 0}, " +
                $"controller={(controller != null)}, server={controller?.IsServer ?? false}, " +
                $"local={controller?.IsLocalClient ?? false}).",
                bag);
        }
    }
}
#endif
