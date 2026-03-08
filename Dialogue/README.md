# Network Dialogue Module (GC2 Dialogue)

This module adds server-authoritative networking for **Game Creator 2 Dialogue** (`Dialogue`, `Story`, choices).

Compile symbol: `GC2_DIALOGUE` (auto-enabled when `com.gamecreator.dialogue` is present).

## Authority Modes

- Coop and most production flows: interception mode is usually enough, no source patch required.
- Strict competitive-grade authority: apply the optional Dialogue patcher so direct `Play/Stop/Continue/Choose` calls are validated before local execution.

Patch menu:

- `Game Creator > Networking Layer > Patches > Dialogue > Patch (Server Authority)`

## Components

- `NetworkDialogueManager`
- `NetworkDialogueController` (one per networked dialogue endpoint)

`NetworkDialogueController` needs:

- a `Dialogue` reference
- an owning `NetworkCharacter` reference (used for `NetworkId` ownership/security)

These can be on different GameObjects.  
If they are not on the same object, assign both references in the controller inspector.
Multiple `Dialogue` components in scene are supported by adding one controller per dialogue you want networked.

## Transport Wiring

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

## Interception Fallback

`NetworkDialogueController` listens to local Dialogue events on owner clients and forwards them as network requests.
This lets existing GC2 Dialogue instructions/UI continue to work without immediate graph rewrites.

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
