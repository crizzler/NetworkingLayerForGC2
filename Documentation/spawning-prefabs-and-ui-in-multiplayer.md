# Spawning Prefabs and UI in Multiplayer

This guide explains where a prefab should be created, what the server owns, and
what a late-joining client must reconstruct. The important distinction is not
whether something is visible on screen; it is whether it is local presentation,
persistent replicated state, or an interactive network object.

## Choose the replication model

| Object or effect | Who authorizes it? | How it appears on clients | Late-join behavior |
|---|---|---|---|
| Canvas, HUD, crosshair, menu | Local client | Instantiate or enable locally | Recreated by the local scene/UI flow |
| Muzzle flash, tracer, hit spark, melee impact | Server validates the combat event | Each observing client pools or instantiates the cosmetic locally | Do not replay old effects |
| Equipped weapon model or attached cosmetic prop | Server owns a small state descriptor | Each client resolves a registered asset and creates the visual locally | Restore from a targeted snapshot |
| Pickup, NPC, authoritative projectile, interactive runtime prop | Server | Spawn a registered PurrNet prefab with `NetworkIdentity` | PurrNet spawn state restores it |

Do not put `NetworkIdentity` on ordinary Canvas objects, tracers, muzzle flashes,
or impact particles. Networking the event or state is cheaper and prevents the
server from becoming responsible for short-lived presentation objects.

## Screen-space UI

Create screen-space UI only on the client that owns the screen. A dedicated
server has no screen and should not instantiate or update presentation UI.

Synchronize the data the UI displays—health, ammo, room members, objective
state—not the Canvas prefab itself. Remote players should not receive another
player's HUD hierarchy.

## Transient combat presentation

Shooter and Melee use a server-authoritative event with local playback:

1. The owner requests a shot or hit.
2. The server validates gameplay and applies damage/state once.
3. The server broadcasts the confirmed presentation data.
4. Each client resolves registered assets and plays the effect locally.

Optimistic local effects may run before confirmation. The confirmed broadcast
must then reconcile or suppress the predicted presentation so the owner does
not see it twice. Observers play the confirmed presentation exactly once.

Old transient events are not sent to a late joiner. A short pending queue may
hold a newly received event while its character controller finishes spawning,
but expired events are discarded.

## Core attached props

`NetworkCoreManager` owns replicated prop attachment descriptors. Add it once
to the session root; do not add `NetworkCoreController` to player prefabs.
`PurrNetCoreTransportBridge` connects Core requests and snapshots to PurrNet.

For every attachable prefab:

1. Add an entry to the manager's Prop Registry with a stable `PropId` and
   prefab.
2. Use the same registry on every peer.
3. Supply the exact character bone name. An empty name/bone hash `0` means the
   character root; an unknown non-zero bone is rejected.
4. Store the approved `PropInstanceId` if more than one copy of a prop can be
   attached. Use instance detachment for an exact removal.

Use `NetworkCoreManager.Instance.RequestDetachPropInstance(characterId,
propInstanceId)` for an exact removal, or
`NetworkCoreManager.Instance.RequestDetachAllProps(characterId)` to clear every
network-managed attachment. The older detach-by-prefab request remains useful
when removing any one matching attachment is sufficient.

Attach/detach requests are validated by the server. Each peer then reconstructs
the registered prefab locally. A full attachment snapshot is sent when a player
loads the scene, so repeated snapshots must not create duplicates.

Arbitrary existing `GameObject` references cannot be synchronized this way.
Use a PurrNet network-spawned object when an instance has gameplay authority,
physics ownership, or a lifecycle independent of its character attachment.

## Shooter setup

Apply the required Shooter server-authority patch before configuring weapons.
The current patch uses a cancellable `NetworkOnHitValidator` before GC2 hit side
effects and reports the native shield result after `OnHit`, so Block, Parry, and
Block Break reconstruct correctly on other peers. A project that still has only
the older `NetworkHitDetected` callback must reapply the patch; a notification
cannot safely cancel client damage.

Populate the PurrNet Shooter bridge's Weapon Registrations. Each registration
maps a `ShooterWeapon` to the remote model prefab and optional GC2 `Handle` used
to equip it. The weapon hash is derived from the weapon asset.

The bridge retains legacy Weapon/Prefab/Handle arrays for upgraded scenes, but
new scenes should use structured registrations. All peers need the same assets
and hashes. The setup wizard reports an empty registry; runtime validation also
reports mismatched legacy arrays, null weapon/model entries, and duplicate
hashes, while missing hashes received from the network are reported once per
hash.

Missing Shooter registrations are non-visual by default: gameplay events and
notifications continue, but the controller does not silently create debug
geometry. Enable `Use Generated Fallback Presentation` on a
`NetworkShooterController` only when a simple generated tracer/impact is an
intentional fallback (for example, during diagnostics).

Tracers, projectile visuals, muzzle flashes, and impact effects are local
presentation. Only use a network-spawned projectile when its ongoing position,
collisions, ownership, or interaction must itself be authoritative.

`NetworkShooterImpactProp` is intended for important scene props whose pushed
pose must be replayed. Its automatic ID is based on the scene/hierarchy path and
is safe only when the same object exists at the same path on every peer. Assign
a stable shared ID for runtime-created equivalents, or make the prop a real
PurrNet network object.

When GC2 Stats owns health, add `NetworkShooterStatsDamageBridge`. Damage and
reaction are independent responsibilities: a Stats handler may consume the
health change, but the Shooter manager still applies the authored reaction once.

## Melee setup

Register melee weapons on `PurrNetMeleeTransportBridge` so remote clients can
resolve weapon and Skill hashes. Configure hit presentation entries on
`NetworkMeleeManager`: each Skill can specify Default, Blocked, Parried, and
Block Broken local effects.

Subscribe to `OnHitPresentationRequested` for project-specific presentation.
Mark the supplied context as handled when the subscriber has played a custom
effect; otherwise the manager uses the registered fallback. Presentation code
must never call the Skill's gameplay hit callback, because damage has already
been authorized by the server.

## Traversal setup

Apply the current Traversal server-authority source patch and add one
`NetworkTraversalController` to each networked Character. PurrNet Traversal
currently supports the built-in movement backend only: selecting Traversal
together with PurrDiction is rejected because the PurrDiction driver does not
implement `INetworkOwnerMotionAuthority`.

Finite traversal links are transient operations. They are validated and shown
to connected peers, but a completed link is never replayed to a late joiner.
An active interactive traversal is persistent state: its stable traversal
identity and relative pose are restored from a versioned snapshot without
rerunning gameplay-bearing enter instructions.

The server serializes traversal operations per Character. A requested start
must produce the matching GC2 motion-enter event within one second; otherwise
it is rejected and any late enter is cancelled. Short-lived events waiting for
a controller are discarded after two seconds, while only the latest persistent
interactive snapshot is retained. A missing Traverse does not consume its state
version, so the same snapshot or start remains retryable when spawn order catches
up. Superseded starts are rejected after GC2 async yields, preventing an older
transition from overwriting the latest target.

Interactive snapshots reconstruct remote presentation without replaying enter,
exit, start, or finish instruction lists. The local owner additionally resumes
the movement loop so climbing input does not freeze. The server accepts the
resulting owner pose only while the correlated traversal motion window is open.

If a network Character cannot resolve its manager, transport, role, ID,
controller, current patch, motion, or supported movement backend, traversal
fails closed and reports the missing requirement. Native traversal remains
available only to Characters that are genuinely not networked.

## Dedicated servers

A dedicated server validates requests, owns persistent state, and emits
broadcasts/snapshots. It may keep transforms and non-rendering components needed
for validation, but should skip audio, particles, tracers, model-only projectiles,
and Canvas creation.

## Troubleshooting

### A remote weapon or effect is invisible

- Check the warning for the missing weapon/Skill/prop hash.
- Confirm the same asset registration exists on every peer.
- Confirm the character has a non-zero network ID and its module controller has
  registered.
- Test with a late joiner; a persistent weapon should arrive in a snapshot,
  while an old hit spark should not.

### An effect plays twice

- Gameplay should be applied only on the server.
- The attacker controller owns shared hit presentation; the target controller
  owns target reaction.
- Do not independently replay a confirmed effect from both callbacks.
- For optimistic effects, make sure the confirmed event matches and consumes
  the predicted presentation.

### A Core prop is missing

- Confirm `NetworkCoreManager` and `PurrNetCoreTransportBridge` exist once.
- Remove legacy `NetworkCoreController` components from character prefabs.
- Verify the prop registry entry, prefab, bone name, and attachment limit.
- Use detach-by-instance when duplicate prop hashes are allowed.

### A moved impact prop resets for a late joiner

- Add `NetworkShooterImpactProp` to important persistent scene props.
- Verify its stable ID is identical on server and clients.
- Runtime-created interactive props should use PurrNet spawning instead.

### Traversal is suppressed or an interactive pose is stale

- Apply and validate the current Traversal source patch in the setup wizard.
- Use the Built-in movement backend; PurrDiction does not yet expose the owner
  motion-authority window required by Traversal.
- Check the diagnostic for an unready role, network ID, controller, motion, or
  route. Networked Characters fail closed instead of running native traversal.
- A `StartTimeout` means GC2 did not emit the matching motion-enter event within
  one second. Verify that the Traverse is enabled, has a usable Motion, and its
  conditions succeed on the server.
- Active interactive traversal is restored from snapshots. Finite links are
  intentionally transient and are not replayed to late joiners.

## Version compatibility

Core, Shooter, Melee, and Traversal packet or snapshot wire layouts changed
with this repair.
Every server and client in a session must use the same package version;
mixed-version sessions are unsupported.
