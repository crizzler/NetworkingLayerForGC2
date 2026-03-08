# GC2 Core Networking Module

Server-authoritative networking for Game Creator 2 Core character features.

## Features Covered

| Feature | Request/Response | Broadcast | Server Direct |
|---------|-----------------|-----------|---------------|
| **Ragdoll** | ✅ `NetworkRagdollRequest/Response` | ✅ `NetworkRagdollBroadcast` | ✅ `ServerStartRagdoll()` |
| **Props** | ✅ `NetworkPropRequest/Response` | ✅ `NetworkPropBroadcast` | - |
| **Invincibility** | ✅ `NetworkInvincibilityRequest/Response` | ✅ `NetworkInvincibilityBroadcast` | ✅ `ServerSetInvincibility()` |
| **Poise** | ✅ `NetworkPoiseRequest/Response` | ✅ `NetworkPoiseBroadcast` | ✅ `ServerDamagePoise()`, `ServerResetPoise()` |
| **Busy Limbs** | ✅ `NetworkBusyRequest/Response` | ✅ `NetworkBusyBroadcast` | - |
| **Interaction** | ✅ `NetworkInteractionRequest/Response` | ✅ `NetworkInteractionBroadcast` | - |

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                     NetworkCoreManager                          │
│  • Message type routing (IDs 200-229)                          │
│  • Prop registry for prefab lookup                              │
│  • Convenience methods for client/server                        │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                    NetworkCoreController                        │
│  • Request/Response handling                                    │
│  • Server validation & cooldowns                                │
│  • Client pending request tracking                              │
│  • Broadcast distribution                                       │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                     NetworkCoreTypes.cs                         │
│  • Compact serializable structs                                 │
│  • Enums for action types & rejection reasons                   │
│  • Combined CoreState for delta sync                            │
└─────────────────────────────────────────────────────────────────┘
```

## Message Type IDs

Reserved range: **200-229**

| ID | Message Type |
|----|--------------|
| 200-202 | Ragdoll (Request, Response, Broadcast) |
| 205-207 | Props (Request, Response, Broadcast) |
| 210-212 | Invincibility (Request, Response, Broadcast) |
| 215-217 | Poise (Request, Response, Broadcast) |
| 220-222 | Busy (Request, Response, Broadcast) |
| 225-228 | Interaction (Request, Response, Broadcast, Focus) |
| 229 | Core State Sync |

## Usage

### Where To Add Components

- Add `NetworkCoreManager` to a single bootstrap object in your scene (or use the Setup Wizard scene step).
- `NetworkCoreController` should live on the same GameObject as `NetworkCoreManager`.
- `NetworkCoreManager` auto-ensures `NetworkCoreController` on itself at runtime if it is missing.
- For character-side core hooks, `NetworkCharacter` can also add/use a local `NetworkCoreController` when `Use Core Networking` is enabled.

### Setup

```csharp
// Get or create manager
var coreManager = NetworkCoreManager.Instance;

// Wire up network delegates
coreManager.SendRagdollRequestToServer = request => {
    SendToServer(NetworkCoreManager.MessageTypes.RagdollRequest, request);
};

coreManager.GetCharacterByNetworkId = networkId => {
    // Return Character component for network ID
    return NetworkCharacterRegistry.Get(networkId)?.Character;
};

// Initialize
coreManager.Initialize(isServer: true, isClient: true);

void SendToServer<TPayload>(byte messageType, TPayload payload) { /* transport send C->S */ }
```

### Client Requests

```csharp
// Request ragdoll with knockback force
NetworkCoreManager.Instance.RequestStartRagdoll(
    characterNetworkId,
    force: Vector3.up * 10f,
    forcePoint: transform.position,
    callback: response => {
        if (response.Approved) Debug.Log("Ragdoll started!");
    }
);

// Request invincibility
NetworkCoreManager.Instance.RequestSetInvincibility(
    characterNetworkId,
    duration: 3f
);

// Request poise damage
NetworkCoreManager.Instance.RequestPoiseDamage(
    characterNetworkId,
    damage: 50f
);

// Attach prop
NetworkCoreManager.Instance.RequestAttachProp(
    characterNetworkId,
    propId: "Sword_01",
    boneName: "Hand_R",
    localPosition: Vector3.zero,
    localRotation: Quaternion.identity
);

// Set busy limbs
NetworkCoreManager.Instance.RequestSetBusy(
    characterNetworkId,
    limbs: BusyLimbs.Arms,
    setBusy: true,
    timeout: 2f
);
```

### Server Direct Methods

```csharp
// Directly apply ragdoll (bypasses client request)
NetworkCoreManager.Instance.ServerStartRagdoll(
    characterNetworkId,
    force: Vector3.forward * 20f
);

// Directly set invincibility
NetworkCoreManager.Instance.ServerSetInvincibility(
    characterNetworkId,
    duration: 5f
);

// Directly damage poise
NetworkCoreManager.Instance.ServerDamagePoise(
    characterNetworkId,
    damage: 100f
);
```

### Network Message Handling

```csharp
// In your network receive handler
void OnNetworkMessage(byte messageType, uint senderId, byte[] data)
{
    var manager = NetworkCoreManager.Instance;
    
    switch (messageType)
    {
        // Server receives
        case NetworkCoreManager.MessageTypes.RagdollRequest:
            var ragdollReq = Deserialize<NetworkRagdollRequest>(data);
            manager.ReceiveRagdollRequest(senderId, ragdollReq);
            break;
            
        // Client receives
        case NetworkCoreManager.MessageTypes.RagdollResponse:
            var ragdollResp = Deserialize<NetworkRagdollResponse>(data);
            manager.ReceiveRagdollResponse(ragdollResp);
            break;
            
        case NetworkCoreManager.MessageTypes.RagdollBroadcast:
            var ragdollBc = Deserialize<NetworkRagdollBroadcast>(data);
            manager.ReceiveRagdollBroadcast(ragdollBc);
            break;
            
        // Add cases for every enabled core message type (Prop/Invincibility/Poise/Busy/Interaction/CoreStateSync).
    }
}
```

## Validation Rules

### Ragdoll
- Cooldown between state changes (configurable)
- Cannot start ragdoll if already ragdoll
- Cannot recover if not ragdoll

### Props
- Maximum props per character (configurable)
- Prop prefab must exist in registry
- Bone must exist on character skeleton

### Invincibility
- Maximum duration cap (configurable)
- Cooldown after invincibility ends (configurable)
- Cannot cancel if not invincible

### Poise
- Validates damage values (optional)
- Tracks broken state
- Supports damage, set, reset, add operations

### Interaction
- Maximum range validation (configurable)
- Per-target cooldown (configurable)
- Character busy state check

## Prop Registry

Register props in inspector or at runtime:

```csharp
// Runtime registration
NetworkCoreManager.Instance.RegisterPropPrefab("Sword_01", swordPrefab);

// Get hash for network messages
int hash = NetworkCoreManager.GetPropHash("Sword_01");
```

## Integration with Existing Modules

This Core module complements:
- **NetworkCharacter** - Death/Revive, Driver assignment
- **UnitMotionNetworkController** - Movement, Dash, Jump
- **NetworkCombatController** - Hit validation, Lag compensation
- **NetworkMeleeController** - Block, Skills, Reactions
- **NetworkShooterController** - Reload, Jam, Sight
- **NetworkStatsController** - Stats, Modifiers, Status Effects

## Statistics

```csharp
var stats = NetworkCoreManager.Instance.CoreController.Stats;

Debug.Log($"Ragdoll: {stats.RagdollApproved} approved, {stats.RagdollRejected} rejected");
Debug.Log($"Poise: {stats.PoiseApproved} approved, {stats.PoiseRejected} rejected");
Debug.Log($"Interactions: {stats.InteractionApproved} approved, {stats.InteractionRejected} rejected");
```
