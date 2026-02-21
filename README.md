# Game Creator 2 Network Character System

This folder contains server-authoritative networking components for Game Creator 2 characters, designed for competitive multiplayer games.

## Architecture Overview

The **NetworkCharacter** component is the primary entry point. It automatically configures the correct driver based on network role:

```
┌─────────────────────────────────────────────────────────────────────┐
│                     NetworkCharacter Component                       │
│           (Auto-assigns driver based on network role)                │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌───────────────┐   ┌───────────────┐   ┌───────────────────────┐ │
│  │   IsServer?   │   │   IsOwner?    │   │   Driver Assigned     │ │
│  ├───────────────┤   ├───────────────┤   ├───────────────────────┤ │
│  │     true      │   │     any       │ → │ UnitDriverNetworkSrv  │ │
│  │     false     │   │     true      │ → │ UnitDriverNetworkCli  │ │
│  │     false     │   │     false     │ → │ UnitDriverNetworkRmt  │ │
│  └───────────────┘   └───────────────┘   └───────────────────────┘ │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Data Flow

```
┌─────────────────────────────────────────────────────────────────────┐
│                         CLIENT SIDE                                  │
│  ┌─────────────────────┐     ┌──────────────────────────────────┐  │
│  │ UnitPlayerNetwork   │────►│ UnitDriverNetworkClient          │  │
│  │ Client              │     │ - Client-side prediction         │  │
│  │ - Input capture     │     │ - Server reconciliation          │  │
│  │ - Compression       │     │ - Input buffering                │  │
│  └─────────────────────┘     └──────────────────────────────────┘  │
└────────────────────────────────────┬────────────────────────────────┘
                                     │ NetworkInputState[] (8 bytes each)
                                     ▼
┌─────────────────────────────────────────────────────────────────────┐
│                         SERVER SIDE                                  │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ UnitDriverNetworkServer                                       │  │
│  │ - Authoritative movement                                      │  │
│  │ - Input validation (anti-cheat)                               │  │
│  │ - Physics simulation                                          │  │
│  └──────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────┬────────────────────────────────┘
                                     │ NetworkPositionState (14 bytes)
                                     ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    OTHER CLIENTS (Remote Players)                    │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ UnitDriverNetworkRemote                                       │  │
│  │ - Snapshot interpolation                                      │  │
│  │ - Limited extrapolation                                       │  │
│  │ - Smooth visual representation                                │  │
│  └──────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
```

## Deployment Architecture

### Build Types

Unity provides a **Dedicated Server** build target (Unity 2021.3+):

| Build Type | Purpose | Contains |
|------------|---------|----------|
| **Client Build** | Player machines | Full game (graphics, audio, UI, input) |
| **Dedicated Server Build** | Server machines | Headless (no graphics, smaller size) |

```
File → Build Settings → Target Platform:
├── Windows, Mac, Linux        ← Client builds (~500MB)
└── Dedicated Server           ← Server build (~80MB, headless)
```

### Character Instances Per Machine

In a **2-player game with dedicated server**:

```
┌─────────────────────────────────────────────────────────────────────┐
│                    DEDICATED SERVER (Headless)                       │
│                     (No screen, no rendering)                        │
├─────────────────────────────────────────────────────────────────────┤
│  Player A Character → UnitDriverNetworkServer (validates A)         │
│  Player B Character → UnitDriverNetworkServer (validates B)         │
│                                                                      │
│  Total: 2 character instances (no visuals)                          │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                    CLIENT A's SCREEN                                 │
├─────────────────────────────────────────────────────────────────────┤
│  My Character (A)  → UnitDriverNetworkClient (predicted, responsive)│
│  Other Player (B)  → UnitDriverNetworkRemote (interpolated)         │
│                                                                      │
│  Total: 2 characters on screen                                       │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                    CLIENT B's SCREEN                                 │
├─────────────────────────────────────────────────────────────────────┤
│  My Character (B)  → UnitDriverNetworkClient (predicted, responsive)│
│  Other Player (A)  → UnitDriverNetworkRemote (interpolated)         │
│                                                                      │
│  Total: 2 characters on screen                                       │
└─────────────────────────────────────────────────────────────────────┘
```

**Total instances across entire game: 6** (2 per machine × 3 machines)

### Driver Assignment Summary

| Location | Player A's Character | Player B's Character |
|----------|---------------------|---------------------|
| **Server** | `UnitDriverNetworkServer` | `UnitDriverNetworkServer` |
| **Client A** | `UnitDriverNetworkClient` (mine) | `UnitDriverNetworkRemote` (theirs) |
| **Client B** | `UnitDriverNetworkRemote` (theirs) | `UnitDriverNetworkClient` (mine) |

### Runtime Driver Selection

The same prefab spawns on all machines, but each machine selects the appropriate driver:

```csharp
void OnNetworkSpawn()
{
#if UNITY_SERVER
    // Dedicated server build: validate all players, no visuals needed
    character.ChangeDriver(new UnitDriverNetworkServer());
    DisableVisuals(); // Save memory on server
#else
    // Client build
    if (IsOwner)
    {
        // This is MY character - use prediction for responsive feel
        character.ChangeDriver(new UnitDriverNetworkClient());
        EnableCameraFollow();
        EnableInput();
    }
    else
    {
        // This is SOMEONE ELSE's character - interpolate their position
        character.ChangeDriver(new UnitDriverNetworkRemote());
    }
#endif
}
```

Or with runtime checks (works in all builds, useful for Host mode):

```csharp
void OnNetworkSpawn()
{
    if (IsServer && !IsHost)
    {
        // Dedicated server mode
        character.ChangeDriver(new UnitDriverNetworkServer());
        DisableVisuals();
    }
    else if (IsHost && IsOwner)
    {
        // I'm hosting AND playing - use client driver for my character
        character.ChangeDriver(new UnitDriverNetworkClient());
    }
    else if (IsServer)
    {
        // Host validates other players
        character.ChangeDriver(new UnitDriverNetworkServer());
    }
    else if (IsOwner)
    {
        // Client connected to host/server - my character
        character.ChangeDriver(new UnitDriverNetworkClient());
    }
    else
    {
        // Remote player I'm watching
        character.ChangeDriver(new UnitDriverNetworkRemote());
    }
}
```

### Data Flow

```
Player A presses W key
       │
       ▼
UnitDriverNetworkClient (on Client A)
  - Moves character immediately (client-side prediction)
  - Sends NetworkInputState to server
       │
       ▼ [Network: 8 bytes input]
       │
UnitDriverNetworkServer (on Server)
  - Validates input (anti-cheat)
  - Moves authoritative position
  - Broadcasts NetworkPositionState
       │
       ├──► [Network: 14 bytes] ──► Client A: reconciles prediction
       │                            (corrects if mispredicted)
       │
       └──► [Network: 14 bytes] ──► Client B: UnitDriverNetworkRemote
                                              interpolates Player A smoothly
```

### Typical Server Deployment

```
┌─────────────────────────────────────────────────────────────────────┐
│                    YOUR CLOUD SERVER                                 │
│  ┌─────────────────────────────────────────────────────────────┐    │
│  │  MyGameServer.exe (Dedicated Server Build)                  │    │
│  │  - UnitDriverNetworkServer for ALL player characters        │    │
│  │  - No GPU required, no monitor needed                       │    │
│  │  - Can run 10-50 game instances per machine                 │    │
│  └─────────────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────────────┘
                          ▲
                          │ Internet
        ┌─────────────────┴─────────────────┐
        │                                   │
┌───────▼───────┐                   ┌───────▼───────┐
│   Player A    │                   │   Player B    │
│  MyGame.exe   │                   │  MyGame.exe   │
│ (Client Build)│                   │ (Client Build)│
│               │                   │               │
│ NetworkClient │                   │ NetworkClient │
│ + Remote(s)   │                   │ + Remote(s)   │
└───────────────┘                   └───────────────┘
```

## Components

### NetworkCharacterTypes.cs
Data structures for network transmission:
- **NetworkInputState** (8 bytes): Compressed player input
- **NetworkPositionState** (14 bytes): Compressed position/rotation/velocity
- **NetworkCharacterConfig**: Inspector-exposed settings
- **PositionSnapshot**: For interpolation buffering

### UnitDriverNetworkServer.cs
Server-authoritative movement driver:
- Processes queued client inputs
- Applies physics and validates movement
- Produces authoritative state for broadcast
- Anti-cheat validation (speed limits, input sanity)

### UnitDriverNetworkClient.cs
Client-side prediction driver:
- Immediately applies local input (responsive feel)
- Buffers inputs for server redundancy
- Reconciles with server state when mismatch detected
- Re-simulates after reconciliation

### UnitPlayerNetworkClient.cs
Input capture unit (WASD/Gamepad):
- Captures and compresses player input
- Integrates with network driver
- Supports programmatic input injection

### UnitPlayerDirectionalEnemyMassesNetwork.cs
Network-aware directional input with Enemy Masses integration:
- WASD/gamepad input with fog of war reveal
- Minimap icon support
- Works with `UnitDriverNetworkClient`
- Input deadzone and compression

### UnitPlayerFollowPointerNetwork.cs
Network-aware pointer-following input:
- Character moves toward mouse/touch pointer
- Direction compressed to 4 bytes (`NetworkPointerDirection`)
- Configurable update rate and direction threshold
- Stop command on pointer release
- Twin-stick shooter style gameplay support

### UnitPlayerTankNetwork.cs
Network-aware tank controls:
- Forward/back relative to character facing
- Left/right for rotation
- Input compressed to 5 bytes (`NetworkTankInput`)
- Server-side `TankInputValidator` for rotation rate limiting
- Forces `UnitFacingTank` for proper rotation handling

### UnitPlayerEnemyMassesRTSNetwork.cs
Network-aware RTS player unit:
- Server-validated click-to-move commands
- RTS selection integration via `IRTSExternalSelectable`
- Fog of war and minimap support
- Optional client-side prediction
- **Batch command support** for efficient multi-unit control

### UnitDriverNetworkRemote.cs
Remote character interpolation:
- Buffers incoming position snapshots
- Interpolates between snapshots for smooth movement
- Limited extrapolation when packets delayed
- Snaps on teleport detection

### NetworkCharacter.cs ⭐ NEW
**Primary network wrapper for GC2 Characters.** This is the recommended way to network GC2 characters.

```
┌─────────────────────────────────────────────────────────────────────┐
│                    NetworkCharacter Component                        │
├─────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────────┐ │
│  │ Driver Assign   │  │ State Sync      │  │ System Config       │ │
│  │ ───────────────│  │ ───────────────│  │ ─────────────────── │ │
│  │ Server → Server │  │ IsDead (sync)   │  │ IK: Sync/Local/Off │ │
│  │ Owner  → Client │  │ IsPlayer (sync) │  │ Footsteps: Local   │ │
│  │ Remote → Remote │  │ Events (RPC)    │  │ Interaction: Off   │ │
│  └─────────────────┘  └─────────────────┘  └─────────────────────┘ │
│                                                                      │
│  Optional Components:                                                │
│  ┌──────────────────────┐  ┌──────────────────────┐                 │
│  │ UnitIKNetworkCtrl    │  │ CharacterLagComp     │                 │
│  │ (if IK = Sync)       │  │ (if enabled)         │                 │
│  └──────────────────────┘  └──────────────────────┘                 │
└─────────────────────────────────────────────────────────────────────┘
```

**Features:**
- **Automatic driver assignment** based on network role (Server/LocalClient/RemoteClient)
- **State synchronization** for IsDead, IsPlayer via NetworkVariables
- **Configurable remote systems** - choose to sync, run locally, or disable:
  - IK: `Synchronized` (uses UnitIKNetworkController), `LocalOnly`, or `Disabled`
  - Footsteps: `LocalOnly` (plays based on local animation) or `Disabled`
  - Interaction: Usually `Disabled` for remotes
  - Combat: Server-authoritative only
- **Optional lag compensation** integration via CharacterLagCompensation
- **Server optimizations** - auto-disable visuals/audio on dedicated server
- **Network-agnostic** - works with Netcode, Photon, FishNet, Mirror, etc.

**Usage with Netcode for GameObjects:**
```csharp
// Just add the component - it handles everything in OnNetworkSpawn
[RequireComponent(typeof(Character))]
public class MyNetworkedPlayer : NetworkBehaviour
{
    private NetworkCharacter m_NetworkCharacter;
    
    public override void OnNetworkSpawn()
    {
        // NetworkCharacter auto-detects role and configures itself
        m_NetworkCharacter = GetComponent<NetworkCharacter>();
    }
}
```

**Usage with other networking solutions:**
```csharp
// Call InitializeNetworkRole manually from your spawn handler
public void OnPhotonInstantiate()
{
    var netChar = GetComponent<NetworkCharacter>();
    netChar.InitializeNetworkRole(
        isServer: PhotonNetwork.IsMasterClient,
        isOwner: photonView.IsMine,
        isHost: PhotonNetwork.IsMasterClient && photonView.IsMine
    );
    
    // Subscribe to state changes for manual sync
    netChar.OnNetworkDeathChanged += isDead => {
        photonView.RPC("SyncDeath", RpcTarget.Others, isDead);
    };
}
```

**RemoteSystemMode options:**
| Mode | Description | Use Case |
|------|-------------|----------|
| `Disabled` | System doesn't run at all | Save CPU on remotes |
| `LocalOnly` | System runs from local data | Footsteps from animation |
| `Synchronized` | System syncs over network | IK look direction |

### NetworkCharacterManager.cs
Coordinator component:
- Manages character role (local/remote/server)
- Handles lifecycle and events
- Provides unified API for network integration

### UnitFacingNetworkPivot.cs ⭐ NEW
Server-authoritative facing direction for networked characters:
- Replaces `UnitFacingPivot` when facing direction affects gameplay
- Server validates and broadcasts yaw angle (2 bytes per update)
- Clients interpolate smoothly toward server yaw
- Use for: backstab mechanics, cone attacks, competitive anti-cheat

```
┌─────────────────────────────────────────────────────────────────────┐
│               NETWORK FACING FLOW                                    │
├─────────────────────────────────────────────────────────────────────┤
│  Local Client                    Server                    Remote   │
│  ┌────────────┐                 ┌────────────┐           ┌────────┐ │
│  │ Calculate  │  RequestYaw    │ Validate   │  Broadcast│ Receive │ │
│  │ desired    │───────────────►│ & clamp to │──────────►│ & lerp  │ │
│  │ direction  │                │ max speed  │           │ smooth  │ │
│  └────────────┘                └────────────┘           └────────┘ │
│       │                              │                       │      │
│       └──────────────────────────────┴───────────────────────┘      │
│                    All use server-authoritative yaw                 │
└─────────────────────────────────────────────────────────────────────┘
```

**Settings:**
| Setting | Default | Description |
|---------|---------|-------------|
| Direction From | MotionDirection | Source of facing direction |
| Interpolation Speed | 15 | How fast clients interpolate to server yaw |
| Min Angle Change | 1° | Minimum change before sending network update |

**When to use:**
- ✅ Backstab damage bonuses depend on facing
- ✅ Cone attacks (cleave, breath weapons)
- ✅ Competitive anti-cheat requirements
- ❌ Most games (local facing is sufficient)

### UnitAnimimNetworkKinematic.cs ⭐ NEW
Server-authoritative animation parameters for networked characters:
- Replaces `UnitAnimimKinematic` when animation state affects gameplay
- Syncs: Speed-X/Y/Z, Pivot, Grounded, Stand (6 bytes per update)
- Delta compression: only sends when values change significantly
- Rate-limited: configurable max updates/second

```
┌─────────────────────────────────────────────────────────────────────┐
│           ANIMATION PARAMETER SYNC (6 bytes)                         │
├─────────────────────────────────────────────────────────────────────┤
│  NetworkAnimimState                                                  │
│  ├── speedX:       1 byte (-1 to 1, 0.01 precision)                 │
│  ├── speedY:       1 byte (-1 to 1, 0.01 precision)                 │
│  ├── speedZ:       1 byte (-1 to 1, 0.01 precision)                 │
│  ├── pivotSpeed:   1 byte (-180 to 180 deg/s, 1.5° precision)       │
│  ├── groundedStand:1 byte (4 bits grounded, 4 bits stand)           │
│  └── flags:        1 byte                                           │
│                                                                      │
│  vs. Raw floats: 48+ bytes (8× bandwidth reduction)                 │
└─────────────────────────────────────────────────────────────────────┘
```

**Settings:**
| Setting | Default | Description |
|---------|---------|-------------|
| Change Threshold | 3 | Min change (0-127 scale) before sending |
| Max Update Rate | 20 | Maximum updates per second |

**When to use:**
- ✅ Animation events trigger gameplay effects (damage, spawns)
- ✅ Pixel-perfect animation sync required
- ✅ Competitive anti-cheat for animation state
- ❌ Most games (local calculation from synced position is sufficient)

**Note:** For most multiplayer games, the default `UnitAnimimKinematic` is sufficient. Remote characters derive animation parameters locally from interpolated position/velocity, which works well because animations are primarily cosmetic.

## Quick Start ⭐

The easiest way to network a GC2 Character is using **NetworkCharacter**:

### Step 1: Add Components to Character Prefab
```
Character (GC2)
├── NetworkCharacter        ← Add this
├── NetworkObject           ← Netcode for GameObjects
└── (Your NetworkBehaviour) ← Your game logic
```

### Step 2: Configure in Inspector
```
┌─────────────────────────────────────────────────────────────────────┐
│ NetworkCharacter                                                     │
├─────────────────────────────────────────────────────────────────────┤
│ ▼ Driver Configuration                                              │
│   Server Driver:  [●] UnitDriverNetworkServer   (authoritative)     │
│   Client Driver:  [●] UnitDriverNetworkClient   (prediction)        │
│   Remote Driver:  [●] UnitDriverNetworkRemote   (interpolation)     │
├─────────────────────────────────────────────────────────────────────┤
│ ▼ Remote Character Systems                                          │
│   IK Mode:          [Synchronized ▼]  ← Sync look/aim direction     │
│   Footsteps Mode:   [LocalOnly ▼]     ← Play from animation locally │
│   Interaction Mode: [Disabled ▼]      ← Remotes don't interact      │
│   Combat Mode:      [Disabled ▼]      ← Server handles combat       │
├─────────────────────────────────────────────────────────────────────┤
│ ▼ Optional Network Components                                       │
│   ☑ Use Network IK         ← Auto-creates UnitIKNetworkController   │
│   ☑ Use Network Motion     ← Validates dash/teleport                │
│   ☑ Use Lag Compensation   ← Enables hit validation                 │
├─────────────────────────────────────────────────────────────────────┤
│ ▼ Server Optimization                                               │
│   ☑ Disable Visuals On Server  ← Saves memory on dedicated server   │
│   ☑ Disable Audio On Server                                         │
└─────────────────────────────────────────────────────────────────────┘
```

### Step 3: NetworkCharacter Auto-Configures on Spawn

**With Netcode for GameObjects:**
```csharp
// NetworkCharacter automatically detects role in OnNetworkSpawn()
// No additional code needed!

public class MyPlayer : NetworkBehaviour
{
    [SerializeField] private NetworkCharacter m_NetworkCharacter;
    
    public override void OnNetworkSpawn()
    {
        // NetworkCharacter already configured itself
        // Access drivers if needed:
        if (m_NetworkCharacter.IsLocalPlayer)
        {
            // Setup camera, UI, etc.
            var clientDriver = m_NetworkCharacter.ClientDriver;
            clientDriver.OnSendInput += SendInputToServer;
        }
    }
}
```

**With Other Networking Solutions (Photon, Mirror, FishNet):**
```csharp
public void OnNetworkSpawn() // Your framework's spawn callback
{
    var netChar = GetComponent<NetworkCharacter>();
    
    // Tell NetworkCharacter what role this instance has
    netChar.InitializeNetworkRole(
        isServer: IsServerOrHost(),
        isOwner: IsLocalPlayer(),
        isHost: IsHost()
    );
    
    // Subscribe to state changes for manual sync
    netChar.OnNetworkDeathChanged += isDead => {
        // Send death state to other clients
        SendRPC("SyncDeath", isDead);
    };
}
```

### What NetworkCharacter Does Automatically

| Role | Driver Assigned | Systems Configured |
|------|----------------|-------------------|
| **Server** | `UnitDriverNetworkServer` | Visuals off, Combat on |
| **LocalClient** | `UnitDriverNetworkClient` | Everything enabled |
| **RemoteClient** | `UnitDriverNetworkRemote` | Per your settings |

## NPC Synchronization Modes ⭐

When networking NPCs, you have two fundamental approaches. The **NetworkCharacter** component supports both via the `NPCSyncMode` property:

### Server-Authoritative vs Client-Side Deterministic

```
┌─────────────────────────────────────────────────────────────────────┐
│               NPC SYNCHRONIZATION MODES                              │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  SERVER-AUTHORITATIVE (Default)                                      │
│  ──────────────────────────────────                                 │
│  ┌─────────────────┐      ┌─────────────────┐                       │
│  │     SERVER      │      │    CLIENTS      │                       │
│  │  Runs AI Logic  │─────►│  Interpolate    │                       │
│  │  NavMesh Path   │      │  Position Only  │                       │
│  │  All Decisions  │      │  (No AI)        │                       │
│  └─────────────────┘      └─────────────────┘                       │
│                                                                      │
│  CLIENT-SIDE DETERMINISTIC                                          │
│  ─────────────────────────────                                      │
│  ┌─────────────────┐      ┌─────────────────┐                       │
│  │     SERVER      │      │    CLIENTS      │                       │
│  │  Spawns + Seed  │─────►│  Run AI Locally │                       │
│  │  Critical Events│      │  Same Seed      │                       │
│  │  Death/Loot Only│      │  Same Behavior  │                       │
│  └─────────────────┘      └─────────────────┘                       │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Comparison

| Aspect | Server-Authoritative | Client-Side Deterministic |
|--------|---------------------|--------------------------|
| **Bandwidth** | ~0.1 KB/s per NPC | ~0 KB/s (spawn only) |
| **Server CPU** | AI + NavMesh per NPC | Minimal (events only) |
| **Consistency** | Perfect | Near-perfect (deterministic) |
| **Cheat Resistance** | High | Low |
| **Latency** | 50-200ms visible | Zero (local) |
| **Scale** | 10-100 NPCs | 100-1000+ NPCs |

### When to Use Each Mode

**Server-Authoritative (NPCSyncMode.ServerAuthoritative)**
- ✅ Boss enemies that drop important loot
- ✅ Quest NPCs with interaction triggers
- ✅ Competitive PvE (leaderboards, speedruns)
- ✅ NPCs that can damage players
- ✅ Escort missions or NPCs that affect objectives

**Client-Side Deterministic (NPCSyncMode.ClientSideDeterministic)**
- ✅ Ambient wildlife (deer, birds, fish)
- ✅ Background civilians in cities
- ✅ Cosmetic decorations (butterflies, fireflies)
- ✅ Large-scale battles with 100+ units
- ✅ Single-player-like feel in multiplayer

### Hierarchy Menu Options

Create networked characters from the hierarchy context menu:

| Menu Item | Description | Driver | NPC Mode |
|-----------|-------------|--------|----------|
| **Network Player** | Human-controlled character | `UnitDriverNetworkClient` | N/A |
| **Network Character (Server)** | Server-authoritative NPC | `UnitDriverNavmeshNetworkServer` | ServerAuthoritative |
| **Network Character (Client-Side)** | Lightweight NPC | `UnitDriverNavmesh` (local) | ClientSideDeterministic |
| **Network Combat Controller** | Central combat coordinator | N/A | N/A |

```
Right-Click → Game Creator → Characters → Network →
├── Network Player                    ← Human players
├── Network Character (Server)        ← Important NPCs (bosses, loot)
└── Network Character (Client-Side)   ← Ambient NPCs (wildlife, crowds)

Right-Click → Game Creator → Networking →
└── Network Combat Controller         ← One per scene, handles all combat
```

### GC2 Kernel Units: Network Alternatives

When setting up a GC2 Character for networking, you can optionally replace standard kernel units with server-authoritative alternatives:

| GC2 Kernel Unit | Network Alternative | When to Use |
|-----------------|---------------------|-------------|
| `UnitFacingPivot` | `UnitFacingNetworkPivot` | Facing affects gameplay (backstabs, cones) |
| `UnitAnimimKinematic` | `UnitAnimimNetworkKinematic` | Animation events affect gameplay |
| `UnitDriverController` | `UnitDriverNetworkClient/Server/Remote` | Always (auto-assigned) |

**In GC2 Character Inspector:**
```
Character → Kernel →
├── Facing:  [Network Pivot (Server-Authoritative)]  ← Use for competitive
├── Animim:  [Network Kinematic (Server-Authoritative)] ← Use if anim events matter
└── Driver:  (Auto-assigned by NetworkCharacter)
```

**Note:** Most games do NOT need the network Facing/Animim units. Use them only when:
- Server-authoritative facing: Backstab mechanics, cone attacks, anti-cheat
- Server-authoritative animation: Animation events trigger damage/effects

### Inspector Configuration

```
┌─────────────────────────────────────────────────────────────────────┐
│ NetworkCharacter                                                     │
├─────────────────────────────────────────────────────────────────────┤
│ ▼ NPC Synchronization                                               │
│   NPC Mode:           [Server Authoritative ▼]                      │
│                       [Client-Side Deterministic]                   │
│                                                                      │
│   Deterministic Seed: [12345]  ← Same seed = same behavior          │
│                                  (Only used in Client-Side mode)    │
└─────────────────────────────────────────────────────────────────────┘
```

### Code Examples

**Server-Authoritative Boss NPC:**
```csharp
// Boss that drops important loot - must be server-authoritative
public class NetworkBossNPC : NetworkBehaviour
{
    [SerializeField] private NetworkCharacter m_NetworkCharacter;
    
    public override void OnNetworkSpawn()
    {
        // NetworkCharacter defaults to ServerAuthoritative
        // Server runs AI, clients interpolate position
        
        if (IsServer)
        {
            // Server handles death event
            m_NetworkCharacter.OnNetworkDeathChanged += OnBossDeath;
        }
    }
    
    [Server]
    private void OnBossDeath(bool isDead)
    {
        if (isDead)
        {
            // Server spawns loot - clients see authoritative drop
            SpawnLootServerRpc();
        }
    }
}
```

**Client-Side Ambient Wildlife:**
```csharp
// Deer wandering in forest - cosmetic, no gameplay impact
public class NetworkAmbientWildlife : NetworkBehaviour
{
    [SerializeField] private NetworkCharacter m_NetworkCharacter;
    
    public override void OnNetworkSpawn()
    {
        // NPCMode is set to ClientSideDeterministic via menu/inspector
        // Each client runs the AI locally with same seed
        
        // Use NetworkObjectId as deterministic seed
        var seed = (int)NetworkObjectId;
        InitializeLocalAI(seed);
    }
    
    private void InitializeLocalAI(int seed)
    {
        // Local random with deterministic seed
        var random = new System.Random(seed);
        
        // All clients get same "random" behavior
        var wanderRadius = random.Next(5, 15);
        var wanderInterval = random.Next(3, 10);
        
        StartCoroutine(WanderRoutine(wanderRadius, wanderInterval));
    }
}
```

**Hybrid Approach - Ambient NPCs That Can Become Important:**
```csharp
// Civilian that's usually client-side, but becomes server-auth when attacked
public class NetworkCivilian : NetworkBehaviour
{
    [SerializeField] private NetworkCharacter m_NetworkCharacter;
    private bool m_IsImportant;
    
    public override void OnNetworkSpawn()
    {
        // Start as client-side for efficiency
        // Switch to server-authoritative if player interacts
    }
    
    [ServerRpc(RequireOwnership = false)]
    public void RequestInteractionServerRpc(ulong playerId)
    {
        if (!m_IsImportant)
        {
            m_IsImportant = true;
            // Switch to server-authoritative mode
            PromoteToServerAuthoritativeClientRpc();
        }
    }
    
    [ClientRpc]
    private void PromoteToServerAuthoritativeClientRpc()
    {
        // Stop local AI, wait for server state
        StopLocalAI();
    }
}
```

## Usage (Manual Setup)

For more control, you can set up components manually:

### Setup Local Player
```csharp
// Option 1: Use NetworkCharacter (recommended)
var netChar = character.gameObject.AddComponent<NetworkCharacter>();
netChar.InitializeNetworkRole(isServer: false, isOwner: true);

// Option 2: Manual setup
var driver = new UnitDriverNetworkClient();
var player = new UnitPlayerDirectionalNetwork();
character.ChangeDriver(driver);
character.ChangePlayer(player);

driver.OnSendInput += inputs => {
    // Send to server via your networking solution
    NetworkManager.SendInputs(inputs);
};
```

### Setup Server Character
```csharp
// Option 1: Use NetworkCharacter (recommended)
var netChar = character.GetComponent<NetworkCharacter>();
netChar.InitializeNetworkRole(isServer: true, isOwner: false);

// Access driver for input processing
var serverDriver = netChar.ServerDriver;

// Receive client inputs
void OnClientInput(NetworkInputState[] inputs)
{
    foreach (var input in inputs)
    {
        serverDriver.QueueInput(input);
    }
}

// Server tick loop (e.g., 20 times per second)
void ServerTick()
{
    var state = serverDriver.ProcessInputs();
    NetworkManager.BroadcastToAll(state);
}
```

### Setup Remote Player
```csharp
// Option 1: Use NetworkCharacter (recommended)
var netChar = character.GetComponent<NetworkCharacter>();
netChar.InitializeNetworkRole(isServer: false, isOwner: false);

// Access driver for state application
var remoteDriver = netChar.RemoteDriver;

// Apply incoming states
void OnReceiveState(NetworkPositionState state, float serverTime)
{
    remoteDriver.SetServerTime(serverTime);
    remoteDriver.AddSnapshot(state, serverTime);
}
```

## Bandwidth Analysis

### Per-Character Bandwidth (typical competitive game)

**Client → Server:**
- Input rate: 60 Hz
- Input size: 8 bytes + redundancy (3x) = ~24 bytes avg
- Bandwidth: 60 × 24 = **1.44 KB/s per player**

**Server → All Clients:**
- State rate: 20 Hz
- State size: 14 bytes
- For 16 players: 20 × 14 × 16 = **4.48 KB/s total broadcast**
- Per client receives: 20 × 14 × 15 = **4.2 KB/s incoming**

**Total per player: ~6 KB/s** (vs ~30-50 KB/s for uncompressed GC2)

## Configuration

### NetworkCharacterConfig

| Setting | Default | Description |
|---------|---------|-------------|
| inputSendRate | 60 | Input packets per second |
| inputRedundancy | 3 | Past inputs included for packet loss |
| reconciliationThreshold | 0.1 | Position error (meters) to trigger reconciliation |
| reconciliationSpeed | 10 | How fast to smooth reconciliation |
| maxReconciliationDistance | 2 | Beyond this, teleport instead of smooth |
| antiCheatMaxSpeed | 15 | Maximum validated movement speed |

## Integration Examples

### Unity Netcode for GameObjects
```csharp
public class NetcodeCharacterSync : NetworkBehaviour
{
    private NetworkCharacterManager manager;
    
    public override void OnNetworkSpawn()
    {
        manager = GetComponent<NetworkCharacterManager>();
        
        if (IsOwner)
        {
            manager.ChangeRole(NetworkCharacterManager.CharacterRole.LocalPlayer);
            manager.OnSendInputs += SendInputsServerRpc;
        }
        else if (IsServer)
        {
            manager.CreateServerDriver();
        }
        else
        {
            manager.ChangeRole(NetworkCharacterManager.CharacterRole.RemotePlayer);
        }
    }
    
    [ServerRpc]
    private void SendInputsServerRpc(NetworkInputState[] inputs)
    {
        manager.ReceiveClientInputs(inputs);
    }
    
    [ClientRpc]
    private void BroadcastStateClientRpc(NetworkPositionState state)
    {
        if (!IsOwner)
        {
            manager.ApplyServerState(state);
        }
    }
}
```

### Mirror Networking
```csharp
public class MirrorCharacterSync : NetworkBehaviour
{
    private NetworkCharacterManager manager;
    
    public override void OnStartAuthority()
    {
        manager = GetComponent<NetworkCharacterManager>();
        manager.ChangeRole(NetworkCharacterManager.CharacterRole.LocalPlayer);
        manager.OnSendInputs += CmdSendInputs;
    }
    
    public override void OnStartServer()
    {
        if (!isLocalPlayer)
        {
            manager = GetComponent<NetworkCharacterManager>();
            manager.CreateServerDriver();
        }
    }
    
    [Command]
    private void CmdSendInputs(NetworkInputState[] inputs)
    {
        manager.ReceiveClientInputs(inputs);
    }
    
    [ClientRpc]
    private void RpcBroadcastState(NetworkPositionState state)
    {
        if (!isLocalPlayer)
        {
            manager.ApplyServerState(state);
        }
    }
}
```

## Debugging

Enable `LogEvents` on `NetworkCharacterManager` to see:
- Input transmission counts and sequence numbers
- Reconciliation triggers and error magnitudes

Remote characters show a yellow gizmo sphere when extrapolating.

## Motion Controller (Dash/Teleport/Abilities)

### NetworkMotionTypes.cs
Data structures for motion commands:
- **NetworkMotionConfig** (20 bytes): Compressed speed, gravity, jump, dash settings
- **NetworkMotionCommand** (20 bytes): Dash, teleport, move commands
- **NetworkMotionResult**: Server validation response

### UnitMotionNetworkController.cs
Server-authoritative motion controller:
- **Config Sync**: Automatically broadcasts when speed/gravity/jump settings change
- **Server-Validated Dash**: `RequestDash()` with distance/cooldown validation
- **Server-Validated Teleport**: `RequestTeleport()` with distance limits
- **Routed Move Commands**: `MoveToDirection()` and `MoveToLocation()` go through server

### Motion Setup Example
```csharp
// Replace motion unit with network version
var motion = new UnitMotionNetworkController();
motion.IsServer = isServer;
character.Kernel.ChangeMotion(character, motion);

// Hook up network events
motion.OnConfigChanged += config => {
    // Broadcast config to all clients
    NetworkManager.BroadcastConfig(config);
};

motion.OnSendCommand += command => {
    // Send command to server for validation
    NetworkManager.SendToServer(command);
};

motion.OnBroadcastCommand += command => {
    // Server broadcasts validated command to all
    NetworkManager.BroadcastToAll(command);
};
```

### Server-Validated Dash
```csharp
// Client requests dash
motion.RequestDash(
    direction: transform.forward,
    speed: 20f,
    duration: 0.2f,
    fade: 0.1f,
    callback: result => {
        if (!result.approved)
            Debug.Log($"Dash rejected: {result.rejectionReason}");
    }
);
```

### Server-Validated Teleport
```csharp
// Client requests teleport
motion.RequestTeleport(
    position: targetPosition,
    rotationY: targetRotation,
    callback: result => {
        if (!result.approved)
            ShowTeleportFailed();
    }
);
```

### Config Synchronization
```csharp
// Server changes speed (e.g., buff/debuff)
motion.LinearSpeed = 8f; // Automatically broadcasts to clients

// Client receives config update
void OnReceiveConfig(NetworkMotionConfig config)
{
    motion.ApplyNetworkConfig(config);
}
```

### Motion Bandwidth
- Config changes: 20 bytes (only when values change)
- Motion commands: 20 bytes per command
- Typical usage: <0.5 KB/s additional (commands are infrequent)

## Animation Sync (Playable Graph)

GC2 uses Unity's Playable Graph system for layered animations without Animator references. The animation sync system handles this elegantly.

### NetworkAnimationTypes.cs
Compact data structures for animation commands:
- **NetworkStateCommand** (16 bytes): Set animation state on a layer
- **NetworkGestureCommand** (14 bytes): Play one-shot gesture animation
- **NetworkStopStateCommand** (6 bytes): Stop a layer
- **NetworkStopGestureCommand** (8 bytes): Stop gestures
- **NetworkAnimationRegistry**: ScriptableObject for pre-registered animations

### UnitAnimimNetworkController.cs
Owner-authoritative animation synchronization:
- **Design**: Animations are cosmetic - owner broadcasts, remotes receive
- **States**: Persistent animations (idle, locomotion) with layer support
- **Gestures**: One-shot animations (attacks, reactions)
- **Rate Limiting**: Configurable to prevent spam
- **Caching**: Animation clips looked up by hash for efficient transmission

### Why Owner-Authority (Not Server)?
Unlike movement, animation cheating doesn't affect gameplay physics. Server-authoritative animation would:
- Add unnecessary latency to visual feedback
- Double bandwidth (client→server→clients vs client→clients)
- Increase server CPU load for no competitive benefit

### Animation Setup Example
```csharp
var animController = character.gameObject.AddComponent<UnitAnimimNetworkController>();
animController.Initialize(character, isLocalPlayer);

// Register animations for hash lookup (required for remotes)
animController.RegisterClip(attackClip);
animController.RegisterClip(jumpClip);
animController.RegisterState(locomotionState);

// Hook up network events (local player only)
if (isLocalPlayer)
{
    animController.OnStateCommandReady += command => {
        NetworkManager.BroadcastToOthers(command);
    };
    
    animController.OnGestureCommandReady += command => {
        NetworkManager.BroadcastToOthers(command);
    };
    
    animController.OnStopStateCommandReady += command => {
        NetworkManager.BroadcastToOthers(command);
    };
    
    animController.OnStopGestureCommandReady += command => {
        NetworkManager.BroadcastToOthers(command);
    };
}
```

### Playing Networked Animations
```csharp
// Instead of: character.Gestures.CrossFade(clip, mask, mode, config, true)
// Use:
animController.PlayGesture(
    clip: attackClip,
    mask: upperBodyMask,
    blendMode: BlendMode.Blend,
    config: new ConfigGesture(0f, 0.5f, 1f, false, 0.1f, 0.1f),
    stopPreviousGestures: true
);

// Instead of: character.States.SetState(state, layer, mode, config)
// Use:
animController.SetState(
    state: locomotionState,
    layer: 0,
    blendMode: BlendMode.Blend,
    config: new ConfigState(0f, 1f, 1f, 0.25f, 0.25f)
);
```

### Remote Player Receives Animation
```csharp
// In your network receive handler
void OnReceiveGestureCommand(NetworkGestureCommand command)
{
    animController.ApplyGestureCommand(command);
}

void OnReceiveStateCommand(NetworkStateCommand command)
{
    animController.ApplyStateCommand(command);
}

void OnReceiveStopState(NetworkStopStateCommand command)
{
    animController.ApplyStopStateCommand(command);
}

void OnReceiveStopGesture(NetworkStopGestureCommand command)
{
    animController.ApplyStopGestureCommand(command);
}
```

### Using Animation Registry
For larger projects, use the ScriptableObject registry instead of runtime registration:

1. Create: `Assets > Create > Game Creator > Network > Animation Registry`
2. Add all networked animation clips and states with unique Network IDs
3. Assign to `UnitAnimimNetworkController.AnimationRegistry`

This allows deterministic lookup without runtime registration.

### Animation Bandwidth
- Gesture command: 14 bytes per gesture
- State command: 16 bytes per state change
- Stop commands: 6-8 bytes
- Typical usage: **0.5-2 KB/s** (animations are event-driven, not continuous)

### Netcode Integration Example
```csharp
public class NetcodeAnimationSync : NetworkBehaviour
{
    private UnitAnimimNetworkController animController;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        animController = gameObject.AddComponent<UnitAnimimNetworkController>();
        animController.Initialize(character, IsOwner);
        
        // Register your animations
        animController.RegisterClip(attackClip);
        animController.RegisterState(locomotionState);
        
        if (IsOwner)
        {
            animController.OnGestureCommandReady += BroadcastGestureClientRpc;
            animController.OnStateCommandReady += BroadcastStateClientRpc;
        }
    }
    
    [ClientRpc]
    private void BroadcastGestureClientRpc(NetworkGestureCommand command)
    {
        if (!IsOwner) animController.ApplyGestureCommand(command);
    }
    
    [ClientRpc]
    private void BroadcastStateClientRpc(NetworkStateCommand command)
    {
        if (!IsOwner) animController.ApplyStateCommand(command);
    }
}
```

## Off-Mesh Link Networking

Server-authoritative synchronization for off-mesh link traversal (jumps, climbs, drops, ladders, etc.). The server detects when an agent enters a link and broadcasts traversal data to clients.

### Design Philosophy

Off-mesh links present unique networking challenges:
- **Server detects link entry** - When NavMeshAgent enters a link
- **Server determines traversal type** - Jump, climb, drop, etc.
- **Server broadcasts start/progress/complete** - Clients animate locally
- **Clients interpolate position** - Smooth movement along link path

### NetworkOffMeshLinkTypes.cs
Compact data structures for link traversal sync:
- **NetworkOffMeshLinkStart** (28 bytes): Start position, end position, duration, type
- **NetworkOffMeshLinkProgress** (8 bytes): Progress updates during traversal
- **NetworkOffMeshLinkComplete** (16 bytes): Completion status and final position
- **NetworkOffMeshLinkAnimation**: Optional custom animation curve data

### Traversal Types

| Type | Description | Default Behavior |
|------|-------------|------------------|
| Auto | Simple linear movement | Linear interpolation |
| Jump | Gap crossing with arc | Parabolic arc |
| Climb | Vertical ascent | Ease-in-out |
| Drop | Falling with gravity | Accelerating |
| Ladder | Vertical ladder | Smooth ease |
| Crawl | Low horizontal passage | Linear |
| Vault | Quick obstacle hop | Fast arc |
| Swim | Water traversal | Linear |
| Teleport | Instant position change | No interpolation |
| Custom | User-defined curve | From registry |

### OffMeshLinkNetworkServer

Server-side controller that:
- Detects when agent enters an off-mesh link
- Determines traversal type from link configuration
- Calculates duration based on distance and type
- Broadcasts start message with positions and timing
- Sends periodic progress updates (optional)
- Broadcasts completion with final position

### OffMeshLinkNetworkClient

Client-side controller that:
- Receives server link messages
- Animates traversal with appropriate curve
- Interpolates between progress updates
- Handles position correction on completion

### Off-Mesh Link Setup

The off-mesh link controllers are **automatically integrated** with the NavMesh network drivers:

```csharp
// Server driver automatically broadcasts link events
var serverDriver = new UnitDriverNavmeshNetworkServer();

// Subscribe to link events
serverDriver.OnLinkStartReady += start => {
    NetworkManager.BroadcastToAll(start);
};

serverDriver.OnLinkProgressReady += progress => {
    NetworkManager.BroadcastToAll(progress);
};

serverDriver.OnLinkCompleteReady += complete => {
    NetworkManager.BroadcastToAll(complete);
};

serverDriver.OnLinkAnimationReady += anim => {
    NetworkManager.BroadcastToAll(anim);
};
```

```csharp
// Client driver automatically handles link messages
var clientDriver = new UnitDriverNavmeshNetworkClient();

// Apply server messages
void OnReceiveLinkStart(NetworkOffMeshLinkStart start)
{
    clientDriver.ApplyLinkStart(start);
}

void OnReceiveLinkProgress(NetworkOffMeshLinkProgress progress)
{
    clientDriver.ApplyLinkProgress(progress);
}

void OnReceiveLinkComplete(NetworkOffMeshLinkComplete complete)
{
    clientDriver.ApplyLinkComplete(complete);
}
```

### Custom Link Types with Registry

Create a registry to define custom link behaviors:

1. Create: `Assets > Create > Game Creator > Network > Off-Mesh Link Registry`
2. Add entries for each link type with:
   - Traversal type (Jump, Climb, etc.)
   - Default duration
   - Animation curve
   - Arc height (for jumps)
   - Animation clip
   - Animation speed

```csharp
// Use registry for consistent link behavior
[SerializeField] private NetworkOffMeshLinkRegistry linkRegistry;

// Server controller will use registry to determine link type
serverDriver.OnLinkStartReady += start => {
    // Link type determined from registry or auto-detected
    NetworkManager.BroadcastToAll(start);
};
```

### Integration with NavMeshLinkClimbable

The off-mesh link system automatically detects `NavMeshLinkClimbable` components:

```csharp
// NavMeshLinkClimbable on link objects is auto-detected
// Traversal type is set to Climb
// Duration is calculated from climb height and speed
```

### Netcode Integration Example

```csharp
public class NetcodeOffMeshLinkSync : NetworkBehaviour
{
    private UnitDriverNavmeshNetworkServer serverDriver;
    private UnitDriverNavmeshNetworkClient clientDriver;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        
        if (IsServer)
        {
            serverDriver = new UnitDriverNavmeshNetworkServer();
            Character.Kernel.ChangeDriver(character, serverDriver);
            
            // Hook up link events
            serverDriver.OnLinkStartReady += BroadcastLinkStartClientRpc;
            serverDriver.OnLinkProgressReady += BroadcastLinkProgressClientRpc;
            serverDriver.OnLinkCompleteReady += BroadcastLinkCompleteClientRpc;
        }
        else
        {
            clientDriver = new UnitDriverNavmeshNetworkClient();
            Character.Kernel.ChangeDriver(character, clientDriver);
        }
    }
    
    [ClientRpc]
    private void BroadcastLinkStartClientRpc(NetworkOffMeshLinkStart start)
    {
        if (IsServer) return;
        clientDriver?.ApplyLinkStart(start);
    }
    
    [ClientRpc]
    private void BroadcastLinkProgressClientRpc(NetworkOffMeshLinkProgress progress)
    {
        if (IsServer) return;
        clientDriver?.ApplyLinkProgress(progress);
    }
    
    [ClientRpc]
    private void BroadcastLinkCompleteClientRpc(NetworkOffMeshLinkComplete complete)
    {
        if (IsServer) return;
        clientDriver?.ApplyLinkComplete(complete);
    }
}
```

### Off-Mesh Link Bandwidth

- Start message: 28 bytes per link entry
- Progress updates: 8 bytes at 10 Hz (optional)
- Complete message: 16 bytes per link exit
- Animation data: Variable (only for custom curves)

**Typical usage**: <0.5 KB/s (link traversals are infrequent)

### Off-Mesh Link Events (Client-Side)

```csharp
// Subscribe to client-side events for animations
var linkController = character.GetComponent<OffMeshLinkNetworkClient>();

linkController.OnTraversalStarted += (type, ascending) => {
    // Play appropriate animation
    switch (type)
    {
        case OffMeshLinkTraversalType.Jump:
            animator.SetTrigger("Jump");
            break;
        case OffMeshLinkTraversalType.Climb:
            animator.SetBool("Climbing", true);
            animator.SetBool("ClimbUp", ascending);
            break;
        // ... etc
    }
};

linkController.OnTraversalCompleted += success => {
    // Reset animation state
    animator.SetBool("Climbing", false);
};
```

## Lag Compensation (Hit Validation)

Server-authoritative hit validation with temporal rewinding. Ensures fair hit detection across varying network latencies.

### Overview

In networked games, when a player fires at a target, the target may have moved by the time the server receives the hit request. Lag compensation rewinds the target's position to where it was when the attacker fired, providing fair hit detection.

```
Without Lag Compensation:
┌─────────────────────────────────────────────────────────────────────┐
│  Client fires at T=0    Server receives at T=0.1    Target moved!  │
│  Target at (0,0,0) ──────────────────────────────► Target at (1,0,0)│
│                                                    HIT MISSED ✗    │
└─────────────────────────────────────────────────────────────────────┘

With Lag Compensation:
┌─────────────────────────────────────────────────────────────────────┐
│  Client fires at T=0    Server rewinds to T=0     Target at (0,0,0)│
│  Target at (0,0,0) ──────────────────────────────► HIT CONFIRMED ✓ │
└─────────────────────────────────────────────────────────────────────┘
```

### CharacterLagCompensation Component

Add to your networked GC2 Character prefab for automatic position history tracking:

```csharp
// Setup on networked character
public override void OnNetworkSpawn()
{
    var lagComp = gameObject.AddComponent<CharacterLagCompensation>();
    lagComp.Initialize(GetComponent<Character>());
    
    // Network ID for identification
    lagComp.SetNetworkId(NetworkObjectId);
}
```

### Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `historySize` | 64 | Snapshots stored per character |
| `snapshotRate` | 60 | Recording frequency (Hz) |
| `maxRewindTime` | 0.5 | Max rewind time (seconds) |
| `hitTolerance` | 0.3 | Extra distance tolerance (meters) |

### Hit Zones

`CharacterLagCompensation` provides three hit zones with damage multipliers:

| Zone | Damage Multiplier | Bounds |
|------|-------------------|--------|
| Head | 2.0× | Upper 15% of character |
| Torso | 1.0× | Middle 40% of character |
| Legs | 0.75× | Lower 45% of character |

### Server-Side Hit Validation

```csharp
[ServerRpc]
void OnShootServerRpc(Vector3 origin, Vector3 direction, float clientTime)
{
    var timestamp = new NetworkTimestamp(clientTime);
    
    // Perform lag-compensated raycast
    var hits = HitValidationUtility.RaycastAll(origin, direction, 100f, timestamp);
    
    foreach (var hit in hits)
    {
        if (hit.isValid)
        {
            // hit.hitZoneMultiplier contains damage multiplier (2.0 for head, etc.)
            ApplyDamage(hit.targetNetworkId, baseDamage * hit.hitZoneMultiplier);
        }
    }
}
```

### Validation Methods

```csharp
// Hitscan weapon (instant raycast)
var result = HitValidationUtility.ValidateHitscan(
    shooterNetworkId, origin, direction, maxDistance, clientTimestamp
);

// Area damage (grenades, explosions)
var hits = HitValidationUtility.OverlapSphereAll(
    explosionCenter, explosionRadius, clientTimestamp
);

// Melee swing (arc-based)
var hits = HitValidationUtility.ValidateMeleeSwing(
    attackerNetworkId, swingOrigin, swingDirection, 
    swingArc: 90f, swingRange: 2f, clientTimestamp
);

// Projectile impact (travel time compensation)
var result = HitValidationUtility.ValidateProjectileHit(
    shooterNetworkId, targetNetworkId, hitPoint,
    projectileSpawnTime, hitTime, projectileSpeed
);
```

### Integration with GC2 Shooter Module

```csharp
// In your networked weapon component
[ServerRpc]
void FireServerRpc(Vector3 muzzlePos, Vector3 direction, float fireTime)
{
    var timestamp = new NetworkTimestamp(fireTime);
    
    // Use GC2 Shooter's raycast but validate with lag compensation
    if (Physics.Raycast(muzzlePos, direction, out RaycastHit hit, range))
    {
        var target = hit.collider.GetComponent<CharacterLagCompensation>();
        if (target != null)
        {
            // Validate hit was legitimate at client's time
            var result = LagCompensationManager.Instance.ValidateHit(
                target.NetworkId, hit.point, timestamp
            );
            
            if (result.isValid)
            {
                // Apply damage through GC2's damage system
                var damageData = new DamageData(
                    baseDamage * result.hitZoneMultiplier,
                    DamageType.Physical,
                    shooter
                );
                target.Character.Combat.ReceiveDamage(damageData);
            }
        }
    }
}
```

### Integration with GC2 Melee Module

```csharp
// In your networked melee weapon
[ServerRpc]
void MeleeSwingServerRpc(Vector3 origin, Vector3 direction, float swingTime)
{
    var timestamp = new NetworkTimestamp(swingTime);
    
    // Validate melee swing with lag compensation
    var hits = HitValidationUtility.ValidateMeleeSwing(
        attackerNetworkId: NetworkObjectId,
        swingOrigin: origin,
        swingDirection: direction,
        swingArc: meleeArc,      // e.g., 90 degrees
        swingRange: meleeRange, // e.g., 2 meters
        clientTimestamp: timestamp
    );
    
    foreach (var hit in hits)
    {
        if (hit.isValid && hit.targetNetworkId != NetworkObjectId)
        {
            ApplyMeleeDamage(hit.targetNetworkId, meleeDamage);
        }
    }
}
```

### Best Practices

1. **Always Validate on Server**: Never trust client hit claims
2. **Include Client Timestamp**: Use `NetworkTime.ServerTime` on client
3. **Cap Rewind Time**: Limit to ~500ms to prevent exploits
4. **Use Hit Zones**: Reward skill with headshot multipliers
5. **Log Suspicious Hits**: Track rejected hits for anti-cheat analysis

### Debug Visualization

Enable gizmos on `CharacterLagCompensation` to see:
- Current hit zones (colored spheres)
- Historical positions (faded trail)
- Hit validation results (green = valid, red = rejected)

## Network Combat System ⭐

Server-authoritative combat for GC2's Melee and Shooter modules. Intercepts local hit detection and routes through server validation with lag compensation.

### Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                    NETWORK COMBAT FLOW                               │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  CLIENT (Local Player)                                              │
│  ┌─────────────────────────────────────────────────────────────────┐│
│  │ 1. GC2 Melee Striker detects hit                                ││
│  │ 2. NetworkCombatInterceptor intercepts                          ││
│  │ 3. Creates NetworkHitRequest (20 bytes)                         ││
│  │ 4. [Optional] Play optimistic effects                           ││
│  │ 5. Send to server                                               ││
│  └─────────────────────────────────────────────────────────────────┘│
│                              │                                       │
│                              ▼ NetworkHitRequest                     │
│                                                                      │
│  SERVER                                                              │
│  ┌─────────────────────────────────────────────────────────────────┐│
│  │ 1. NetworkCombatController receives request                     ││
│  │ 2. CharacterLagCompensation rewinds target to clientTime        ││
│  │ 3. Validate: range, invincibility, block/parry                  ││
│  │ 4. Calculate damage: base × zone × backstab × defense           ││
│  │ 5. Apply damage via GC2 Combat.GetHitReaction()                 ││
│  │ 6. Broadcast NetworkHitBroadcast (16 bytes) to all clients      ││
│  └─────────────────────────────────────────────────────────────────┘│
│                              │                                       │
│                              ▼ NetworkHitBroadcast                   │
│                                                                      │
│  ALL CLIENTS                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐│
│  │ 1. Receive broadcast with hit details                           ││
│  │ 2. Play hit effects (particles, sounds)                         ││
│  │ 3. Trigger hit reactions on target character                    ││
│  └─────────────────────────────────────────────────────────────────┘│
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Role | Location |
|-----------|------|----------|
| **NetworkCombatController** | Central combat coordinator | Scene (singleton) |
| **NetworkCombatInterceptor** | Per-character hit interception | On Character |
| **CharacterLagCompensation** | Position history for validation | On Character |
| **NetworkCombatTypes** | Network data structures | N/A (types only) |

### Data Structures (Bandwidth Efficient)

```
NetworkHitRequest (Client → Server): ~20 bytes
├── requestId: 2 bytes (for response matching)
├── targetNetworkId: 4 bytes
├── clientTime: 4 bytes (for lag compensation)
├── hitPoint: ~6 bytes (compressed)
├── direction: 2 bytes (compressed angle)
├── weaponHash: 4 bytes
└── hitType: 1 byte

NetworkHitResponse (Server → Client): ~12 bytes
├── requestId: 2 bytes
├── result: 1 byte (Valid/Blocked/Parried/etc.)
├── finalDamage: 4 bytes
├── hitZone: 1 byte
└── effects: 1 byte (Critical/Backstab flags)

NetworkHitBroadcast (Server → All): ~16 bytes
├── attackerNetworkId: 4 bytes
├── targetNetworkId: 4 bytes
├── hitOffset: 6 bytes (relative to target)
├── hitZone: 1 byte
└── effects: 1 byte
```

### Setup

**Step 1: Add NetworkCombatController to Scene**

```csharp
// Add to a persistent game manager object
public class CombatNetworkManager : NetworkBehaviour
{
    [SerializeField] private NetworkCombatController m_CombatController;
    
    public override void OnNetworkSpawn()
    {
        m_CombatController.Initialize(IsServer, IsClient);
        
        // Wire up network callbacks
        m_CombatController.SendHitRequestToServer = SendHitRequest;
        m_CombatController.SendHitResponseToClient = SendHitResponse;
        m_CombatController.BroadcastHitToClients = BroadcastHit;
        m_CombatController.GetServerTime = () => (float)NetworkManager.ServerTime.Time;
        m_CombatController.GetCharacterByNetworkId = GetCharacter;
    }
    
    [ServerRpc(RequireOwnership = false)]
    private void SendHitRequest(NetworkHitRequest request) { /* ... */ }
    
    [ClientRpc]
    private void BroadcastHit(NetworkHitBroadcast broadcast) { /* ... */ }
}
```

**Step 2: Configure NetworkCharacter**

Set Combat Mode to control interception:

```
┌─────────────────────────────────────────────────────────────────────┐
│ NetworkCharacter                                                     │
├─────────────────────────────────────────────────────────────────────┤
│ ▼ Remote Character Systems                                          │
│   Combat Mode:      [Disabled ▼]                                    │
│                     ┌────────────────────────────────────────────┐  │
│                     │ Disabled    - No local combat (remotes)    │  │
│                     │ LocalOnly   - Intercept & send to server   │  │
│                     │ Synchronized- Full server-auth combat      │  │
│                     └────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
```

**Step 3: Intercept GC2 Melee Hits**

```csharp
// In your custom Striker or combat component
public class NetworkedMeleeAttack : MonoBehaviour
{
    private NetworkCombatInterceptor m_Interceptor;
    
    void OnStrikeHit(StrikeOutput output, Skill skill)
    {
        // Let interceptor handle network routing
        bool processLocally = m_Interceptor.InterceptStrikeOutput(output, skill);
        
        if (processLocally)
        {
            // Server or allowed local effects
            PlayHitEffects(output.Point);
        }
        // Damage application happens on server via NetworkCombatController
    }
}
```

**Step 4: Intercept GC2 Shooter Hits**

```csharp
// In your networked weapon
[ServerRpc]
void FireServerRpc(ShooterShotData data)
{
    // On server: validate and apply via lag compensation
    var interceptor = m_Character.GetComponent<NetworkCombatInterceptor>();
    
    if (Physics.Raycast(data.Origin, data.Direction, out RaycastHit hit))
    {
        var target = hit.collider.GetComponent<Character>();
        if (target != null)
        {
            // Interceptor routes through NetworkCombatController
            interceptor.InterceptProjectileHit(target, hit.point, data.Direction, m_Weapon);
        }
    }
}
```

### Hit Results

The server validates hits and returns one of these results:

| Result | Description | Common Cause |
|--------|-------------|--------------|
| `Valid` | Hit confirmed, damage applied | Normal hit |
| `OutOfRange` | Target not at claimed position | Network desync, cheating |
| `Invincible` | Target was invincible/dodging | I-frames, dodge roll |
| `Blocked` | Target blocked the attack | Shield up |
| `Parried` | Target parried (counter window) | Perfect block timing |
| `InvalidTarget` | Target not found or dead | Race condition |
| `TooOld` | Request exceeded max rewind time | High latency |

### Hit Zones & Damage Multipliers

```csharp
// Default hit zones
public enum HitZone : byte
{
    Body = 0,      // 1.0x damage
    Head = 1,      // 2.0x damage (critical)
    Torso = 2,     // 1.0x damage
    LeftArm = 3,   // 0.75x damage
    RightArm = 4,  // 0.75x damage
    LeftLeg = 5,   // 0.75x damage
    RightLeg = 6   // 0.75x damage
}

// Effect flags
[Flags]
public enum HitEffectFlags : byte
{
    None = 0,
    Critical = 1 << 0,    // Headshot
    Backstab = 1 << 1,    // Hit from behind (1.5x)
    Knockback = 1 << 2,   // Physics push
    Stagger = 1 << 3,     // Interrupt action
    BreakGuard = 1 << 4,  // Break shield
    Lethal = 1 << 5       // Killing blow
}
```

### Optimistic Effects

For responsive feel, enable optimistic hit effects:

```csharp
// In NetworkCombatController inspector
[Header("Client Settings")]
[x] Optimistic Hit Effects  ← Play effects before server confirms

// What happens:
// 1. Client fires → immediately shows muzzle flash, plays sound
// 2. Hit detected → plays hit particles, blood, etc.
// 3. Server responds:
//    - If Valid: effects already played ✓
//    - If Rejected: optionally revert (usually not noticeable)
```

### Anti-Cheat Considerations

```csharp
// Server-side validation catches:
// ✓ Aim bots (hit point doesn't match historical position)
// ✓ Speed hacks (rewind time too large)
// ✓ Damage hacks (server calculates damage, not client)
// ✓ Range exploits (distance validation)

// Log suspicious activity
m_CombatController.OnHitRejected += (request, reason) => {
    if (reason == HitResult.OutOfRange)
    {
        AntiCheatSystem.LogSuspiciousHit(request.attackerNetworkId, reason);
    }
};
```

### Bandwidth Analysis

**Per Hit:**
- Request: ~20 bytes
- Response: ~12 bytes (to requester only)
- Broadcast: ~16 bytes × clients

**Example: 4-player game, 60 hits/minute:**
- Requests: 60 × 20 = 1.2 KB/min
- Responses: 60 × 12 = 0.72 KB/min
- Broadcasts: 60 × 16 × 3 = 2.88 KB/min
- **Total: ~5 KB/min per player** (very efficient)

## Server-Authoritative Kernel Units ⭐

Optional GC2 Kernel unit replacements for when standard local calculation isn't sufficient.

### UnitFacingNetworkPivot

Server-authoritative facing direction. Use when facing affects gameplay.

**Setup:**
1. In GC2 Character Inspector → Kernel → Facing
2. Select "Network Pivot (Server-Authoritative)"
3. Configure settings:

```
┌─────────────────────────────────────────────────────────────────────┐
│ Facing: Network Pivot (Server-Authoritative)                        │
├─────────────────────────────────────────────────────────────────────┤
│   Direction From:      [Motion Direction ▼]                         │
│   Axonometry:          [Standard ▼]                                 │
│                                                                      │
│ ▼ Network Settings                                                  │
│   Interpolation Speed: [15    ]  ← How fast clients lerp to server  │
│   Min Angle Change:    [1     ]  ← Degrees before sending update    │
└─────────────────────────────────────────────────────────────────────┘
```

**How it works:**
```
┌─────────────────────────────────────────────────────────────────────┐
│ Local Client Input                                                   │
│ ┌─────────────────────────────────────────────────────────────────┐ │
│ │ 1. UnitFacingNetworkPivot calculates desired facing              │ │
│ │ 2. Sends RequestFacingUpdateServerRpc(desiredYaw)                │ │
│ │ 3. Interpolates toward last known server yaw for smooth visuals  │ │
│ └─────────────────────────────────────────────────────────────────┘ │
│                              │                                       │
│                              ▼                                       │
│ Server                                                               │
│ ┌─────────────────────────────────────────────────────────────────┐ │
│ │ 1. Receives requested yaw                                        │ │
│ │ 2. ValidateFacingRequest() - clamps to max rotation speed        │ │
│ │ 3. Updates NetworkVariable<float> m_NetworkFacingYaw             │ │
│ │ 4. Broadcasts to all clients                                     │ │
│ └─────────────────────────────────────────────────────────────────┘ │
│                              │                                       │
│                              ▼                                       │
│ All Clients                                                          │
│ ┌─────────────────────────────────────────────────────────────────┐ │
│ │ OnServerYawReceived() - smooth interpolation to new yaw          │ │
│ └─────────────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

**Use cases:**
```csharp
// Backstab detection using server-authoritative facing
[Server]
void ProcessBackstab(Character attacker, Character target)
{
    // Get target's facing unit
    var facing = target.Kernel.Facing as UnitFacingNetworkPivot;
    if (facing == null) return;
    
    // Calculate angle between attack direction and target facing
    Vector3 attackDir = (target.transform.position - attacker.transform.position).normalized;
    Vector3 targetForward = Quaternion.Euler(0, facing.ServerYaw, 0) * Vector3.forward;
    
    float angle = Vector3.Angle(attackDir, targetForward);
    bool isBackstab = angle < 60f; // Hit from behind
    
    if (isBackstab)
    {
        // Apply 1.5x backstab damage
        ApplyDamage(target, baseDamage * 1.5f);
    }
}
```

### UnitAnimimNetworkKinematic

Server-authoritative animation parameters. Use when animation state affects gameplay.

**Setup:**
1. In GC2 Character Inspector → Kernel → Animim
2. Select "Network Kinematic (Server-Authoritative)"
3. Configure settings:

```
┌─────────────────────────────────────────────────────────────────────┐
│ Animim: Network Kinematic (Server-Authoritative)                    │
├─────────────────────────────────────────────────────────────────────┤
│   Animator:           [Animator ▼]                                  │
│   Smooth Time:        [0.15   ]                                     │
│                                                                      │
│ ▼ Network Settings                                                  │
│   Change Threshold:   [3      ]  ← Min change (0-127) before sync   │
│   Max Update Rate:    [20     ]  ← Updates per second               │
└─────────────────────────────────────────────────────────────────────┘
```

**What gets synced (6 bytes total):**
| Parameter | Bytes | Precision | Range |
|-----------|-------|-----------|-------|
| Speed-X | 1 | 0.008 | -1 to 1 |
| Speed-Y | 1 | 0.008 | -1 to 1 |
| Speed-Z | 1 | 0.008 | -1 to 1 |
| Pivot | 1 | 1.5° | -180 to 180 °/s |
| Grounded+Stand | 1 | 0.067 | 0 to 1 each |
| Flags | 1 | - | - |

**Bandwidth:**
- Updates only when values change significantly
- At 20 Hz max: 6 bytes × 20 = **120 bytes/sec worst case**
- Typical: ~30-60 bytes/sec (delta compression)

**Use cases:**
```csharp
// Animation-driven damage zones (server-validated)
[Server]
void OnMeleeAnimationEvent(Character attacker, string eventName)
{
    // Only server receives animation events
    if (eventName == "DamageWindow_Start")
    {
        EnableHitbox(attacker);
    }
    else if (eventName == "DamageWindow_End")
    {
        DisableHitbox(attacker);
    }
}
```

### When NOT to Use Network Kernel Units

For **most games**, the standard `UnitFacingPivot` and `UnitAnimimKinematic` are sufficient:

| Scenario | Use Standard Units | Use Network Units |
|----------|-------------------|-------------------|
| Casual multiplayer | ✅ | ❌ |
| Co-op PvE | ✅ | ❌ |
| Competitive PvP | ❌ | ✅ |
| Backstab mechanics | ❌ | ✅ Facing |
| Animation-driven hitboxes | ❌ | ✅ Animim |
| 100+ players | ✅ (bandwidth) | ❌ |

**Why standard units work:**
- Facing is derived from movement direction (already synced via position)
- Animation parameters are derived from movement (already synced)
- Visual differences are minor and acceptable for most games
- Saves significant bandwidth

## NavMesh Agent Networking

Server-authoritative pathfinding for **both player-controlled and AI characters**. Supports click-to-move gameplay (Diablo, Path of Exile), RTS unit control (StarCraft, Age of Empires), and traditional AI/NPC navigation.

### Design Philosophy

NavMesh networking serves two distinct use cases with different requirements:

| Use Case | Examples | Latency Tolerance | Prediction Needed |
|----------|----------|-------------------|-------------------|
| **Player Click-to-Move** | Diablo, PoE, Lost Ark | Low (50-150ms) | Optional |
| **RTS Unit Control** | StarCraft, AoE | Medium (100-200ms) | Rarely |
| **AI/NPC Navigation** | Any game | High (200ms+) | No |

**Core Architecture:**
- **Server calculates all paths** - Single source of truth for pathfinding
- **Server broadcasts path corners** - Clients know the full intended route
- **Clients follow paths locally** - Smooth movement without per-frame sync
- **Optional client prediction** - For responsive player-controlled movement

### Player vs AI: Key Differences

| Aspect | Player-Controlled | AI-Controlled |
|--------|-------------------|---------------|
| Command source | Client input | Server AI |
| Path authority | Server (validated) | Server (generated) |
| Position updates | Higher rate (20-30 Hz) | Lower rate (10-20 Hz) |
| Prediction | Recommended | Not needed |
| Latency hiding | Critical | Less important |

### NetworkNavMeshTypes.cs
Compact data structures for NavMesh sync:
- **NetworkNavMeshCommand** (14 bytes): Client→Server movement requests
- **NetworkNavMeshPathState** (8 + 6×corners bytes): Server→Client path data
- **NetworkNavMeshPositionUpdate** (10 bytes): Lightweight position sync
- **NetworkNavMeshConfig**: Rate limiting and interpolation settings

### UnitDriverNavmeshNetworkServer.cs
Server-side authoritative NavMesh driver:
- Processes queued client commands (MoveToPosition, MoveToDirection, Stop, Warp)
- Calculates NavMesh paths and validates targets
- Broadcasts path corners and position updates
- **Optional click validation** - Anti-cheat for competitive games
- Anti-cheat validation (reachable destinations, valid paths)

### UnitDriverNavmeshNetworkClient.cs
Client-side NavMesh driver (for both local players and remote characters):
- Sends movement commands to server (player input)
- Receives and follows server-provided paths
- **Optional local prediction** - Bake NavMesh on client for instant response
- Interpolates between position updates (remote characters)
- Extrapolates along path when packets delayed
- Reconciles with server path on mismatch

### UnitPlayerPointClickNetwork.cs
Network-aware point-and-click input capture unit (parallels `UnitPlayerNetworkClient`):
- Captures mouse/touch click input via raycast
- Validates clicks before sending (rate limiting, distance checks)
- Sends `NetworkNavMeshCommand` to driver
- Supports hold-to-move for continuous following
- Client-side anti-cheat (max distance, line of sight)
- Programmatic `MoveTo()` / `Stop()` for UI or AI takeover

### ClickValidator.cs
Server-side click command validator (anti-cheat):
- Rate limiting (max clicks per second)
- Distance validation (max click distance from character)
- Speed checking (teleport/speed hack detection)
- NavMesh position snapping
- Path validation (ensure destination reachable)
- Violation tracking and kick recommendations

### Player-Controlled NavMesh (Click-to-Move)

For Diablo-style click-to-move games, the client driver handles player input:

```csharp
public class ClickToMovePlayer : NetworkBehaviour
{
    private UnitDriverNavmeshNetworkServer serverDriver;
    private UnitDriverNavmeshNetworkClient clientDriver;
    private UnitPlayerPointClickNetwork playerUnit;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        
        if (IsServer)
        {
            // Server always has the authoritative driver
            serverDriver = new UnitDriverNavmeshNetworkServer();
            Character.Kernel.ChangeDriver(character, serverDriver);
            
            // Set owner for click validation
            serverDriver.SetOwnerClientId(OwnerClientId);
            
            serverDriver.OnPathStateReady += BroadcastPathStateClientRpc;
            serverDriver.OnPositionUpdateReady += BroadcastPositionClientRpc;
            
            // Handle anti-cheat kicks
            serverDriver.OnShouldKickClient += (clientId, reason) => {
                Debug.LogWarning($"Client {clientId} should be kicked: {reason}");
                // NetworkManager.DisconnectClient(clientId);
            };
        }
        
        if (IsOwner && !IsServer)
        {
            // Owning client uses client driver with optional prediction
            clientDriver = new UnitDriverNavmeshNetworkClient();
            clientDriver.EnableLocalPrediction = true; // Requires NavMesh on client
            Character.Kernel.ChangeDriver(character, clientDriver);
            
            clientDriver.OnSendCommand += SendCommandServerRpc;
            
            // Use the point-and-click player unit for input
            playerUnit = new UnitPlayerPointClickNetwork();
            playerUnit.SetNetworkDriver(clientDriver);
            Character.Kernel.ChangePlayer(character, playerUnit);
            
            // Optional: Listen to click events
            playerUnit.OnClickCaptured += pos => Debug.Log($"Moving to {pos}");
            playerUnit.OnClickRejected += reason => Debug.LogWarning($"Click rejected: {reason}");
        }
        else if (!IsServer)
        {
            // Remote players use client driver for interpolation
            clientDriver = new UnitDriverNavmeshNetworkClient();
            clientDriver.EnableLocalPrediction = false;
            Character.Kernel.ChangeDriver(character, clientDriver);
        }
    }
    
    // Note: When using UnitPlayerPointClickNetwork, click handling is automatic.
    // The following method is only needed for programmatic movement:
    public void ProgrammaticMove(Vector3 destination)
    {
        if (!IsOwner) return;
        
        if (IsServer)
        {
            // Host: directly queue command
            serverDriver.QueueCommand(
                NetworkNavMeshCommand.CreateMoveToPosition(destination, 0)
            );
        }
        else
        {
            // Client: use player unit's MoveTo (handles validation + driver)
            playerUnit?.MoveTo(destination);
        }
    }
    
    [ServerRpc]
    private void SendCommandServerRpc(NetworkNavMeshCommand command)
    {
        serverDriver?.QueueCommand(command);
    }
    
    [ClientRpc]
    private void BroadcastPathStateClientRpc(NetworkNavMeshPathState state)
    {
        clientDriver?.ApplyPathState(state);
    }
    
    [ClientRpc]
    private void BroadcastPositionClientRpc(NetworkNavMeshPositionUpdate update)
    {
        clientDriver?.ApplyPositionUpdate(update);
    }
}
```

### AI/NPC NavMesh Setup
```csharp
// On server (AI/NPC spawned)
var character = GetComponent<Character>();
var serverDriver = new UnitDriverNavmeshNetworkServer();
Character.Kernel.ChangeDriver(character, serverDriver);

// Hook up network events
serverDriver.OnPathStateReady += pathState => {
    NetworkManager.BroadcastToAll(pathState);
};

serverDriver.OnPositionUpdateReady += posUpdate => {
    NetworkManager.BroadcastToAll(posUpdate);
};

// AI controller sets destination
serverDriver.QueueCommand(NetworkNavMeshCommand.CreateMoveToPosition(targetPosition, sequence));
```

### Remote Character Setup (Both Players and NPCs)
```csharp
// On client (remote player or NPC)
var character = GetComponent<Character>();
var clientDriver = new UnitDriverNavmeshNetworkClient();
Character.Kernel.ChangeDriver(character, clientDriver);

// Receive server state
void OnReceivePathState(NetworkNavMeshPathState pathState)
{
    clientDriver.ApplyPathState(pathState);
}

void OnReceivePositionUpdate(NetworkNavMeshPositionUpdate update)
{
    clientDriver.ApplyPositionUpdate(update);
}
```

### Client-Side Prediction for Click-to-Move

For responsive click-to-move gameplay, enable local prediction:

```csharp
// Enable prediction (requires NavMesh baked on client)
clientDriver.EnableLocalPrediction = true;

// When player clicks:
// 1. Client immediately calculates local path (prediction)
// 2. Client sends command to server
// 3. Server validates and calculates authoritative path
// 4. Server sends path back to client
// 5. Client reconciles if paths differ significantly
```

**Prediction Trade-offs:**
| Aspect | With Prediction | Without Prediction |
|--------|-----------------|-------------------|
| Response time | Instant | RTT/2 delay |
| NavMesh required | Client + Server | Server only |
| Bandwidth | Slightly higher | Lower |
| Complexity | Higher | Lower |
| Best for | Competitive ARPG | Casual/Co-op |

### RTS Multi-Unit Selection

For RTS games with group movement commands:

```csharp
// Server processes and validates
void OnReceiveCommand(NetworkNavMeshCommand command)
{
    serverDriver.QueueCommand(command);
}
```

### Enemy Masses RTS Network Integration

For Enemy Masses RTS controller integration with networking:

```csharp
public class NetworkedRTSPlayer : NetworkBehaviour
{
    private UnitPlayerEnemyMassesRTSNetwork playerUnit;
    private UnitDriverNavmeshNetworkServer serverDriver;
    private UnitDriverNavmeshNetworkClient clientDriver;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        
        if (IsServer)
        {
            serverDriver = new UnitDriverNavmeshNetworkServer();
            Character.Kernel.ChangeDriver(character, serverDriver);
            
            serverDriver.OnPathStateReady += BroadcastPathClientRpc;
            serverDriver.OnPositionUpdateReady += BroadcastPositionClientRpc;
        }
        
        if (IsOwner)
        {
            // Setup RTS player unit for local player
            playerUnit = new UnitPlayerEnemyMassesRTSNetwork();
            playerUnit.NetworkId = NetworkObjectId;
            playerUnit.IsLocalPlayer = true;
            Character.Kernel.ChangePlayer(character, playerUnit);
            
            if (!IsServer)
            {
                clientDriver = new UnitDriverNavmeshNetworkClient();
                clientDriver.EnableLocalPrediction = true;
                Character.Kernel.ChangeDriver(character, clientDriver);
                
                playerUnit.SetNetworkDriver(clientDriver);
            }
            
            // Hook up command events
            playerUnit.OnSendMoveCommand += SendMoveServerRpc;
            playerUnit.OnSendStopCommand += SendStopServerRpc;
        }
    }
    
    [ServerRpc]
    private void SendMoveServerRpc(Vector3 dest, ulong id, ushort seq)
    {
        serverDriver?.QueueCommand(
            NetworkNavMeshCommand.CreateMoveToPosition(dest, seq)
        );
    }
    
    [ServerRpc]
    private void SendStopServerRpc(ulong id, ushort seq)
    {
        serverDriver?.QueueCommand(NetworkNavMeshCommand.CreateStop(seq));
    }
    
    [ClientRpc]
    private void BroadcastPathClientRpc(NetworkNavMeshPathState state)
    {
        clientDriver?.ApplyPathState(state);
    }
    
    [ClientRpc]
    private void BroadcastPositionClientRpc(NetworkNavMeshPositionUpdate update)
    {
        clientDriver?.ApplyPositionUpdate(update);
    }
}
```

### RTS Batch Commands (Multi-Unit Selection)

For efficient multi-unit commands, use batch structs instead of individual messages:

```csharp
public class RTSBatchController : NetworkBehaviour
{
    private List<UnitPlayerEnemyMassesRTSNetwork> selectedUnits = new();
    private ushort batchSequence;
    
    // Called when player issues move command with multiple units selected
    public void MoveSelectedUnits(Vector3 destination)
    {
        if (selectedUnits.Count == 0) return;
        
        batchSequence++;
        
        // Create batch command (single network message for all units)
        var batch = RTSBatchMoveCommand.CreateSameDestination(
            selectedUnits, destination, batchSequence
        );
        
        // ~3 + 8×N + 12×N bytes vs N×14 bytes for individual commands
        // For 10 units: 203 bytes vs 140 bytes (batch is slightly larger)
        // But: single RPC call vs 10 RPC calls = much lower overhead!
        
        SendBatchMoveServerRpc(batch);
        
        // Apply prediction locally for responsive feel
        foreach (var unit in selectedUnits)
        {
            unit.SetDestination(destination); // With prediction enabled
        }
    }
    
    [ServerRpc]
    private void SendBatchMoveServerRpc(RTSBatchMoveCommand batch)
    {
        // Server calculates formation positions
        var positions = CalculateFormation(batch.UnitCount, batch.Destinations[0]);
        
        for (int i = 0; i < batch.UnitCount; i++)
        {
            if (TryGetServerDriver(batch.UnitIds[i], out var driver))
            {
                driver.QueueCommand(
                    NetworkNavMeshCommand.CreateMoveToPosition(positions[i], batch.Sequence)
                );
            }
        }
    }
}
```

### Path Synchronization Flow
```
Player clicks destination → NetworkNavMeshCommand (14 bytes) → Server
                                                                ↓
                                                    Server validates & calculates path
                                                                ↓
Server broadcasts path → NetworkNavMeshPathState (8 + 6×N bytes) → All Clients
                                                                ↓
                                              Clients follow path corners locally
                                                                ↓
Server broadcasts updates → NetworkNavMeshPositionUpdate (10 bytes) → All Clients
                                    (only when significant deviation or periodic sync)
```

### NavMesh Bandwidth Analysis

**Player Click-to-Move (per player):**
- Command: 14 bytes per click (infrequent, ~2-5/second max)
- Path response: ~38 bytes per new path
- Position updates: 10 bytes × 20 Hz = **200 bytes/second**
- **Total: ~0.3 KB/s per player**

**AI/NPC (per agent):**
- No commands (server-controlled)
- Path changes: ~38 bytes (infrequent)
- Position updates: 10 bytes × 10 Hz = **100 bytes/second**
- **Total: ~0.1 KB/s per NPC**

**Scaling Examples:**
- 4-player co-op ARPG: ~1.2 KB/s for players
- 50 NPCs: ~5 KB/s total
- RTS with 200 units: ~20 KB/s total

### Configuration

| Setting | Default | Player | AI/NPC | Description |
|---------|---------|--------|--------|-------------|
| PositionSendRate | 20 | 20-30 | 10-15 | Position updates per second |
| PositionThreshold | 0.05 | 0.03 | 0.1 | Minimum movement to trigger update |
| TeleportThreshold | 5 | 3 | 5 | Distance to teleport vs interpolate |
| InterpolationBuffer | 0.1 | 0.05 | 0.15 | Buffer time in seconds |
| MaxExtrapolationTime | 0.5 | 0.3 | 0.5 | Max time to extrapolate |
| EnableLocalPrediction | false | true | false | Use client-side pathfinding |
| ReconciliationThreshold | 0.5 | 0.3 | 1.0 | Distance to trigger path reconciliation |

### Anti-Cheat Configuration (ClickValidationConfig)

For competitive games, enable server-side click validation:

| Setting | Casual | Competitive | Description |
|---------|--------|-------------|-------------|
| MaxClickDistance | 0 (unlimited) | 100m | Max click distance from character |
| MaxSpeed | 30 m/s | 15 m/s | Teleport detection threshold |
| MaxClickRate | 20/s | 10/s | Rate limiting (anti-spam) |
| RequireNavMeshPosition | false | true | Require clicks on NavMesh |
| NavMeshSampleDistance | - | 1m | Snap tolerance for NavMesh |
| ValidatePath | false | true | Verify path exists to destination |
| MaxPathDistanceMultiplier | - | 2.5x | Max path length vs direct distance |
| TrackViolations | false | true | Track suspicious behavior |
| ViolationThreshold | - | 3 | Violations before kick warning |

**Client-side Settings (UnitPlayerPointClickNetwork):**
| Setting | Default | Description |
|---------|---------|-------------|
| MaxClickRate | 10/s | Local rate limiting |
| MinMoveDistance | 0.5m | Ignore clicks too close |
| HoldToMove | true | Continuous move while holding |
| HoldUpdateRate | 5/s | Update rate when holding |
| MaxClickDistance | 0 | Client-side distance check (0 = off) |
| RequireLineOfSight | false | Require LoS to click point |

### Netcode Integration Example (AI/NPC)
```csharp
public class NetcodeNavMeshNPC : NetworkBehaviour
{
    private UnitDriverNavmeshNetworkServer serverDriver;
    private UnitDriverNavmeshNetworkClient clientDriver;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        
        if (IsServer)
        {
            serverDriver = new UnitDriverNavmeshNetworkServer();
            Character.Kernel.ChangeDriver(character, serverDriver);
            
            serverDriver.OnPathStateReady += BroadcastPathStateClientRpc;
            serverDriver.OnPositionUpdateReady += BroadcastPositionClientRpc;
        }
        else
        {
            clientDriver = new UnitDriverNavmeshNetworkClient();
            Character.Kernel.ChangeDriver(character, clientDriver);
        }
    }
    
    // Server AI sets destination
    public void SetDestination(Vector3 target)
    {
        if (!IsServer) return;
        serverDriver.QueueCommand(
            NetworkNavMeshCommand.CreateMoveToPosition(target, 0)
        );
    }
    
    [ClientRpc]
    private void BroadcastPathStateClientRpc(NetworkNavMeshPathState state)
    {
        if (IsServer) return;
        clientDriver?.ApplyPathState(state);
    }
    
    [ClientRpc]
    private void BroadcastPositionClientRpc(NetworkNavMeshPositionUpdate update)
    {
        if (IsServer) return;
        clientDriver?.ApplyPositionUpdate(update);
    }
}
```

### When to Use NavMesh vs CharacterController

| Scenario | Driver | Reason |
|----------|--------|--------|
| FPS/TPS Player | UnitDriverNetworkClient | WASD input, client prediction |
| Click-to-Move Player | UnitDriverNavmeshNetworkClient | Pathfinding, optional prediction |
| RTS Player Units | UnitDriverNavmeshNetworkClient | Multi-unit pathfinding |
| MOBA Player | UnitDriverNavmeshNetworkClient | Click-to-move with abilities |
| AI/NPC (server) | UnitDriverNavmeshNetworkServer | Full server control |
| Remote Player (FPS) | UnitDriverNetworkRemote | Interpolation only |
| Remote Player/NPC (NavMesh) | UnitDriverNavmeshNetworkClient | Path following + interpolation |

### Game Genre Recommendations

| Genre | Player Movement | Example Games |
|-------|-----------------|---------------|
| **FPS/TPS** | CharacterController + prediction | Call of Duty, Fortnite |
| **ARPG** | NavMesh + prediction | Diablo, Path of Exile |
| **MOBA** | NavMesh + prediction | League of Legends, Dota 2 |
| **RTS** | NavMesh (no prediction) | StarCraft, Age of Empires |
| **Survival** | CharacterController + prediction | Rust, Valheim |
| **MMO (action)** | CharacterController + prediction | Lost Ark (combat) |
| **MMO (classic)** | NavMesh (no prediction) | WoW (click-to-move) |

## IK Rig Sync (Procedural Animations)

GC2 uses IK Rig layers for procedural animations like look-at, aim, lean, and foot planting. The IK sync system handles network synchronization efficiently by only syncing what's necessary.

### Which IK Rigs Need Sync?

| Rig | Sync Needed? | Reason |
|-----|--------------|--------|
| **RigLookTo** | YES | Target position defines where character looks |
| **RigAimTowards** | YES | Aim direction affects visual feedback |
| **RigLean** | NO | Derives from movement velocity (already synced) |
| **RigFeetPlant** | NO | Uses local physics raycasts |
| **RigBreathing** | NO | Cosmetic randomness (local only) |
| **RigTwitching** | NO | Cosmetic randomness (local only) |
| **RigAlignGround** | NO | Uses local physics |

### NetworkIKTypes.cs
Compact data structures for IK sync:
- **NetworkLookToState** (13 bytes): Target position + weight + layer
- **NetworkAimState** (5 bytes): Pitch/yaw angles + weight
- **NetworkIKState** (~20 bytes): Combined state for batch transmission
- **NetworkIKConfig**: Rate limiting and delta compression settings

### UnitIKNetworkController.cs
Efficient IK synchronization:
- **Delta Compression**: Only sends when IK state changes significantly
- **Configurable Rate**: Default 20 Hz (adjustable)
- **Position Threshold**: Minimum 5cm change to trigger update
- **Angle Threshold**: Minimum 2° change to trigger update
- **Smooth Interpolation**: Remote characters interpolate smoothly

### How NetworkLookToState Works with GC2's RigLookTo

GC2's `RigLookTo` system uses an **interface-based target system** (`ILookTo`) with layered priority. The network system integrates seamlessly by implementing this interface:

```
┌──────────────────────────────────────────────────────────────────────┐
│                    LOCAL PLAYER (Owner)                              │
├──────────────────────────────────────────────────────────────────────┤
│  RigLookTo.LookToTarget (GC2's native system)                        │
│       │                                                              │
│       ▼                                                              │
│  UnitIKNetworkController reads:                                      │
│    - Position from target.Position                                   │
│    - Weight from RigLookTo.Weight                                    │
│    - Layer from target.Layer                                         │
│       │                                                              │
│       ▼                                                              │
│  NetworkLookToState (13 bytes compressed)                            │
│    - Relative XZ (±327m, 0.01m precision)                            │
│    - Absolute Y (0-4095, 0.1m precision)                             │
│    - Weight (0-255, byte)                                            │
│    - Layer (byte)                                                    │
│    - Optional network target ID                                      │
└────────────────────────────────────────────────────────────────────┬─┘
                                                                     │
                                         [Network: 13 bytes]         │
                                                                     ▼
┌──────────────────────────────────────────────────────────────────────┐
│                    REMOTE PLAYER (Other Clients)                     │
├──────────────────────────────────────────────────────────────────────┤
│  UnitIKNetworkController receives NetworkLookToState                 │
│       │                                                              │
│       ▼                                                              │
│  Creates NetworkLookTarget (implements ILookTo interface)            │
│    - Position property returns decompressed position                 │
│    - Layer property returns network layer                            │
│    - Exists property tracks if target is valid                       │
│       │                                                              │
│       ▼                                                              │
│  Calls: RigLookTo.SetTarget(networkLookTarget, layer)                │
│       │                                                              │
│       ▼                                                              │
│  GC2's RigLookTo animates head/neck/chest to look at target          │
│  (Uses native bone chain: Chest → Neck → Head)                       │
└──────────────────────────────────────────────────────────────────────┘
```

**Key Insight**: You use GC2's native `RigLookTo` exactly as normal. When `UnitIKNetworkController` is attached:
- **Local player**: Controller reads from your existing `RigLookTo.LookToTarget`
- **Remote players**: Controller injects a `NetworkLookTarget` into their `RigLookTo`

No changes to your GC2 workflow are needed - it becomes network-synced automatically.

### ILookTo Interface (What Gets Synced)

```csharp
// GC2's native interface that RigLookTo uses
public interface ILookTo
{
    int Layer { get; }           // Priority layer (higher = takes precedence)
    bool Exists { get; }         // Whether target is still valid
    Vector3 Position { get; }    // World position to look at
    GameObject Target { get; }   // Optional GameObject reference
}
```

The `NetworkLookTarget` class implements this interface, allowing it to be injected into remote players' `RigLookTo` systems as if it were a local target.

### Delta Compression Logic

Updates are only sent when the look target changes significantly:

```csharp
// Only sends update if:
bool shouldSend = 
    Vector3.Distance(currentPos, lastSentPos) > 0.05f ||  // Moved 5cm+
    Mathf.Abs(currentWeight - lastSentWeight) > 0.02f;    // Weight changed 2%+
```

This means a character staring at a fixed point sends **zero bandwidth** after the initial state.

### IK Setup Example
```csharp
var ikController = character.gameObject.AddComponent<UnitIKNetworkController>();
ikController.Initialize(character, isLocalPlayer);

// Configure sync options
ikController.SyncLookTo = true;  // Sync look-at targets
ikController.SyncAim = true;     // Sync aim direction

// Hook up network events (local player only)
if (isLocalPlayer)
{
    ikController.OnIKStateReady += state => {
        NetworkManager.BroadcastToOthers(state);
    };
}
```

### Remote Player Receives IK State
```csharp
// In your network receive handler
void OnReceiveIKState(NetworkIKState state)
{
    ikController.ApplyIKState(state);
}
```

### Manual Look Target Control
```csharp
// Force look at a specific position with network sync
ikController.SetLookTarget(enemyPosition, layer: 1);

// Clear look target
ikController.ClearLookTarget();
```

### IK Bandwidth Analysis
- LookTo state: 13 bytes per update
- Aim state: 5 bytes per update
- Combined: ~20 bytes per update
- With delta compression at 20 Hz: **<0.4 KB/s** (only when changing)
- Typical usage: **0.1-0.3 KB/s** (IK targets change infrequently)

### Why Most IK is Local-Only

**RigLean (Momentum Lean)**
- Calculates lean from `Driver.LocalMoveDirection`
- Movement is already synced → Lean automatically matches

**RigFeetPlant (Foot IK)**
- Uses physics raycasts against local terrain
- Each client has the same terrain → Same results locally

**RigBreathing / RigTwitching**
- Procedural noise for visual variety
- Randomness doesn't need to match (cosmetic)

### Netcode Integration Example
```csharp
public class NetcodeIKSync : NetworkBehaviour
{
    private UnitIKNetworkController ikController;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        ikController = gameObject.AddComponent<UnitIKNetworkController>();
        ikController.Initialize(character, IsOwner);
        
        if (IsOwner)
        {
            ikController.OnIKStateReady += BroadcastIKStateClientRpc;
        }
    }
    
    [ClientRpc]
    private void BroadcastIKStateClientRpc(NetworkIKState state)
    {
        if (!IsOwner) ikController.ApplyIKState(state);
    }
}
```

### Complete Character Network Setup
```csharp
// Full setup with all network components
public class FullNetworkCharacter : NetworkBehaviour
{
    private NetworkCharacterManager characterManager;
    private UnitAnimimNetworkController animController;
    private UnitIKNetworkController ikController;
    private UnitMotionNetworkController motionController;
    
    public override void OnNetworkSpawn()
    {
        var character = GetComponent<Character>();
        
        // 1. Movement (server-authoritative)
        characterManager = GetComponent<NetworkCharacterManager>();
        characterManager.ChangeRole(IsOwner 
            ? NetworkCharacterManager.CharacterRole.LocalPlayer 
            : NetworkCharacterManager.CharacterRole.RemotePlayer);
        
        // 2. Animation (owner-authoritative)
        animController = gameObject.AddComponent<UnitAnimimNetworkController>();
        animController.Initialize(character, IsOwner);
        
        // 3. IK (owner-authoritative)
        ikController = gameObject.AddComponent<UnitIKNetworkController>();
        ikController.Initialize(character, IsOwner);
        
        // 4. Motion abilities (server-authoritative)
        // motionController setup...
        
        // Hook up events
        if (IsOwner)
        {
            characterManager.OnSendInputs += SendInputsServerRpc;
            animController.OnGestureCommandReady += BroadcastGestureClientRpc;
            ikController.OnIKStateReady += BroadcastIKStateClientRpc;
        }
    }
    
    // ... RPC methods ...
}
```

### Total Bandwidth Summary

| System | Typical Usage | Peak Usage |
|--------|---------------|------------|
| Movement (input) | 1.44 KB/s | 1.44 KB/s |
| Movement (state) | 4.2 KB/s | 4.2 KB/s |
| Animation | 0.5-2 KB/s | 5 KB/s |
| IK | 0.1-0.3 KB/s | 0.4 KB/s |
| Motion | <0.5 KB/s | 1 KB/s |
| NavMesh (per agent) | 0.2 KB/s | 0.4 KB/s |
| Off-Mesh Links | <0.1 KB/s | 0.5 KB/s |
| **Total (player)** | **~7 KB/s** | **~12 KB/s** |
| **Total (NPC)** | **~0.3 KB/s** | **~1 KB/s** |

This is 4-6x more efficient than uncompressed GC2 networking.
