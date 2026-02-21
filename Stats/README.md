# GC2 Stats Network Integration

Server-authoritative networking solution for Game Creator 2 Stats module, designed for competitive multiplayer environments where preventing stat manipulation/cheating is critical.

## Overview

This module provides:
- **Server-Authoritative Stats** - All stat/attribute modifications validated by server
- **Anti-Cheat Protection** - Rate limiting, change validation, unauthorized modification rejection
- **Optimistic Updates** - Responsive client-side feedback with server reconciliation
- **Bandwidth Efficiency** - Delta sync, bitmask compression, minimal data structures
- **Transport Agnostic** - Works with Netcode for GameObjects, FishNet, Mirror, etc.

## Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                         CLIENT SIDE                                   │
├──────────────────────────────────────────────────────────────────────┤
│  GC2 Traits Component                                                 │
│       ↓ (stat change attempt)                                        │
│  NetworkStatsController                                               │
│       ↓ Request (optimistic update applied locally)                  │
│  NetworkStatsManager                                                  │
│       ↓ OnSendStatModifyRequest delegate                             │
│  [Your Transport Layer]                                              │
└──────────────────────────────────────────────────────────────────────┘
                              │
                              ↓ (Network)
                              │
┌──────────────────────────────────────────────────────────────────────┐
│                         SERVER SIDE                                   │
├──────────────────────────────────────────────────────────────────────┤
│  [Your Transport Layer]                                              │
│       ↓                                                              │
│  NetworkStatsManager.ReceiveStatModifyRequest()                      │
│       ↓ (validates request)                                          │
│  NetworkStatsController.ProcessStatModifyRequest()                   │
│       ↓ (applies to authoritative state)                             │
│  GC2 Traits Component                                                 │
│       ↓                                                              │
│  Broadcast response + state change to all clients                    │
└──────────────────────────────────────────────────────────────────────┘
```

## Files

| File | Purpose | Size (bytes) |
|------|---------|--------------|
| `NetworkStatsTypes.cs` | Data structures for network serialization | ~500 |
| `NetworkStatsController.cs` | Per-character stat network management | ~1000 |
| `NetworkStatsManager.cs` | Global message routing singleton | ~600 |

## Setup

### 1. Add Manager to Scene

Add `NetworkStatsManager` component to a persistent GameObject in your scene:

```csharp
// Usually on NetworkManager or similar
gameObject.AddComponent<NetworkStatsManager>();
NetworkStatsManager.Instance.IsServer = NetworkManager.Singleton.IsServer;
```

### 2. Add Controller to Characters

Add `NetworkStatsController` to any character with GC2 `Traits`:

```csharp
// When spawning network character
var statsController = character.AddComponent<NetworkStatsController>();
statsController.Initialize(isServer, isLocalPlayer);
NetworkStatsManager.Instance.RegisterController(networkId, statsController);
```

### 3. Wire Transport Delegates

Connect the manager's delegates to your networking solution:

```csharp
// Example with Netcode for GameObjects
void SetupNetworkDelegates()
{
    var manager = NetworkStatsManager.Instance;
    
    // Client → Server
    manager.OnSendStatModifyRequest = (request) => 
        SendStatModifyRequestServerRpc(request);
    
    manager.OnSendAttributeModifyRequest = (request) =>
        SendAttributeModifyRequestServerRpc(request);
    
    // Server → All Clients
    manager.OnBroadcastStatChange = (broadcast) =>
        BroadcastStatChangeClientRpc(broadcast);
    
    manager.OnBroadcastAttributeChange = (broadcast) =>
        BroadcastAttributeChangeClientRpc(broadcast);
    
    // Server → Single Client
    manager.OnSendStatModifyResponse = (networkId, response) =>
        SendStatModifyResponseClientRpc(response, GetClientRpcParams(networkId));
}
```

## Usage

### Modifying Stats (Client-Side)

Instead of directly modifying GC2 stats:

```csharp
// DON'T: Direct modification (can be cheated)
traits.RuntimeStats.Get("strength").Base = 100;

// DO: Network-validated modification
var statsController = GetComponent<NetworkStatsController>();
statsController.RequestStatModify(
    new IdString("strength"),
    StatModificationType.SetBase,
    100f,
    StatModificationSource.Ability,
    abilityHash
);
```

### Modifying Attributes (Health, Mana, etc.)

```csharp
// Request damage to health
statsController.RequestAttributeModify(
    new IdString("health"),
    AttributeModificationType.Add,
    -50f,  // Negative for damage
    StatModificationSource.Combat,
    attackerWeaponHash
);
```

### Status Effects

```csharp
// Apply poison effect
statsController.RequestStatusEffectAction(
    new IdString("poison"),
    StatusEffectAction.Add,
    1,  // Stack count
    StatModificationSource.StatusEffect
);

// Remove all stacks of a buff
statsController.RequestStatusEffectAction(
    new IdString("strength_buff"),
    StatusEffectAction.RemoveAll
);
```

### Listening for Changes

```csharp
void Start()
{
    statsController.OnStatChanged += OnStatChanged;
    statsController.OnAttributeChanged += OnAttributeChanged;
    statsController.OnModificationRejected += OnRejected;
}

void OnStatChanged(NetworkStatChangeBroadcast broadcast)
{
    Debug.Log($"Stat {broadcast.StatHash} now {broadcast.NewComputedValue}");
}

void OnRejected(StatRejectionReason reason, string details)
{
    Debug.LogWarning($"Modification rejected: {reason} - {details}");
}
```

## Network Data Efficiency

All structs are designed for minimal bandwidth:

| Message Type | Approx. Size |
|--------------|--------------|
| StatModifyRequest | ~20 bytes |
| StatModifyResponse | ~6 bytes |
| AttributeModifyRequest | ~20 bytes |
| AttributeChangeBroadcast | ~20 bytes |
| StatusEffectRequest | ~16 bytes |
| Delta sync (per changed stat) | ~12 bytes |

### Delta Sync

The controller automatically tracks which stats/attributes changed and only sends deltas:

```csharp
// Server settings
statsController.m_FullSyncInterval = 5f;   // Full sync every 5 seconds
statsController.m_DeltaSyncInterval = 0.1f; // Delta sync at 10Hz
statsController.m_SmartDeltaSync = true;    // Only sync changed values
```

## Anti-Cheat Features

### Rate Limiting

```csharp
// Max stat change per second (prevents rapid-fire exploits)
statsController.m_MaxChangePerSecond = 1000f;
```

### Server Validation

The server can reject modifications for:
- `NotAuthorized` - Client doesn't own target character
- `TargetNotFound` - Invalid network ID
- `StatNotFound` / `AttributeNotFound` - Invalid stat/attribute ID
- `InvalidValue` - Value outside allowed range
- `RateLimitExceeded` - Too many requests
- `Cooldown` - Ability/effect on cooldown

### Custom Validation

```csharp
// Add custom server-side validation
NetworkStatsManager.Instance.CustomStatValidator = (request, clientId) =>
{
    // Example: Prevent modifying "level" stat directly
    if (request.StatHash == new IdString("level").Hash)
        return (false, StatRejectionReason.NotAuthorized);
    
    return (true, StatRejectionReason.None);
};
```

## Optimistic Updates

For responsive gameplay, clients can apply changes immediately:

```csharp
statsController.OptimisticUpdates = true;  // Apply locally before confirmation
statsController.m_RollbackOnReject = true;  // Revert if server rejects
```

Flow:
1. Client sends request
2. Client applies change locally (optimistic)
3. Server validates and responds
4. If rejected → rollback to original value
5. If approved → keep current value

## Integration with Combat

The Stats module integrates with the Melee and Shooter combat modules:

```csharp
// When damage is dealt (in NetworkMeleeController/NetworkShooterController)
private void ApplyDamage(uint targetId, float damage)
{
    var targetStats = NetworkStatsManager.Instance.GetController(targetId);
    targetStats?.RequestAttributeModify(
        new IdString("health"),
        AttributeModificationType.Add,
        -damage,
        StatModificationSource.Combat,
        weaponHash
    );
}
```

## Debugging

Enable logging for troubleshooting:

```csharp
// Controller level
statsController.m_LogAllChanges = true;
statsController.m_LogRejections = true;

// Manager level
NetworkStatsManager.Instance.m_LogNetworkMessages = true;
```

## Requirements

- Unity 2021.3+
- Game Creator 2 Stats module
- Compatible networking solution (Netcode, FishNet, Mirror, etc.)

## Module Dependencies

```
Arawn.EnemyMasses.GameCreator2.Stats
├── GameCreator.Runtime.Core
├── GameCreator.Runtime.Stats
├── Arawn.EnemyMasses.Runtime
├── Arawn.EnemyMasses.GameCreator2
└── Arawn.NetworkingCore.Runtime
```

## Version History

- **1.0.0** - Initial release
  - Server-authoritative stat/attribute/status effect management
  - Optimistic updates with rollback
  - Delta sync for bandwidth efficiency
  - Rate limiting and custom validation hooks
