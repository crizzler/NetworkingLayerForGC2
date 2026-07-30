# GC2 Inventory Networking

This module adds server-authoritative synchronization for Game Creator 2 Inventory. It supports
player and world Bags, complete runtime Item properties and sockets, native Grid/List operations,
merchant and crafting transactions, scene pickups, runtime PurrNet pickups, and late join.

Inventory protocol and snapshot layouts changed with the 3.0 repair. The server and every client
must use the same Networking Layer package version.

## Required setup

1. Open `Game Creator > Networking Layer > Patches > Inventory > Patch (Server Authority)`.
2. Confirm the status is **Patched**, **Backups Available**, and `3.0.0-inventory`.

   The v3 networking assembly consumes an interception ABI injected by this patch. Applying the
   patch defines `GC2_NETWORK_INVENTORY_PATCHED`; unpatching removes that symbol and conditionally
   excludes Inventory networking, its PurrNet bridge, editor tools, and tests before Unity reloads.
   This lets pristine GC2 Inventory compile and work offline without shipping modified third-party
   source. Reapply the patch before opening or building a networked Inventory scene.

3. Add one `NetworkInventoryManager` and one transport bridge to the session root. The PurrNet
   Scene Setup Wizard creates and connects both.
4. Add one `NetworkInventoryController` beside each networked `Bag` and `NetworkCharacter`.
   The PurrNet wizard adds it to the selected player prefab.
5. For PurrNet, keep all Inventory channels on `ReliableOrdered`.

The patch installs semantic interception points in GC2 Inventory 2.8.x. Native Add, Remove,
Move/stack, Split, transfer, Use, Drop, Wealth, merchant, crafting, dismantling, combine, and the
`Inventory > Bags > Add Item` instruction then enter the authoritative request flow. Do not ship a
networked Inventory scene with an old or missing patch.

## Authority rules

| Origin | Result |
|---|---|
| Offline/unmanaged Bag | GC2 runs normally |
| Server or host | Operation runs locally once and broadcasts the confirmed revision |
| Owning client | One semantic request is sent; local mutation waits for server confirmation |
| Remote proxy | Mutation is rejected |

Composite operations run inside a nestable authoritative-apply scope. For example, a Move that
internally removes and adds items still produces one network operation, not three requests.

## Add Item, rewards, and client requests

An `InstructionInventoryAddItem` on an owning client now waits for its authoritative response. The
next instruction runs only after the confirmed revision has been applied locally. If the server
rejects the grant, the instruction fails and later instructions do not run. This prevents a pickup's
`Destroy Self` instruction from running after a rejected claim.

Generic client-created items are denied by default. This is intentional: a client must not be able
to grant itself any registered Item simply by running Add Item. Choose one of these patterns:

- **Server-authored reward:** validate quest completion, combat reward, or an admin action on the
  server and call `TryServerGrantItem`.
- **Runtime Item reward:** construct or load the complete runtime payload on the server and call
  `TryServerGrantRuntimeItem`. Client-supplied runtime payloads are never trusted.
- **Validated client request:** set `NetworkInventoryManager.CustomAddValidator` and validate the
  request's actor, `Source`, `SourceHash`, range, prerequisite, cost, and one-time state.
- **Trusted co-op compatibility:** enable `AllowUnvalidatedOwnedClientAdds` only when clients are
  intentionally trusted. The wizard displays a security warning while it is enabled.

```csharp
NetworkInventoryManager.Instance.CustomAddValidator = (request, senderClientId) =>
{
    bool validQuestReward =
        request.Source == InventoryModificationSource.Quest &&
        QuestService.ServerCanClaim(senderClientId, request.SourceHash);

    return validQuestReward
        ? (true, InventoryRejectionReason.None)
        : (false, InventoryRejectionReason.NotAuthorized);
};
```

Trusted server code can grant an asset directly:

```csharp
bool granted = controller.TryServerGrantItem(
    rewardItem,
    TBagContent.INVALID,
    allowStack: true,
    InventoryModificationSource.Quest,
    questIdHash);
```

For code that genuinely starts on the owning client, await the response instead of predicting the
mutation:

```csharp
NetworkContentAddResponse response = await controller.RequestAddItemAsync(
    item,
    TBagContent.INVALID,
    allowStack: true,
    InventoryModificationSource.Quest,
    questIdHash);

if (!response.Authorized)
{
    Debug.LogWarning($"Inventory request rejected: {response.RejectionReason}");
}
```

## Native bag organization

The normal GC2 Inventory UI remains the supported interface. Dragging between cells, merging
stacks, splitting a stack, transferring between Bags, using an Item, dropping it, buying/selling,
crafting, dismantling, and combining are intercepted at their native entry points. An owning
client does not need a separate visual-scripting instruction for each operation.

The server validates ownership and operation-specific state, commits atomically, and returns a
versioned result. Project-specific rules belong in `CustomRemoveValidator`,
`CustomMerchantValidator`, and `CustomCraftingValidator`.

```csharp
NetworkInventoryManager.Instance.CustomMerchantValidator = (request, senderClientId) =>
{
    return MerchantRules.ServerValidate(senderClientId, request)
        ? (true, InventoryRejectionReason.None)
        : (false, InventoryRejectionReason.NotAuthorized);
};
```

## Static scene pickups

Add `NetworkInventoryPickupSource` to the root of a scene pickup and configure:

- a stable, unique Pickup ID;
- the fixed Item asset resolved by the server;
- maximum interaction distance;
- optional line-of-sight validation;
- whether renderers and colliders are hidden after consumption.

The server reserves the pickup atomically, validates ownership/range/capacity/availability, grants
the Item once, then broadcasts consumed state. Two clients racing for one pickup produce one
winner, and a late joiner receives the consumed-state snapshot.

The PurrNet wizard converts stock `_Template_Pickup_Item` scene instances when their Add Item
instruction resolves to a fixed Item. You can also run:

`Game Creator > Networking Layer > Inventory > Convert Stock Scene Pickups`

Then run **Validate Open Scenes** from the same menu. Duplicate IDs and unresolved Items are
release-blocking errors. An automatically derived hierarchy ID is safe only while every peer loads
an identical scene hierarchy; serialized IDs are preferred.

## Runtime PurrNet pickups

An interactive pickup created at runtime must be a registered server-spawned PurrNet prefab. Add:

- `NetworkIdentity`;
- `NetworkInventoryPickupSource`;
- `PurrNetInventoryRuntimePickupIdentityAdapter`.

Assign the adapter as the pickup source's Runtime Identity. The bridge maps the spawned identity to
the Inventory request, and a successful server claim despawns that identity for every peer. Do not
use the scene-path ID of an arbitrary runtime `GameObject`.

The existing dropped-runtime-item path is also server validated and range checked.

## Stable synchronization and UI behavior

Every Bag has a monotonically increasing `StateVersion`. Full snapshots remain enabled as recovery,
but reconciliation is structural rather than destructive:

- identical state raises no Add/Remove events;
- moved stacks reuse their existing `RuntimeItem` objects;
- property and socket changes update objects in place;
- only genuinely added, removed, or irreconcilable entries are rebuilt;
- stale/duplicate revisions are ignored and gaps request a targeted resync.

This preserves Inventory UI selection and the selected `RuntimeItem` reference, so periodic sync
does not flicker an Item or clear its description panel. There is no session-profile interval that
needs to be reduced to hide a rebuild.

Snapshots contain the complete ordered stack, runtime IDs, runtime properties, sockets, equipment,
and wealth. Persistent messages received before a controller registers are bounded and replayed in
version order; a newer snapshot replaces obsolete queued state.

## Diagnostics and security

Enable Inventory network diagnostics on the manager/bridge only while troubleshooting. Normal
pickup success traces are gated. Missing routes, duplicate pickup IDs, protocol mismatches, and
security failures remain rate-limited warnings.

Recommended release checks:

- unvalidated client Add is rejected without appearing and disappearing;
- a validated Add and a trusted server grant persist exactly once;
- drag/stack/Split/transfer persists for host and connected clients;
- selection survives periodic full snapshots;
- distinct runtime properties and sockets remain distinct;
- two clients racing for a pickup produce one winner;
- merchant/crafting/combine and late join restore the same revision;
- all peers use the same package version.

## Dependencies

- Game Creator 2 Core
- Game Creator 2 Inventory 2.8.x
- a Networking Layer transport integration, such as PurrNet
- `GC2_INVENTORY` define (provided by the Inventory assembly version define)
