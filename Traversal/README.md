# Network Traversal Module (GC2 Traversal)

This module adds server-authoritative networking for **Game Creator 2 Traversal** (`TraverseLink`, `TraverseInteractive`, `TraversalStance`).

Compile symbol: `GC2_TRAVERSAL` (auto-enabled when `com.gamecreator.traversal` is present).

## Authority Modes

- Coop and most production flows: interception mode is usually enough, no source patch required.
- Strict competitive-grade authority: apply the optional Traversal patcher so direct traversal calls are validated before local execution.

Patch menu:

- `Game Creator > Networking Layer > Patches > Traversal > Patch (Server Authority)`

## Components

- `NetworkTraversalManager`
- `NetworkTraversalController` (requires `Character` + `NetworkCharacter`)

## Transport Wiring

Wire the manager delegates to your transport layer:

- `OnSendTraversalRequest`
- `OnSendTraversalResponse`
- `OnBroadcastTraversalChange`
- `OnBroadcastFullSnapshot`
- `OnSendSnapshotToClient`

Then route inbound packets to:

- `ReceiveTraversalRequest(request, rawSenderClientId)` on server
- `ReceiveTraversalResponse(response, targetNetworkId)` on clients
- `ReceiveTraversalChangeBroadcast(broadcast)` on clients
- `ReceiveFullSnapshot(snapshot)` on clients

## Supported Authoritative Actions

- `RunTraverseLink`
- `EnterTraverseInteractive`
- `TryCancel`
- `ForceCancel`
- `TryJump`
- `TryAction`
- `TryStateEnter`
- `TryStateExit`

## Interception Fallback

`NetworkTraversalController` listens to traversal stance enter/exit signals and forwards owner-originated traversal transitions as network requests.
This keeps common coop traversal flows working without immediate graph rewrites.

For strictest competitive behavior, combine explicit `Request*` APIs with the Traversal patcher.

## Initialization

1. Set manager server role:
   - `NetworkTraversalManager.Instance.IsServer = isServerSession;`
2. Initialize each controller role:
   - `controller.Initialize(isServer, isLocalClient);`

The controller auto-registers itself with `NetworkTraversalManager` when it has a valid `NetworkCharacter.NetworkId`.

## Security

Server request processing uses:

- `SecurityIntegration.ValidateModuleRequest(...)`
- `SecurityIntegration.ValidateTargetEntityOwnership(...)`

with strict ownership + protocol correlation checks.
