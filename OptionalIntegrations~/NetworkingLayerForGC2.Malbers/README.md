# Malbers Animal Controller Companion

Optional companion for using the Malbers Animal Controller (MAnimal) and the Malbers Game
Creator 2 integration with the Game Creator 2 Networking Layer.

The main package stays transport-neutral and has no compile-time Malbers reference. This
companion compiles into `Assembly-CSharp` behind the automatically managed
`ARAWN_GC2_MALBERS` scripting define, exactly like the optional Fusion Advanced KCC companion.

## Requirements

- Game Creator 2 Networking Layer 2.4.5 or newer.
- Malbers Animal Controller.
- Malbers Game Creator 2 integration (`Assets/Malbers Animations/Integrations/GameCreator2`).
- A GC2 `Character` and Malbers `MAnimal` on the same root GameObject.

## Installation

1. Keep this folder beside the main package at
   `Assets/Arawn/NetworkingLayerForGC2.Malbers`.
2. Let Unity compile Malbers and the Malbers GC2 integration.
3. Open the project once. `GC2NetworkingDefineSymbols` detects
   `MalbersAnimations.Integrations.GameCreator2.UnitDriverMAnimal` and enables
   `ARAWN_GC2_MALBERS` for the active build target.
4. Use the Malbers GC2 setup wizard as usual, then add a `NetworkCharacter` to the same
   character.

`NetworkCharacter` automatically attaches `NetworkMalbersMotorBridge` and
`NetworkMalbersStateBridge` when it finds `MAnimal` and the optional bridge assembly. You can
also add either component manually to a prefab; both are idempotent. `NetworkMalbersStateBridge`
provisions a `NetworkMalbersStateManager` when the scene has none; disable `Create Manager If
Missing` when your own bootstrap owns manager creation.

If a bridge does not attach, verify that `ARAWN_GC2_MALBERS` appears under
`Project Settings > Player > Scripting Define Symbols` for the active build target, that this
folder still has no `.asmdef`, and that the Malbers Game Creator 2 integration compiled.

## Setup wizard

`Game Creator > Networking Layer > Scene Setup Wizard` reports this integration under
**Optional Integrations**. It generates nothing for Malbers, because the bridges and the state
manager provision themselves at runtime as described above.

The wizard, both transport scene setup wizards
(`GC2SceneSetupShared.ConfigureNetworkReadyCharacterKernel`), and the `NetworkCharacter` menu
tools deliberately skip the GC2 kernel rewrite for a Malbers character. The validated Malbers
demos author `UnitDriverMAnimal`, `UnitFacingMAnimal`, and `UnitAnimimMAnimal`, and
`NetworkCharacter` swaps in the role driver (server, owning client, remote) at runtime;
replacing those units only detaches MAnimal from locomotion, facing, and the Animator. The wizard
records the skip in its setup report warnings.

If the status is wrong after importing or removing Malbers, use **Refresh integration detection**
in the wizard; it re-runs `GC2NetworkingDefineSymbols.RefreshNow()`.

## What the bridge does

| Network role | Active GC2 driver | MAnimal ownership |
| --- | --- | --- |
| Server-authored NPC | Malbers `UnitDriverMAnimal` | Native. MAnimal owns movement, rotation, root motion, and animation. |
| Server player | `UnitDriverNetworkServer` | Presentation. The network driver owns the root; MAnimal animates from driver velocity. |
| Local player | `UnitDriverNetworkClient` | Presentation. Prediction owns the root; MAnimal animates from predicted velocity. |
| Remote player/NPC | `UnitDriverNetworkRemote` | Presentation. Snapshots own the root; MAnimal animates from interpolated velocity. |
| NavMesh NPC | `UnitDriverNavmeshNetworkServer` (authority) / `UnitDriverNetworkRemote` (observer) | Presentation. The NavMesh or remote driver owns the root. |

In presentation mode the bridge:

- disables MAnimal position and rotation writes and root motion;
- makes the root `Rigidbody` kinematic while a network driver owns the transform, and
  registers that body with `NetworkRagdollPhysicsGuard` so a GC2 ragdoll epoch cannot race
  the presentation state;
- disables Malbers input sources (`IInputSource`, which includes `MalbersInput` and
  `MInputLink`) and, by default, AI components (`MAnimalAIControl`, `MAnimalBrain`) on
  replicas;
- feeds the active driver's local-space velocity into `MAnimal.SetInputAxis` every frame and
  reasserts it during `MAnimal.PreInput` so an explicit facing/look-at intent cannot switch
  MAnimal back to rotate-only input. It re-subscribes after `Character.Kernel`
  `EventChangeDriver`, `EventChangePlayer`, and `EventChangeFacing`, moves its own kernel
  handlers to the end as well, and repeats the reassert in `LateUpdate` once per kernel event
  because Malbers facing units move themselves to the end of the `PreInput` invocation list
  on every driver change;
- optionally copies the driver's grounded flag into `MAnimal.Grounded`;
- upgrades a Malbers `UnitPlayerTankMAnimal` to `UnitPlayerTankNetwork` for local owners so
  tank input reaches the network input sink. The authored unit is restored only while this
  bridge still owns the installed network unit and `Character.IsPlayer` still permits local
  input; a unit cleared or replaced by `NetworkCharacter` cleanup is never revived.

When authority returns, the bridge restores the captured MAnimal flags, input components,
Rigidbody state, and any authored Malbers tank player unit. If GC2 is still inside a ragdoll
epoch, the bridge publishes the newly intended owner to `NetworkRagdollPhysicsGuard` before
deferring its own restore. The guard therefore restores the intended Rigidbody state at
recovery even if the bridge is disabled or destroyed first, including when the bridge is
destroyed while still in presentation. Entering presentation during an active epoch adopts the
presentation baseline instead of capturing the suspended body; ordinary adopters preserve an
existing epoch baseline, while presentation re-entry explicitly replaces it. The deferred
restore remains subscribed across a bridge disable or role reset. If a foreign custom or network
driver still owns the root at recovery, the bridge releases its pending bookkeeping and leaves
the guard registration intact; it does not force the authored state into that driver. The
bridge does not replace the authored `UnitDriverMAnimal` on server-authoritative NPCs.

`RegisterAdditionalInputSource(MonoBehaviour)` can register a custom `IInputSource` that is not
a child of the character. Registration applies only while presentation mode is active; the
source is ignored while the character is native or inactive. Parent input sources require
`Suppress Input Sources On Parents`; sibling or completely external input sources should be
registered explicitly.

## GC2 States, Gestures, and native MAnimal state

The Networking Layer's existing `UnitAnimimNetworkController` continues to replicate GC2 States
and Gestures. `UnitAnimimMAnimal` layers those playables over MAnimal's native controller as it
does in single-player.

`NetworkMalbersStateBridge` adds the native Malbers layer that GC2 does not observe:

- active state (`MAnimal.ActiveStateID`), applied with `State_Force`;
- active mode and ability (`ActiveModeID` / `ModeAbility`), applied with
  `Mode_ForceActivate` or `Mode_Stop`;
- stance (`MAnimal.Stance`), applied with `Stance_Set`;
- the grounded flag when the motor bridge does not already own it.

Authority follows the motor bridge: a server NPC broadcasts, an owning client sends to the
server, and the server validates `TryVerifyActorOwnership` before rebroadcasting. Proxies apply
the last accepted snapshot during `LateUpdate`.

State, mode, stance, and ability identifiers are the integer IDs of the Malbers assets. Every
peer must run the same Malbers Animal Controller package with the same ID catalog. A peer with a
different catalog cannot resolve the force and keeps its current state; the Networking Layer
does not add an asset registry or automatically negotiate catalog versions. Protocol changes
must be shipped to every peer at the same time.

The bridge samples on a change and therefore does not stream continuous Malbers speed-set or
free-movement selections. Continuous locomotion continues to come from the network driver.

It also resends the last snapshot every `m_HeartbeatInterval` seconds (default `1.5`, `0`
disables it). Without that heartbeat a peer joining after the last Malbers transition would wait
for the next state change, so late joiners and recovering proxies converge on their own.

Proxy diagnostics are exposed on the bridge: `AppliedSnapshotCount`, `LastAppliedCharacterId`,
and `LastAppliedStateId` prove that a snapshot was applied to a specific actor rather than only
arriving at the process.

## Demo

The optional demos are built from the licensed Malbers GC2 Wolf scene and reference Malbers
assets; they never copy Malbers scripts, models, materials, or animations into this repository:

- `Game Creator/Networking Layer/Demos/Rebuild Fusion Malbers Installer`
- `Game Creator/Networking Layer/Demos/Rebuild PurrNet Malbers Installer`

Each builder creates a player prefab, a server-authoritative NPC prefab, a copied Core demo scene
with the transport session intact, three persistent NPCs, and the `NetworkMalbersDemoHud`. It
exports `Demo/{Transport}/Packages/Malbers/Package.unitypackage` and updates the matching GC2
installer asset. Press `1` on the local authority to cycle the native MAnimal state; every remote
peer must show the same state.

## Limitations

- Player movement is owned by the Networking Layer's server-authoritative/predicted driver.
  MAnimal's native physics and root motion are intentionally not double-applied on those
  replicas; MAnimal provides animation and state presentation.
- Server-authoritative NPCs keep native MAnimal movement only while the authored
  `UnitDriverMAnimal` remains the active driver.
- Malbers input components are disabled on network-owned replicas. Do not rely on a local
  `MalbersInput` or AI component to move a predicted player or a remote replica. AI suppression
  can be disabled per bridge, and non-child input sources can be registered manually; parent
  sources require `Suppress Input Sources On Parents`. Sibling or global input sources are not
  discoverable automatically. Re-enabling a suppressed `MAnimalAIControl` or `MAnimalBrain`
  restarts that component; internal task state is preserved only if the component was never
  disabled.
- The tank-unit upgrade is best effort. If the authored `UnitPlayerTankMAnimal` cannot be
  restored (for example, when `Character.IsPlayer` is false during a remote role or the
  player unit was replaced by cleanup), the authored unit and its custom input binding are
  dropped for the session. The character keeps the generic `UnitPlayerTankNetwork` default
  `InputValueVector2MotionPrimary` binding thereafter. A successful restore re-runs the
  authored unit's `OnStartup`, which assumes Malbers' `InputPropertyValue` startup is
  re-entrant after `OnDispose`.
- The missing-bridge warning can be disabled on `NetworkCharacter` when the optional Malbers
  companion is intentionally omitted.
- Malbers mount/dismount root placement is local presentation and is not replicated by this
  companion. A rider needs a dedicated networked mount/parenting flow.
- `UnitFacingMAnimal` / `UnitFacingTankMAnimal` remain local presentation units. They do not
  implement `INetworkFacingUnit`; replicated root yaw comes from the active network driver.
  Use a network facing unit when aim yaw must be server-authoritative.
- Moving platforms: MAnimal's `PlatformMovement` writes the root outside its
  `DisablePosition` guard. The bridge emits a one-time warning when MAnimal detects a platform;
  do not rely on Malbers platform carry on presentation replicas.
- Unknown custom drivers are left at MAnimal native ownership (`Inactive`). Derive from a
  Networking Layer driver or use `UnitDriverMAnimal` to opt into automatic presentation.
- Presentation input is scaled by `Character.Motion.LinearSpeed`. Walk/trot/sprint and strafe
  animation speed mapping is approximate because Malbers speed-set selections are not
  replicated.
- `m_SynchronizeGrounded` is off by default. Remote presentation normally relies on MAnimal's
  own ground probing; enable it only when the replicated driver flag is the desired authority.
- This companion does not support mixing Malbers with the optional Advanced KCC driver on the
  same character. Choose one movement backend.

## Assembly layout

Do not add an `.asmdef` to this folder. The bridge must compile into `Assembly-CSharp` so it can
reference Malbers assemblies that the main package does not own. Every source file is guarded by
`#if ARAWN_GC2_MALBERS`. `Runtime/link.xml` preserves the bridge type for IL2CPP builds because
`NetworkCharacter` locates it by name rather than a direct assembly reference.

## Validation

- EditMode tests live in
  `Assets/Arawn/NetworkingLayerForGC2/Tests/NetworkMalbersIntegrationTests.cs`,
  `Assets/Arawn/NetworkingLayerForGC2/Tests/NetworkMalbersStateSyncTests.cs`,
  `Assets/Arawn/NetworkingLayerForGC2/Tests/MalbersExamplesInstallerTests.cs`, and
  `Assets/Arawn/NetworkingLayerForGC2/Tests/NetworkRagdollPhysicsGuardTests.cs`.
- The focused tests cover role/driver policy, local-space axis conversion, clamping,
  suspension, the define guard, the absence of a main-package Malbers reference, root
  Rigidbody ragdoll registration/restoration, mid-epoch intended-baseline adoption,
  source-level lifecycle hardening (`EventChangeDriver` / `EventChangePlayer` /
  `EventChangeFacing` re-priority, stale tank-unit restore guard, instance-aware driver
  refresh, deferred Rigidbody restore, and input/AI suppression options), the
  `NetworkMalbersStateManager` authority/broadcast contract, Fusion/PurrNet transport wiring,
  the generated demo installer contents, and `SetupTools_KeepTheMalbersCharacterKernel`,
  which fails if any setup tool ever starts replacing Malbers kernel units again.
- `.github/workflows/unity-malbers-tests.yml` runs the `GC2Networking.Malbers` category with a
  minimum passed count and both required suites on a runner that has Malbers installed.
- These are compile/source-contract tests. Unity EditMode, Play Mode, IL2CPP, authority
  migration, and multi-peer behavior still require an isolated Unity project.
