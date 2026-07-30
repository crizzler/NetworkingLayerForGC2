# GC2 Melee Network Integration

Server-authoritative melee combat networking for Game Creator 2.

For melee hit presentation and the broader multiplayer prefab decision guide,
see [Spawning Prefabs and UI in Multiplayer](../Documentation/spawning-prefabs-and-ui-in-multiplayer.md).

## Overview

This module provides network-aware melee combat for GC2, enabling server-authoritative hit validation with lag compensation. It integrates seamlessly with the base GC2 Network Integration and requires it as a dependency.

## Requirements

- Game Creator 2 Core
- Game Creator 2 Melee Module
- GC2 Network Integration (base module)
- PurrNet integration or a configured custom transport adapter (NGO/FishNet/Mirror/custom)
- Game Creator 2 Melee `2.2.x`
- The required GC2 Melee source patch (`3.5.0-melee`)

## Installation

1. Import the GC2 Network Integration base module
2. Import this GC2 Melee Network module
3. The module will auto-detect GC2 Melee via the `GC2_MELEE` define symbol

## PurrNet Scene Setup Wizard

For PurrNet projects, enable **Melee** on the PurrNet wizard Modules page. The wizard applies/verifies the required source patch, then creates/reuses `NetworkMeleeManager` and `PurrNetMeleeTransportBridge`. Setup is blocked when the patch is missing or stale.

Updating or reinstalling GC2 Melee can overwrite the injected source hooks. The Networking Layer
keeps its editor/runtime assemblies compilable in that state so the wizard can reapply the patch,
but networked Melee fails closed until patch validation succeeds.

When a Player Prefab is assigned on the Scene page and prefab preparation is enabled, selecting Melee adds `NetworkMeleeController` to that prefab. If Stats is also selected, the Core page can add the optional Melee -> Stats damage bridge.

The PurrNet path synchronizes skill input, skill validation/broadcasts, hit validation, hit responses, hit reactions, and reaction root motion. Hit reactions that launch characters upward, such as air-launch clips driven by root motion, should run through the networked reaction path so the authoritative motion driver accepts the vertical displacement instead of correcting it away.

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
│  3. Patched AttackSkill hook     │                         │            │
│     intercepts after CanHit      │                         │            │
│     ↓                            │                         │            │
│  4. NetworkMeleeController       │                         │            │
│     sends hit request ─────────► 5. Validate hit           │            │
│     ↓                            │  (lag compensation)     │            │
│  [Optimistic effects]            │     ↓                   │            │
│                                  │  6. Apply damage and    │            │
│                                  │     target reaction     │            │
│  7. Receive response ◄─────────── 8. Send/broadcast ──────► 9. React/FX│
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
    SendHitRequestToServer(request);
};

// Server -> Client
meleeManager.SendHitResponseToClient = (clientId, response) => {
    SendHitResponseToClient(clientId, response);
};

// Server -> All Clients
meleeManager.BroadcastHitToAllClients = (broadcast) => {
    BroadcastHitToClients(broadcast);
};

// Helper delegates
meleeManager.GetCharacterByNetworkIdFunc = (id) => {
    return NetworkTransportBridge.Active != null
        ? NetworkTransportBridge.Active.ResolveCharacter(id)
        : null;
};

void SendHitRequestToServer(NetworkMeleeHitRequest request) { /* serialize + send C->S */ }
void SendHitResponseToClient(uint clientId, NetworkMeleeHitResponse response) { /* send S->C target */ }
void BroadcastHitToClients(NetworkMeleeHitBroadcast broadcast) { /* send S->all */ }
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

#### ConditionNetworkMeleeHit (legacy Visual Scripting fallback)
Upgraded Skill assets may keep this condition. The required AttackSkill patch now intercepts every networked strike automatically, so new Skills do not need it.

When present, the condition queues the same request before the automatic hook is reached. It always suppresses native gameplay for a network character. Optimistic feedback is played through the presentation registry and never by replaying `Skill.OnHit`.

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

### Step 3: Patch and Skill Setup

1. Apply the required Melee patch from the PurrNet wizard or **Game Creator > Networking Layer > Patches > Melee > Patch (Server Authority)**.
2. Register every remotely resolved Skill/weapon on all peers.
3. Configure presentation-only hit effects on `NetworkMeleeManager` when desired.

Do not add `ConditionNetworkMeleeHit` to new Skills. Existing conditions can remain during migration.

Server damage hooks and reactions are independent. `NetworkMeleeStatsDamageBridge`, `TryApplyDamageFunc`, and `ApplyDamageFunc` only modify damage; returning handled cannot suppress the target's authored reaction. Use `TryApplyAuthoritativeReactionFunc` only when replacing the complete GC2 reaction yourself. The current source patch emits the reaction broadcast immediately after GC2 enters its Reaction phase, so even a very short reaction cannot be missed by a frame poll.

### Step 4: Network Transport

Connect the manager to your transport adapter. Optional NGO example:
This sample follows NGO 2.10 unified RPC API (`[Rpc]`, `RpcParams`, `RpcTarget`).

```csharp
// using Unity.Netcode;
public class MeleeNetworkBridge : NetworkBehaviour
{
    private void Start()
    {
        var manager = NetworkMeleeManager.Instance;

        manager.SendHitRequestToServer = SendHitRequestRpc;
        manager.SendHitResponseToClient = SendResponseToClient;
        manager.BroadcastHitToAllClients = BroadcastHitRpc;
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SendHitRequestRpc(NetworkMeleeHitRequest request, RpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        NetworkMeleeManager.Instance.ReceiveHitRequest((uint)clientId, request);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void SendHitResponseRpc(NetworkMeleeHitResponse response, RpcParams rpcParams = default)
    {
        NetworkMeleeManager.Instance.ReceiveHitResponse(response);
    }

    [Rpc(SendTo.ClientsAndHost)]
    private void BroadcastHitRpc(NetworkMeleeHitBroadcast broadcast)
    {
        NetworkMeleeManager.Instance.ReceiveHitBroadcast(broadcast);
    }

    private void SendResponseToClient(uint clientId, NetworkMeleeHitResponse response)
    {
        SendHitResponseRpc(
            response,
            RpcTarget.Single((ulong) clientId, RpcTargetUse.Temp)
        );
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
1. Check the wizard reports the required Melee patch as applied
2. Verify `NetworkMeleeController` is on the attacker character
3. Check the transport ownership registry and network role are initialized before attacks are enabled

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
