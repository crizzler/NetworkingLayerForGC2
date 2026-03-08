# GC2 Shooter Network Integration

Server-authoritative shooter combat networking for Game Creator 2.

## Overview

This module provides network-aware shooter combat for GC2, enabling server-authoritative shot and hit validation with lag compensation. It integrates seamlessly with the base GC2 Network Integration and supports raycast, projectile, and all GC2 shot types.

## Requirements

- Game Creator 2 Core
- Game Creator 2 Shooter Module
- GC2 Network Integration (base module)
- A configured transport adapter (NGO/FishNet/Mirror/custom)

## Installation

1. Import the GC2 Network Integration base module
2. Import this GC2 Shooter Network module
3. The module will auto-detect GC2 Shooter via the `GC2_SHOOTER` define symbol

## Architecture

### Network Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│                       SHOOTER NETWORK FLOW                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  LOCAL CLIENT                    SERVER                    REMOTE CLIENT│
│  ────────────                    ──────                    ─────────────│
│                                                                         │
│  1. Player fires weapon          │                         │            │
│     ↓                            │                         │            │
│  2. NetworkShooterController     │                         │            │
│     intercepts shot              │                         │            │
│     ↓                            │                         │            │
│  3. Send shot request ─────────► 4. Validate shot          │            │
│     ↓                            │  (ammo, cooldown, etc)  │            │
│  [Optimistic tracer/muzzle]      │     ↓                   │            │
│     ↓                            │  5. Broadcast shot ────► 6. Play FX  │
│  4. Raycast/projectile detects   │                         │            │
│     hit                          │                         │            │
│     ↓                            │                         │            │
│  5. ConditionNetworkShooterHit   │                         │            │
│     intercepts via CanHit        │                         │            │
│     ↓                            │                         │            │
│  6. Send hit request ──────────► 7. Validate hit           │            │
│     ↓                            │  (lag compensation)     │            │
│  [Optimistic impact FX]          │     ↓                   │            │
│                                  │  8. Apply damage        │            │
│                                  │     ↓                   │            │
│  9. Receive response ◄─────────── 10. Broadcast hit ──────► 11. Play FX│
│                                  │                         │            │
└─────────────────────────────────────────────────────────────────────────┘
```

### Two-Phase Validation

Unlike melee, shooter combat uses **two-phase validation**:

1. **Shot Validation**: When trigger is pulled
   - Validates: ammo, weapon state, cooldown, position
   - Results in: muzzle flash, tracer, sound
   
2. **Hit Validation**: When shot hits something
   - Validates: hit position, target state, obstruction
   - Results in: damage, impact effects, reactions

This allows for accurate projectile-based weapons where the hit happens after the shot.

### Components

#### NetworkShooterManager
Global singleton that coordinates all shooter networking.

```csharp
// Add to your NetworkManager or scene root
[AddComponentMenu("Game Creator/Network/Shooter/Network Shooter Manager")]
```

**Setup:**
```csharp
var shooterManager = NetworkShooterManager.Instance;

// Client -> Server
shooterManager.SendShotRequestToServer = (request) => {
    SendShotRequestToServer(request);
};

shooterManager.SendHitRequestToServer = (request) => {
    SendHitRequestToServer(request);
};

// Server -> Client
shooterManager.SendShotResponseToClient = (clientId, response) => {
    SendShotResponseToClient(clientId, response);
};

shooterManager.SendHitResponseToClient = (clientId, response) => {
    SendHitResponseToClient(clientId, response);
};

// Server -> All Clients
shooterManager.BroadcastShotToAllClients = (broadcast) => {
    BroadcastShotToClients(broadcast);
};

shooterManager.BroadcastHitToAllClients = (broadcast) => {
    BroadcastHitToClients(broadcast);
};

void SendShotRequestToServer(NetworkShotRequest request) { /* serialize + send C->S */ }
void SendHitRequestToServer(NetworkShooterHitRequest request) { /* serialize + send C->S */ }
void SendShotResponseToClient(uint clientId, NetworkShotResponse response) { /* send S->C target */ }
void SendHitResponseToClient(uint clientId, NetworkShooterHitResponse response) { /* send S->C target */ }
void BroadcastShotToClients(NetworkShotBroadcast broadcast) { /* send S->all */ }
void BroadcastHitToClients(NetworkShooterHitBroadcast broadcast) { /* send S->all */ }
```

#### NetworkShooterController
Per-character component that handles shot and hit interception.

```csharp
// Add to each character with shooter weapons
[AddComponentMenu("Game Creator/Network/Shooter/Network Shooter Controller")]
```

**Properties:**
- `Optimistic Shot Effects`: Show tracer/muzzle flash before server confirmation
- `Optimistic Hit Effects`: Show impact effects before server confirmation
- `Weapon State Sync Interval`: How often to sync ammo/reload/jam state
- `Aim State Sync Interval`: How often to sync aim direction

#### ConditionNetworkShooterHit (Visual Scripting)
A GC2 Condition that intercepts hits in the ShooterWeapon's "Can Hit" conditions.

**Usage:**
1. Open your ShooterWeapon asset
2. Find the "Can Hit" conditions section
3. Add the "Network Shooter Hit" condition

## Setup Guide

### Step 1: Scene Setup

1. Add `NetworkShooterManager` to your scene
2. Configure the network delegates

### Step 2: Character Setup

For each networked character with shooter weapons:

1. Add `NetworkCharacter` component
   - Set Combat Mode = **Disabled**
   
2. Add `NetworkShooterController` component
   - Configure optimistic effects preferences

### Step 3: Weapon Setup

For each ShooterWeapon that should use network hit validation:

1. Open the ShooterWeapon ScriptableObject
2. In "Can Hit" conditions, add **Network Shooter Hit** condition

### Step 4: Network Transport

Connect the manager to your transport adapter. Optional NGO example:
This sample follows NGO 2.10 unified RPC API (`[Rpc]`, `RpcParams`, `RpcTarget`).

```csharp
// using Unity.Netcode;
public class ShooterNetworkBridge : NetworkBehaviour
{
    private void Start()
    {
        var manager = NetworkShooterManager.Instance;
        
        manager.SendShotRequestToServer = SendShotRequestRpc;
        manager.SendHitRequestToServer = SendHitRequestRpc;
        manager.SendShotResponseToClient = SendShotResponse;
        manager.SendHitResponseToClient = SendHitResponse;
        manager.BroadcastShotToAllClients = BroadcastShotRpc;
        manager.BroadcastHitToAllClients = BroadcastHitRpc;
    }
    
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SendShotRequestRpc(NetworkShotRequest request, RpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        NetworkShooterManager.Instance.ReceiveShotRequest((uint)clientId, request);
    }
    
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SendHitRequestRpc(NetworkShooterHitRequest request, RpcParams rpcParams = default)
    {
        ulong clientId = rpcParams.Receive.SenderClientId;
        NetworkShooterManager.Instance.ReceiveHitRequest((uint)clientId, request);
    }
    
    [Rpc(SendTo.ClientsAndHost)]
    private void BroadcastShotRpc(NetworkShotBroadcast broadcast)
    {
        NetworkShooterManager.Instance.ReceiveShotBroadcast(broadcast);
    }
    
    [Rpc(SendTo.ClientsAndHost)]
    private void BroadcastHitRpc(NetworkShooterHitBroadcast broadcast)
    {
        NetworkShooterManager.Instance.ReceiveHitBroadcast(broadcast);
    }
    
    [Rpc(SendTo.SpecifiedInParams)]
    private void SendShotResponseRpc(NetworkShotResponse response, RpcParams rpcParams = default)
    {
        NetworkShooterManager.Instance.ReceiveShotResponse(response);
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void SendHitResponseRpc(NetworkShooterHitResponse response, RpcParams rpcParams = default)
    {
        NetworkShooterManager.Instance.ReceiveHitResponse(response);
    }

    private void SendShotResponse(uint clientId, NetworkShotResponse response)
    {
        SendShotResponseRpc(
            response,
            RpcTarget.Single((ulong) clientId, RpcTargetUse.Temp)
        );
    }

    private void SendHitResponse(uint clientId, NetworkShooterHitResponse response)
    {
        SendHitResponseRpc(
            response,
            RpcTarget.Single((ulong) clientId, RpcTargetUse.Temp)
        );
    }
}
```

## Data Types

### NetworkShotRequest (~40 bytes)
Sent from client to server when a shot is fired.

| Field | Type | Description |
|-------|------|-------------|
| RequestId | ushort | Unique ID for response matching |
| ClientTimestamp | float | When shot was fired |
| ShooterNetworkId | uint | Shooter's network ID |
| MuzzlePosition | Vector3 | Muzzle position at fire time |
| ShotDirection | Vector3 | Direction with spread applied |
| WeaponHash | int | Hash of weapon used |
| SightHash | int | Hash of sight used |
| ChargeRatio | float | For charged weapons (0-1) |
| ProjectileIndex | byte | Index for multi-projectile |
| TotalProjectiles | byte | Total projectiles in shot |

### NetworkShooterHitRequest (~36 bytes)
Sent when a shot hits something.

| Field | Type | Description |
|-------|------|-------------|
| RequestId | ushort | Unique ID |
| ClientTimestamp | float | When hit detected |
| ShooterNetworkId | uint | Who fired |
| TargetNetworkId | uint | Who was hit (0 for environment) |
| HitPoint | Vector3 | World position of hit |
| HitNormal | Vector3 | Surface normal |
| Distance | float | Distance from muzzle |
| WeaponHash | int | Weapon used |
| PierceIndex | byte | Pierce order (0 = first) |
| IsCharacterHit | bool | Hit a character vs environment |

### NetworkShotBroadcast (~32 bytes)
Broadcast when shot is confirmed.

| Field | Type | Description |
|-------|------|-------------|
| ShooterNetworkId | uint | Who fired |
| MuzzlePosition | Vector3 | Muzzle position |
| ShotDirection | Vector3 | Shot direction |
| WeaponHash | int | For effects lookup |
| SightHash | int | For effects lookup |
| HitPoint | Vector3 | Final hit point (for tracer) |
| DidHit | bool | Whether it hit something |

### NetworkShooterHitBroadcast (~28 bytes)
Broadcast when hit is confirmed.

| Field | Type | Description |
|-------|------|-------------|
| ShooterNetworkId | uint | Who fired |
| TargetNetworkId | uint | Who was hit |
| HitPoint | Vector3 | Hit position |
| HitNormal | Vector3 | Hit normal |
| WeaponHash | int | For effects |
| BlockResult | byte | Block/parry result |
| MaterialHash | int | For impact sound |

### NetworkWeaponState (~12 bytes)
Synced periodically for weapon status.

| Field | Type | Description |
|-------|------|-------------|
| WeaponHash | int | Equipped weapon |
| SightHash | int | Current sight |
| AmmoInMagazine | ushort | Current ammo |
| StateFlags | byte | Bitflags for state |

**State Flags:**
- `0x01`: Is Reloading
- `0x02`: Is Jammed
- `0x04`: Is Aiming
- `0x08`: Is Shooting
- `0x10`: Is Charging

## Rejection Reasons

### Shot Rejection

| Enum | Description |
|------|-------------|
| ShooterNotFound | Shooter doesn't exist on server |
| WeaponNotEquipped | Weapon not equipped |
| NoAmmo | Out of ammunition |
| WeaponJammed | Weapon is jammed |
| CooldownActive | Fire rate limit |
| InvalidPosition | Suspicious muzzle position |
| InvalidDirection | Suspicious shot direction |
| TimestampTooOld | Request too far in past |
| RateLimitExceeded | Firing too fast |
| CheatSuspected | Anti-cheat triggered |

### Hit Rejection

| Enum | Description |
|------|-------------|
| ShotNotValidated | Associated shot was rejected |
| TargetNotFound | Target doesn't exist |
| OutOfRange | Hit too far from shot |
| ObstructionDetected | Something blocking shot |
| TargetInvincible | Target has invincibility |
| TargetDodged | Target was dodging |
| AlreadyHit | Duplicate hit detection |
| TimestampTooOld | Too far in past |
| CheatSuspected | Anti-cheat triggered |

## Weapon State Synchronization

The controller automatically syncs weapon state (ammo, reload, jam) at configurable intervals:

```csharp
// Sync weapon state every 0.5 seconds
[SerializeField] private float m_WeaponStateSyncInterval = 0.5f;

// Sync aim state every 0.1 seconds (for smooth aiming)
[SerializeField] private float m_AimStateSyncInterval = 0.1f;
```

Subscribe to state changes:

```csharp
controller.OnWeaponStateChanged += (state) => {
    // Update UI, sync to clients, etc.
};
```

## Advanced: Custom Shot Types

If you create custom TShot implementations, integrate network awareness:

```csharp
public class MyNetworkShot : TShot
{
    public override bool Run(Args args, ShooterWeapon weapon, ...)
    {
        var controller = args.Self.Get<NetworkShooterController>();
        
        // Before firing
        if (controller != null)
        {
            bool shouldProceed = controller.InterceptShot(
                muzzlePosition,
                direction,
                weapon,
                chargeRatio,
                projectileIndex,
                totalProjectiles
            );
            
            if (!shouldProceed) return false;
        }
        
        // ... normal shot logic ...
        
        // On hit
        if (controller != null)
        {
            bool shouldApplyHit = controller.InterceptHit(
                hitObject,
                hitPoint,
                hitNormal,
                distance,
                weapon,
                pierceIndex
            );
            
            if (!shouldApplyHit) return true; // Shot fired, but hit handled by network
        }
        
        // ... normal hit logic ...
    }
}
```

## Troubleshooting

### Shots not registering on server
1. Check `NetworkShooterManager` is in scene
2. Verify network delegates are connected
3. Check ammo sync - server may think you have no ammo

### Tracer not showing on remote clients
1. Verify `BroadcastShotToAllClients` is connected
2. Check remote clients are receiving broadcasts
3. Ensure weapon effects are configured

### Hit effects delayed
1. Check optimistic effects setting
2. Verify network latency
3. Consider enabling optimistic for better feel

### Ammo desynced between client/server
1. Check `WeaponStateSyncInterval` setting
2. Ensure server is authoritative for ammo
3. Use `OnWeaponStateChanged` event to sync

## Transport Agnostic Design

Like the Melee module, this is **completely transport-agnostic**:

| Transport | Implementation |
|-----------|----------------|
| **Netcode for GameObjects** | `[Rpc(SendTo.*)]` + `RpcTarget.Single(...)` |
| **FishNet** | `[ServerRpc]`, `[ObserversRpc]` |
| **Mirror** | `[Command]`, `[ClientRpc]` |
| **Photon Fusion** | `[Rpc]` attributes |

You implement the bridge layer; we handle the combat logic.

## Version History

- 1.0.0: Initial release
  - Two-phase shot/hit validation
  - All GC2 shot types supported
  - Weapon state synchronization
  - Aim state synchronization
  - Pierce/penetration support
  - Optimistic/confirmed effects modes
