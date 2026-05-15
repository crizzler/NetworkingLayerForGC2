# Lag Compensation

A network-agnostic lag compensation library for Unity multiplayer games.

## Overview

Lag Compensation provides server-authoritative hit validation through position history tracking and temporal rewinding. It works with any networking solution (Netcode for GameObjects, Photon, FishNet, Mirror, etc.).

## Features

- **Network-Agnostic**: No dependencies on specific networking libraries
- **Position History**: Per-entity circular buffers with configurable size
- **Temporal Interpolation**: Smooth position reconstruction between snapshots
- **Hit Validation**: Raycast, sphere, cone, and melee hit validation
- **Hit Zones**: Support for damage multipliers per body region
- **High Performance**: Pooled allocations, batch operations for crowds

## PurrNet Setup

The PurrNet Scene Setup Wizard does not expose Lag Compensation as a separate module checkbox. It creates the combat managers and bridges that use lag compensation during server validation, while lag compensation history is still configured through `LagCompensationBootstrap` or direct `LagCompensationManager.Initialize(...)`.

For Shooter, Melee, and Abilities sessions, add `LagCompensationBootstrap` to a persistent server/host object when you need rewind validation for custom entities. Characters can use `CharacterLagCompensation` to register their body bounds and hit zones.

## Quick Start

### 1. Initialize the Manager

```csharp
// On server startup
var config = new LagCompensationConfig
{
    historySize = 64,        // Snapshots per entity
    snapshotRate = 60,       // Hz
    maxRewindTime = 0.5f,    // Max rewind in seconds
    hitTolerance = 0.3f      // Extra tolerance (meters)
};
LagCompensationManager.Initialize(config);
```

### 2. Implement ILagCompensated

```csharp
public class MyNetworkedEntity : MonoBehaviour, ILagCompensated
{
    public uint NetworkId => /* your network ID */;
    public Vector3 Position => transform.position;
    public Quaternion Rotation => transform.rotation;
    public Bounds Bounds => /* your collider bounds */;
    public bool IsActive => gameObject.activeInHierarchy;
    public float Radius => 0.5f;
    public float Height => 1.8f;
}
```

### 3. Register/Unregister Entities

```csharp
void OnSpawn()
{
    LagCompensationManager.Instance.Register(this);
}

void OnDespawn()
{
    LagCompensationManager.Instance.Unregister(this);
}
```

### 4. Record Frames (Server)

```csharp
// In your network tick loop
void FixedUpdate()
{
    var timestamp = new NetworkTimestamp(NetworkTime.ServerTime);
    LagCompensationManager.Instance.RecordFrame(timestamp);
}
```

### 5. Validate Hits (Server)

```csharp
[ServerRpc]
void OnShootServerRpc(Vector3 origin, Vector3 direction, 
    float clientTime, ServerRpcParams rpcParams = default)
{
    var timestamp = new NetworkTimestamp(clientTime);
    
    // Perform lag-compensated raycast
    var hits = HitValidationUtility.RaycastAll(
        origin, direction, 100f, timestamp
    );
    
    foreach (var hit in hits)
    {
        if (hit.isValid)
        {
            ApplyDamage(hit.targetNetworkId, hit.hitZoneMultiplier);
        }
    }
}
```

### Alternative: Use the LagCompensationBootstrap Component

Instead of writing steps 1 and 4 by hand, add the **LagCompensationBootstrap**
MonoBehaviour to a persistent GameObject in your scene. It handles initialization
and per-tick recording automatically.

1. Add `LagCompensationBootstrap` to your NetworkManager (or any persistent object).
2. Configure history size, snapshot rate, rewind time, and tolerance in the Inspector.
3. Tell it when the server starts:

```csharp
// From your networking solution's server/host start callback:
GetComponent<LagCompensationBootstrap>().SetServerMode(true);

// Or enable "Treat As Server" in the Inspector for local testing.
```

The bootstrap will:
- Call `LagCompensationManager.Initialize()` with your Inspector config
- Call `RecordFrame()` every `FixedUpdate` on the server
- Dispose the manager on `OnDestroy`

> **Important:** Without initialization, `CharacterLagCompensation.Register()`
> silently skips registration when `ServerOnly` is true (the default), because it
> checks `LagCompensationManager.IsInitialized` first. Without `RecordFrame()`,
> the history buffer stays empty and hit validation cannot rewind time.

## API Reference

### Interfaces

| Interface | Description |
|-----------|-------------|
| `ILagCompensated` | Base interface for any entity needing lag compensation |
| `ILagCompensatedWithHitZones` | Extended interface with body-region hit zones |

### Types

| Type | Description |
|------|-------------|
| `NetworkTimestamp` | Server time wrapper with tick support |
| `RTTTracker` | Round-trip time tracking for latency calculation |
| `HitValidationResult` | Result of hit validation with details |
| `LagCompensatedHitZone` | Hit zone definition (name, bounds, multiplier) |

### Classes

| Class | Description |
|-------|-------------|
| `LagCompensationManager` | Singleton coordinator for all tracked entities |
| `LagCompensationHistory` | Per-entity circular buffer for state snapshots |
| `LagCompensationConfig` | Configuration settings |
| `HitValidationUtility` | Static hit validation utilities |

### HitValidationUtility Methods

```csharp
// Raycast against all tracked entities at historical time
List<HitValidationResult> RaycastAll(origin, direction, maxDistance, timestamp);

// Sphere overlap at historical time
List<HitValidationResult> OverlapSphereAll(center, radius, timestamp);

// Box overlap at historical time
List<HitValidationResult> OverlapBoxAll(center, halfExtents, orientation, timestamp);

// Cone overlap (for shotgun spread, etc.)
List<HitValidationResult> OverlapConeAll(origin, direction, angle, maxDistance, timestamp);

// Validate hitscan weapon
HitValidationResult ValidateHitscan(shooterNetworkId, origin, direction, 
    maxDistance, clientTimestamp);

// Validate projectile hit
HitValidationResult ValidateProjectileHit(shooterNetworkId, targetNetworkId, 
    hitPoint, projectileSpawnTime, hitTime, projectileSpeed);

// Validate melee swing
List<HitValidationResult> ValidateMeleeSwing(attackerNetworkId, swingOrigin, 
    swingDirection, swingArc, swingRange, clientTimestamp);
```

## Integration Examples

### Game Creator 2

Use `CharacterLagCompensation` component:
- Add to your networked Character prefab
- Automatically provides Head/Torso/Legs hit zones
- Integrates with GC2's Character component

### Enemy Masses

Use `EnemyMassesLagCompensation` component:
- Add to your EnemyMassesRuntime GameObject
- Efficiently tracks large crowds with pooled adapters
- Batch recording and validation


## Configuration

### LagCompensationConfig

| Property | Default | Description |
|----------|---------|-------------|
| `historySize` | 64 | Snapshots per entity |
| `snapshotRate` | 60 | Recording frequency (Hz) |
| `maxHistoryAge` | 1.0 | Max age before cleanup (seconds) |
| `hitTolerance` | 0.2 | Extra distance tolerance (meters) |
| `maxRewindTime` | 0.5 | Max allowed rewind (seconds) |

## Best Practices

1. **Server Authority**: Always validate hits on the server
2. **Client Timestamps**: Send `NetworkTime.ServerTime` from client
3. **RTT Compensation**: Account for network latency in validation
4. **Reasonable Limits**: Cap rewind time to prevent exploits
5. **Batch Operations**: Use crowd adapters for large numbers of entities

## NetworkSpawnListener

A late-subscriber spawn tracker. When a new subscriber registers after objects have already spawned, it immediately replays all existing spawns to that subscriber.

```csharp
// On your spawn manager (server or client, depends on your flow):
NetworkSpawnListener.Instance.NotifySpawned(playerGO, clientId, isLocal: true, tag: "Player");

// On a late-joining client or any system that needs to know about spawns:
NetworkSpawnListener.Instance.Subscribe(data =>
{
    Debug.Log($"Player {data.OwnerId} spawned: {data.GameObject.name}");
    // This fires immediately for every already-spawned object, plus future spawns
});

// On despawn:
NetworkSpawnListener.Instance.NotifyDespawned(playerGO);

// Queries:
var localPlayer = NetworkSpawnListener.Instance.GetLocalPlayer();
var allNPCs = NetworkSpawnListener.Instance.GetByTag("NPC");

// Session cleanup:
NetworkSpawnListener.Instance.ClearAll();
```

### Key Properties
- **Late-subscriber pattern**: New subscribers instantly receive all existing spawns
- **Singleton**: Auto-creates if not in scene, persists across scenes
- **Despawn tracking**: Separate subscribe for despawn events
- **Query API**: `GetLocalPlayer()`, `GetByOwnerId(id)`, `GetByTag(tag)`, `IsSpawned(go)`
- **Housekeeping**: `PurgeDestroyed()` cleans up entries for destroyed objects

## License

MIT License - See LICENSE file for details.
