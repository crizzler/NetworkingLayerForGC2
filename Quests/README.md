# Network Quests Module (GC2 Quests)

This module adds server-authoritative networking for **Game Creator 2 Quests** (`Journal`).

Compile symbol: `GC2_QUESTS` (auto-enabled when `com.gamecreator.quests` is present).

## Authority Modes

- Coop and most production flows: interception mode is usually enough, no source patch required.
- Strict competitive-grade authority: apply the optional Quests patcher so direct `Journal` mutations are validated before local execution.

Patch menu:

- `Game Creator > Networking Layer > Patches > Quests > Patch (Server Authority)`

## Components

- `NetworkQuestsManager`
- `NetworkQuestsController` (requires `Journal`)

## Transport Wiring

Wire the manager delegates to your transport layer:

- `OnSendQuestRequest`
- `OnSendQuestResponse`
- `OnBroadcastQuestChange`
- `OnBroadcastFullSnapshot`
- `OnSendSnapshotToClient`

Then route inbound packets to:

- `ReceiveQuestRequest(request, rawSenderClientId)` on server
- `ReceiveQuestResponse(response, targetNetworkId)` on clients
- `ReceiveQuestChangeBroadcast(broadcast)` on clients
- `ReceiveFullSnapshot(snapshot)` on clients

## Supported Authoritative Actions

- Quest: activate, deactivate, track, untrack, untrack-all
- Task: activate, deactivate, complete, abandon, fail, set value

## Interception Fallback

`NetworkQuestsController` listens to local `Journal` events on owner clients and forwards them as network requests.
This lets existing GC2 Quest instructions work without immediate graph rewrites.

For strictest competitive behavior, combine `Request*` APIs with the Quests patcher.

## Initialization

1. Set manager server role:
   - `NetworkQuestsManager.Instance.IsServer = isServerSession;`
2. Initialize each controller role:
   - `controller.Initialize(isServer, isLocalClient);`

The controller auto-registers itself with `NetworkQuestsManager` when it has a valid `NetworkCharacter.NetworkId`.

## Security

Server request processing uses:

- `SecurityIntegration.ValidateModuleRequest(...)`
- `SecurityIntegration.ValidateTargetEntityOwnership(...)`

with strict ownership + protocol correlation checks.
