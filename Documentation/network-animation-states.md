# Network GC2 Animation States With PurrNet

> **Audience:** Game Creator 2 + PurrNet integrators using `UnitAnimimNetworkController`
> or the network visual scripting instructions for replicated character states.

## TL;DR

Use the networking instructions when a Game Creator 2 State must replicate:

- `Network/Characters/Animation/Network Enter State`
- `Network/Characters/Animation/Network Stop State`

Do not use the stock GC2 `Characters/Animation/Enter State` or
`Characters/Animation/Stop State` instructions for synchronized multiplayer
state changes. Those instructions call `Character.States.SetState(...)` and
`Character.States.Stop(...)` directly. That is valid for local-only gameplay, but
it bypasses the Game Creator 2 Networking Layer and no PurrNet packet is sent.

The network instructions route through `UnitAnimimNetworkController`,
`NetworkAnimationManager`, and `PurrNetAnimationMotionTransportBridge`, so the
owner/server request can be validated and the same State command can be applied
to remote replicas.

If a state appears to enter and then immediately exit, check the active GC2 state
layers before assuming a stop command was sent. GC2 states on higher layers can
visually override lower-layer states. For example, a full-body `Sit` state on
layer `1` can be hidden by an already-active weapon state such as `Sword_Normal`
on layer `5`.

## PurrNet Setup

For PurrNet projects, create the scene setup from:

`Game Creator > Networking Layer > PurrNet Scene Setup Wizard`

The wizard creates or reuses the required runtime pieces:

- `NetworkAnimationManager`
- `NetworkMotionManager`
- `PurrNetAnimationMotionTransportBridge`
- `PurrNetTransportBridge`
- `NetworkCharacter` and `PurrNetNetworkCharacterAuto` on prepared player
  prefabs

The Player Prefab should be prepared on the wizard **Scene** page. If the State
uses Animation Clips, Runtime Animator Controllers, or State assets that are only
referenced from visual scripting assets, register them on the player prefab or in
the relevant runtime registry so every peer can resolve the same stable hash.

Network State instructions are transport-agnostic. In a PurrNet scene, the
PurrNet animation/motion bridge is the transport adapter that carries those
commands.

## Use The Network Instructions

Add these entries in GC2 Instruction Lists through the Unity Inspector:

| Title | Category | Use |
| --- | --- | --- |
| `Network Enter State` | `Network/Characters/Animation/Network Enter State` | Starts a GC2 State and sends the command through the active network animation bridge. |
| `Network Stop State` | `Network/Characters/Animation/Network Stop State` | Stops a replicated GC2 State layer through the active network animation bridge. |

These instructions support GC2 `StateData` sources:

| StateData source | Notes |
| --- | --- |
| `AnimationClip` | Good for one-off animation states. Register clips that are only referenced from visual scripting. |
| `RuntimeAnimatorController` | Good for controller-driven temporary states. Register controllers consistently on all peers. |
| `State` | Good for authored GC2 State assets and layered state behavior. Ensure the same State asset exists and can be resolved on every peer. |

For local-only state changes, the stock GC2 instructions are still fine.

## Authority Rules

Network State instructions should run on the local owner or on the server.
Remote replicas skip owner-only requests so they do not create duplicate or
conflicting animation commands.

Recommended pattern:

1. Put input-driven state changes behind `Is Network Owner` or
   `Is Local Network Player`.
2. Put server-driven state changes behind `Is Network Server` when the server is
   deciding the state.
3. Avoid running the same state instruction from both local input and a remote
   replica Event.

If a network instruction appears to do nothing, first check that the selected
Character resolves to the owned `NetworkCharacter`, not a remote clone or a
scene object without network character ownership.

## Root Motion

`Network Enter State` exposes the GC2 State configuration, including root motion.
Enable root motion on the network instruction when the State animation should
move the character.

Root-motion movement is still owned by the GC2 networking motion layer. Do not
add a PurrNet `NetworkTransform` to the same player root managed by
`NetworkCharacter`; that creates two systems writing position and rotation.

Typical root-motion state examples:

- launch or knock-up states
- roll, dodge, or dash-like animation states
- sit, sleep, emote lock, or cutscene positioning states
- hit reactions that move the character root

If the animation plays but motion is missing or too small, check:

- the `Root Motion` option on the network instruction
- the clip import/root motion settings
- whether another state layer or locomotion state is overriding the pose
- whether motion reconciliation is correcting against a server state that never
  received the same root-motion command

## Local Fallback

The network instructions include optional local fallback behavior for offline or
incomplete scenes. This is useful while editing a single-player test scene, but
it does not replicate anything by itself.

If fallback is the path being used in a PurrNet session, the scene is missing the
network animation setup, the `NetworkCharacter`, or the `UnitAnimimNetworkController`.
Fix the setup rather than relying on fallback.

## State layers matter

GC2 animation states are stacked by layer. Higher layer numbers are evaluated
above lower layer numbers. If a higher layer has weight `1.000`, it can make a
lower state look like it ended even though the lower state is still active.

Typical symptom:

1. A state asset such as `Sit` has an entry clip, for example `State@Sit_Enter`.
2. The entry clip plays as a gesture, so the character visibly starts sitting.
3. The entry gesture completes.
4. A higher state layer, such as an equipped weapon stance, visually takes over.
5. It looks like `Sit` stopped, but GC2 still has `Sit` active underneath.

Example diagnostic output:

```text
[NetworkStateDebug][AnimimController] PurrNetDemoPlayer:
observed GC2 States activeCount 1->2
layers=[layer=1 count=1 latest=Sit exiting=False complete=False weight=0.000;
        layer=5 count=1 latest=Sword_Normal exiting=False complete=False weight=1.000]
```

Later samples can show the state still active:

```text
[NetworkStateDebug][AnimimController] PurrNetDemoPlayer:
observed GC2 state sample activeCount=2 activeFor=10.058s
layers=[layer=1 count=1 latest=Sit exiting=False complete=False weight=1.000;
        layer=5 count=1 latest=Sword_Normal exiting=False complete=False weight=1.000]
```

In this case the network state did not exit. It is still active at full weight,
but the higher layer can override the final visible pose.

## Fixing layer conflicts

Pick one of these approaches:

1. Put the full-body state on a higher layer than active weapon/combat states.
   If `Sword_Normal` uses layer `5`, play `Sit` on layer `6` or higher.
2. Stop the higher state layer before entering the full-body state.
3. Use avatar masks so the higher state only owns the body parts it should
   control.
4. Reserve layer ranges by purpose, for example:

| Layer range | Suggested use |
| --- | --- |
| `0` | Base locomotion |
| `1-4` | Additive or partial-body states |
| `5-9` | Combat/weapon stances |
| `10+` | Full-body override states such as sit, sleep, cutscene, emote locks |

The exact numbers are project-specific. The important rule is that full-body
states should not sit below an unrelated full-weight state that owns the same
bones.

## Common Patterns

For a networked emote or sit state:

1. Use `Network Enter State`.
2. Select the `State` asset or clip.
3. Use a layer above active combat/weapon states if the state is full-body.
4. Use `Network Stop State` when the character should leave the state.

For a networked launch, knock-up, or hit reaction:

1. Trigger the reaction through the networked combat flow when possible.
2. If a custom Instruction List drives it, use `Network Enter State`.
3. Enable root motion when the State animation is supposed to move the target.
4. Keep the target under `NetworkCharacter` motion authority.

For local-only UI or first-person camera presentation:

1. Use the stock GC2 State instruction only if the state should never replicate.
2. Guard it with `Is Network Owner` so remote replicas do not run local-only
   presentation effects.

## Reading the state debug logs

Relevant log prefixes:

- `[NetworkStateInstruction]` - visual scripting instruction request and chosen state.
- `[NetworkStateDebug][AnimimController]` - local GC2 state lifetime, layer stack, lookup, and apply results.
- `[NetworkStateDebug][AnimationManager]` - routing, ownership validation, and broadcast application.
- `[NetworkStateDebug][PurrNetAnimationMotionBridge]` - PurrNet packet send/receive boundaries.

If the network layer sends a stop, you will see `stop-state` or `StopState` logs.
If no stop appears and the state remains `exiting=False complete=False`, the state
is still active and the issue is usually layer priority, masks, or the state
controller contents.

## Common Problems

- State plays only on the local player: replace the stock GC2 State instruction
  with `Network Enter State`.
- Stop only affects the local player: replace the stock GC2 Stop State
  instruction with `Network Stop State`.
- Instruction is skipped: the selected Character is a remote replica or does not
  resolve to an initialized `NetworkCharacter`.
- Remote peer cannot resolve the State: register the clip, controller, or State
  asset consistently on all peers.
- State looks like it ended immediately: check higher GC2 state layers and avatar
  masks before assuming a network stop was sent.
- Root motion does not move the character: enable root motion on the network
  instruction and avoid duplicate transform sync components.
- State works in a manual scene but not a generated PurrNet scene: compare the
  player prefab against the PurrNet wizard output and confirm
  `PurrNetAnimationMotionTransportBridge` exists in the session.

## Related Docs

- [PurrNet Quickstart](purrnet-quickstart.md)
- [PurrNet Visual Scripting API](purrnet-visual-scripting-api.md)
- [Network Animation Clip Registration](network-animation-clip-registration.md)

## Related files

- [UnitAnimimNetworkController.cs](../Runtime/Animation/UnitAnimimNetworkController.cs)
- [NetworkAnimationManager.cs](../Runtime/Animation/NetworkAnimationManager.cs)
- [PurrNetAnimationMotionTransportBridge.cs](../Runtime/Transport/PurrNet/PurrNetAnimationMotionTransportBridge.cs)
- [InstructionNetworkCharacterEnterState.cs](../VisualScripting/Instructions/InstructionNetworkCharacterEnterState.cs)
- [InstructionNetworkCharacterStopState.cs](../VisualScripting/Instructions/InstructionNetworkCharacterStopState.cs)
