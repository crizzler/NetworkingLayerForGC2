# Game Creator 2 Multiplayer Guide

## Server-Authoritative Networking with Unity Netcode for GameObjects 2.9.2

This guide explains how to set up Game Creator 2 characters for multiplayer using server-authoritative networking, transport-agnostic movement sync, and profile-based tuning for competitive sessions.

---

## Table of Contents

1. [Overview](#overview)
2. [Prerequisites](#prerequisites)
3. [Understanding the Architecture](#understanding-the-architecture)
4. [Installation](#installation)
5. [Quick Start](#quick-start)
6. [The Three Network Drivers](#the-three-network-drivers)
7. [Player Prefab Setup](#player-prefab-setup)
8. [Input Types](#input-types)
9. [Session Profiles and Transport Bridge](#session-profiles-and-transport-bridge)
10. [Host Mode (P2P)](#host-mode-p2p)
11. [Dedicated Server](#dedicated-server)
12. [Animation Synchronization](#animation-synchronization)
13. [IK Synchronization](#ik-synchronization)
14. [Motion Abilities (Dash/Teleport)](#motion-abilities-dashteleport)
15. [Network Data Types](#network-data-types)
16. [Bandwidth Analysis](#bandwidth-analysis)
17. [Best Practices](#best-practices)
18. [Troubleshooting](#troubleshooting)
19. [FAQ](#faq)

---

## Overview

### Why Server-Authoritative?

Standard GC2 characters work great for single-player, but multiplayer requires:

- **Cheat Prevention**: Server validates all movement
- **Responsive Feel**: Client-side prediction removes input lag
- **Smooth Visuals**: Interpolation for other players
- **Bandwidth Efficiency**: Compressed network data

### What This System Provides

| Feature | Description |
|---------|-------------|
| **Client-Side Prediction** | Your character moves instantly, no waiting for server |
| **Server Reconciliation** | Corrections when prediction differs from server |
| **Remote Interpolation** | Other players move smoothly on your screen |
| **Animation Sync** | Gestures and states sync across network |
| **IK Sync** | Look-at and aim targets sync efficiently |
| **Motion Abilities** | Server-authoritative dash/teleport |
| **Anti-Cheat** | Server validates movement speed and inputs |

### Bandwidth Summary

| Per Player | Bandwidth |
|------------|-----------|
| Movement (input → server) | ~1.5 KB/s |
| Movement (state → clients) | ~4.5 KB/s |
| Animation | ~0.5-2 KB/s |
| IK | ~0.1-0.3 KB/s |
| Motion | <0.5 KB/s |
| **Total** | **~7 KB/s** |

This is 4-6x more efficient than sending raw Transform data.

---

## Prerequisites

### Required Packages

Add to your `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.unity.netcode.gameobjects": "2.9.2"
  }
}
```

Or via Package Manager:
1. Window → Package Manager
2. Click **+** → Add package by name
3. Enter `com.unity.netcode.gameobjects` version `2.9.2`

### Assembly Definition Requirements

If you keep this package in asmdefs, ensure both runtime and editor assemblies reference `Unity.Netcode.Runtime`:

- `Arawn.GameCreator2.Networking.asmdef` includes `Unity.Netcode.Runtime` and version define `UNITY_NETCODE`.
- `Arawn.GameCreator2.Networking.Editor.asmdef` also includes `Unity.Netcode.Runtime` and version define `UNITY_NETCODE`.

### Required Assets

- **Unity 6 LTS** or newer
- **Game Creator 2** (Core module minimum)
- **Enemy Masses with GC2 Integration** (optional — needed only for `UnitPlayerDirectionalEnemyMassesNetwork` and `UnitPlayerEnemyMassesRTSNetwork` input units)

### Recommended Knowledge

- Basic Unity networking concepts (NetworkBehaviour, RPCs, NetworkVariables)
- Game Creator 2 character setup
- C# scripting fundamentals

---

## Understanding the Architecture

### The Problem with Standard Networking

Naive approaches to networking GC2 characters have issues:

```
❌ Bad: Sync Transform directly
   - 36+ bytes per update (position + rotation)
   - No prediction = laggy input
   - No validation = easy to cheat

❌ Bad: Send raw input, server moves character
   - Responsive on server, laggy on client
   - Client sees delayed movement

✅ Good: Client prediction + server authority
   - Client moves immediately (feels responsive)
   - Server validates and broadcasts truth
   - Client corrects if prediction was wrong
```

### The Three Roles

Every networked character exists in one of three roles:

```
┌─────────────────────────────────────────────────────────────────────┐
│  ROLE: Local Player (IsOwner = true)                                │
│  DRIVER: UnitDriverNetworkClient                                    │
│  BEHAVIOR:                                                          │
│    • Processes input immediately (prediction)                       │
│    • Sends inputs to server                                         │
│    • Receives server state, reconciles if needed                    │
│    • Feels responsive to the player                                 │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  ROLE: Server Authority (IsServer = true)                           │
│  DRIVER: UnitDriverNetworkServer                                    │
│  BEHAVIOR:                                                          │
│    • Receives inputs from clients                                   │
│    • Validates inputs (anti-cheat)                                  │
│    • Simulates authoritative movement                               │
│    • Broadcasts position to all clients                             │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│  ROLE: Remote Player (IsOwner = false, IsServer = false)            │
│  DRIVER: UnitDriverNetworkRemote                                    │
│  BEHAVIOR:                                                          │
│    • Receives position updates from server                          │
│    • Interpolates between snapshots (smooth)                        │
│    • Extrapolates briefly if packets delayed                        │
│    • Visual-only, no physics simulation                             │
└─────────────────────────────────────────────────────────────────────┘
```

### How It All Connects (Bridge Flow)

```
Player A presses WASD
       │
       ▼
┌──────────────────────────────────┐
│ UnitDriverNetworkClient          │
│ (Client A)                       │
│                                  │
│ 1. Move character immediately    │
│ 2. Store predicted state         │
│ 3. Pack input → 8 bytes          │
└──────────────┬───────────────────┘
               │
               │ [NetworkTransportBridge.SendToServer]
               ▼
┌──────────────────────────────────┐
│ UnitDriverNetworkServer          │
│ (Server/Host)                    │
│                                  │
│ 1. Validate input (anti-cheat)   │
│ 2. Apply movement                │
│ 3. Pack state → 14 bytes         │
└──────────────┬───────────────────┘
               │
               │ [NetworkTransportBridge.Broadcast]
               ▼
┌──────────────────────────────────┬──────────────────────────────────┐
│ Client A (Owner)                 │ Client B (Remote)                │
│                                  │                                  │
│ Compare server vs predicted      │ Add to snapshot buffer           │
│ If different: reconcile          │ Interpolate smoothly             │
└──────────────────────────────────┴──────────────────────────────────┘
```

### Instance Distribution (2 Players + Dedicated Server)

```
┌─────────────────────────────────────────────────────────────────────┐
│                    DEDICATED SERVER (Headless)                       │
├─────────────────────────────────────────────────────────────────────┤
│  Player A → UnitDriverNetworkServer (validates A's inputs)          │
│  Player B → UnitDriverNetworkServer (validates B's inputs)          │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                    CLIENT A's SCREEN                                 │
├─────────────────────────────────────────────────────────────────────┤
│  My Character (A)  → UnitDriverNetworkClient (predicted)            │
│  Other Player (B)  → UnitDriverNetworkRemote (interpolated)         │
└─────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────┐
│                    CLIENT B's SCREEN                                 │
├─────────────────────────────────────────────────────────────────────┤
│  My Character (B)  → UnitDriverNetworkClient (predicted)            │
│  Other Player (A)  → UnitDriverNetworkRemote (interpolated)         │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Installation

### Step 1: Import the Networking Package

The GC2 networking components should be placed in your project:

```
Assets/
└── Plugins/
    ├── GameCreator2NetworkingLayer/
    │   ├── NetworkCharacter.cs              ← Primary entry point
    │   ├── NetworkTransportBridge.cs        ← Transport abstraction
    │   ├── NetcodeGameObjectsTransportBridge.cs
    │   ├── NetworkSessionProfile.cs         ← Duel/Standard/Massive tuning
    │   ├── StableHashUtility.cs
    │   ├── NetworkCharacterTypes.cs
    │   ├── NetworkAnimationTypes.cs
    │   ├── NetworkIKTypes.cs
    │   ├── NetworkMotionTypes.cs
    │   ├── UnitDriverNetworkClient.cs
    │   ├── UnitDriverNetworkServer.cs
    │   ├── UnitDriverNetworkRemote.cs
    │   ├── UnitAnimimNetworkController.cs
    │   ├── UnitIKNetworkController.cs
    │   ├── UnitMotionNetworkController.cs
    │   ├── VisualScripting/               ← Conditions & Instructions
    │   ├── Variables/                     ← NetworkVariableSync
    │   └── Documentation/
    └── NetworkingCore/
        └── Runtime/
            ├── LagCompensation/
            └── NetworkSpawnListener.cs
```

### Step 2: Create NetworkManager

1. Create empty GameObject: `GameObject → Create Empty`
2. Name it **NetworkManager**
3. Add component: `Netcode → NetworkManager`
4. Add component: `Netcode → UnityTransport`
5. Add component: `Game Creator → Network → Transport → Netcode Bridge`

Configure NetworkManager:
```
Network Manager:
├── Player Prefab: (your player prefab)
├── Network Prefabs List: (add your prefabs)
└── Tick Rate: 30-60
```

Configure UnityTransport:
```
Unity Transport:
├── Connection Data:
│   ├── Address: 127.0.0.1 (for testing)
│   ├── Port: 7777
│   └── Server Listen Address: 0.0.0.0
```

### Step 3: Create a Session Profile (Recommended)

1. Create asset: `Create → Game Creator → Network → Session Profile`
2. Pick preset: `Duel`, `Standard`, or `Massive`
3. Assign it to `NetcodeGameObjectsTransportBridge.Global Session Profile`
4. (Optional) Override per character in `NetworkCharacter > Session and Compatibility`

---

## Quick Start

### Minimal Working Example (5 Minutes)

1. **Create Player Prefab** with GC2 `Character`
2. **Add `NetworkObject` + `NetworkCharacter`** to the same prefab
3. **Register prefab** in `NetworkManager.NetworkPrefabs`
4. **Use `NetcodeGameObjectsTransportBridge`** in scene
5. **Assign a Session Profile** (recommended)
6. **Play**

For NGO, `NetworkCharacter` initializes itself in `OnNetworkSpawn()` and auto-assigns:
- `UnitDriverNetworkServer` on server authority instances
- `UnitDriverNetworkClient` on local predicted owner
- `UnitDriverNetworkRemote` on remote proxies

You only call `InitializeNetworkRole(...)` manually when integrating a non-NGO transport/framework.

### Test It

1. Build two instances of your game
2. Instance 1: Call `NetworkManager.Singleton.StartHost()`
3. Instance 2: Call `NetworkManager.Singleton.StartClient()`
4. Both players should see each other with smooth movement
5. Confirm movement packets flow through `NetcodeGameObjectsTransportBridge` (not direct RPC wiring)

> **Note:** For tuning and custom network backends, see [Session Profiles and Transport Bridge](#session-profiles-and-transport-bridge).

---

## The Three Network Drivers

### UnitDriverNetworkClient

**Purpose:** Your local character with prediction

**Key Features:**
- Moves immediately on input (no lag)
- Stores prediction history
- Reconciles when server disagrees
- Smooth correction (not teleporting)

**When to Use:**
```csharp
if (IsOwner) // This is MY character
{
    character.ChangeDriver(new UnitDriverNetworkClient());
}
```

**Key Methods:**
```csharp
// Event: Send inputs to server (with redundancy)
clientDriver.OnSendInput += (NetworkInputState[] inputs) => { };

// Apply server correction
clientDriver.ApplyServerState(serverState);

// Event: Reconciliation occurred (for debugging)
clientDriver.OnReconciliation += (float errorMagnitude) => { };
```

### UnitDriverNetworkServer

**Purpose:** Server's authoritative simulation

**Key Features:**
- Validates client inputs
- Applies physics simulation
- Detects speed hacks
- Produces authoritative state

**When to Use:**
```csharp
if (IsServer) // Server validates this character
{
    character.ChangeDriver(new UnitDriverNetworkServer());
}
```

**Key Methods:**
```csharp
// Queue input from client
serverDriver.QueueInput(input);

// Process all queued inputs, produce state
NetworkPositionState state = serverDriver.ProcessInputs();

// Event: Speed violation detected
serverDriver.OnSpeedViolation += (int violationCount) => { };

// Get current state without processing
NetworkPositionState current = serverDriver.GetCurrentState();
```

### UnitDriverNetworkRemote

**Purpose:** Other players on your screen

**Key Features:**
- Buffers server snapshots
- Interpolates for smooth movement
- Extrapolates briefly on packet loss
- Snaps on teleport detection

**When to Use:**
```csharp
if (!IsOwner && !IsServer) // Someone else's character
{
    character.ChangeDriver(new UnitDriverNetworkRemote());
}
```

**Key Methods:**
```csharp
// Add snapshot from server
remoteDriver.AddSnapshot(state, serverTimestamp);

// Force teleport (skip interpolation)
remoteDriver.TeleportTo(position, rotationY);

// Check if extrapolating (packets delayed)
bool isExtrapolating = remoteDriver.IsExtrapolating;
```

---

## Player Prefab Setup

### Required Components

Your player prefab needs:

```
Player (Prefab Root)
│
├── Character (Game Creator 2)
│   ├── Motion: Configure speed, jump, gravity
│   ├── Driver: (assigned at runtime by NetworkCharacter)
│   └── Player: Use standard input unit
│
├── NetworkObject (Unity Netcode)
│   └── (default settings fine)
│
├── NetworkCharacter
│   ├── NPC Mode: (for NPCs only)
│   └── Use Core Networking: ✓
│
├── Model / Animator
│   └── Your character model
│
└── [Optional] Camera Rig
    └── (disabled for remote players)
```

### CharacterController Note

The network drivers automatically add/configure Unity's `CharacterController`. You don't need to add it manually.

---

## Input Types

### Available Network Player Units

| Unit | Input Style | Bytes/Update | Requires Enemy Masses |
|------|-------------|--------------|----------------------|
| `UnitPlayerDirectionalNetwork` | WASD/Gamepad | 8 | No |
| `UnitPlayerFollowPointerNetwork` | Mouse follow | 4 | No |
| `UnitPlayerTankNetwork` | Tank controls | 5 | No |
| `UnitPlayerPointClickNetwork` | Click destination | 14 | No |
| `UnitPlayerDirectionalEnemyMassesNetwork` | WASD + Fog of War | 8 | Yes |
| `UnitPlayerEnemyMassesRTSNetwork` | RTS selection | 14+ | Yes |

### Using Different Input Units

```csharp
// Standard WASD/Gamepad
var player = new UnitPlayerDirectionalNetwork();
Character.Kernel.ChangePlayer(character, player);

// Tank controls
var tankPlayer = new UnitPlayerTankNetwork();
Character.Kernel.ChangePlayer(character, tankPlayer);

// Click-to-move (pair with NavMesh driver)
var clickPlayer = new UnitPlayerPointClickNetwork();
Character.Kernel.ChangePlayer(character, clickPlayer);
Character.Kernel.ChangeDriver(character, new UnitDriverNavmeshNetworkClient());
```

> **Tip:** For local predicted owners, `NetworkCharacter` auto-upgrades common GC2 units (`UnitPlayerDirectional`, `UnitPlayerPointClick`, `UnitPlayerFollowPointer`, `UnitPlayerTank`) to their `*Network` variants.

---

## Session Profiles and Transport Bridge

Recent updates moved movement sync from direct hard-coded RPC wiring to a transport bridge plus profile-driven tuning.

### NetworkSessionProfile

`NetworkSessionProfile` is a `ScriptableObject` that controls:

- server simulation rate (`serverSimulationRate`)
- server state broadcast rate (`serverStateBroadcastRate`)
- client input send rate and redundancy
- reconciliation thresholds
- anti-cheat thresholds
- near/mid/far relevance tiers (state rate + subsystem sync toggles)

Built-in presets:

| Preset | Intended Match Size | Default Behavior |
|--------|---------------------|------------------|
| `Duel` | 1v1 to small teams | High rates, tighter reconciliation |
| `Standard` | Typical PvP sessions | Balanced defaults |
| `Massive` | Large sessions (up to ~48v48) | Lower far-tier rates, expensive sync reduced |

### Relevance Tiering

Remote characters are automatically tiered by distance:

- `Near`: highest state rates, full sync
- `Mid`: reduced rates, optional IK off
- `Far`: low rates, animation/core/combat can be disabled

This is applied through `UnitDriverNetworkRemote.ApplyTierSettings(...)` plus per-subsystem toggles in `NetworkCharacter`.

### Fan-Out Relevance Filtering (New)

Relevance now also affects the actual server send path (not just local remote playback settings).

`NetworkSessionProfile` controls:
- `requireObserverCharacterForRelevance`
- `enableDistanceCulling`
- `cullDistance`
- `culledKeepAliveRate`

Recommended for large sessions:
- `Massive` preset enables strict observer requirement + distance culling by default.
- Keepalive is set to low frequency to avoid stale forever snapshots while still reducing bandwidth.

### Host Fairness Policy

`NetworkCharacter` now exposes:

- `Host Owner Uses Client Prediction` (`m_HostOwnerUsesClientPrediction`)

Default is **off** for stricter server-authority fairness in competitive sessions.  
If enabled, host-owned characters use `LocalClient` role for snappier feel.

### Transport-Agnostic Movement Path

`NetworkCharacter` now emits/consumes payload events and delegates movement transport:

- `OnInputPayloadReady(uint networkId, NetworkInputState[] inputs)`
- `OnStatePayloadReady(uint networkId, NetworkPositionState state, float serverTime)`
- `NetworkTransportBridge.SendToServer(...)`
- `NetworkTransportBridge.Broadcast(...)`
- `NetworkTransportBridge.OnInputReceivedServer(senderClientId, characterNetworkId, inputs)`
- `NetworkTransportBridge.OnStateReceivedClient`
- `NetworkTransportBridge.SetCharacterOwner(...) / TryGetCharacterOwner(...)`

For NGO, use `NetcodeGameObjectsTransportBridge` (named messages: `GC2N/Input`, `GC2N/State`).

### Registry and Runtime ID Policy (New)

`NetworkTransportBridge` now exposes two important competitive-session controls:

- `Strict Registry Lookup` (default: ON)
  - Character resolution is O(1) through bridge registration only.
  - No runtime `FindObjectsByType` scene scan fallback on misses.
  - Recommended for 16v16 to 48v48 sessions.

- `Use Server-Issued IDs When Available` (default: ON)
  - Server applies transport-issued runtime IDs when the transport can provide one.
  - In NGO, this maps to `NetworkObject.NetworkObjectId`.

- `Allocate Server-Issued IDs When Transport Missing` (default: OFF)
  - Optional bridge allocator fallback for custom transports that do not provide object IDs.
  - Keep OFF unless your transport layer also replicates the allocated IDs to clients.

Why this matters:

- Removes expensive scene scans from runtime hot paths.
- Reduces ID instability risk from hierarchy/scene-hash fallbacks in long sessions.
- Keeps ownership validation and state routing tied to authoritative IDs.

### Plugging in Your Favorite Networking Stack

Implement your own bridge by inheriting `NetworkTransportBridge`:

```csharp
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public class MyTransportBridge : NetworkTransportBridge
    {
        public override bool IsServer => /* your transport check */;
        public override bool IsClient => /* your transport check */;
        public override bool IsHost => /* your transport check */;
        public override float ServerTime => /* synchronized server clock */;

        public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
        {
            // Serialize and send to server through your networking SDK.
        }

        public override void SendToOwner(uint ownerClientId, uint characterNetworkId,
            NetworkPositionState state, float serverTime)
        {
            // Optional unicast response path.
        }

        public override void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null)
        {
            // Broadcast authoritative state to clients.
            // Apply optional relevanceFilter(targetClientId, characterNetworkId, state, serverTime).
        }

        // Optional override if your framework has a direct ID->Character map.
        public override Character ResolveCharacter(uint networkId)
        {
            return base.ResolveCharacter(networkId);
        }
    }
}
```

`NetworkCharacter` keeps compatibility fallback via `NetworkCharacterManager` when no bridge is active.

---

## Host Mode (P2P)

### What is Host Mode?

One player runs both client AND server:

```
┌─────────────────────────────────────────────────────────────────────┐
│                    HOST (Player 1's PC)                              │
│                                                                      │
│  ┌─────────────────┐  ┌─────────────────┐                          │
│  │ Client Logic    │  │ Server Logic    │                          │
│  │ (Renders, UI)   │  │ (Validates)     │                          │
│  └─────────────────┘  └─────────────────┘                          │
│                                                                      │
│  IsServer = true   IsClient = true   IsHost = true                  │
└─────────────────────────────────────────────────────────────────────┘
                              │
                         Internet/LAN
                              │
┌─────────────────────────────────────────────────────────────────────┐
│                    CLIENT (Player 2's PC)                            │
│                                                                      │
│  ┌─────────────────┐                                                │
│  │ Client Logic    │                                                │
│  │ (Renders, UI)   │                                                │
│  └─────────────────┘                                                │
│                                                                      │
│  IsServer = false  IsClient = true   IsHost = false                 │
└─────────────────────────────────────────────────────────────────────┘
```

### Host Mode Setup

```csharp
public class SimpleNetworkUI : MonoBehaviour
{
    [SerializeField] private TMP_InputField m_IPInput;
    
    public void OnHostButtonClicked()
    {
        NetworkManager.Singleton.StartHost();
    }
    
    public void OnJoinButtonClicked()
    {
        string ip = m_IPInput.text;
        if (string.IsNullOrEmpty(ip)) ip = "127.0.0.1";
        
        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.ConnectionData.Address = ip;
        
        NetworkManager.Singleton.StartClient();
    }
}
```

### How NetworkCharacter Handles Host Mode

`NetworkCharacter` resolves host-owned characters with a configurable policy:

- default (`Host Owner Uses Client Prediction = false`): host-owned character stays in `Server` role for stricter fairness
- optional (`Host Owner Uses Client Prediction = true`): host-owned character uses `LocalClient` role for lower input latency

Other non-owned characters on the host remain server-authoritative.

| Pros | Cons |
|------|------|
| No server costs | Host has 0 latency advantage |
| Easy setup | Game ends if host disconnects |
| Good for friends | Host's PC does extra work |

---

## Dedicated Server

### What is Dedicated Server?

A separate process that only runs server logic:

```
┌─────────────────────────────────────────────────────────────────────┐
│                    DEDICATED SERVER (Cloud/VPS)                      │
│                                                                      │
│  ┌─────────────────┐                                                │
│  │ Server Logic    │  ← No rendering, no audio, no UI              │
│  │ (Validates)     │                                                │
│  └─────────────────┘                                                │
│                                                                      │
│  IsServer = true   IsClient = false   IsHost = false                │
└─────────────────────────────────────────────────────────────────────┘
                              │
                         Internet
        ┌─────────────────────┴─────────────────────┐
        │                                           │
┌───────▼────────┐                         ┌───────▼────────┐
│   CLIENT A     │                         │   CLIENT B     │
│   (Player 1)   │                         │   (Player 2)   │
└────────────────┘                         └────────────────┘
```

### Building for Dedicated Server

1. File → Build Settings
2. Target Platform: **Dedicated Server**
3. Build

The dedicated server build:
- Has no graphics/audio
- Smaller size (~80MB vs ~500MB)
- Can run multiple instances per machine

### Dedicated Server Startup

```csharp
public class DedicatedServerBootstrap : MonoBehaviour
{
    private void Start()
    {
        #if UNITY_SERVER
        Application.targetFrameRate = 60;
        QualitySettings.vSyncCount = 0;
        NetworkManager.Singleton.StartServer();
        Debug.Log("Dedicated server started on port 7777");
        #endif
    }
}
```

| Pros | Cons |
|------|------|
| Fair for all players | Server hosting costs |
| Game continues if player leaves | More complex deployment |
| Better for competitive | Requires infrastructure |

---

## Animation Synchronization

### Why Owner Authority?

Animation sync uses **owner authority** (not server) because:

1. **Animations are cosmetic** — don't affect gameplay physics
2. **Immediate feedback** — player sees their animation instantly
3. **Half the bandwidth** — owner → all, not owner → server → all

### How It Works

```
Player A attacks
       │
       ▼
UnitAnimimNetworkController (Owner)
  - Captures animation command
  - Compresses to 14 bytes
       │
       ▼ [Transport broadcast to remotes]
       │
       ├──► Client A: (already playing locally)
       └──► Client B: ApplyGestureCommand() → plays animation
```

### Animation Registration

Animations are sent as hashes for efficiency. Register clips at startup:

```csharp
void Start()
{
    // Register all clips that can be networked
    m_AnimController.RegisterClip(attackClip);
    m_AnimController.RegisterClip(jumpClip);
    m_AnimController.RegisterClip(rollClip);
    
    // Register states
    m_AnimController.RegisterState(locomotionState);
}
```

> **Tip:** For larger projects, use a **ScriptableObject Animation Registry** instead of runtime registration. Create via `Assets > Create > Game Creator > Network > Animation Registry`.

### Playing Networked Animations

```csharp
// Use the network controller instead of GC2 directly:
m_AnimController.PlayGesture(
    clip: attackClip,
    mask: upperBodyMask,
    blendMode: BlendMode.Blend,
    config: new ConfigGesture(0f, 0.5f, 1f, false, 0.1f, 0.1f),
    stopPreviousGestures: true
);
```

---

## IK Synchronization

### Which IK Needs Sync?

| IK Rig | Sync? | Why |
|--------|-------|-----|
| **RigLookTo** | ✅ YES | Where character is looking matters |
| **RigAimTowards** | ✅ YES | Aim direction for weapons |
| **RigLean** | ❌ NO | Calculated from movement (already synced) |
| **RigFeetPlant** | ❌ NO | Uses local physics raycasts |
| **RigBreathing** | ❌ NO | Cosmetic randomness |

### How Look-At Sync Works

```
LOCAL PLAYER                          REMOTE PLAYER
────────────                          ─────────────

RigLookTo.LookToTarget                    │
      │                                   │
      ▼                                   │
Controller reads:                         │
  - target.Position                       │
  - RigLookTo.Weight                      │
  - target.Layer                          │
      │                                   │
      ▼                                   │
Compress to 13 bytes                      │
      │                                   │
      └────────[Network]──────────────────┤
                                          │
                                          ▼
                                    Controller creates
                                    NetworkLookTarget (ILookTo)
                                          │
                                          ▼
                                    RigLookTo.SetTarget(networkTarget)
                                          │
                                          ▼
                                    Character looks at target
```

**Key Insight:** Use GC2's RigLookTo normally. When `UnitIKNetworkController` is attached, it auto-syncs. No code changes needed.

### Manual IK Control

```csharp
// Force look at a specific position with network sync
m_IKController.SetLookTarget(enemyPosition, layer: 1);

// Clear look target
m_IKController.ClearLookTarget();
```

---

## Motion Abilities (Dash/Teleport)

Dash and teleport are **server-authoritative** to prevent cheating:

```csharp
// Client requests dash
motionController.RequestDash(
    direction: transform.forward,
    speed: 20f,
    duration: 0.2f,
    callback: result => {
        if (!result.approved)
            Debug.Log($"Dash rejected: {result.rejectionReason}");
    }
);

// Client requests teleport
motionController.RequestTeleport(
    position: targetPosition,
    rotationY: 180f,
    callback: result => {
        if (result.approved)
            PlayTeleportEffect();
    }
);
```

The server validates:
- Cooldown timers
- Destination reachability
- Movement speed limits
- Any active stuns/roots

---

## Network Data Types

### NetworkInputState (8 bytes)

```csharp
public struct NetworkInputState : INetworkSerializable
{
    // 2 bytes: Input direction (quantized)
    // 2 bytes: Sequence number
    // 2 bytes: Delta time (quantized)
    // 1 byte: Flags (jump, sprint, etc.)
    // 1 byte: Reserved
    
    public const byte FLAG_JUMP = 1 << 0;
    public const byte FLAG_SPRINT = 1 << 1;
    
    public Vector2 GetInputDirection();
    public float GetDeltaTime();
    public bool HasFlag(byte flag);
}
```

### NetworkPositionState (14 bytes)

```csharp
public struct NetworkPositionState : INetworkSerializable
{
    // 4 bytes: X position (half precision)
    // 4 bytes: Z position (half precision)
    // 2 bytes: Y position (quantized)
    // 2 bytes: Y rotation (quantized)
    // 2 bytes: Sequence + flags
    
    public Vector3 GetPosition();
    public float GetRotationY();
    public bool IsGrounded { get; }
    public bool IsJumping { get; }
}
```

### NetworkGestureCommand (14 bytes)

```csharp
public struct NetworkGestureCommand : INetworkSerializable
{
    // 4 bytes: Animation hash
    // 4 bytes: Config (blend times, speed)
    // 2 bytes: Mask hash
    // 2 bytes: Blend mode + flags
    // 2 bytes: Reserved
}
```

### NetworkLookToState (13 bytes)

```csharp
public struct NetworkLookToState : INetworkSerializable
{
    // 4 bytes: Relative X position
    // 4 bytes: Relative Z position
    // 2 bytes: Absolute Y position
    // 1 byte: Weight
    // 1 byte: Layer
    // 1 byte: Flags
}
```

---

## Bandwidth Analysis

### Input Rate (Client → Server)

```
60 Hz input rate
× 8 bytes per input
× 3 redundancy (for packet loss)
= 1,440 bytes/sec
≈ 1.4 KB/s per player
```

### State Rate (Server → Clients)

```
20 Hz state rate
× 14 bytes per state
× (players - 1) states received
= 280 bytes/sec per other player
```

### Total Per Player

| Component | Bandwidth |
|-----------|-----------|
| Input upload | ~1.4 KB/s |
| Position download | ~4.5 KB/s |
| Animation | ~0.5 KB/s |
| IK | ~0.2 KB/s |
| Motion | <0.5 KB/s |
| **Total** | **~7 KB/s** |

### Comparison to Naive Approach

| Approach | Bandwidth |
|----------|-----------|
| Raw Transform sync | ~30-50 KB/s |
| Our compressed system | ~7 KB/s |
| **Savings** | **4-7x better** |

### Reducing Bandwidth

```csharp
// Use profile-driven tuning
sessionProfile.serverStateBroadcastRate = 10;
sessionProfile.inputSendRate = 20;
sessionProfile.far.syncIK = false;
sessionProfile.far.syncAnimation = false;

// Optional: looser reconciliation for unstable links
sessionProfile.reconciliationThreshold = 0.2f;
```

---

## Best Practices

### 1. Use Appropriate Tick Rates

```csharp
// Competitive/action games
sessionProfile.ApplyPreset(NetworkSessionPreset.Duel);

// Mixed/standard sessions
sessionProfile.ApplyPreset(NetworkSessionPreset.Standard);

// Large sessions
sessionProfile.ApplyPreset(NetworkSessionPreset.Massive);
```

### 2. Handle Disconnections

```csharp
public override void OnNetworkDespawn()
{
    if (IsOwner)
    {
        SceneManager.LoadScene("MainMenu");
    }
}
```

### 3. Lag Compensation (Advanced)

For fair hit detection across varying latencies, use `CharacterLagCompensation` + `LagCompensationBootstrap`. See the main [README.md](README.md#lag-compensation-hit-validation) for the full lag compensation guide.

```csharp
// Quick setup: add LagCompensationBootstrap to your NetworkManager
// Then on each character:
var lagComp = gameObject.AddComponent<CharacterLagCompensation>();
lagComp.Initialize(GetComponent<Character>());
lagComp.SetNetworkId(NetworkObjectId);
```

### 4. Network Culling (Large Worlds)

Only sync characters within range:

```csharp
foreach (var character in allCharacters)
{
    foreach (var client in connectedClients)
    {
        float distance = Vector3.Distance(
            character.position, client.playerPosition
        );
        if (distance < cullDistance)
            SendStateToClient(client, character.state);
    }
}
```

### 5. Late-Join State Sync

Use `NetworkLateJoinCoordinator` to send current game state (inventory, stats, triggers, variables) to clients that join mid-match. See [Documentation/late-join-coordinator.md](Documentation/late-join-coordinator.md).

---

## Troubleshooting

### Problem: Character teleports/snaps frequently

**Cause:** Reconciliation threshold too low

**Fix:**
```csharp
clientDriver.Config.reconciliationThreshold = 0.15f; // Up from 0.1
```

### Problem: Remote players move jerkily

**Cause:** Low state broadcast rate or packet loss

**Fix:**
```csharp
// Increase authoritative broadcast rate:
sessionProfile.serverStateBroadcastRate = 30;

// Increase interpolation buffer for tier(s):
sessionProfile.near.interpolationDelay = 0.12f;
sessionProfile.mid.interpolationDelay = 0.15f;
```

### Problem: Input feels laggy

**Cause:** Not using client-side prediction

**Fix:** Ensure local owner resolves to `NetworkRole.LocalClient` (this is automatic for non-host owners).  
For host mode, enable `Host Owner Uses Client Prediction` if you prefer lower host input latency over strict fairness.

### Problem: Animation doesn't sync

**Cause:** Clips not registered

**Fix:**
```csharp
m_AnimController.RegisterClip(yourClip);
```

### Problem: Compilation errors about serialization

**Cause:** NGO 2.9.2 migration mismatches

**Fix checklist:**

1. Use NGO 2.9.2:
```json
"com.unity.netcode.gameobjects": "2.9.2"
```
2. Use static `NetworkManager.ServerClientId` access (not instance access).
3. Ensure editor asmdef references `Unity.Netcode.Runtime`.
4. Ensure custom RPC payload structs implement `INetworkSerializable` (for example `NetworkAnimimState`).

---

## FAQ

### Q: Can I use this with Mirror/Photon/FishNet?

**A:** Yes. The drivers are transport-agnostic. Implement a custom `NetworkTransportBridge` (or subscribe to `OnInputPayloadReady` / `OnStatePayloadReady`) and map your framework messages to server input + client state delivery. Use `InitializeNetworkRole(...)` from your framework's spawn lifecycle.

### Q: Do I need Enemy Masses?

**A:** No. The networking system works standalone with just Game Creator 2. Enemy Masses is only needed for `UnitPlayerDirectionalEnemyMassesNetwork` and `UnitPlayerEnemyMassesRTSNetwork` input units.

### Q: How many players can this support?

**A:** At ~7 KB/s per player, a typical 1 Mbps connection supports ~140 players. Practical limits depend on server CPU and game logic.

### Q: Can NPCs use this system?

**A:** Yes. See the [NPC Synchronization Modes section in the README](README.md#npc-synchronization-modes-) for server-authoritative, client-side deterministic, and hybrid NPC modes.

### Q: How do I handle abilities like dash/teleport?

**A:** Use `UnitMotionNetworkController` with server authority. See [Motion Abilities](#motion-abilities-dashteleport).

### Q: What about mobile/WebGL?

**A:** Works on all platforms Unity Netcode supports. Consider reducing tick rates for mobile bandwidth.

### Q: What about the old `NetworkCharacterManager` API?

**A:** It still works as compatibility fallback. The preferred path is `NetworkCharacter` + `NetworkTransportBridge` + `NetworkSessionProfile`.

---

## Further Reading

- [README.md](README.md) — Full API reference for all networking components
- [Documentation/late-join-coordinator.md](Documentation/late-join-coordinator.md) — Late-join state sync guide

---

*Last updated: March 3, 2026 | Unity Netcode for GameObjects 2.9.2 | Game Creator 2*
