# Network Dialogue Module (GC2 Dialogue)

This module adds server-authoritative networking for **Game Creator 2 Dialogue** (`Dialogue`, `Story`, choices).

Compile symbol: `GC2_DIALOGUE` (auto-enabled when `com.gamecreator.dialogue` is present).

Detailed setup guide:

- `Assets/Arawn/NetworkingLayerForGC2/Documentation/network-dialogue.md`

## Authority Modes

- Coop and most production flows: interception mode is usually enough, no source patch required.
- Strict competitive-grade authority: apply the optional Dialogue patcher so direct `Play/Stop/Continue/Choose` calls are validated before local execution.

Patch menu:

- `Game Creator > Networking Layer > Patches > Dialogue > Patch (Server Authority)`

## Components

- `NetworkDialogueManager`
- `NetworkDialogueController` (one per networked dialogue endpoint)

## PurrNet Scene Setup Wizard

For PurrNet projects, enable **Dialogue** on the PurrNet wizard Modules page. The wizard creates/reuses `NetworkDialogueManager` and `PurrNetDialogueTransportBridge`.

`NetworkDialogueController` is usually not added to the Player Prefab because most GC2 dialogue endpoints live on NPCs or scene objects. Add one controller to each networked dialogue endpoint you want synchronized, or use the PurrNet demo scene creator for a generated test NPC.

`NetworkDialogueController` needs:

- a `Dialogue` reference
- a target identity, resolved either from an owning `NetworkCharacter`, a stable scene/NPC id, or a manual id

These can be on different GameObjects.
If they are not on the same object, assign the references in the controller inspector.
Multiple `Dialogue` components in scene are supported by adding one controller per dialogue you want networked.

## Dialogue Targets

Most GC2 projects put `Dialogue` components on NPCs or scene objects, not on player prefabs. The networking layer supports that directly:

- `PlayerOwned`: use this for dialogue endpoints that belong to a player-owned `NetworkCharacter`. Requests validate both the requesting actor and the dialogue target ownership.
- `ServerOwnedNpc`: use this for NPC dialogue endpoints. Requests validate the requesting player actor, while the dialogue target is treated as server-owned.
- `GlobalScene`: use this for scene-wide dialogue endpoints and demo/test objects. Requests validate the requesting player actor, while the dialogue target uses a stable scene id.

For NPC/scene dialogue, leave `Network Character` empty, set the authority mode to `ServerOwnedNpc` or `GlobalScene`, and keep automatic ids enabled unless you need a hand-authored stable id. Network instructions expose both a `Dialogue` target and an `Actor` field: set `Dialogue` to the NPC/scene object and `Actor` to the player initiating the conversation.

The PurrNet demo scene creator builds a `Scene Network Dialogue NPC` object with a `Dialogue` component and `NetworkDialogueController`. The generated T/Y/U/I test instructions target that scene object and use the local player as the requesting actor.

## Transport Wiring

PurrNet users can add manually, or let the PurrNet wizard create:

- `PurrNetDialogueTransportBridge`

The PurrNet wizard and demo scene creator add this bridge automatically when the Dialogue transport assembly is available.

Wire the manager delegates to your transport layer:

- `OnSendDialogueRequest`
- `OnSendDialogueResponse`
- `OnBroadcastDialogueChange`
- `OnBroadcastFullSnapshot`
- `OnSendSnapshotToClient`

Then route inbound packets to:

- `ReceiveDialogueRequest(request, rawSenderClientId)` on server
- `ReceiveDialogueResponse(response, targetNetworkId)` on clients
- `ReceiveDialogueChangeBroadcast(broadcast)` on clients
- `ReceiveFullSnapshot(snapshot)` on clients

## Supported Authoritative Actions

- `Play`
- `Stop`
- `Continue`
- `Choose`

## Pacing and Skipping

The default networked Dialogue behavior is shared lockstep:

- a network `Play` starts the same Dialogue for every relevant peer
- a network `Continue` advances that Dialogue for every peer
- a network `Choice` selects the same branch for every peer

This is the correct mode for shared cutscenes, party conversations, and Baldur's Gate-style group dialogue. It also means one player skipping text advances the shared dialogue for everyone.

For MMO-style dialogue where one player can skip while another keeps listening, do not network every line advance. Use one of these patterns:

- network only the authoritative game consequences, then let each client run the GC2 Dialogue UI locally at their own pace
- leader-driven dialogue, where only the host/party leader can send `Continue` and `Choice`
- all-ready dialogue, where each client can mark their local UI ready and the server advances only after all relevant clients are ready or a timeout expires

The PurrNet demo scene uses shared lockstep because it is the clearest way to verify transport synchronization. Production projects can layer custom validators, custom instructions, or project-specific pacing state on top to choose a different policy per conversation.

## Network Instructions

Use the network Dialogue instructions when you want Instruction List level authority without modifying Game Creator source:

- `Network Play Dialogue`
- `Network Stop Dialogue`
- `Network Continue Dialogue`
- `Network Dialogue Choice Index`

These instructions call `NetworkDialogueController` request APIs and let the server broadcast the accepted state to all clients. Default GC2 Play/Stop can also be picked up by controller event interception, but Continue and Choice are safest when routed through the network instructions or a strict patched integration.

## Interception Fallback

`NetworkDialogueController` listens to local Dialogue events on owner clients and forwards them as network requests.
This lets existing GC2 Dialogue instructions/UI continue to work without immediately rewriting every Instruction List.

For strictest competitive behavior, combine `Request*` APIs with the Dialogue patcher.

## Initialization

1. Set manager server role:
   - `NetworkDialogueManager.Instance.IsServer = isServerSession;`
2. Initialize each controller role:
   - `controller.Initialize(isServer, isLocalClient);`

The controller auto-registers itself with `NetworkDialogueManager` when it has a valid owner `NetworkCharacter.NetworkId`.

## Security

Server request processing uses:

- `SecurityIntegration.ValidateModuleRequest(...)`
- `SecurityIntegration.ValidateTargetEntityOwnership(...)`

with strict ownership + protocol correlation checks.
