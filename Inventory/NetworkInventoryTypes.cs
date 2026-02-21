#if GC2_INVENTORY
using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ENUMS
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Types of inventory content operations.
    /// </summary>
    public enum InventoryContentAction : byte
    {
        Add = 0,
        AddAtPosition = 1,
        Remove = 2,
        RemoveAtPosition = 3,
        Move = 4,
        Use = 5,
        Drop = 6,
        Sort = 7
    }
    
    /// <summary>
    /// Types of equipment operations.
    /// </summary>
    public enum EquipmentAction : byte
    {
        Equip = 0,
        EquipToSlot = 1,
        EquipToIndex = 2,
        Unequip = 3,
        UnequipFromIndex = 4
    }
    
    /// <summary>
    /// Types of socket operations.
    /// </summary>
    public enum SocketAction : byte
    {
        Attach = 0,
        AttachToSocket = 1,
        Detach = 2,
        DetachFromSocket = 3
    }
    
    /// <summary>
    /// Types of wealth operations.
    /// </summary>
    public enum WealthAction : byte
    {
        Set = 0,
        Add = 1,
        Subtract = 2
    }
    
    /// <summary>
    /// Types of merchant operations.
    /// </summary>
    public enum MerchantAction : byte
    {
        BuyFromMerchant = 0,
        SellToMerchant = 1
    }
    
    /// <summary>
    /// Types of crafting operations.
    /// </summary>
    public enum CraftingAction : byte
    {
        Craft = 0,
        Dismantle = 1,
        Combine = 2
    }
    
    /// <summary>
    /// Reasons for inventory operation rejection.
    /// </summary>
    public enum InventoryRejectionReason : byte
    {
        None = 0,
        NotAuthorized = 1,
        BagNotFound = 2,
        ItemNotFound = 3,
        RuntimeItemNotFound = 4,
        InsufficientSpace = 5,
        InvalidPosition = 6,
        CannotStack = 7,
        ItemEquipped = 8,
        CannotEquip = 9,
        CannotUnequip = 10,
        InsufficientFunds = 11,
        MerchantNotFound = 12,
        CannotBuy = 13,
        CannotSell = 14,
        InsufficientIngredients = 15,
        CannotCraft = 16,
        CannotDismantle = 17,
        SocketNotFound = 18,
        CannotAttach = 19,
        CannotDetach = 20,
        CooldownActive = 21,
        CannotUse = 22,
        CannotDrop = 23,
        RateLimitExceeded = 24,
        InvalidOperation = 25
    }
    
    /// <summary>
    /// Source of inventory modification (for auditing/validation).
    /// </summary>
    public enum InventoryModificationSource : byte
    {
        Direct = 0,
        Pickup = 1,
        Loot = 2,
        Trade = 3,
        Merchant = 4,
        Craft = 5,
        Quest = 6,
        Ability = 7,
        StatusEffect = 8,
        Admin = 9
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK RUNTIME ITEM REPRESENTATION
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Minimal network representation of a RuntimeProperty.
    /// ~16 bytes
    /// </summary>
    [Serializable]
    public struct NetworkRuntimeProperty
    {
        public int PropertyHash;      // 4 bytes - IdString hash
        public float Number;          // 4 bytes
        public string Text;           // Variable (null for most properties)
        
        public static NetworkRuntimeProperty FromProperty(int hash, float number, string text)
        {
            return new NetworkRuntimeProperty
            {
                PropertyHash = hash,
                Number = number,
                Text = text
            };
        }
    }
    
    /// <summary>
    /// Minimal network representation of a RuntimeSocket.
    /// ~12 bytes without attachment
    /// </summary>
    [Serializable]
    public struct NetworkRuntimeSocket
    {
        public int SocketHash;                     // 4 bytes
        public bool HasAttachment;                 // 1 byte
        public NetworkRuntimeItem Attachment;     // Variable (null if no attachment)
    }
    
    /// <summary>
    /// Network representation of a RuntimeItem.
    /// This is the core data structure for syncing items.
    /// </summary>
    [Serializable]
    public struct NetworkRuntimeItem
    {
        public int ItemHash;                       // 4 bytes - Item.ID hash
        public long RuntimeIdHash;                 // 8 bytes - RuntimeItem.RuntimeID hash (use long for uniqueness)
        public string RuntimeIdString;             // Variable - Full RuntimeID string for reconstruction
        public NetworkRuntimeProperty[] Properties; // Variable
        public NetworkRuntimeSocket[] Sockets;     // Variable
        
        /// <summary>
        /// Estimated serialization size in bytes.
        /// </summary>
        public int EstimatedSize
        {
            get
            {
                int size = 12; // Base fields
                size += (RuntimeIdString?.Length ?? 0) * 2;
                size += (Properties?.Length ?? 0) * 16;
                size += (Sockets?.Length ?? 0) * 12;
                return size;
            }
        }
    }
    
    /// <summary>
    /// Network representation of a Cell (inventory slot with stacked items).
    /// </summary>
    [Serializable]
    public struct NetworkCell
    {
        public Vector2Int Position;                // 8 bytes
        public int ItemHash;                       // 4 bytes - Item type
        public int StackCount;                     // 4 bytes
        public NetworkRuntimeItem RootItem;        // Variable - The root item of the stack
        public long[] StackedRuntimeIds;           // Variable - RuntimeIDs of stacked items
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // CONTENT REQUESTS / RESPONSES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to add item to bag content.
    /// ~40 bytes + item data
    /// </summary>
    [Serializable]
    public struct NetworkContentAddRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public int ItemHash;                       // 4 bytes - Item type to create, or 0 if providing RuntimeItem
        public NetworkRuntimeItem RuntimeItem;     // Variable - If adding existing runtime item
        public Vector2Int Position;                // 8 bytes - (-1,-1) for auto-placement
        public bool AllowStack;                    // 1 byte
        public InventoryModificationSource Source; // 1 byte
        public int SourceHash;                     // 4 bytes
    }
    
    /// <summary>
    /// Response to content add request.
    /// ~24 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentAddResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public Vector2Int ResultPosition;          // 8 bytes - Where item was placed
        public long AssignedRuntimeId;             // 8 bytes - Server-assigned RuntimeID hash
        public string AssignedRuntimeIdString;     // Variable
    }
    
    /// <summary>
    /// Request to remove item from bag.
    /// ~24 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentRemoveRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public long RuntimeIdHash;                 // 8 bytes - RuntimeItem to remove
        public Vector2Int Position;                // 8 bytes - Or position to remove from
        public bool UsePosition;                   // 1 byte - Whether to use position instead of RuntimeID
        public InventoryModificationSource Source; // 1 byte
    }
    
    /// <summary>
    /// Response to content remove request.
    /// ~20 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentRemoveResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public NetworkRuntimeItem RemovedItem;     // Variable - The item that was removed
    }
    
    /// <summary>
    /// Request to move item within bag.
    /// ~24 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentMoveRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public Vector2Int FromPosition;            // 8 bytes
        public Vector2Int ToPosition;              // 8 bytes
        public bool AllowStack;                    // 1 byte
    }
    
    /// <summary>
    /// Response to content move request.
    /// ~8 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentMoveResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public Vector2Int FinalPosition;           // 8 bytes
    }
    
    /// <summary>
    /// Request to use an item.
    /// ~20 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentUseRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public long RuntimeIdHash;                 // 8 bytes
        public Vector2Int Position;                // 8 bytes - Alternative to RuntimeID
        public bool UsePosition;                   // 1 byte
    }
    
    /// <summary>
    /// Response to use request.
    /// ~8 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentUseResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public bool WasConsumed;                   // 1 byte
    }
    
    /// <summary>
    /// Request to drop an item.
    /// ~32 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentDropRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public long RuntimeIdHash;                 // 8 bytes
        public Vector3 DropPosition;               // 12 bytes
        public int MaxAmount;                      // 4 bytes - For dropping from stack
    }
    
    /// <summary>
    /// Response to drop request.
    /// ~8 bytes
    /// </summary>
    [Serializable]
    public struct NetworkContentDropResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public int DroppedCount;                   // 4 bytes
        // Prop spawning handled separately via NetworkObject spawn
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // EQUIPMENT REQUESTS / RESPONSES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to equip/unequip item.
    /// ~20 bytes
    /// </summary>
    [Serializable]
    public struct NetworkEquipmentRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public long RuntimeIdHash;                 // 8 bytes
        public EquipmentAction Action;             // 1 byte
        public int SlotOrIndex;                    // 4 bytes - Slot number or equipment index
    }
    
    /// <summary>
    /// Response to equipment request.
    /// ~8 bytes
    /// </summary>
    [Serializable]
    public struct NetworkEquipmentResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public int EquippedIndex;                  // 4 bytes - Final equipment index
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // SOCKET REQUESTS / RESPONSES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to attach/detach socket.
    /// ~28 bytes
    /// </summary>
    [Serializable]
    public struct NetworkSocketRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public long ParentRuntimeIdHash;           // 8 bytes - Parent item
        public long AttachmentRuntimeIdHash;       // 8 bytes - Attachment item (for attach) or socket contents (for detach)
        public int SocketHash;                     // 4 bytes - Specific socket (0 for auto)
        public SocketAction Action;                // 1 byte
    }
    
    /// <summary>
    /// Response to socket request.
    /// ~16 bytes
    /// </summary>
    [Serializable]
    public struct NetworkSocketResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public int UsedSocketHash;                 // 4 bytes - Which socket was used
        public NetworkRuntimeItem DetachedItem;    // Variable - If detaching
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // WEALTH REQUESTS / RESPONSES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to modify wealth.
    /// ~20 bytes
    /// </summary>
    [Serializable]
    public struct NetworkWealthRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint TargetBagNetworkId;            // 4 bytes
        public int CurrencyHash;                   // 4 bytes
        public int Value;                          // 4 bytes
        public WealthAction Action;                // 1 byte
        public InventoryModificationSource Source; // 1 byte
        public int SourceHash;                     // 4 bytes
    }
    
    /// <summary>
    /// Response to wealth request.
    /// ~12 bytes
    /// </summary>
    [Serializable]
    public struct NetworkWealthResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public int NewValue;                       // 4 bytes
        public int OldValue;                       // 4 bytes
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // MERCHANT REQUESTS / RESPONSES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to buy/sell from merchant.
    /// ~24 bytes
    /// </summary>
    [Serializable]
    public struct NetworkMerchantRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint ClientBagNetworkId;            // 4 bytes
        public uint MerchantNetworkId;             // 4 bytes - NetworkId of merchant's Bag
        public long RuntimeIdHash;                 // 8 bytes - Item to buy/sell
        public MerchantAction Action;              // 1 byte
        public int Amount;                         // 4 bytes - For stacked purchases
    }
    
    /// <summary>
    /// Response to merchant request.
    /// ~16 bytes
    /// </summary>
    [Serializable]
    public struct NetworkMerchantResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public int TotalPrice;                     // 4 bytes
        public int NewClientWealth;                // 4 bytes
        public int NewMerchantWealth;              // 4 bytes
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // CRAFTING REQUESTS / RESPONSES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to craft/dismantle.
    /// ~20 bytes
    /// </summary>
    [Serializable]
    public struct NetworkCraftingRequest
    {
        public ushort RequestId;                   // 2 bytes
        public uint InputBagNetworkId;             // 4 bytes
        public uint OutputBagNetworkId;            // 4 bytes
        public int ItemHash;                       // 4 bytes - Item to craft (for Craft action)
        public long RuntimeIdHash;                 // 8 bytes - RuntimeItem to dismantle (for Dismantle)
        public CraftingAction Action;              // 1 byte
    }
    
    /// <summary>
    /// Response to crafting request.
    /// ~12 bytes + created item
    /// </summary>
    [Serializable]
    public struct NetworkCraftingResponse
    {
        public ushort RequestId;                   // 2 bytes
        public bool Authorized;                    // 1 byte
        public InventoryRejectionReason RejectionReason; // 1 byte
        public NetworkRuntimeItem CreatedItem;     // Variable - The crafted item
        public NetworkRuntimeItem[] ReturnedItems; // Variable - Dismantle returns
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BROADCASTS
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Broadcast when item is added to bag.
    /// </summary>
    [Serializable]
    public struct NetworkItemAddedBroadcast
    {
        public uint BagNetworkId;
        public NetworkRuntimeItem Item;
        public Vector2Int Position;
        public int StackCount;
    }
    
    /// <summary>
    /// Broadcast when item is removed from bag.
    /// </summary>
    [Serializable]
    public struct NetworkItemRemovedBroadcast
    {
        public uint BagNetworkId;
        public long RuntimeIdHash;
        public Vector2Int Position;
        public int RemainingStackCount;
    }
    
    /// <summary>
    /// Broadcast when item is moved within bag.
    /// </summary>
    [Serializable]
    public struct NetworkItemMovedBroadcast
    {
        public uint BagNetworkId;
        public long RuntimeIdHash;
        public Vector2Int FromPosition;
        public Vector2Int ToPosition;
    }
    
    /// <summary>
    /// Broadcast when item is used.
    /// </summary>
    [Serializable]
    public struct NetworkItemUsedBroadcast
    {
        public uint BagNetworkId;
        public long RuntimeIdHash;
        public bool WasConsumed;
    }
    
    /// <summary>
    /// Broadcast when item is equipped.
    /// </summary>
    [Serializable]
    public struct NetworkItemEquippedBroadcast
    {
        public uint BagNetworkId;
        public long RuntimeIdHash;
        public int EquipmentIndex;
    }
    
    /// <summary>
    /// Broadcast when item is unequipped.
    /// </summary>
    [Serializable]
    public struct NetworkItemUnequippedBroadcast
    {
        public uint BagNetworkId;
        public long RuntimeIdHash;
        public int EquipmentIndex;
    }
    
    /// <summary>
    /// Broadcast when socket attachment changes.
    /// </summary>
    [Serializable]
    public struct NetworkSocketChangeBroadcast
    {
        public uint BagNetworkId;
        public long ParentRuntimeIdHash;
        public int SocketHash;
        public bool HasAttachment;
        public NetworkRuntimeItem Attachment;      // If attached
    }
    
    /// <summary>
    /// Broadcast when wealth changes.
    /// </summary>
    [Serializable]
    public struct NetworkWealthChangeBroadcast
    {
        public uint BagNetworkId;
        public int CurrencyHash;
        public int NewValue;
        public int Change;
    }
    
    /// <summary>
    /// Broadcast when property value changes.
    /// </summary>
    [Serializable]
    public struct NetworkPropertyChangeBroadcast
    {
        public uint BagNetworkId;
        public long RuntimeIdHash;
        public int PropertyHash;
        public float NewNumber;
        public string NewText;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // FULL STATE SYNC
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Full inventory snapshot for initial sync or reconnection.
    /// </summary>
    [Serializable]
    public struct NetworkInventorySnapshot
    {
        public uint BagNetworkId;
        public float Timestamp;
        public int BagType;                        // Grid vs List
        public Vector2Int BagSize;                 // For grid bags
        public int MaxWeight;
        public NetworkCell[] Cells;                // All occupied cells
        public NetworkEquipmentSlot[] Equipment;   // All equipment slots
        public NetworkWealthEntry[] Wealth;        // All currencies
    }
    
    /// <summary>
    /// Equipment slot state for snapshot.
    /// </summary>
    [Serializable]
    public struct NetworkEquipmentSlot
    {
        public int SlotIndex;
        public int BaseItemHash;                   // What type of item can go here
        public bool IsOccupied;
        public long EquippedRuntimeIdHash;
    }
    
    /// <summary>
    /// Wealth entry for snapshot.
    /// </summary>
    [Serializable]
    public struct NetworkWealthEntry
    {
        public int CurrencyHash;
        public int Amount;
    }
    
    /// <summary>
    /// Delta update for efficient sync.
    /// </summary>
    [Serializable]
    public struct NetworkInventoryDelta
    {
        public uint BagNetworkId;
        public float Timestamp;
        public uint ChangeMask;                    // Bit flags for what changed
        public NetworkCell[] ChangedCells;
        public NetworkEquipmentSlot[] ChangedEquipment;
        public NetworkWealthEntry[] ChangedWealth;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // TRANSFER BETWEEN BAGS
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to transfer item between two bags (trade, loot, etc.).
    /// </summary>
    [Serializable]
    public struct NetworkTransferRequest
    {
        public ushort RequestId;
        public uint SourceBagNetworkId;
        public uint DestinationBagNetworkId;
        public long RuntimeIdHash;
        public Vector2Int DestinationPosition;     // (-1,-1) for auto
        public bool AllowStack;
        public InventoryModificationSource Source;
    }
    
    /// <summary>
    /// Response to transfer request.
    /// </summary>
    [Serializable]
    public struct NetworkTransferResponse
    {
        public ushort RequestId;
        public bool Authorized;
        public InventoryRejectionReason RejectionReason;
        public Vector2Int FinalPosition;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // LOOT / PICKUP
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to pick up a dropped Prop.
    /// </summary>
    [Serializable]
    public struct NetworkPickupRequest
    {
        public ushort RequestId;
        public uint PickerBagNetworkId;
        public uint PropNetworkId;                 // NetworkId of the Prop object
    }
    
    /// <summary>
    /// Response to pickup request.
    /// </summary>
    [Serializable]
    public struct NetworkPickupResponse
    {
        public ushort RequestId;
        public bool Authorized;
        public InventoryRejectionReason RejectionReason;
        public NetworkRuntimeItem PickedUpItem;
        public Vector2Int PlacedPosition;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // COMBINE (Two items into one)
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to combine two items (if crafting.AllowToCombine).
    /// </summary>
    [Serializable]
    public struct NetworkCombineRequest
    {
        public ushort RequestId;
        public uint BagNetworkId;
        public Vector2Int PositionA;
        public Vector2Int PositionB;
    }
    
    /// <summary>
    /// Response to combine request.
    /// </summary>
    [Serializable]
    public struct NetworkCombineResponse
    {
        public ushort RequestId;
        public bool Authorized;
        public InventoryRejectionReason RejectionReason;
        public NetworkRuntimeItem ResultItem;
        public Vector2Int ResultPosition;
    }
}
#endif
