# Network Traversal Module (GC2 Traversal)

This module adds server-authoritative networking for **Game Creator 2 Traversal** (`TraverseLink`, `TraverseInteractive`, `TraversalStance`).

Compile symbol: `GC2_TRAVERSAL` (auto-enabled when `com.gamecreator.traversal` is present).

Supported source line: Game Creator 2 Traversal `2.0.x`.

## Required Authority Patch

The Traversal source patch is mandatory for networked Characters. The transport
fails closed when its hooks are absent so a missing controller or route cannot
silently fall through to local-only GC2 traversal.

Patch menu:

- `Game Creator > Networking Layer > Patches > Traversal > Patch (Server Authority)`

The PurrNet setup wizard applies and validates the compatible patch. It also
reports an actionable error after a GC2 Traversal update changes the expected
source signatures. Characters without `NetworkCharacter` remain ordinary local
GC2 Characters and do not require a network route. Patch `2.6.0-traversal`
also rejects superseded starts after an async GC2 yield, routes authored
`ContinueA`/`ContinueB` edges before native transitions, and supplies the
presentation-safe local-owner snapshot motion loop.

Updating or reinstalling GC2 Traversal can overwrite these hooks. The Networking Layer remains
compilable so the editor patcher can repair the module, while networked Traversal remains disabled
until the complete patch passes validation.

## Components

- `NetworkTraversalManager`
- `NetworkTraversalController` (requires `Character` + `NetworkCharacter`)
- `PurrNetTraversalTransportBridge` when using PurrNet

## PurrNet Scene Setup Wizard

For PurrNet projects, enable **Traversal** on the PurrNet wizard Modules page. The wizard creates/reuses `NetworkTraversalManager` and `PurrNetTraversalTransportBridge`.

Traversal support is capability-based. **Built-in** remains the stable fallback.
The optional **PurrDiction Native (Experimental)** backend can be selected only
when its compiled movement adapter implements both
`INetworkOwnerMotionAuthority` and `INetworkServerOwnerMotionAuthority`. The
wizard and runtime route fail closed when either capability is absent so
reconciliation cannot overwrite traversal-driven poses.

When a Player Prefab is assigned on the Scene page and prefab preparation is enabled, selecting Traversal adds `NetworkTraversalController` to that prefab.

## Transport Wiring

Wire the manager delegates to your transport layer:

- `OnSendTraversalRequest`
- `OnSendTraversalResponse`
- `OnBroadcastTraversalChange`
- `OnBroadcastFullSnapshot`
- `OnSendSnapshotToClient`
- `OnResolveRequestRouteStatusForActor` (validate the exact requesting NetworkId)

The older parameterless `OnResolveRequestRouteStatus` delegate remains as a
one-cycle compatibility fallback for custom transports. New transports should
always use the actor-aware delegate; traversal input is transient and is never
held for a later controller scan.

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

## Ordering, Start Acknowledgement, and Snapshots

Every accepted server state carries a monotonic `StateVersion`. Older responses,
broadcasts, and snapshots are ignored, and state received before controller
registration is retained in a bounded readiness cache. Resolution failures do
not consume the version: the latest persistent snapshot is retried, while
unresolved transient starts expire after two seconds. A server start is not
accepted merely because an async GC2 method returned: its matching motion-enter
event must arrive within one second. Otherwise the request is rejected with
`StartTimeout`, the late start is cancelled, and a snapshot reconciles clients.

`TraverseLink` motion is transient and is never replayed to a late joiner. An
active `TraverseInteractive` is persistent: the targeted snapshot restores its
stable identity and relative pose through the patched presentation-only stance
entry point. That path does not invoke Traverse enter/exit instructions or
Motion start/finish instruction lists. Remote proxies keep a presentation shell;
the local owner resumes the interactive movement loop without replaying those
gameplay lists. The server accepts owner-driven traversal poses only during the
correlated traversal window and closes that window on exit or timeout.

## Initialization

1. Set manager server role:
   - `NetworkTraversalManager.Instance.IsServer = isServerSession;`
2. Initialize each controller role:
   - `controller.Initialize(isServer, isLocalClient);`

The controller auto-registers itself with `NetworkTraversalManager` when it has a valid `NetworkCharacter.NetworkId`.

All peers must use the same Networking Layer version. `StateVersion`, snapshot
kind, and relative-pose fields changed the Traversal wire layout and are not
mixed-version compatible.

## Security

Server request processing uses:

- `SecurityIntegration.ValidateModuleRequest(...)`
- `SecurityIntegration.ValidateTargetEntityOwnership(...)`

with strict ownership + protocol correlation checks.
