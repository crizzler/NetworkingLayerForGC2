# Network Abilities Module

Server-authoritative networking solution for the **DaimahouGames Abilities** system.

## Overview

This module provides complete network synchronization for the Abilities system, including:
- **Ability Casting** - Server-validated ability execution
- **Cooldowns** - Server-authoritative cooldown tracking (prevents manipulation)
- **Projectiles** - Server-spawned with client-side visual representation
- **Impacts** - Server-calculated AOE with synchronized hit detection
- **Ability Learning** - Server-validated ability slot management

## Security Modes

This module offers two security levels:

### Option A: Interception-Based (Default)
- **No third-party code modification required**
- Intercepts ability requests and validates on server
- Good protection against casual cheaters
- ⚠️ Determined cheaters could bypass via reflection

### Option C: Full Server Authority (Optional Patch)
- **Requires applying a patch to DaimahouGames Abilities**
- Hooks directly into Caster.Cast() at the source
- Clients cannot bypass validation by calling methods directly
- ✅ Maximum security - true server authority

To enable Option C, use the menu:
```
Game Creator > Networking Layer > Patches > Abilities > Patch (Server Authority)
```

## Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                              CLIENT                                      │
│  ┌──────────────────┐     ┌──────────────────┐     ┌──────────────────┐ │
│  │     Caster       │────▶│ NetworkAbilities │────▶│  Network Layer   │ │
│  │   (Cast Call)    │     │   Controller     │     │  (Your Choice)   │ │
│  └──────────────────┘     └──────────────────┘     └────────┬─────────┘ │
└──────────────────────────────────────────────────────────────┼───────────┘
                                                               │
                                                               ▼
┌──────────────────────────────────────────────────────────────┼───────────┐
│                              SERVER                          │           │
│  ┌──────────────────┐     ┌──────────────────┐     ┌────────┴─────────┐ │
│  │    Validation    │◀────│ NetworkAbilities │◀────│  Network Layer   │ │
│  │  (Requirements)  │     │   Controller     │     │  (Your Choice)   │ │
│  └────────┬─────────┘     └────────┬─────────┘     └──────────────────┘ │
│           │                        │                                     │
│           ▼                        ▼                                     │
│  ┌──────────────────┐     ┌──────────────────┐                          │
│  │  Execute Ability │     │   Broadcast to   │                          │
│  │   (Server-side)  │     │   All Clients    │                          │
│  └──────────────────┘     └──────────────────┘                          │
└──────────────────────────────────────────────────────────────────────────┘
```

## File Structure

```
Networking/Abilities/
├── NetworkAbilitiesTypes.cs      # Network data structures
├── NetworkAbilitiesController.cs # Server-authoritative controller
├── NetworkAbilitiesManager.cs    # Message routing & convenience API
├── NetworkAbilitiesPatchHooks.cs # Runtime hooks for patched mode
└── README.md                     # This file

Editor/GameCreator2/Patches/
├── AbilitiesPatcher.cs           # Patcher for server authority mode
└── Backups/                      # Backup files (created when patching)
```

## Quick Start

### Where To Add Components

- Add exactly one `NetworkAbilitiesController` to a shared bootstrap/manager GameObject in scene.
- Do not add `NetworkAbilitiesController` per player character.
- The Setup Wizard `Scene Objects` step can create/reuse the shared controller when `Ensure NetworkAbilitiesController` is enabled.
- `NetworkAbilitiesManager` is a static API/service (`NetworkAbilitiesManager.cs`), not a `MonoBehaviour` component. Do not add it to a GameObject.

### NetworkAbilitiesManager Role

`NetworkAbilitiesManager` is the transport-facing facade for Abilities. It owns:

- Message type IDs (`MessageTypes`) used by your transport router.
- Asset registries (`Ability`, `Projectile`, `Impact`) for hash-to-asset resolution.
- Pawn ↔ network-id tracking (`RegisterPawn`, `UnregisterPawn`).
- Incoming packet routing (`RouteIncomingMessage(...)`) into `NetworkAbilitiesController`.
- Convenience gameplay API (`RequestCast`, `RequestLearn`, cooldown queries, server spawn helpers).

Typical lifecycle:

1. Call `NetworkAbilitiesManager.Initialize(...)` once when your network session starts.
2. Register ability/projectile/impact assets used by this session.
3. Register pawns on spawn and unregister on despawn.
4. Forward received Abilities packets to `RouteIncomingMessage(...)`.
5. Call `NetworkAbilitiesManager.Clear()` on session shutdown.

### 1. Initialize Manager & Registries

```csharp
// In your network manager initialization
void OnNetworkStart()
{
    // Initialize the manager with time and player ID providers
    NetworkAbilitiesManager.Initialize(
        getServerTime: () => NetworkTime.ServerTime,
        getLocalPlayerNetworkId: () => NetworkManager.LocalClient.Id
    );
    
    // Register abilities that can be used
    NetworkAbilitiesManager.RegisterAbility(fireballAbility);
    NetworkAbilitiesManager.RegisterAbility(healAbility);
    NetworkAbilitiesManager.RegisterProjectile(fireballProjectile);
    NetworkAbilitiesManager.RegisterImpact(explosionImpact);
}

// When a pawn spawns on network
void OnPawnSpawned(Pawn pawn, uint networkId)
{
    NetworkAbilitiesManager.RegisterPawn(pawn, networkId);
}

// When a pawn despawns
void OnPawnDespawned(Pawn pawn)
{
    NetworkAbilitiesManager.UnregisterPawn(pawn);
}

// Route inbound transport packets (messageId -> payload already deserialized)
void OnAbilitiesPacket(ushort messageId, object payload, uint senderClientNetworkId)
{
    NetworkAbilitiesManager.RouteIncomingMessage(messageId, payload, senderClientNetworkId);
}

// On session shutdown
void OnNetworkStop()
{
    NetworkAbilitiesManager.Clear();
}
```

### 2. Controller Setup

```csharp
// Add NetworkAbilitiesController to your scene
var controller = gameObject.AddComponent<NetworkAbilitiesController>();

// Initialize based on role
if (IsServer)
    controller.InitializeAsServer();
else
    controller.InitializeAsClient();

// If controller was created after Initialize(...), wire manager lookups explicitly.
NetworkAbilitiesManager.WireUpController(controller);

// Wire up network delegates
controller.SendCastRequestToServer = (request) => 
    SendToServer(NetworkAbilitiesManager.MessageTypes.AbilityCastRequest, request);

controller.BroadcastCastToClients = (broadcast) => 
    BroadcastToClients(NetworkAbilitiesManager.MessageTypes.AbilityCastBroadcast, broadcast);

// ... wire up other delegates

void SendToServer<TPayload>(ushort messageType, TPayload payload) { /* transport send C->S */ }
void BroadcastToClients<TPayload>(ushort messageType, TPayload payload) { /* transport send S->all */ }
```

### 3. Using the System

```csharp
// Client requests ability cast
NetworkAbilitiesManager.RequestCast(playerPawn, fireballAbility, target, response =>
{
    if (response.Approved)
        Debug.Log("Cast approved!");
    else
        Debug.Log($"Cast rejected: {response.RejectReason}");
});

// Or with convenience methods
NetworkAbilitiesManager.RequestCastAtPosition(playerPawn, aoeAbility, groundPosition);
NetworkAbilitiesManager.RequestCastAtTarget(playerPawn, targetedAbility, enemyPawn);

// Check cooldowns
if (NetworkAbilitiesManager.IsOnCooldown(playerPawn, ability))
{
    float remaining = NetworkAbilitiesManager.GetCooldownRemaining(playerPawn, ability);
    Debug.Log($"On cooldown: {remaining}s remaining");
}

// Learn/Unlearn abilities
NetworkAbilitiesManager.RequestLearn(playerPawn, newAbility, slotIndex);
NetworkAbilitiesManager.RequestUnlearn(playerPawn, slotIndex);
```

## Message Types

Reserved message type IDs: **230-259**

| ID  | Message                     | Direction          |
|-----|-----------------------------|--------------------|
| 230 | AbilityCastRequest          | Client → Server    |
| 231 | AbilityCastResponse         | Server → Client    |
| 232 | AbilityCastBroadcast        | Server → All       |
| 233 | AbilityEffectBroadcast      | Server → All       |
| 234 | CastCancelRequest           | Client → Server    |
| 235 | ProjectileSpawnBroadcast    | Server → All       |
| 236 | ProjectileEventBroadcast    | Server → All       |
| 238 | ImpactSpawnBroadcast        | Server → All       |
| 239 | ImpactHitBroadcast          | Server → All       |
| 240 | CooldownRequest             | Client → Server    |
| 241 | CooldownResponse            | Server → Client    |
| 242 | CooldownBroadcast           | Server → All       |
| 243 | AbilityLearnRequest         | Client → Server    |
| 244 | AbilityLearnResponse        | Server → Client    |
| 245 | AbilityLearnBroadcast       | Server → All       |
| 246 | AbilityStateRequest         | Client → Server    |
| 247 | AbilityStateResponse        | Server → Client    |

## Server Validation

The server validates:
- **Caster exists** and has the Caster feature
- **Ability is known** (in one of the caster's slots)
- **Cooldown check** (server-tracked, not client)
- **Requirements met** (via RuntimeAbility.CanUse)
- **Range validation** (with grace for latency)
- **Cast queue** (prevents ability spam)

## Integration with Ability Effects

For projectile and impact effects in networked games, use the server spawn methods:

```csharp
// In your custom ability effect
public class NetworkedProjectileEffect : AbilityEffect
{
    [SerializeField] private Projectile m_Projectile;
    
    protected override void Apply_Internal(ExtendedArgs args)
    {
        // Get cast ID from args (server sets this)
        uint castId = args.Get<CastContext>()?.CastInstanceId ?? 0;
        
        // Use network spawn instead of direct Projectile.Get()
        NetworkAbilitiesManager.ServerSpawnProjectile(
            castId,
            m_Projectile,
            spawnPosition,
            direction,
            targetPosition,
            targetPawn
        );
    }
}
```

## Statistics & Debugging

```csharp
// Get statistics
var stats = NetworkAbilitiesManager.GetControllerStats();
Debug.Log($"Total casts: {stats.TotalCastRequests}");
Debug.Log($"Approved: {stats.ApprovedCasts}");
Debug.Log($"Rejected: {stats.RejectedCasts}");
Debug.Log($"Rejected (cooldown): {stats.RejectedOnCooldown}");

// Get current state
var state = NetworkAbilitiesManager.GetSystemState();
Debug.Log($"Active casters: {state.ActiveCasters}");
Debug.Log($"Ongoing casts: {state.OngoingCasts}");
Debug.Log($"Active projectiles: {state.ActiveProjectiles}");
```

## Inspector Settings

The `NetworkAbilitiesController` exposes these settings:

### Cast Settings
- **Allow Client Prediction** - Show cast effects before server response
- **Max Cast Queue Size** - Prevent ability spam (default: 2)
- **Range Validation Grace** - Extra range for latency (default: 0.5m)

### Cooldown Settings
- **Cooldown Buffer** - Server adds this to prevent edge-case exploits

### Projectile Settings
- **Max Projectiles Per Character** - Limit for performance
- **Cleanup Interval** - How often to clean stale projectiles

### Impact Settings
- **Max Concurrent Impacts** - Global limit for AOE impacts

## Best Practices

1. **Register all abilities** during network initialization
2. **Use server spawn methods** for projectiles/impacts in networked games
3. **Don't modify cooldowns locally** - server is authoritative
4. **Check IsOnCooldown** before showing UI elements
5. **Handle rejection gracefully** - show feedback to player
6. **Clean up on disconnect** - call `NetworkAbilitiesManager.Clear()`

## Server Authority Patch System

### Applying the Patch

1. Open Unity
2. Go to `Game Creator > Networking Layer > Patches > Abilities > Patch (Server Authority)`
3. Read the confirmation dialog and click "Apply Patch"
4. Wait for Unity to recompile

### What the Patch Does

The patch modifies `Caster.cs` to add:
- Static validation hooks (`NetworkCastValidator`, `NetworkLearnValidator`, `NetworkUnLearnValidator`)
- Completion callbacks (`NetworkCastCompleted`)
- Direct execution methods (`LearnDirect`, `UnLearnDirect`) for server-side use

### Checking Patch Status

```
Game Creator > Networking Layer > Patches > Abilities > Check Status
```

### Removing the Patch

```
Game Creator > Networking Layer > Patches > Abilities > Unpatch
```

This restores the original files from the backup created during patching.

### Using Patched Mode

When the patch is applied, add `NetworkAbilitiesPatchHooks` to enable the hooks:

```csharp
// After NetworkAbilitiesController initialization
var patchHooks = gameObject.AddComponent<NetworkAbilitiesPatchHooks>();
patchHooks.Initialize(isServer, isClient);

// Check if patched mode is active
if (NetworkAbilitiesPatchHooks.IsCasterPatched())
{
    Debug.Log("Server authority patch is active");
}
```

### Compatibility Notes

- Backups are created before patching
- Safe to update DaimahouGames Abilities (unpatch first, update, re-patch)
- Patch version is tracked to detect incompatibilities
- Works alongside the interception-based system as a security enhancement

## Compatibility

- Works with existing Ability definitions (no modifications needed)
- Integrates with existing Caster feature
- Supports all activator types (Single, Channeled)
- Compatible with targeting system
- Works alongside other network modules (Stats, Inventory, etc.)
