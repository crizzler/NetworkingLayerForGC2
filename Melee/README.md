# GC2 Melee Network Integration

Server-authoritative melee combat networking for Game Creator 2.

For melee hit presentation and the broader multiplayer prefab decision guide,
see the [online documentation](../Documentation/Online%20Documentation.md).

## Overview

This module provides network-aware melee combat for GC2, enabling server-authoritative hit validation with lag compensation. It integrates seamlessly with the base GC2 Network Integration and requires it as a dependency.

The optional Free Flow Combat adapter adds authority-owned PvE target, enemy,
counter-window, and late-join state without introducing a concrete Free Flow
dependency into this assembly. See
[Server-Authoritative Free Flow Combat](../Documentation/Server%20Authoritative%20Free%20Flow%20Combat.md)
for its supported scope and complete setup.

## Requirements

- Game Creator 2 Core
- Game Creator 2 Melee Module
- GC2 Network Integration (base module)
- Fusion or PurrNet integration, or a custom transport adapter updated for the
  current Melee and optional Free Flow payloads
- Game Creator 2 Melee `2.2.x` or `2.3.x`
- The required GC2 Melee source patch (`3.7.0-melee`)

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

## Optional Free Flow Combat Setup

Free Flow networking currently supports authenticated players fighting
server-authoritative NPCs. It does not authorize PvP Free Flow targeting or
combat-capable client-deterministic NPCs.

For each participating character:

1. Use an explicit actor classification: `PlayerOwned` for a human prefab, or
   `NPC` with `ServerAuthoritative` sync for an enemy.
2. Put `NetworkMeleeController` and `NetworkFreeFlowCombatAdapter` on the same
   Character root. On an enemy, keep one replica-safe On Start Trigger outside
   `NetworkCharacterAuthorityGate`; it must run on every admitted replica,
   resolve Self to that exact Character, and equip the registered canonical
   `FreeFlowMeleeWeapon`.
3. Add `NetworkNpcTargetSelector` to each enemy. Keep enemy Behavior processing
   and any additional, non-initialization GC2 AI Trigger roots authority-only.
4. Configure the existing Melee transport bridge and optional Melee-to-Stats
   damage bridge normally.
5. Set each directly executed targeted Skill to GC2 **Motion Warp** mode and
   retain its target-relative motion-warp track. The canonical Brawl fixture
   requires `FreeFlow_Brawl_Combo1`, `FreeFlow_Brawl_Combo2`, and
   `FreeFlow_Brawl_Combo3` to satisfy this contract; a serialized warp track is
   inactive while the Skill remains in ordinary Root Motion mode.

The replica-safe equip/bootstrap creates the receiver, target, enemy-agent,
Behavior `Processor`, indicator, and motion-warp candidate components wherever
the NPC is admitted. The local player runtime still runs only for the
authenticated owner, while the enemy agent, director, and `Processor` execute
only on server, dedicated-server, or Fusion Shared-master authority. Observer
replicas keep their bootstrap components present but disabled and receive
revisioned attackable/counterable/token, score, and selected-target state for
presentation and late joining.

PurrNet scene NPCs that have a `NetworkIdentity` defer Networking Layer role
registration until that identity is spawned. This ensures authority and every
observer register the same server-assigned character ID. The wait remains
fail-closed after its warning timeout; falling back to a peer-local scene hash
would make targeted Skill validation inconsistent across peers.

When PurrNet deactivates a pooled remote Character, presentation cleanup defers
restoring the authored Mannequin hierarchy until Unity's activation callback
stack has unwound. A persistent restore queue reparents the visual root before
destroying the empty temporary wrapper, avoiding the prohibited hierarchy write
and the missing-Animator cascade that follows if the pooled Mannequin is lost.
Focused semantic smoke diagnostics keep packet-wide logging gated off while
retaining the Skill/input evidence needed for authority tracing.

A held Free Flow movement lock may invoke GC2 `StopToDirection` every rendered
frame. The network motion controller still applies the stop locally on every
call, but sends only the first reliable `StopDirection` for an unchanged stopped
state. Priority/context transitions and the first nonzero direction after a
stop send immediately. The multi-process Free Flow smoke fails on any Core
`RateLimitExceeded` violation, so this traffic is fixed by coalescing rather
than by weakening the normal security budget.

Fusion non-authority peers timestamp semantic requests from
`LatestServerTick.Raw * DeltaTime`, the latest confirmed server timeline, rather
than their prediction-ahead `SimulationTime`. Host, dedicated-server, and
Shared-master authority retain the simulation clock; the existing future-time
security tolerance is unchanged.

A normal counter carries the authority-published window revision in its Melee
Skill request. Authority checks the assigned player, Skill allow-list, target,
range, line of sight, cooldown, and current revision before atomically consuming
the window once. Shield-forced local counters currently fail closed because
they do not yet carry a server-validated parry lease.

Free Flow changes the Melee Skill request and persistent snapshot wire shape.
Fusion and PurrNet peers must all use the same Networking Layer 2.3.0 version
and matching registered weapon/Skill assets.

Dedicated `GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples@1.0.0`
and `GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples@1.0.0`
installers provide a PlayerOwned prefab, a server-authoritative enemy prefab,
five persistent network enemies, Free Flow attack/counter inputs, and baked
NavMesh fixtures. See the complete
[server-authoritative Free Flow guide](../Documentation/Server%20Authoritative%20Free%20Flow%20Combat.md)
for exact scene paths, rebuild commands, limitations, and validation.

The current example archives have these SHA-256 values:

- Fusion: `0a9f98fd1578ff6fd5edbc1e579ab7e2f42ef81a9a1be1c8d7899dfea2ecb864`
- PurrNet: `34aa4f83f95316ed4f65b28fa914f164a3b052ca4d906b331e62c44ca064bdae`

The 2026-08-27 isolated Unity `6000.5.9f1` Free Flow category passed 56/56
tests with zero failed, inconclusive, or skipped tests across all nine required
suites. The retained XML is
`.unity-ci/editmode.qT3OMh/TestResults/editmode-results.xml`. The same run's
non-development player build completed with process exit `0` and
`Build Finished, Result: Success`. Its PurrNet
`VisualPresentation_PooledDeactivationDefersHierarchyMutation` test deactivates
the Character while its temporary presentation wrapper owns the Mannequin,
verifies cleanup is queued, then proves the Mannequin is restored and the
wrapper retired. The semantic-smoke regression primes an eligible target only
to activate the real Free Flow candidate path, snapshots the explicit-NPC set,
then adopts the actual first post-`Attack()` authority broadcast from the
authenticated actor to an attack-time or currently registered
server-authoritative NPC. Complete role, presentation, indicator, and admission
readiness is required before the invocation; the harness retains that ready
result afterward because entering `AttackRadius` may legitimately clear both
indicators. It rejects stale pre-invocation events, wrong actors, unknown
targets, and zero-Skill broadcasts, so an authored directional/fallback
selection may differ safely from the primed candidate.

Final paired PurrNet smoke also passed on both peers in both supported
topologies. Host + Client agreed on actor `9`, target `4`, and
`FreeFlow_Brawl_Kick` Skill hash `-342895613`, with two authenticated humans and
five explicit NPCs. The client retained five NPC presentations and ran zero
enemy Processors; the Host enabled and iterated all five. An indicator path was
active, and the client recorded `7.428 m` of actor displacement and `4.568 m` of
relative approach progress. Results are under
`.unity-ci/network-smoke.qoKLXZ/NetworkSmoke/purrnet-freeflow-host-client/Results`.

Dedicated Server + Client agreed on actor `7`, target `2`, and the same Skill
hash, with one authenticated human and five explicit NPCs. The client retained
five presentations, ran zero enemy Processors, displayed its fallback indicator,
and recorded `2.454 m` of actor displacement plus `3.172 m` of relative approach
progress; the server enabled and iterated all five authority Processors. Results
are under
`.unity-ci/network-smoke.rtNdH4/NetworkSmoke/purrnet-freeflow-dedicated-client/Results`.
Both PurrNet topologies recorded zero validated hits and neither guarded teardown
error pattern.

Final paired Fusion smoke also passed in Host + Client and Shared two-peer modes.
Host + Client agreed on actor `1033`, target `1026`, and Skill `-342895613`, with
two authenticated humans and `5/5` spawned/admitted explicit NPCs. Authority
alone enabled and iterated all five NPC Processors; the Client recorded `4.213 m`
of actor motion and `4.956 m` of relative progress. Results are under
`.unity-ci/network-smoke.sjtTkp/NetworkSmoke/fusion-freeflow-host-client/Results`.

Shared master and observer agreed on actor `525319`, target `525313`, and the
same Skill, again with two authenticated humans, `5/5` spawned/admitted NPCs,
and five versus zero enabled/iterated Processors. The observer recorded
`4.432 m` of actor motion and `4.441 m` of relative progress. Results are under
`.unity-ci/network-smoke.Zl04uJ/NetworkSmoke/fusion-freeflow-shared-two-peer/Results`.
Both Fusion topologies recorded zero validated hits and no Core rate-limit,
future-timestamp, Mannequin-parenting, or missing-Animator violation. The current
live smokes prove semantic delivery, authority-role separation, admission,
presentation, and locally observed approach motion—not Melee hits or damage,
counters, late joining, disconnect recovery, cross-peer Motion Warp convergence,
or Shared-master departure/migration.

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

### Patch retry still reports an older matcher error
1. Resolve every Unity script compilation error first. The patch menu blocks
   source changes while Unity is compiling or reports a failed compilation.
2. Close and reopen Unity after resolving the errors, then wait for a successful
   script compilation and domain reload.
3. Confirm that the patch confirmation title and body show `3.7.0-melee` before
   applying the patch.

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
