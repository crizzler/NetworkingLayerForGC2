# Server-Authoritative Free Flow Combat

The optional Free Flow Combat integration routes Free Flow attacks, enemy AI,
counter windows, and presentation through the Networking Layer's existing
server-authoritative Melee pipeline. Fusion and PurrNet use the same gameplay
contract; only their packet delivery differs.

## Supported scope

The current integration is deliberately PvE:

- an authenticated `PlayerOwned` character may attack or counter a
  server-authoritative `NPC`;
- a genuinely server-owned NPC may select and attack a living authenticated
  `PlayerOwned` character;
- remote clients never simulate the enemy director, Behavior `Processor`, or
  authoritative enemy Skills;
- generic networked Melee remains available for other combat patterns, but the
  Free Flow target-selection and counter contract does not currently authorize
  player-versus-player Free Flow actions;
- `ClientSideDeterministic` NPCs are cosmetic and cannot author Free Flow hits,
  counters, Stats damage, or other durable gameplay state.

Normal counters against an authority-published open counter window are
supported. A counter forced directly from a local shield `OnDefend` callback is
currently rejected because it does not yet carry a server-validated parry
lease. This fails closed instead of trusting a client-only defense event.

## Actor classification controls authority

`NetworkCharacter.ActorType`, authenticated transport ownership, and effective
runtime role decide who may simulate or request combat. GC2's local
`Character.IsPlayer` flag does not grant network authority.

| Actor setup | Local Free Flow runtime | Enemy AI and Behavior graph | Durable combat authority |
|---|---|---|---|
| `PlayerOwned` with an authenticated local owner | Local owner only | Disabled | Sends semantic requests; authority validates and applies them |
| `NPC` + `ServerAuthoritative` on server, dedicated server, or Fusion Shared master | Disabled | Enabled on simulation authority only | May originate trusted NPC Skills and publish Free Flow state |
| `NPC` + `ServerAuthoritative` observer replica | Bootstrap/receiver components present; runtime disabled | Agent/Processor components present; execution disabled | Replicated presentation only |
| `NPC` + `ClientSideDeterministic` | Disabled by this integration | No authoritative AI | None |
| `LegacyAutomatic` | Compatibility behavior is ambiguous | Not suitable for new setup | Migrate before enabling Free Flow combat |

For explicit actors, the Networking Layer corrects unauthorized changes to
`Character.IsPlayer`. A modified client therefore cannot turn an NPC replica
into a player owner or enter the trusted server-NPC path.

## Required setup

### Prerequisites

1. Install Game Creator 2 Core and Melee, the Networking Layer, one supported
   Fusion or PurrNet transport, and Free Flow Combat.
2. Apply and verify the current GC2 Melee server-authority patch.
3. Configure the normal `NetworkMeleeManager` and transport Melee bridge.
4. Let the Networking Layer synchronize the
   `ARAWN_GC2_TRANSPORT_INTEGRATION` scripting define, then wait for Unity to
   finish compiling and reload the domain.
5. Keep the same Free Flow weapon, Combo/Skill assets, stable registrations,
   layer masks, and Networking Layer version available on every peer.
6. For the canonical Brawl fixture, keep all three directly executed Skills
   (`FreeFlow_Brawl_Combo1`, `FreeFlow_Brawl_Combo2`, and
   `FreeFlow_Brawl_Combo3`) in GC2's **Motion Warp** mode. A serialized
   `TrackMeleeMotionWarping` or `ClipMeleeMotionWarping` does not execute while
   the Skill is configured as ordinary Root Motion.

The Networking Layer Melee assembly does not reference concrete Free Flow
classes. `NetworkFreeFlowCombatAdapter` exposes a transport-neutral receiver
contract, and Free Flow supplies its receiver from `Assembly-CSharp` only when
the integration define is enabled. This inverted dependency lets the Networking
Layer compile when Free Flow Combat is not installed.

### Player character

On the network player prefab:

1. Configure `NetworkCharacter` explicitly as `PlayerOwned`.
2. Add `NetworkMeleeController` and follow the normal Melee weapon/Skill
   registration workflow.
3. Add `NetworkFreeFlowCombatAdapter` to the same Character root.
4. Equip a `FreeFlowMeleeWeapon` and configure its Combo, targeted attack,
   fallback, counter, range, visibility, and indicator settings as usual.
5. Set every directly selected targeted Combo Skill to **Motion Warp** and keep
   its authored target-relative motion-warp track. The demo's three canonical
   direct Combo Skills are the compatibility baseline.
6. If GC2 Stats owns health, configure the normal
   `NetworkMeleeStatsDamageBridge`; Free Flow does not bypass Melee/Stats
   authority.

When the Free Flow weapon is equipped, `FreeFlowNetworkIntegration` is added on
the Character root when needed. The adapter keeps the complete Combo/usage map
separate from the smaller direct-play allow-list. A Skill referenced only by a
later Combo node therefore cannot be forged as a direct request; only an exact
authored direct-play instruction may use `ComboNodeId == NODE_INVALID`. If an
adapter is present but its receiver or equipped weapon cannot be resolved,
networked Free Flow requests fail closed.

Free Flow may maintain its movement lock by calling GC2 `StopToDirection` every
rendered frame. The Networking Layer applies that local stop every time but
coalesces an unchanged stopped network state after its first reliable
`StopDirection`. A changed priority/context and the first nonzero movement after
the stop are still sent immediately. This keeps the normal shared Core request
budget intact without weakening rate-limit protection.

### Enemy character

On every network enemy prefab:

1. Configure `NetworkCharacter` explicitly as `NPC` with
   `ServerAuthoritative` NPC sync. Do not assign a client owner.
2. Add `NetworkMeleeController` and `NetworkFreeFlowCombatAdapter` to the
   Character root.
3. Add and configure `NetworkNpcTargetSelector`. Its selected authenticated
   player supplies the stable `TargetNetworkId` used by NPC attacks and
   counter assignment; without a selected network target, a trusted NPC attack
   is rejected.
4. Keep one replica-safe On Start equip/bootstrap Trigger on the Character root,
   outside `NetworkCharacterAuthorityGate`. It must run on every admitted replica,
   resolve **Self** to that exact Character (not to its selected combat target),
   and equip the same registered canonical `FreeFlowMeleeWeapon`.
5. Configure `FreeFlowCombatTarget`, `FreeFlowEnemyAgent`, its director, and the
   Free Flow Behavior graph. The weapon's optional non-player auto-setup may
   create missing Free Flow components, but it does not replace an unrelated
   GC2 Behavior graph already assigned to a `Processor`.
6. Put any additional GC2 AI Trigger roots behind
   `NetworkCharacterAuthorityGate` and use a supported server-authoritative
   movement backend.

For a Built-in/NavMesh enemy, keep **NavMesh Agent Network (Server)** as the
authored GC2 Driver. It is a complete authority driver and runs on a Fusion
Host, the current Fusion Shared master, a PurrNet Host, and a dedicated server;
it is not dedicated-server-only. The Networking Layer automatically replaces
it with the remote interpolation driver on observer replicas and restores it
when authority migrates. Do not assign **NavMesh Agent Network (Client)** to a
server-authoritative NPC: that driver is for client-owned movement and would
create a second, untrusted simulation writer. The Free Flow target selector's
**Follow Selected Target** option is intentionally disabled in the supplied
demos because the authored Free Flow Behavior graph owns approach, orbit, and
retreat through ordinary GC2 movement commands.

The setup wizard repairs that mistake at authoring time. Runtime also fails
safe for older or manually edited prefabs: an explicit server-owned NPC found
with **NavMesh Agent Network (Client)** is given a sampled **NavMesh Agent
Network (Server)** authority driver, while an already correct authored server
driver remains preserved.

An enemy prefab may still contain `UnitPlayerDirectionalNetwork` when it was
derived from a network-player prefab. That Player unit is inactive while
`Character.IsPlayer == false`: it clears only its cached local input and any
client input sink, and deliberately leaves `Character.Motion` untouched. GC2
updates Player before Motion and Driver, so clearing `Motion.MoveDirection` in
that branch would erase the Behavior graph's approach/orbit command before the
server NavMesh driver could consume it. Do not try to compensate for a motion
ownership problem by assigning the client NavMesh driver.

The equip/bootstrap Trigger initializes `FreeFlowCombatTarget`,
`FreeFlowEnemyAgent`, the Behavior `Processor`, the integration receiver,
indicators, and motion-warp candidates on authority and observer replicas. The
adapter then enables the enemy agent, director, and `Processor` only on NPC
simulation authority. Observer replicas keep those components present for
replicated presentation but do not run the director queue, movement decisions,
counter-window logic, or attack selection. On Fusion Shared-master migration,
transient enemy attack/counter leases are cancelled and the promoted authority
starts fresh instead of adopting another peer's private director state.

### Adapter settings

`NetworkFreeFlowCombatAdapter` normally needs no object reference assigned by
hand. Its serialized settings are safeguards and diagnostics:

- **State Poll Interval** controls how often NPC authority checks for a changed
  presentation state. State is sent only when its value changes.
- **Server Range Tolerance** adds a small authority-side allowance to the
  weapon's authored range for quantization and normal network timing. It should
  not be used to hide incorrect target or movement synchronization.
- **Log Authority** emits one role/setup diagnostic and then turns itself off.

## Request and trust flow

### Player attack

1. The local authenticated player selects a Free Flow target and starts an
   authored Combo or direct Skill.
2. `NetworkMeleeController` sends the existing semantic Skill request with the
   actor, target, weapon, Skill, Combo context, timestamp, and correlation ID.
3. The server authenticates the sender and actor ownership. The Free Flow
   adapter additionally validates the weapon, direct-Skill allow-list, target
   classification, liveness, attackable state, authored range, and optional
   line of sight.
4. Only the normal patched Melee authority path may apply the hit and optional
   Stats damage. Confirmed reactions and presentation are then broadcast to
   observers.

Client target selection is a request hint, not proof. Authority resolves the
stable target ID and repeats the gameplay checks against its own state.

On Fusion, a non-authority peer timestamps this semantic request from the latest
confirmed server tick rather than its prediction-ahead local simulation time.
Host, dedicated-server, and Shared-master authority continue using the
authoritative simulation clock. This prevents honest requests from appearing to
come from the future while preserving the existing security tolerance.

### Server-owned NPC attack

The enemy director and Behavior graph run only on the server, dedicated server,
or Fusion Shared master. A Skill that they start enters
`TryPublishTrustedServerSkill`; it is accepted only when:

- the registered controller belongs to a genuinely unowned,
  server-authoritative NPC with simulation authority;
- the correlation ID belongs to that actor and has not already been handled;
- the equipped weapon and Combo/direct Skill identity match registered assets;
- a direct Free Flow Skill is allow-listed by an exact authored path in that
  NPC's Free Flow Behavior graph or bound enemy-agent fallback;
- the target is a living authenticated `PlayerOwned` actor within the
  authority-side range and optional line of sight.

The trusted path creates one attack lease and one presentation broadcast for an
operation. Repeated callbacks with the same actor/correlation pair are
idempotent, and the patched Melee hit path retains its own exact-once hit and
damage protection. A rejected Skill cannot manufacture a lease later from a
strike callback, so it also cannot deal invisible damage without a matching
Skill broadcast.

## Counter windows are consumed exactly once

Authority publishes each NPC counter window as revisioned state assigned to one
authenticated player network ID. Preparing a normal local counter does not
close that window. Instead, the outgoing Skill request is stamped with the
`FreeFlowCounter` action flag and the exact state revision the owner observed.

Authority then validates all of the following before committing the counter:

- authenticated ownership of the requesting `PlayerOwned` actor;
- a living server-authoritative NPC target;
- a counter Skill allow-listed by the equipped Free Flow weapon;
- the current window is still open, has the same revision, and is assigned to
  the requesting player's network ID;
- counter radius, optional line of sight, and server cooldown;
- atomic consumption succeeds in the authoritative `FreeFlowEnemyAgent`.

Successful consumption immediately closes the window and publishes a newer
state revision. A duplicate, stale, late, wrong-player, or already-consumed
request cannot consume it again. Host/Shared-master loopback uses the same
validation and commit path as a remote client. If local counter setup aborts
before producing a counter Skill, its pending revision is cancelled; an
ordinary attack cannot inherit that stale counter context.

The connected owner's local Free Flow weapon may force-cancel an existing
Melee phase when a counter starts. If the server presentation replica is still
busy with that prior phase, only a `FreeFlowCounter` request that has passed the
full action-context checks and atomically committed the exact assigned revision
may interrupt it. Authority force-cancels that stale presentation phase just
before playing the approved counter. An ordinary Skill, a forged flag, or a
stale revision still receives the normal Busy or invalid-context rejection.

The counterable flag remains part of authority state for validation and late
join convergence, but the overhead counter indicator is presented only on the
authenticated local peer whose network ID owns that window. Other observers do
not receive an actionable prompt for a counter they are not allowed to issue.

## Replicated and late-join state

Authority replicates only the presentation-relevant
`NetworkFreeFlowCombatState`:

- character network ID and wrap-safe state revision;
- selected player network ID;
- attackable, counterable, and attack-token flags;
- Free Flow score bonus.

The state is broadcast when it changes and is also stored in the existing
revisioned Melee character snapshot. A late joiner receives the current value,
not a replay of old telegraphs or attacks. If the character controller,
adapter, receiver, or selected target has not spawned yet, the newest state is
retained and retried until its stable IDs resolve. Older revisions are ignored.
Because a targeted character snapshot is a complete replacement, an omitted
Free Flow field explicitly clears any older observer presentation. This clear
uses an internal snapshot-only version-zero tombstone; a live version-zero
packet is rejected and cannot clear state.

Director queues, random choices, Behavior runtime data, cooldown dictionaries,
movement decisions, and partially executing Skills remain authority-local.
They are deliberately not reconstructed on observers or late joiners.

## Fusion and PurrNet demos

The dedicated `1.0.0` installers contain these scenes:

- `GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples@1.0.0/Requires Melee & Free Flow Combat - FusionFreeFlowCombatDemo.unity`
- `GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples@1.0.0/Requires Melee & Free Flow Combat - PurrNetFreeFlowCombatDemo.unity`

Each scene contains one explicitly `PlayerOwned` spawn prefab and five
persistent server-authoritative Free Flow NPC instances. The enemy prefab uses
Built-in/NavMesh movement, `NetworkNpcTargetSelector`,
`NetworkCharacterAuthorityGate`, and `NetworkFreeFlowCombatAdapter`. Its
replica-safe On Start Trigger equips the canonical Brawl Free Flow weapon on
every admitted replica so each peer creates the presentation/bootstrap
components it needs. Only the enemy agent, director, Behavior `Processor`, and
additional AI Triggers execute on server/Shared-master authority. The scene
replaces the stock Melee input Trigger with Free Flow attack and counter inputs,
packages its NavMesh, and deliberately has no bot-slot replacement coordinator.

Keep `Free Flow Network Enemies` as a root object in the gameplay scene. Do not
parent it below the Fusion/PurrNet session object: the session bootstrap may
move its root to `DontDestroyOnLoad` before transport scene-object admission.
If that happens, transport admission, role registration, and the NPC lifecycle
remain incomplete, so the replica bootstrap and authority policy cannot become
valid consistently. In Fusion this fails closed and can end the session when a
full snapshot sees active Characters that were never admitted.

`Disable Visuals on Server` and `Disable Audio on Server` remain useful on a
true dedicated server. PurrNet Host startup briefly has a server half before
its local client half is ready, so `PurrNetNetworkCharacterAuto` waits for that
pending Host topology and `NetworkCharacter` restores authored presentation
states if a role reset or migration occurs. A listen Host must therefore see
the same five NPC presentations as its connected client.

A PurrNet scene NPC with a `NetworkIdentity` also waits until that identity is
actually spawned before registering its Networking Layer role. The spawned
PurrNet object ID then becomes the stable character ID on authority and every
observer. Initializing from the local, pre-spawn scene hash would let the same
NPC acquire different IDs on different peers and correctly cause a targeted
attack to fail authority validation. If identity spawn exceeds the configured
startup timeout, initialization remains fail-closed and continues waiting; the
diagnostic points to scene-object admission, `skipSceneAutoSpawning`, and
prefab registration instead of silently accepting a divergent ID.

PurrNet may deactivate a pooled Character while its remote-presentation wrapper
still owns the authored Mannequin. Unity does not allow a `SetParent` hierarchy
write from inside that activation/deactivation callback stack. Cleanup therefore
queues the visual-root restore on a persistent active runner, returns the
Mannequin to the Character after that stack has unwound, and only then destroys
the empty temporary wrapper. This prevents the pooled visual from being
destroyed with the wrapper and avoids the follow-on missing-Animator failures.
The semantic smoke trace also enables only its focused Skill/input diagnostic
gates; the packet-wide gate stays off so repeated controller-registry refreshes
do not flood lifecycle and teardown logs. The multi-process Free Flow runner
also fails if either peer records a Core `RateLimitExceeded` violation, so a
valid semantic result cannot conceal gameplay that exhausted the normal
security budget.

The installer descriptors and exact package archives are located at:

- `Demo/Fusion/Packages/FreeFlowCombat/GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples.asset`
- `Demo/Fusion/Packages/FreeFlowCombat/Package.unitypackage`
- `Demo/PurrNet/Packages/FreeFlowCombat/GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples.asset`
- `Demo/PurrNet/Packages/FreeFlowCombat/Package.unitypackage`

The current rebuilt archives have these SHA-256 values:

- Fusion: `0a9f98fd1578ff6fd5edbc1e579ab7e2f42ef81a9a1be1c8d7899dfea2ecb864`
- PurrNet: `34aa4f83f95316ed4f65b28fa914f164a3b052ca4d906b331e62c44ca064bdae`

Rebuild them from the installed Melee fixtures with
`FusionFreeFlowCombatDemoBuilder.BuildInstaller` and
`PurrNetFreeFlowCombatDemoBuilder.BuildInstaller` in an isolated Unity project.
The builders export only their generated install root; Free Flow Combat and
licensed Game Creator/transport dependencies remain external.

Validate player attacks, target switching, one successful normal counter, a
deliberately stale counter rejection, NPC attacks, death, and a late join.

Run Fusion in Host + Client and Shared two-peer modes, including a Shared-master
change. Run PurrNet in Host + Client and Dedicated Server + Client modes.
Observers must never advance enemy AI locally, while every peer must converge
on target, attackable/counterable flags, token presentation, reactions, and
health.

## Wire-version requirement

This feature extends `NetworkSkillRequest` with Free Flow action context, adds a
new revisioned Free Flow state payload, includes that state in Melee late-join
snapshots, and adds transport messages/codecs for Fusion and PurrNet. Fusion's
codec also rejects trailing payload data rather than guessing another schema.

Every peer in a session must therefore use the same Networking Layer 2.3.0 wire
implementation and matching Free Flow/weapon assets. Mixed older/newer clients
are unsupported even if ordinary Melee appears to connect.

## Validation commands

The `Tools/...` commands below are private-development CI commands and are not
part of the flattened public package mirror. Run them from the private
development checkout in an isolated Unity project; do not run batch Unity while
the Editor owns that checkout.

```bash
export GC2_NETWORK_PRISTINE_ROOT=/path/to/pristine-gc2

Tools/run_gc2_patch_roundtrip.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root "$GC2_NETWORK_PRISTINE_ROOT"

Tools/run_unity_editmode_tests.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root "$GC2_NETWORK_PRISTINE_ROOT" \
  --prepare-freeflow-fixtures \
  --test-category GC2Networking.FreeFlow \
  --minimum-passed 56 \
  --require-suite Arawn.GameCreator2.Networking.CorePurrNet.Tests.PurrNetMovementArchitectureTests \
  --require-suite Arawn.GameCreator2.Networking.CorePurrNet.Tests.PurrNetNetworkCharacterAutoTests \
  --require-suite Arawn.GameCreator2.Networking.Melee.Tests.FreeFlowNetworkingTests \
  --require-suite Arawn.GameCreator2.Networking.Melee.Tests.FreeFlowCombatExamplesInstallerTests \
  --require-suite Arawn.GameCreator2.Networking.Melee.Tests.MeleeReplicationTests \
  --require-suite Arawn.GameCreator2.Networking.Tests.NetworkNpcAuthorityTests \
  --require-suite Arawn.GameCreator2.Networking.Melee.Transport.Fusion.Tests.FusionMeleeCodecTests \
  --require-suite Arawn.GameCreator2.Networking.Transport.Fusion.Tests.FusionEditorIntegrationTests \
  --require-suite Arawn.GameCreator2.Networking.Transport.PurrNet.Editor.Tests.PurrNetWizardPredictionTests \
  --release-build

Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --topology purrnet-freeflow-host-client

Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --topology purrnet-freeflow-dedicated-client

# Fusion also requires GC2_NETWORK_SMOKE_PHOTON_APP_ID.
Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --topology fusion-freeflow-host-client

Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --topology fusion-freeflow-shared-two-peer
```

When invoking the new Unity CLI directly, do not use
`unity test --filter GC2Networking.FreeFlow` as a category selector: `--filter`
matches test names and produced a zero-test run here. Forward
`-testCategory GC2Networking.FreeFlow` to the Unity Test Framework and inspect
the generated XML for 56 passes, zero failed/inconclusive tests, and all nine
required suites.

The current 2026-08-27 isolated Unity `6000.5.9f1` acceptance run passed the
focused category 56/56 with zero failed, inconclusive, or skipped tests and all
nine named suites present. The retained result is
`.unity-ci/editmode.qT3OMh/TestResults/editmode-results.xml`. The same run's
non-development Linux player build completed with process exit `0` and
`Build Finished, Result: Success`; the broader shared Melee
filter passed 86/86. The focused gate guards scene-root admission, reversible
dedicated-server presentation, exact self-targeted replica bootstrap, the
canonical indicator/Behavior authoring, all three canonical direct Combo Skills
remaining in Motion Warp mode, and PurrNet NPC role initialization waiting for
its spawned stable identity. The added
`VisualPresentation_PooledDeactivationDefersHierarchyMutation` test in the
PurrNet movement fixture proves pooled deactivation defers the unsafe hierarchy
mutation, preserves the Mannequin, and restores it before retiring the temporary
presentation wrapper. The semantic-smoke regression also proves that the
harness ignores stale pre-invocation events and adopts the actual first
post-`Attack()` broadcast for the authenticated local actor and an explicit
server-authoritative NPC captured immediately before invocation. This matters
because Free Flow's authored directional/fallback selection may legitimately
choose a different NPC than the candidate primed to activate targeting. A valid
action may also enter `AttackRadius` and intentionally clear both indicators,
so complete role/presentation/indicator/admission readiness gates only the
invocation. The harness retains that fully ready result while polling the exact
post-baseline tuple through the transient UI change; wrong actors, unknown
attack-time targets, and zero-Skill broadcasts remain rejected.
EditMode and compile evidence do not replace the multi-peer topologies below.

The focused suite covers authority/replay policy, distinct direct and Combo
allow-lists, Behavior-graph NPC Skills, stale counter-context cancellation,
cross-category hit redirection, accepted-only trusted NPC attack leases,
PurrNet and Fusion serialization, revision ordering/wrap,
persistent snapshots, state arriving before its adapter, explicit demo actor
roles, five explicit NPCs, replica-safe equip Triggers outside the authority
gate, authority-gated non-initialization AI Triggers, prefab registration,
NavMesh references, canonical-current exact-path package contents, snapshot
omission clearing, and rejection of live version-zero clears. The isolated
fixture step rebuilds both ignored install roots without rewriting the source
archives.

The final 2026-08-28 multi-process runs invoke the real
`FreeFlowCombatRuntime.Attack()` and `Counter()` entry points. The observer
freezes the actual eligible NPC chosen by authored selection, then authority
must independently match that exact actor/NPC/Skill tuple; the harness does not
manufacture or relax a gameplay request.

PurrNet Host + Client passed under
`.unity-ci/network-smoke.TeUhEw/NetworkSmoke/purrnet-freeflow-host-client/Results`.
Authority ran all five server NavMesh drivers and Processors; the Client saw all
five enemies displaced up to `3.96285 m`, three correctly facing telegraphs,
and its target indicator. Its real Attack Motion Warp moved `0.70367 m`, and
both peers matched canonical Counter actor `7`, NPC `2`, Skill `-1382833785`.

PurrNet Dedicated Server + Client passed under
`.unity-ci/network-smoke.SCM827/NetworkSmoke/purrnet-freeflow-dedicated-client/Results`.
The headless server ran all five authority gates, targets, Processors, and
bound/on-mesh server NavMesh drivers without requiring presentation. The Client
retained five presentations and candidates, saw all five NPCs displaced up to
`2.73247 m`, three telegraphs facing at a best error of `0` degrees, and its
target indicator. Its real Attack moved `0.53721 m`; both peers matched Counter
actor `7`, NPC `4`, Skill `-1382833785`. A dedicated server has no local client
endpoint, so it validates the exact manager event raised after authoritative
Skill playback rather than requiring its ObserversRPC to loop back locally.

Fusion Host + Client passed under
`.unity-ci/network-smoke.iheQHd/NetworkSmoke/fusion-freeflow-host-client/Results`.
Both peers retained two authenticated humans and `5/5` spawned/admitted NPCs.
Authority ran all five server NavMesh drivers and Processors; the Client saw all
five NPCs displaced up to `9.88825 m`, two correctly facing telegraphs at a best
error of `0.05935` degrees, and its target indicator. Its real Attack Motion
Warp moved `0.31955 m`; both peers matched Counter actor `1033`, NPC `1028`,
Skill `-1382833785`.

Fusion Shared two-peer passed under
`.unity-ci/network-smoke.i3QITg/NetworkSmoke/fusion-freeflow-shared-two-peer/Results`.
The Shared master alone ran all five Processors and server NavMesh drivers; the
observer saw all five NPCs displaced up to `10.824 m` and its real Attack Motion
Warp moved `4.14173 m`. Both peers matched Counter actor `525318`, NPC `525316`,
Skill `-1382833785`.

These topologies prove semantic request, authority validation, observer
delivery, role separation, admission, presentation/indicator readiness, real
NPC NavMesh movement and facing, player Motion Warp, and canonical Counter
convergence. They recorded zero validated hits and therefore do not prove
health changes or exact-once damage, late joining, disconnect recovery, or
Fusion Shared-master departure/migration.

Archive both peer logs and result files for each transport. Do not claim combat
or movement behavior beyond the fields recorded by those results.

## Troubleshooting

### Adapter reports no receiver

- Confirm Free Flow Combat is installed and the
  `ARAWN_GC2_TRANSPORT_INTEGRATION` define exists for the active build target.
- Wait for a successful Unity compile/domain reload.
- Confirm the adapter is on the same Character root before the
  `FreeFlowMeleeWeapon` is equipped.

### Player attacks are rejected

- Verify the player is explicitly `PlayerOwned` with an authenticated transport
  owner and the target is an explicitly server-authoritative NPC.
- Confirm the same weapon and Skill assets are registered on authority.
- In PurrNet, confirm the NPC `NetworkIdentity` spawned and that the same
  Networking Layer character ID is reported on authority and the requesting
  client. Do not bypass the identity-spawn wait to make the request proceed.
- Keep registered Skill names unique; the existing Melee registry uses their
  stable name hash as its wire identity.
- Check authored attack/scan radius and visibility occlusion layers.

### Attack plays but does not warp toward the target

- Open every directly executed targeted Skill and set **Motion** to
  **Motion Warp**, not Root Motion. For the canonical Brawl fixture, verify
  `FreeFlow_Brawl_Combo1`, `FreeFlow_Brawl_Combo2`, and
  `FreeFlow_Brawl_Combo3`.
- Confirm each Skill still contains its authored `TrackMeleeMotionWarping` or
  `ClipMeleeMotionWarping` and target-relative position getter. The track being
  serialized is not enough if the Skill's motion mode does not select it.
- Verify the local Free Flow target indicator is active and the selected target
  resolves to the expected spawned NPC before starting the attack.

### PurrNet NPC identity never becomes ready

- Keep the NPC's `NetworkIdentity` registered and admitted as a PurrNet scene
  object. Check the scene prefab registry and `skipSceneAutoSpawning` setup.
- Treat the startup-timeout diagnostic as an admission/configuration failure.
  Explicit NPC role registration intentionally continues waiting rather than
  falling back to a peer-local scene hash.

### NPC does not attack

- Confirm the replica-safe equip/bootstrap Trigger exists on the Character root
  outside `NetworkCharacterAuthorityGate`, runs on both authority and observers,
  resolves Self to that exact Character, and equips the canonical registered
  weapon. A target-based getter can equip the selected player instead of the
  NPC.
- Confirm authority and observers all contain the Free Flow receiver,
  integration, agent, `Processor`, runtime, indicators, and target candidates.
- Confirm only authority has the Free Flow Behavior `Processor` enabled.
- Confirm only authority advances agent/`Processor` iterations; component
  presence on an observer is expected, execution is not.
- Add/configure `NetworkNpcTargetSelector` and verify it selected a living
  authenticated player.
- Confirm the NPC has no client owner and its movement/AI roots are allowed by
  the authority gate.

### Counter is rejected

- Confirm the replicated window is open and assigned to the local player.
- Check counter radius, visibility, and cooldown.
- Do not use the shield-forced counter route until a server-validated parry
  lease is implemented.
