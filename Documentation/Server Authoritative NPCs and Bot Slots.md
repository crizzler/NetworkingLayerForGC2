# Server-Authoritative NPCs and Bot Slots

This guide explains how network actor authority differs from Game Creator 2's
local `Character.IsPlayer` flag, how to run GC2 enemy AI on authority only, and
how optional bot-backed player slots hand a bot's place to a joining human.

## Actor type is the authority contract

`NetworkCharacter.ActorType` is the network identity contract. `Character.IsPlayer`
is only GC2's local shortcut/input designation; changing it at runtime never grants
network ownership or permission to send gameplay requests.

| Actor Type | Intended use | Logical client owner | Where GC2 `IsPlayer` is true | Durable gameplay authority |
|---|---|---|---|---|
| `LegacyAutomatic` | Existing assets awaiting migration | Inferred for compatibility | Inferred from the authored asset and resolved role | Existing compatibility rules |
| `PlayerOwned` | A human player's character | Required and transport-authenticated | Only on that player's local owning peer | Validated client requests and server authority |
| `NPC` + `ServerAuthoritative` | Enemies, companions, and replacement bots | None | Nowhere | Server, dedicated server, or Fusion Shared master only |
| `NPC` + `ClientSideDeterministic` | Matching cosmetic simulations | None | Nowhere | None; Shooter, Melee, Stats, and other durable gameplay are prohibited |

Use `LegacyAutomatic` only to preserve old prefabs while migrating. New player and
NPC prefabs should always be classified explicitly. The Network Character inspector
reports effective actor type, authenticated ownership, and simulation authority at
runtime, and warns about ambiguous legacy assets.

For an explicitly classified actor, `NetworkCharacter` continually corrects an
incorrect `Character.IsPlayer` value. A modified client therefore cannot turn an
NPC replica into an owned player. Player ownership is accepted only when the
transport supplies authenticated logical-owner information.

## Preparing a server-owned enemy

The Fusion and PurrNet Scene Setup Wizards contain a **Server-Owned NPCs and Bot
Slots** section. Assign a prefab, enable NPC preparation, choose every child root
that contains authority-only GC2 Triggers, then review validation before applying
the setup.

The setup adds or configures:

- `Character.IsPlayer = false` and an explicit `NetworkCharacter` actor type of
  `NPC` with `ServerAuthoritative` sync;
- the transport identity and automatic role initializer;
- a supported movement backend and requested Shooter, Melee, or Stats controllers;
- `NetworkCharacterAuthorityGate` with the exact selected AI roots;
- `NetworkNpcTargetSelector`, which chooses the nearest living authenticated
  `PlayerOwned` character and retargets after death or disconnect;
- transport prefab registration and optional bot-slot scene infrastructure.

`NetworkCharacterAuthorityGate` disables its selected roots before network role
resolution. It restores their authored active state only on the authoritative
server/Shared master and disables them immediately when authority is lost. Put
start, update, perception, aim, fire, and release Triggers below selected roots.
Do not select the character root itself.

The target selector uses the bridge's registered-character collection without a
scene scan or per-refresh array allocation. Equal-distance candidates are broken
by stable network ID. Its optional GC2 follow command updates the authoritative
character; authored visibility, aim, fire, and release Conditions and Instructions
remain ordinary GC2 logic below the authority gate.

### Movement backend

- Fusion NPCs support **Built-in server NavMesh** and **Fusion Native**. The wizard
  does not select Advanced KCC for an NPC because that integration is intended for
  supported player movement unless an NPC-specific implementation says otherwise.
- PurrNet NPCs use **Built-in server NavMesh**. The wizard removes PurrDiction
  owner-prediction components from an NPC prefab.

For Built-in movement, authority preserves the prefab's authored GC2/NavMesh
driver instead of replacing it with a player driver. Observers use the remote
interpolation driver. If authority migrates, the authored driver is restored on
the promoted peer. `UnitDriverNavmeshNetworkServer` honors normal GC2 movement
type and follow commands. The transport-neutral pose sampler publishes the
existing `NetworkPositionState` schema for PurrNet and Built-in Fusion; Fusion
Native continues to use its native tick state.

## Security behavior

A trusted server-origin Shooter or Melee event is accepted only when its actor is
a genuinely server-authoritative NPC with no registered client owner, or the
server's own authenticated local `PlayerOwned` actor. Remote player replicas,
client-deterministic NPCs, and characters whose `IsPlayer` flag was locally
changed do not qualify. Client requests that claim an NPC actor ID fail ownership
validation.

Client-deterministic NPCs are cosmetic. `NetworkStatsController` rejects their
durable stat, attribute, modifier, and status-effect mutations on both request and
server-processing paths. Do not place combat-authoring GC2 Triggers or network
gameplay controllers on them.

## Bot-backed player slots

Bot slots are optional and reusable. Each slot has a stable string ID, an anchor,
and a server-owned bot prefab. The transport coordinator replicates the slot's
occupant type, actor network ID, revision, position, and rotation.

The player spawner performs the handoff as one reservation transaction:

1. Authority fills vacant configured slots with bots.
2. A joining human reserves a vacant slot and the bot is removed.
3. The player is spawned at that bot's last authoritative position and rotation.
4. Successful spawning commits human membership. Failed spawning rolls the
   reservation back and restores a fresh bot.
5. A disconnect records the human's last transform and creates a fresh replacement
   bot there.

Only transform and membership are handed over. Health, inventory, equipment,
ammo, and AI state are deliberately not transferred. Humans beyond the configured
slot count use the existing normal spawn points. Duplicate join/leave callbacks
are idempotent.

Fusion replicates membership through `FusionBotSlotStateReplicator`, allowing a
new Shared master to rebuild assignments without duplicate actors. PurrNet uses
authority-authenticated snapshots for host and dedicated-server lifecycles,
including reconnects and late joins.

## Wizard workflow

### Fusion

1. Open **Game Creator > Networking Layer > Fusion Scene Setup Wizard**.
2. Prepare the human prefab as `PlayerOwned`.
3. Assign the NPC prefab and choose Built-in NavMesh or Fusion Native.
4. Add every GC2 AI Trigger root by its path.
5. Optionally enable bot-backed slots and choose the count.
6. Resolve every Review error, then create/update the scene setup.

Validation blocks missing `NetworkObject` identity, unsafe ownership flags,
unregistered prefabs, Fusion KCC NPC components, durable client-deterministic
combat, and ungated Trigger roots.

### PurrNet

1. Open **Game Creator > Networking Layer > PurrNet Scene Setup Wizard**.
2. Prepare the human prefab as `PlayerOwned`.
3. Assign and prepare the NPC prefab.
4. Add every GC2 AI Trigger root and optionally configure bot-backed slots.
5. Resolve every Review error, then create/update the scene setup.

Validation blocks missing root `NetworkIdentity`, unsafe or missing prefab
registration, PurrDiction on the server NPC, durable client-deterministic combat,
and ungated Trigger roots.

## Enemy Shooter examples

Shooter Examples `1.1.0` adds separate scenes without changing the existing PvP
Shooter + Stats fixtures:

- `Requires Shooter Demos - FusionEnemyShooterDemo.unity`
- `Requires Shooter Demos - PurrNetEnemyShooterDemo.unity`

Each scene uses an Arawn-owned enemy prefab based on the useful structure of GC2's
`19_Enemy_AI`: authority-gated start/update Triggers, nearest-player targeting and
following, raycast visibility, aim, fire, and release. The copied AI references
the demo-owned AK with collider-free hit effects instead of packaging the
third-party example or its pistol asset. A persisted NavMesh and three bot-backed
combatant slots are included. With one host, two bots remain; a second human
replaces another bot while at least one server-owned enemy remains for testing.

## Migrating existing prefabs

1. Identify whether each `LegacyAutomatic` character represents a human player or
   an NPC. Do not decide from a runtime `IsPlayer` mutation.
2. Use the Network Character inspector migration action or the relevant setup
   wizard to set `PlayerOwned` or `NPC` explicitly.
3. For an NPC, select its authority-only Trigger roots, choose a supported backend,
   and resolve all validation errors.
4. Test host, remote client, late join, disconnect/reconnect, and Fusion Shared
   authority migration before removing legacy compatibility assumptions.
