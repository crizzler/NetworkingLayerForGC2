# Public API

This document defines the **public runtime integration contract** for wiring any networking stack into the Game Creator 2 Networking Layer.

Use this as the authoritative reference when implementing your transport adapter.

## Scope

This covers:

- Bridge contract (`NetworkTransportBridge` / `INetworkTransportBridge`)
- Manager/controller entry points you must route to
- Ownership/security requirements for authoritative sessions
- Protocol v2 request context/correlation requirements

This does not cover:

- Transport-specific SDK code (NGO/FishNet/Mirror/etc.)
- Per-module gameplay behavior details (see each module README)

PurrNet users normally do not need to implement this bridge contract from scratch. Use `Game Creator > Networking Layer > PurrNet Scene Setup Wizard` to create the included PurrNet bridge stack, managers, selected module bridges, player spawning helpers, and prefab preparation.

For the PurrNet-specific runtime/editor API surface, see
`Assets/Arawn/NetworkingLayerForGC2/Documentation/purrnet-public-api.md`.

## 1) Bridge Contract

Your transport adapter must derive from:

- `Arawn.GameCreator2.Networking.NetworkTransportBridge`

You must implement:

- `bool IsServer { get; }`
- `bool IsClient { get; }`
- `bool IsHost { get; }`
- `float ServerTime { get; }`
- `void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)`
- `void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime)`
- `void Broadcast(uint characterNetworkId, NetworkPositionState state, float serverTime, uint excludeClientId = uint.MaxValue, NetworkRecipientFilter relevanceFilter = null)`

You should use these helper APIs:

- `NetworkTransportBridge.TryConvertSenderClientId(ulong rawSenderClientId, out uint senderClientId)`
- `NetworkTransportBridge.IsValidClientId(uint clientId)`
- `SetCharacterOwner(uint characterNetworkId, uint ownerClientId)`
- `ClearCharacterOwner(uint characterNetworkId)`
- `TryVerifyActorOwnership(uint senderClientId, uint actorNetworkId, out uint ownerClientId)` (override for native transport ownership)

For input/state relay in your bridge implementation:

- `TryAcceptInputFromSender(uint senderClientId, uint characterNetworkId)`
- `RaiseInputReceivedServer(uint senderClientId, uint characterNetworkId, NetworkInputState[] inputs)`
- `RaiseStateReceivedClient(uint characterNetworkId, NetworkPositionState state, float serverTime)`

## 2) Session Initialization Contract

Initialize module managers only after transport role is known.

Required role initialization APIs:

- `NetworkCoreManager.Initialize(bool isServer, bool isClient)`
- `NetworkShooterManager.Initialize(bool isServer, bool isClient)`
- `NetworkMeleeManager.Initialize(bool isServer, bool isClient)`

State-based managers:

- `NetworkInventoryManager.IsServer = isServer`
- `NetworkStatsManager.IsServer = isServer`
- `NetworkQuestsManager.IsServer = isServer`
- `NetworkDialogueManager.IsServer = isServer`
- `NetworkTraversalManager.IsServer = isServer`

Abilities:

- `NetworkAbilitiesManager.Initialize(Func<float> getServerTime, Func<uint> getLocalPlayerNetworkId)`
- `NetworkAbilitiesController.InitializeAsServer()`
- `NetworkAbilitiesController.InitializeAsClient()`
- `NetworkAbilitiesController.InitializeAsHost()`

## 3) Inbound Routing Contract (Transport -> GC2 Layer)

When packets arrive, deserialize and call the matching `Receive*` / `Process*` method.

Sender ID requirements:

- Modules expecting `uint senderClientId`: convert once with `TryConvertSenderClientId(...)`.
- Modules expecting `ulong clientId`: pass raw transport ID.

Primary inbound APIs by module:

- `NetworkCoreManager.Receive*Request(uint senderClientId, ...)`
- `NetworkInventoryManager.Receive*Request(..., ulong clientId)`
- `NetworkStatsManager.Receive*Request(..., ulong clientId)`
- `NetworkShooterManager.Receive*Request(uint senderClientId, ...)`
- `NetworkMeleeManager.Receive*Request(uint senderClientId, ...)`
- `NetworkQuestsManager.ReceiveQuestRequest(..., ulong clientId)`
- `NetworkDialogueManager.ReceiveDialogueRequest(..., ulong clientId)`
- `NetworkTraversalManager.ReceiveTraversalRequest(..., ulong clientId)`
- `NetworkAbilitiesController.Process*Request(uint senderClientId, ...)`

Client-side receives:

- Use each module manager/controller `Receive*Response(...)`, `Receive*Broadcast(...)`, and snapshot/delta receives where available.

## 4) Outbound Wiring Contract (GC2 Layer -> Transport)

Wire manager/controller delegates to transport send operations.

Patterns:

- `Send*RequestToServer` -> client-to-server send
- `Send*ResponseToClient` / `OnSend*Response` -> server-to-one-client send
- `Broadcast*` / `OnBroadcast*` -> server-to-many send
- `OnSendSnapshotToClient` -> targeted reliable snapshot send

Examples of delegate hosts:

- `NetworkCoreManager` (`Send*RequestToServer`, `Send*ResponseToClient`, `Broadcast*`)
- `NetworkInventoryManager` (`OnSend*Request`, `OnSend*Response`, `OnBroadcast*`, `OnSendSnapshotToClient`)
- `NetworkStatsManager` (same request/response/broadcast/snapshot pattern)
- `NetworkShooterManager` and `NetworkMeleeManager` (request/response/broadcast delegates)
- `NetworkQuestsManager`, `NetworkDialogueManager`, `NetworkTraversalManager` (request/response/broadcast/snapshot delegates)
- `NetworkAbilitiesController` (`Send*ToServer`, `Send*ToClient`, `Broadcast*ToClients`)

## 5) Ownership and Security Contract

Authoritative mode is strict:

- Ownership must be registered before first gameplay request.
- Requests fail if owner cannot be resolved or sender is not owner.

Required ownership lifecycle:

1. On spawn/ownership assignment (server):
   - `SetCharacterOwner(characterNetworkId, ownerClientId)`
2. On despawn/ownership loss:
   - `ClearCharacterOwner(characterNetworkId)`

For non-character entities (bags, module-owned runtime entities), register with:

- `SecurityIntegration.RegisterEntityActor(entityNetworkId, actorNetworkId)`
- `SecurityIntegration.RegisterEntityOwner(entityNetworkId, ownerClientId)`

Server security bootstrap:

- Ensure one `NetworkSecurityManager` exists in authoritative scenes.
- Managers call `SecurityIntegration.SetModuleServerContext(...)` internally.
- Keep server manager initialization aligned with actual transport role.

## 6) Protocol v2 Request Context Contract

Gameplay requests are v2-context based:

- Include `ActorNetworkId`
- Include `CorrelationId`

Use:

- `NetworkRequestContext.Create(actorNetworkId, correlationId)`
- `NetworkCorrelation.Compose(actorNetworkId, localRequestIdOrCounter)`
- `NetworkCorrelation.Next(actorNetworkId, ref counter)`

Do not send zero context fields for gameplay requests.

## 7) Character Contract

For each `NetworkCharacter` instance:

1. Assign stable network identity for your session model.
2. Initialize role via:
   - `InitializeNetworkRole(bool isServer, bool isOwner, bool isHost)`
3. Keep bridge ownership map synchronized with actual transport ownership.

## 8) Menu Entry Contract (Editor UX)

Runtime setup and patch workflows are exposed under:

- `Game Creator > Networking Layer > PurrNet Scene Setup Wizard` when the PurrNet transport integration is installed
- `Game Creator > Networking Layer > Scene Setup Wizard` only when no transport-specific setup wizard is installed
- `Game Creator > Networking Layer > Patches`

Transport-specific setup presence is represented by the editor define `ARAWN_GC2_TRANSPORT_INTEGRATION`. It is managed automatically by `GC2NetworkingDefineSymbols` and hides the generic wizard menu to avoid duplicate setup paths.

## 9) Optional Patchers

Patchers are optional.

Recommended adoption policy:

- Start unpatched first (interception/fallback mode).
- Patch only when threat level justifies it (for example confirmed cheating, ranked PvP pressure, or economy abuse attempts).
- Apply patchers module-by-module, prioritizing combat/economy-critical modules.

Use:

- `Game Creator > Networking Layer > Patches > <Module> > Patch (Server Authority)`

Detailed rollout guidance:

- `Assets/Arawn/NetworkingLayerForGC2/Documentation/PATCHING_STRATEGY.md`

## 10) Recommended Integration Checklist

1. Implement `NetworkTransportBridge` subclass.
2. Initialize managers per role after transport startup.
3. Wire all outbound delegates to transport send paths.
4. Route all inbound message types to one correct `Receive*` / `Process*` API.
5. Register ownership immediately on server spawn.
6. Validate all server receives use authoritative sender IDs.
7. Test host + dedicated server + multiple clients including forged request scenarios.

## Related Docs

- `Assets/Arawn/NetworkingLayerForGC2/Documentation/TRANSPORT_QUICKSTART.md`
- `Assets/Arawn/NetworkingLayerForGC2/Documentation/purrnet-public-api.md`
- `Assets/Arawn/NetworkingLayerForGC2/Documentation/PATCHING_STRATEGY.md`
- `Assets/Arawn/NetworkingLayerForGC2/README.md`
- Module-specific docs under each module folder (`Core`, `Inventory`, `Stats`, `Shooter`, `Melee`, `Quests`, `Dialogue`, `Traversal`, `Abilities`)
