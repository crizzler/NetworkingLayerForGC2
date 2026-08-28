# GC2 Core Networking Module

Server-authoritative networking for Game Creator 2 Core character features.

For the local UI, cosmetic effect, attached prop, and network-object decision
guide, see the [online documentation](../Documentation/Online%20Documentation.md).
For explicit player/NPC authority, authority-gated GC2 AI, target selection,
movement backends, and bot-backed player handoff, see
[Server-Authoritative NPCs and Bot Slots](../Documentation/Server%20Authoritative%20NPCs%20and%20Bot%20Slots.md).

## Features Covered

| Feature | Request/Response | Broadcast | Server Direct |
|---------|-----------------|-----------|---------------|
| **Ragdoll** | ✅ `NetworkRagdollRequest/Response` | ✅ `NetworkRagdollBroadcast` | ✅ `ServerStartRagdoll()` |
| **Props** | ✅ `NetworkPropRequest/Response` | ✅ `NetworkPropBroadcast` | ✅ attach, exact detach, detach all |
| **Invincibility** | ✅ `NetworkInvincibilityRequest/Response` | ✅ `NetworkInvincibilityBroadcast` | ✅ `ServerSetInvincibility()` |
| **Poise** | ✅ `NetworkPoiseRequest/Response` | ✅ `NetworkPoiseBroadcast` | ✅ `ServerDamagePoise()`, `ServerResetPoise()` |
| **Busy Limbs** | ✅ `NetworkBusyRequest/Response` | ✅ `NetworkBusyBroadcast` | - |
| **Interaction** | ✅ `NetworkInteractionRequest/Response` | ✅ `NetworkInteractionBroadcast` | - |

> Note: This package also syncs GC2 **States** and **Gestures** animations, but that path is handled by `NetworkCharacter` + `UnitAnimimNetworkController` (not by Core message IDs `200-229`).

## PurrNet Scene Setup Wizard

For PurrNet projects, open `Game Creator > Networking Layer > PurrNet Scene Setup Wizard`.
Core, Variables, Animation, and Motion are always included. The wizard creates/reuses `NetworkSecurityManager`, `NetworkCoreManager`, `NetworkAnimationManager`, `NetworkMotionManager`, `NetworkVariableManager`, `PurrNetTransportBridge`, `PurrNetCoreTransportBridge`, `PurrNetVariableTransportBridge`, and `PurrNetAnimationMotionTransportBridge`.

If a Player Prefab is assigned on the Scene page and prefab preparation is enabled, the wizard adds `NetworkIdentity`, `NetworkCharacter`, `PurrNetNetworkCharacterAuto`, network-ready GC2 character units, optional local-variable sync, and optional pre-registered Network Dash/Gesture clips.

The Fusion and PurrNet wizards can also prepare explicit server-owned NPC
prefabs and optional bot-backed slots. Network authority comes from
`NetworkCharacter.ActorType` and transport-authenticated ownership, never from
changing GC2's local `Character.IsPlayer` flag.

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

- Add `NetworkCoreManager` to a single bootstrap object in your scene, or let the PurrNet wizard create it from the Core page.
- Do **not** manually add `NetworkCoreController` in the PurrNet wizard output or on player prefabs.
- `NetworkCoreManager` auto-ensures and initializes `NetworkCoreController` on the manager GameObject at runtime if it is missing.
- Keep `Use Core Networking` enabled on `NetworkCharacter` when you want core feature interception, but avoid manually placing extra `NetworkCoreController` components on characters (prevents duplicate-controller ambiguity).
- For animation state/gesture sync, enable `Use Animation Sync` on `NetworkCharacter` so it adds/uses `UnitAnimimNetworkController`.

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

## States & Gestures Sync (Also Supported)

In addition to the Core features above, the networking layer also syncs GC2 animation commands for:

- `Character.States`
- `Character.Gestures`

This uses `UnitAnimimNetworkController` on `NetworkCharacter` (owner drives commands, remotes apply).
Common API surface:

- `SetState(...)`
- `StopState(...)`
- `PlayGesture(...)`
- `StopGesture(...)` / `StopGestures(...)`

This is intentionally separate from `NetworkCoreManager` message routing and Core type IDs.

## Validation Rules

### Ragdoll
- Cooldown between state changes (configurable)
- Cannot start ragdoll if already ragdoll
- Cannot recover if not ragdoll

#### Ragdoll physics safety

The Networking Layer temporarily removes the active movement backend's root
collision shape from physics before GC2 enables the dynamic bone colliders. It
restores the exact previous controller, agent, capsule, or supported native
backend collision state after GC2 has disabled the bone colliders, while normal
network locomotion remains suspended until the get-up recovery finishes. This
applies on owners, server/Shared authority, and observer replicas and does not
change the ragdoll wire format.

Do not use another script to re-enable the root `CharacterController`, NavMesh
agent/capsule, or native movement shape while the character is physically
ragdolled. Also configure the referenced GC2 Skeleton for the exact character
model. Skeleton assets contain authored collider dimensions; reusing a default
Skeleton made for substantially different proportions or non-uniformly scaled
models can create intersecting bone colliders and unstable physics independently
of networking.

#### Fusion and PurrNet ragdoll examples

Install **Core Examples 1.1.0** for the active transport and open one of these
scenes:

- `Requires GC2 Core Demos - FusionRagdollDemo.unity`
- `Requires GC2 Core Demos - PurrNetRagdollDemo.unity`

Start a Host and connect a second player, then use **Start Ragdoll**, **Recover**,
or **Validate 3 Cycles** on either peer. The buttons execute authored
GC2 `Network Start Ragdoll` and `Network Recover Ragdoll` Instructions, so the
request uses the normal ownership/security validation, authority-apply, and
observer-broadcast path. The proof panel lists every registered representation
on that peer. Before each locally initiated cycle it captures the identity and
exact enabled/collision state of every supported root physics component, checks
that root locomotion collision is absent while dynamic bone bodies are active,
and requires the same components and states during recovery. Actor rows report
only that peer's current replicas; they do not correlate delivery across two
processes. Authority approval is correlated to the exact emitted request and
correlation IDs, including synchronous Host loopback. A completed three-cycle
run is an in-session local-replica hang/regression check; the focused EditMode
suites remain the automated lifecycle evidence, and a two-process smoke is still
required for observer delivery/convergence evidence.

GC2's default ragdoll temporarily unparents the configured Animator while its
bone bodies are dynamic. Diagnostics and optional ragdoll-force application
therefore follow `Character.Animim.Animator` instead of assuming that the bone
hierarchy remains below the Character root. A post-ragdoll
`NetworkCharacterPresentation` warning can indicate the fail-closed visual
interpolation fallback after GC2 has generated physics components beneath the
model; it is not by itself a Core request or ragdoll-replication failure.

These scenes use GC2's standard Mannequin with its matching stock Skeleton.
Create and tune a separate GC2 Skeleton for a character with substantially
different proportions. The Networking Layer synchronizes the durable ragdoll
state and start/recover commands; individual bone poses remain local physics
simulation and are not streamed as a deterministic bone-pose snapshot.

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
- **UnitAnimimNetworkController** - States/Gestures animation command sync
- **UnitAnimimNetworkKinematic** - Optional server-authoritative locomotion animation parameters
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
