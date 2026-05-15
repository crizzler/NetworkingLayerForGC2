# Network Dialogue

The Dialogue module syncs selected Game Creator 2 `Dialogue` endpoints through
server-authoritative requests, responses, broadcasts, and snapshots.

It does not use a quest-style profile asset. Dialogue authority is configured on
each `NetworkDialogueController`, because dialogue endpoints are often scene
objects, NPCs, or player-owned actors rather than shared quest assets.

## Goals

- Keep normal GC2 Dialogue authoring and UI workflows.
- Add networking only to Dialogue endpoints that have a `NetworkDialogueController`.
- Support player-owned, server-owned NPC, and global scene dialogue targets.
- Support shared lockstep dialogue for party conversations and cutscenes.
- Allow custom project validation for party leader, distance, faction, quest
  prerequisites, or interaction rules.
- Avoid editing GC2 Dialogue source code for normal operation.

## Components

Add these to the session/network infrastructure:

- `NetworkDialogueManager`
- `PurrNetDialogueTransportBridge` when using PurrNet

Add this to each Dialogue endpoint that should sync:

- GC2 `Dialogue`
- `NetworkDialogueController`

Optional, depending on authority model:

- `NetworkCharacter` for player-owned dialogue endpoints
- a stable manual id or automatic scene id for NPC/global scene endpoints

Most projects should not add `NetworkDialogueController` to the Player Prefab by
default. Dialogue endpoints usually live on NPCs, scene objects, interactables,
or cutscene controllers. The PurrNet wizard creates the manager and bridge; you
add controllers to the actual Dialogue endpoints.

## Authority Modes

`PlayerOwned` means the Dialogue target belongs to a player-owned
`NetworkCharacter`. Requests validate both the requesting actor and target
ownership.

Use this for player-attached dialogue, player-specific conversation widgets, or
networked actors whose dialogue authority should follow character ownership.

`ServerOwnedNpc` means the Dialogue target is treated as server-owned while the
requesting actor is still validated.

Use this for NPCs, shops, quest givers, and interactable characters.

`GlobalScene` means the Dialogue target uses a stable scene id and is treated as
a shared scene endpoint.

Use this for cutscenes, shared party conversations, demo/test dialogue, or any
scene object that is not owned by a specific player.

For `ServerOwnedNpc` and `GlobalScene`, leave `Network Character` empty on the
controller unless the endpoint is also represented by a networked character. Use
automatic ids for simple scenes, or assign a manual id / stable salt when the
same endpoint must resolve identically across builds and loaded scenes.

## Network Instructions

Use the network-specific Dialogue instructions inside GC2 Instruction Lists:

- `Network Play Dialogue`
- `Network Stop Dialogue`
- `Network Continue Dialogue`
- `Network Dialogue Choice Index`

Each instruction exposes:

- `Dialogue`: the GameObject with `Dialogue` and `NetworkDialogueController`
- `Actor`: the player or networked actor requesting the action

The Actor must resolve to a valid `NetworkCharacter.NetworkId` so the server can
validate ownership and request context. The Dialogue target can be an NPC/scene
object and does not have to be the same object as the Actor.

`Network Dialogue Choice Index` uses a one-based visible choice index. For
example, index `1` means the first currently visible choice.

## PurrNet Demo Scene

`Game Creator > Networking Layer > PurrNet Transport > Demo Scenes > Dialogue`
creates a dialogue-ready setup when the GC2 Dialogue package and default skin are
installed:

- a `Network Dialogue Manager`
- a `PurrNet Dialogue Bridge`
- a scene object named `Scene Network Dialogue NPC`
- a GC2 `Dialogue` component on that scene object
- a `NetworkDialogueController` using `GlobalScene` authority
- visual scripting demo objects that run network Dialogue instructions
- `T` starts the network Dialogue
- `Y` continues/skips the shared Dialogue
- `U` selects visible choice 1
- `I` selects visible choice 2

This demo validates the full request, server validation, broadcast, and remote
application path. If the generated scene syncs correctly, compare custom scene
setup against its manager, bridge, controller authority mode, stable scene id,
and Instruction List targets.

## Pacing And Skipping

The default networked Dialogue behavior is shared lockstep:

- `Network Play Dialogue` starts the same Dialogue for all relevant peers.
- `Network Continue Dialogue` advances that Dialogue for everyone.
- `Network Dialogue Choice Index` selects the same branch for everyone.
- `Network Stop Dialogue` stops the shared Dialogue for everyone.

This is the right model for shared cutscenes, party conversations, co-op story
moments, and Baldur's Gate-style group dialogue.

It also means one player can advance or choose for everyone. For MMO-style
dialogue where each player reads and skips independently, avoid syncing every UI
line advance. Use one of these patterns instead:

- network only the gameplay consequence, then let each client run local Dialogue
  UI at its own pace
- leader-driven dialogue, where only the host/party leader can send Continue or
  Choice requests
- all-ready dialogue, where clients mark themselves ready and the server
  advances after all relevant clients are ready or after a timeout

Use `NetworkDialogueManager.CustomDialogueValidator` for leader, party,
distance, faction, or prerequisite checks.

## Interception And Patch Mode

Normal operation uses interception/fallback mode. `NetworkDialogueController`
listens to local Dialogue events and forwards them as network requests when
possible, so existing GC2 Dialogue flows can be migrated gradually.

For strict authority, apply the optional Dialogue patcher:

`Game Creator > Networking Layer > Patches > Dialogue > Patch (Server Authority)`

Patch mode validates direct GC2 Dialogue `Play`, `Stop`, `Continue`, and
`Choose` calls before local execution. Use it when you need stronger protection
against bypassing the network instructions.

## Side Effects

Dialogue is presentation-heavy. Applying a network Dialogue action can run local
Dialogue UI, choice UI, visited-state updates, and Dialogue event side effects on
the receiving peer.

Avoid running a default local GC2 Dialogue instruction and a network Dialogue
instruction for the same interaction in the same Instruction List. Prefer the
network instruction for synchronized conversations. Use local-only GC2 Dialogue
instructions only for conversations that should not sync.

`Optimistic Updates` on `NetworkDialogueController` lets the local owner apply a
Dialogue action before server confirmation. Leave it disabled for strict shared
dialogue, or enable it only when the project can tolerate rollback/rejection
behavior.

## Late Join

`NetworkDialogueController` can capture full snapshots containing:

- current Dialogue identity
- playing/visited state
- current node id
- visited node ids
- visited tag ids

`PurrNetDialogueTransportBridge` sends snapshots to clients when they load into
the scene, and the manager can broadcast periodic full snapshots based on the
controller's sync interval.

This is runtime session repair, not long-term persistence. If a campaign needs
Dialogue state to survive server restarts or save/load, persist the authoritative
project state separately and restore the relevant Dialogue controllers when the
session starts.

## Transport Wiring

PurrNet users normally let the wizard create:

- `NetworkDialogueManager`
- `PurrNetDialogueTransportBridge`

The PurrNet bridge wires manager delegates:

- `OnSendDialogueRequest`
- `OnSendDialogueResponse`
- `OnBroadcastDialogueChange`
- `OnBroadcastFullSnapshot`
- `OnSendSnapshotToClient`

It routes inbound packets to:

- `ReceiveDialogueRequest(request, rawSenderClientId)` on server
- `ReceiveDialogueResponse(response, targetNetworkId)` on clients
- `ReceiveDialogueChangeBroadcast(broadcast)` on clients
- `ReceiveFullSnapshot(snapshot)` on clients

## Security

Dialogue requests are validated by:

- `SecurityIntegration.ValidateModuleRequest(...)`
- target ownership when the controller uses `PlayerOwned`
- `NetworkDialogueManager.CustomDialogueValidator`
- pending request limits per player
- Dialogue identity matching
- valid action/state checks

Use `CustomDialogueValidator` for project rules such as:

- only the party leader can continue or choose
- player must be near the NPC
- player must be in the same party or faction
- quest prerequisites must be met
- a conversation can only be started once
- combat state blocks dialogue

## Recommended Patterns

For shared co-op conversations:

1. Put `NetworkDialogueController` on the NPC/scene Dialogue object.
2. Set authority to `ServerOwnedNpc` or `GlobalScene`.
3. Use `Network Play Dialogue`, `Network Continue Dialogue`, and
   `Network Dialogue Choice Index`.
4. Restrict Continue/Choice in `CustomDialogueValidator` if only one player
   should drive the conversation.

For player-private NPC conversations:

1. Network only the gameplay consequence, such as quest accept, shop purchase,
   reward, or reputation change.
2. Let each client run local Dialogue UI at its own pace.
3. Use network instructions only for the consequence that must be authoritative.

For player-owned dialogue endpoints:

1. Place `NetworkDialogueController` near the owned `NetworkCharacter`.
2. Use `PlayerOwned` authority.
3. Ensure the actor and target ownership mappings are registered before
   requests are sent.

## Debugging

Enable logging on:

- `NetworkDialogueManager`
- `NetworkDialogueController`
- `PurrNetDialogueTransportBridge`

Useful symptoms:

- Rejected request: check actor `NetworkCharacter.NetworkId`, ownership,
  pending request count, and `CustomDialogueValidator`.
- Dialogue starts locally but not remotely: use the network Dialogue instruction
  or enable/verify interception on the `NetworkDialogueController`.
- Choice request is rejected: confirm the current node is a choice node and the
  visible choice index is valid.
- NPC/scene Dialogue does not register: check authority mode, stable id/manual
  id, and that the PurrNet Dialogue bridge auto-registers scene controllers.
- One player skipping advances everyone: this is expected in shared lockstep
  mode. Use a per-player pacing pattern for MMO-style conversations.

