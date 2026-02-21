# GC2 Inventory Network Integration

Server-authoritative inventory networking for Game Creator 2 Inventory module. Designed for competitive multiplayer with cheat prevention.

## Features

### Server Authority
- **Content Operations**: Add, Remove, Move, Use, Drop items validated server-side
- **Equipment System**: Equip/Unequip with server validation
- **Socket Operations**: Attach/Detach items from sockets
- **Wealth Management**: Currency Add/Subtract/Set with server authority
- **Merchant Transactions**: Buy/Sell validated server-side
- **Crafting Operations**: Craft/Dismantle with ingredient verification
- **Transfer System**: Bag-to-bag transfers with validation

### Anti-Cheat Protection
- Item duplication prevention
- Gold/currency exploit prevention
- Illegal crafting blocked
- Equipment slot validation
- Socket compatibility checks
- Rate limiting per client

## Architecture

```
NetworkInventoryManager (Singleton)
    ├── Transport Delegates (wire to your networking)
    ├── Controller Registry
    └── Request Routing

NetworkInventoryController (Per-Bag)
    ├── Request Methods (Client)
    ├── Process Methods (Server)
    ├── Broadcast Receivers (Client)
    └── RuntimeItem Tracking
```

## Quick Start

### 1. Setup Manager

Add `NetworkInventoryManager` to a persistent scene object:

```csharp
// On server
NetworkInventoryManager.Instance.IsServer = true;

// Wire transport delegates (example with any networking solution)
NetworkInventoryManager.Instance.OnSendContentAddRequest = SendToServerMethod;
NetworkInventoryManager.Instance.OnBroadcastItemAdded = BroadcastToClientsMethod;
// ... wire all needed delegates
```

### 2. Setup Per-Bag Controller

Add `NetworkInventoryController` alongside each `Bag` component:

```csharp
// During spawn
var bag = GetComponent<Bag>();
var controller = GetComponent<NetworkInventoryController>();
controller.Initialize(bag, networkId, isServer, isOwner);
```

### 3. Client Operations

Instead of calling Bag methods directly, use controller requests:

```csharp
// OLD (don't use in multiplayer):
// bag.Content.Add(item, true);

// NEW (server-authoritative):
controller.RequestAddItem(itemIdHash, 1);

// With callback
controller.RequestAddItem(itemIdHash, 1, OnAddResult);

void OnAddResult(NetworkContentAddResponse response)
{
    if (response.Authorized)
        Debug.Log($"Added item: {response.RuntimeIdString}");
    else
        Debug.Log($"Rejected: {response.RejectionReason}");
}
```

## Operations Reference

### Content Operations

| Operation | Client Request | Server Validates |
|-----------|----------------|------------------|
| Add | `RequestAddItem(hash, count)` | Space, stacking rules |
| Remove | `RequestRemoveItem(runtimeId, count)` | Item exists, count valid |
| Move | `RequestMoveItem(from, to)` | Valid indices, space |
| Use | `RequestUseItem(runtimeId)` | Item exists, usable |
| Drop | `RequestDropItem(runtimeId, count, pos)` | Item exists, count valid |

### Equipment Operations

| Operation | Client Request | Server Validates |
|-----------|----------------|------------------|
| Equip | `RequestEquip(runtimeId)` | Item exists, compatible |
| Unequip | `RequestUnequip(runtimeId)` | Item equipped |
| UnequipFromIndex | `RequestUnequipFromIndex(index)` | Index valid, item equipped |

### Socket Operations

| Operation | Client Request | Server Validates |
|-----------|----------------|------------------|
| Attach | `RequestAttachToSocket(parentId, childId, slot)` | Items exist, compatible |
| Detach | `RequestDetachFromSocket(parentId, childId, slot)` | Items attached |

### Wealth Operations

| Operation | Client Request | Server Validates |
|-----------|----------------|------------------|
| Add Currency | `RequestWealthModify(Add, currency, amount)` | Server-only typically |
| Subtract | `RequestWealthModify(Subtract, currency, amount)` | Sufficient funds |
| Set | `RequestWealthModify(Set, currency, amount)` | Server-only typically |

## Network Types

### Core Structures

```csharp
// Network representation of RuntimeItem
NetworkRuntimeItem
{
    ItemHash,           // Item definition hash
    RuntimeIdHash,      // Unique instance hash
    RuntimeIdString,    // String for reconstruction
    Properties[],       // Serialized properties
    Sockets[]          // Nested items in sockets
}

// Inventory cell
NetworkCell
{
    CellIndex,
    Stack[]            // Items in this cell
}

// Full inventory state
NetworkInventorySnapshot
{
    BagNetworkId,
    Cells[],
    EquippedItems[],
    Wealth[]
}
```

### Request/Response Pattern

All operations follow request-response pattern:

```csharp
// Client sends request
NetworkContentAddRequest
{
    RequestId,          // Unique request ID
    TargetBagNetworkId, // Which bag
    ItemIdHash,         // What item
    Amount,             // How many
    Source             // Modification source
}

// Server responds
NetworkContentAddResponse
{
    RequestId,          // Matching request
    Authorized,         // Was it allowed
    RejectionReason,    // Why rejected
    ResultingItem       // The created item (if authorized)
}
```

## Custom Validation

Extend validation with custom logic:

```csharp
// Example: Prevent adding items during combat
NetworkInventoryManager.Instance.CustomAddValidator = (request, clientId) =>
{
    if (IsInCombat(clientId))
        return (false, InventoryRejectionReason.ConditionFailed);
    return (true, InventoryRejectionReason.None);
};

// Example: Custom merchant restrictions
NetworkInventoryManager.Instance.CustomMerchantValidator = (request, clientId) =>
{
    if (!IsNearMerchant(clientId, request.MerchantNetworkId))
        return (false, InventoryRejectionReason.OutOfRange);
    return (true, InventoryRejectionReason.None);
};
```

## Transport Integration Examples

### Netcode for GameObjects

```csharp
public class InventoryNetworkBridge : NetworkBehaviour
{
    void Start()
    {
        var manager = NetworkInventoryManager.Instance;
        
        // Client → Server
        manager.OnSendContentAddRequest = (req) => AddItemServerRpc(req);
        manager.OnSendEquipmentRequest = (req) => EquipmentServerRpc(req);
        
        // Server → Client
        manager.OnBroadcastItemAdded = (bc) => ItemAddedClientRpc(bc);
        manager.OnSendSnapshotToClient = (id, snap) => SendSnapshotClientRpc(snap, id);
    }
    
    [ServerRpc]
    void AddItemServerRpc(NetworkContentAddRequest request, ServerRpcParams p = default)
    {
        NetworkInventoryManager.Instance.ReceiveContentAddRequest(request, p.Receive.SenderClientId);
    }
    
    [ClientRpc]
    void ItemAddedClientRpc(NetworkItemAddedBroadcast broadcast)
    {
        if (!IsServer) // Avoid double-processing on host
            NetworkInventoryManager.Instance.ReceiveItemAddedBroadcast(broadcast);
    }
}
```

### FishNet

```csharp
public class InventoryNetworkBridge : NetworkBehaviour
{
    public override void OnStartNetwork()
    {
        var manager = NetworkInventoryManager.Instance;
        
        manager.OnSendContentAddRequest = (req) => AddItemServer(req);
        manager.OnBroadcastItemAdded = (bc) => ItemAddedObservers(bc);
    }
    
    [ServerRpc]
    void AddItemServer(NetworkContentAddRequest request)
    {
        var clientId = Owner.ClientId;
        NetworkInventoryManager.Instance.ReceiveContentAddRequest(request, (ulong)clientId);
    }
    
    [ObserversRpc]
    void ItemAddedObservers(NetworkItemAddedBroadcast broadcast)
    {
        if (!IsServerInitialized)
            NetworkInventoryManager.Instance.ReceiveItemAddedBroadcast(broadcast);
    }
}
```

## Best Practices

### DO:
- ✅ Always use request methods for player inventory changes
- ✅ Handle rejection callbacks gracefully
- ✅ Implement custom validators for game-specific rules
- ✅ Use snapshots for late-joining players
- ✅ Log rejections for debugging potential exploits

### DON'T:
- ❌ Call Bag methods directly on client for authoritative data
- ❌ Trust client-reported item counts
- ❌ Skip server validation for "trusted" operations
- ❌ Broadcast sensitive inventory data to non-owners

## Events

The controller fires events for UI updates:

```csharp
controller.OnItemAdded += (item) => RefreshInventoryUI();
controller.OnItemRemoved += (runtimeId) => RefreshInventoryUI();
controller.OnWealthChanged += (currency, newAmount) => UpdateGoldDisplay(newAmount);
controller.OnEquipmentChanged += () => RefreshEquipmentUI();
```

## Rejection Reasons

| Reason | Description |
|--------|-------------|
| `None` | No rejection |
| `BagNotFound` | Target bag doesn't exist |
| `ItemNotFound` | Referenced item not found |
| `BagFull` | No space in inventory |
| `InsufficientFunds` | Not enough currency |
| `InsufficientAmount` | Not enough items |
| `ConditionFailed` | Custom condition failed |
| `InvalidOperation` | Operation not supported |
| `NotOwner` | Client doesn't own this bag |
| `OutOfRange` | Target too far away |
| `Cooldown` | Operation on cooldown |
| `EquipmentSlotFull` | Equipment slot occupied |
| `IncompatibleSocket` | Socket doesn't accept item |

## Files

| File | Purpose |
|------|---------|
| `NetworkInventoryTypes.cs` | All network data structures |
| `NetworkInventoryController.cs` | Per-bag inventory network management |
| `NetworkInventoryManager.cs` | Global singleton, transport routing |

## Dependencies

- Game Creator 2 Core
- Game Creator 2 Inventory Module
- Unity 2021.3+

## Define Symbol

This module requires `GC2_INVENTORY` define symbol, which is automatically set via Version Defines when `com.gamecreator.inventory` package is present.
