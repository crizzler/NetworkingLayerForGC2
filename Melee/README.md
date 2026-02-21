# GC2 Melee Network Integration

Server-authoritative melee combat networking for Game Creator 2.

## Overview

This module provides network-aware melee combat for GC2, enabling server-authoritative hit validation with lag compensation. It integrates seamlessly with the base GC2 Network Integration and requires it as a dependency.

## Requirements

- Game Creator 2 Core
- Game Creator 2 Melee Module
- GC2 Network Integration (base module)
- Unity Netcode for GameObjects (or compatible transport)

## Installation

1. Import the GC2 Network Integration base module
2. Import this GC2 Melee Network module
3. The module will auto-detect GC2 Melee via the `GC2_MELEE` define symbol

## Architecture

### Network Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         MELEE HIT FLOW                                  │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  LOCAL CLIENT                    SERVER                    REMOTE CLIENT│
│  ────────────                    ──────                    ─────────────│
│                                                                         │
│  1. Player attacks               │                         │            │
│     ↓                            │                         │            │
│  2. Striker detects hit          │                         │            │
│     ↓                            │                         │            │
│  3. ConditionNetworkMeleeHit     │                         │            │
│     intercepts via CanHit        │                         │            │
│     ↓                            │                         │            │
│  4. NetworkMeleeController       │                         │            │
│     sends hit request ─────────► 5. Validate hit           │            │
│     ↓                            │  (lag compensation)     │            │
│  [Optimistic effects]            │     ↓                   │            │
│                                  │  6. Apply damage        │            │
│                                  │     ↓                   │            │
│  7. Receive response ◄─────────── 8. Send response ───────► 9. Play FX │
│     ↓                            │     ↓                   │            │
│  8. Confirm/rollback             │  9. Broadcast hit       │            │
│                                  │                         │            │
└─────────────────────────────────────────────────────────────────────────┘
```

### Components

#### NetworkMeleeManager
Global singleton that coordinates all melee networking.

```csharp
// Add to your NetworkManager or scene root
[AddComponentMenu("Game Creator/Network/Melee/Network Melee Manager")]
```

**Setup:**
1. Add to a persistent GameObject in your scene
2. Connect the network delegates to your transport:

```csharp
var meleeManager = NetworkMeleeManager.Instance;

// Client -> Server
meleeManager.SendHitRequestToServer = (request) => {
    // Send via your network transport
    MyNetworkRPC.SendHitRequestServerRpc(request);
};

// Server -> Client
meleeManager.SendHitResponseToClient = (clientId, response) => {
    MyNetworkRPC.SendHitResponseClientRpc(clientId, response);
};

// Server -> All Clients
meleeManager.BroadcastHitToAllClients = (broadcast) => {
    MyNetworkRPC.BroadcastHitClientRpc(broadcast);
};

// Helper delegates
meleeManager.GetCharacterByNetworkIdFunc = (id) => {
    return NetworkCharacterManager.GetById(id);
};
```

#### NetworkMeleeController
Per-character component that handles melee hit interception.

```csharp
// Add to each character with melee combat
[AddComponentMenu("Game Creator/Network/Melee/Network Melee Controller")]
```

**Setup:**
1. Add to any Character that uses melee combat
2. Ensure NetworkCharacter is also present
3. Set `Combat Mode = Disabled` on NetworkCharacter (melee handles combat separately)

**Properties:**
- `Optimistic Effects`: Show hit effects before server confirmation
- `Log Hits`: Debug logging for hit detection

#### ConditionNetworkMeleeHit (Visual Scripting)
A GC2 Condition that intercepts hits in the Skill's "Can Hit" conditions.

**Usage:**
1. Open your Melee Skill asset
2. Find the "Can Hit" conditions section
3. Add the "Network Melee Hit" condition

This condition:
- Returns `true` on server (server processes hits directly)
- Returns `true/false` on client based on optimistic setting
- Always returns `false` on remote clients (they receive broadcasts)

## Setup Guide

### Step 1: Scene Setup

1. Add `NetworkMeleeManager` to your scene (on NetworkManager or persistent object)
2. Configure the network delegates (see above)

### Step 2: Character Setup

For each networked character with melee combat:

1. Add `NetworkCharacter` component
   - Set Combat Mode = **Disabled** (important!)
   
2. Add `NetworkMeleeController` component
   - Configure optimistic effects preference

### Step 3: Skill Setup

For each Melee Skill that should use network hit validation:

1. Open the Skill ScriptableObject
2. In "Can Hit" conditions, add **Network Melee Hit** condition
3. This intercepts the hit and routes through server

### Step 4: Network Transport

Connect the manager to your network layer:

```csharp
public class MeleeNetworkBridge : NetworkBehaviour
{
    private void Start()
    {
        var manager = NetworkMeleeManager.Instance;
        
        manager.SendHitRequestToServer = SendHitRequestServerRpc;
        manager.SendHitResponseToClient = SendResponseToClient;
        manager.BroadcastHitToAllClients = BroadcastHitClientRpc;
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void SendHitRequestServerRpc(NetworkMeleeHitRequest request, ServerRpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        NetworkMeleeManager.Instance.ReceiveHitRequest((uint)clientId, request);
    }
    
    [ClientRpc]
    private void SendHitResponseClientRpc(NetworkMeleeHitResponse response, ClientRpcParams rpcParams = default)
    {
        NetworkMeleeManager.Instance.ReceiveHitResponse(response);
    }
    
    [ClientRpc]
    private void BroadcastHitClientRpc(NetworkMeleeHitBroadcast broadcast)
    {
        NetworkMeleeManager.Instance.ReceiveHitBroadcast(broadcast);
    }
    
    private void SendResponseToClient(uint clientId, NetworkMeleeHitResponse response)
    {
        ClientRpcParams rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { (ulong)clientId } }
        };
        SendHitResponseClientRpc(response, rpcParams);
    }
}
```

## Data Types

### NetworkMeleeHitRequest (~30 bytes)
Sent from client to server when a hit is detected.

| Field | Type | Description |
|-------|------|-------------|
| RequestId | ushort | Unique ID for response matching |
| ClientTimestamp | float | When hit was detected |
| AttackerNetworkId | uint | Attacker's network ID |
| TargetNetworkId | uint | Target's network ID |
| HitPoint | Vector3 | World position of hit |
| StrikeDirection | Vector3 | Direction of strike |
| SkillHash | int | Hash of skill being used |
| WeaponHash | int | Hash of weapon being used |
| ComboNodeId | int | Current combo position |
| AttackPhase | byte | Current attack phase |

### NetworkMeleeHitResponse (~8 bytes)
Server response to hit request.

| Field | Type | Description |
|-------|------|-------------|
| RequestId | ushort | Matching request ID |
| Validated | bool | Whether hit was valid |
| RejectionReason | byte | Why hit was rejected |
| Damage | float | Calculated damage |
| PoiseBroken | bool | Whether poise broke |

### NetworkMeleeHitBroadcast (~24 bytes)
Broadcast to all clients when hit is confirmed.

| Field | Type | Description |
|-------|------|-------------|
| AttackerNetworkId | uint | Who attacked |
| TargetNetworkId | uint | Who was hit |
| HitPoint | Vector3 | Where to show effects |
| StrikeDirection | Vector3 | Direction for effects |
| SkillHash | int | For looking up effects |
| BlockResult | byte | Block/parry result |
| PoiseBroken | bool | For reaction animation |

## Rejection Reasons

| Enum | Description |
|------|-------------|
| None | No rejection (hit valid) |
| TargetNotFound | Target doesn't exist on server |
| AttackerNotFound | Attacker doesn't exist on server |
| OutOfRange | Hit position too far from target |
| InvalidPhase | Not in strike phase |
| TargetInvincible | Target has invincibility |
| TargetDodged | Target was dodging |
| SkillMismatch | Skill doesn't match expected |
| WeaponMismatch | Weapon doesn't match expected |
| AlreadyHit | Target already hit this strike |
| TimestampTooOld | Hit too far in the past |
| CheatSuspected | Suspicious hit pattern |

## Advanced: Custom Validation

Override validation by extending `NetworkMeleeController`:

```csharp
public class MyNetworkMeleeController : NetworkMeleeController
{
    public override NetworkMeleeHitResponse ProcessHitRequest(
        NetworkMeleeHitRequest request, 
        uint clientNetworkId)
    {
        // Custom validation logic
        // e.g., check line of sight, special armor, etc.
        
        return base.ProcessHitRequest(request, clientNetworkId);
    }
}
```

## Optimistic vs Confirmed Effects

**Optimistic Effects (Recommended for action games):**
- Hit effects play immediately on local client
- If server rejects, effects already played (minor visual inconsistency)
- Better game feel, responsive combat

**Confirmed Effects (For competitive/esports):**
- Wait for server confirmation before effects
- Adds ~RTT/2 latency to visual feedback
- 100% accurate to server state

Configure per-character via `NetworkMeleeController.OptimisticEffects`.

## Troubleshooting

### Hits not being intercepted
1. Check `ConditionNetworkMeleeHit` is in skill's "Can Hit" conditions
2. Verify `NetworkMeleeController` is on the attacker character
3. Check network role is initialized correctly

### All hits rejected
1. Check `NetworkMeleeManager` is in scene and initialized
2. Verify `GetCharacterByNetworkIdFunc` is set correctly
3. Check timestamps aren't too old (increase `MaxRewindTime`)

### Effects not playing
1. For local client: Check optimistic effects setting
2. For remote clients: Verify broadcast is being received
3. Check skill effects are configured in GC2 Skill asset

## Version History

- 1.0.0: Initial release
  - Server-authoritative hit validation
  - Lag compensation support
  - GC2 Visual Scripting integration
  - Optimistic/confirmed effects modes
