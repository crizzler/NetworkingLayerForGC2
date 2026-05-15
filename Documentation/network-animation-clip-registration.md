# Network Animation Clip Registration

> **Audience:** Game Creator 2 + PurrNet integrators using `UnitAnimimNetworkController`
> for replicated animation instructions such as Network Dash, Network Enter
> State with AnimationClip sources, and Network Gesture.

## TL;DR

If a networked instruction plays an `AnimationClip` that is **only referenced by
the instruction asset itself** (not by the character's Animator graph or by
`Character.States` / `Character.Gestures` data already present on the prefab),
**every receiving peer must be able to resolve the clip's hash to the actual
`AnimationClip` asset**. Otherwise the animation does not play on remote
representations of the dashing character (the position movement still works,
but the body animates as if no gesture were issued).

You have three setup options. Pick the wizard path for PurrNet player prefabs:

1. **(Recommended for PurrNet player prefabs)** Open `Game Creator > Networking Layer > PurrNet Scene Setup Wizard`, go to **5 Scene**, enable the network instruction animation clip option, and add the clips to the wizard's pre-registered clip list. When prefab preparation is enabled, the wizard writes those clips to the prepared player's `NetworkCharacter` / `UnitAnimimNetworkController` setup.

2. **(Recommended for manual prefab-local clips like the dash gesture)** Drop the clip
   into the new `Pre Registered Clips` array on the character prefab's
   `UnitAnimimNetworkController`. Same prefab is instantiated on every peer, so
   every peer can resolve the hash without any further plumbing.

3. **(Recommended for clips shared across many prefabs)** Author a
   `NetworkAnimationRegistry` (`Create > Game Creator > Network > Animation
   Registry`) ScriptableObject, add the clip there, and assign the registry to
   the controller's `Animation Registry` field. Every peer that loads the same
   project ships the same registry, so every peer can resolve the hash.

## What you'll see when this is broken

Receiver-side log (host or remote, whichever is *not* the dasher):

```
[NetworkDashDebug][AnimationManager] applying gesture broadcast
    character=3 controller=PurrNetDemoPlayer 14(Clone) local=False
    clipHash=1976926865 …
[NetworkDashDebug][AnimimController] PurrNetDemoPlayer 14(Clone):
    ApplyGesture failed: clip hash 1976926865 not found.
    registry=null localCache=0
```

Visually: the character translates correctly across the screen (the motion
broadcast and predictive-snapshot pipeline works), but no dash animation plays
on top of the slide. On the dasher's own peer everything looks correct because
that peer has the clip locally (via the instruction asset).

## Why this is necessary for Network Dash, Network Enter State, and gestures

`InstructionNetworkCharacterNavigationDash` (in
`Assets/Arawn/NetworkingLayerForGC2/VisualScripting/Instructions/InstructionNetworkCharacterNavigationDash.cs`)
plays the dash gesture on the local peer and also broadcasts the gesture
through `UnitAnimimNetworkController.PlayGesture` so the same animation plays
on remote peers. The replicated gesture packet is intentionally **slim**: it
carries only `clipHash = StableHashUtility.GetStableHash(clip)`, the
`ConfigGesture` (durations, blend params), and a `BlendMode` — **not** the
clip asset itself.

That hash is resolved on the receiver in `UnitAnimimNetworkController.TryGetClip`:

```
private bool TryGetClip(int hash, out AnimationClip clip)
{
    if (m_ClipCache.TryGetValue(hash, out clip)) return true;
    if (m_AnimationRegistry != null &&
        m_AnimationRegistry.TryGetEntry(hash, out var entry))
    {
        clip = entry.Clip;
        m_ClipCache[hash] = clip;
        return true;
    }
    return false;
}
```

If the clip exists in neither `m_ClipCache` (populated by `RegisterClip` and by
`DiscoverAnimationClipsFromCharacter`) nor in a `NetworkAnimationRegistry`, the
hash cannot be turned back into an `AnimationClip` and the gesture is dropped.

The crucial detail is that the dash clip is **referenced from the instruction
asset's `m_DashAnimation` field**. That serialized reference exists only on
the peer that runs the instruction, so the dasher peer's controller picks the
clip up via `RegisterClip(animationClip)` inside the instruction's
`Run(...)`. Receivers never deserialize that instruction asset for that
specific dash invocation, so they never see the clip — they only see the hash.
`InstructionNetworkCharacterGesture` has the same requirement when it plays a
clip that is only referenced by the Instruction List entry.

`InstructionNetworkCharacterEnterState` has the same requirement when its
`StateData` source is an `AnimationClip` referenced only by the Instruction List
entry. The network State packet carries the clip hash and State configuration,
not the clip asset itself.

This is why the new `Pre Registered Clips` field on `UnitAnimimNetworkController`
exists: it lets you opt the prefab itself into knowing the clip ahead of time,
so every peer's instance of the controller has the hash → clip mapping
populated at `Initialize` time before any gesture broadcast arrives.

## Why this is **not** necessary for the Melee `Skill` system

The Melee path replicates skill executions via `NetworkMeleeController` and
`NetworkMeleeManager`, broadcasting a packet that identifies the skill by its
*skill hash* (and weapon hash and combo index), not by an animation clip hash.

When a remote peer receives the skill broadcast it ends up in
`NetworkMeleeController.PlayNetworkSkill(...)` which delegates to
`Character.Skills` and from there to `Skill.cs` itself. `Skill.cs` is a
GC2 asset that already lives on **all peers' disks** (it ships in the
project), and its `m_AnimationClip`, animator, and other animation data are
serialized fields on the asset. The receiver loads the same `Skill` asset
from the same `AssetDatabase` / addressable as the sender and plays the
animation directly from its own deserialized copy of the asset.

In other words:

|                              | Network Dash gesture          | Melee `Skill` |
| ---------------------------- | ----------------------------- | ------------- |
| What the wire packet carries | Clip *hash*                   | Skill *hash* (+ weapon, combo) |
| Where the animation lives    | Inside the instruction asset, **not in the prefab** | Inside the `Skill` asset, **shared by all peers** |
| Receiver resolves animation by | Looking the hash up in a clip cache or `NetworkAnimationRegistry` | Looking the hash up in the skill registry, then reading the `AnimationClip` field directly off the `Skill` asset |
| What can go wrong            | Receiver doesn't have the clip → animation never plays | Receiver doesn't have the skill registered → fails the same way, but the skill asset itself always carries its clip once registered |

So a `Skill`-based attack inherently bundles its animation with the asset that
all peers share. A standalone `Network Dash` instruction does **not** — the
clip reference is parameter-data on the instruction, not on the prefab — which
is why prefab-side or registry-side pre-registration is required.

## How to set it up (prefab-local clips)

For each character prefab that should be able to dash on the network:

1. Preferred PurrNet path: open `Game Creator > Networking Layer > PurrNet Scene Setup Wizard`, go to **5 Scene**, assign the Player Prefab, enable **Prepare referenced Player Prefab**, enable the network instruction animation clip option, and add the dash/state/gesture clips.
2. Manual path: open the prefab (the one spawned by your `PurrNetDemoPlayerSpawner` /
   equivalent).
3. Find the `UnitAnimimNetworkController` component.
4. Under **Configuration**, expand **Pre Registered Clips** and drag in:
   - The single clip if your `Network Dash` instruction is configured in
     `SingleAnimation` mode.
   - All four cardinal clips (Forward, Backward, Left, Right) if it is
     configured in `CardinalAnimation` mode.
   - Any AnimationClip used directly by `InstructionNetworkCharacterEnterState`.
   - Any clips used by `InstructionNetworkCharacterGesture`.
5. Save the prefab.

Verification: after re-running the session, the receiver-side log should now
read:

```
[NetworkDashDebug][AnimimController] PurrNetDemoPlayer 14(Clone):
    ApplyGesture playing clip=Sword@Dash hash=1976926865
    duration=0.67 speed=1.00 transitionIn=0.10 transitionOut=0.20
```

instead of the previous `ApplyGesture failed: clip hash … not found`.

## How to set it up (project-shared clips)

For clips reused by many prefabs or by tooling/UI, prefer a registry:

1. `Right-click in Project > Create > Game Creator > Network > Animation Registry`.
2. Add an `AnimationEntry` for each clip with a stable, unique `NetworkId`.
3. Drag the registry asset into every relevant character prefab's
   `UnitAnimimNetworkController.Animation Registry` field.

The registry approach also lets you transmit a compact `NetworkId` (int) when
you author your own commands, instead of the 32-bit stable hash, although the
default gesture command still uses the hash (which the registry resolves
identically via `TryGetEntry`).

## Related files

- [UnitAnimimNetworkController.cs](../Runtime/Animation/UnitAnimimNetworkController.cs)
- [NetworkAnimationTypes.cs](../Runtime/Animation/NetworkAnimationTypes.cs) — `NetworkAnimationRegistry`
- [InstructionNetworkCharacterNavigationDash.cs](../VisualScripting/Instructions/InstructionNetworkCharacterNavigationDash.cs)
- [InstructionNetworkCharacterEnterState.cs](../VisualScripting/Instructions/InstructionNetworkCharacterEnterState.cs)
- [UnitMotionNetworkController.cs](../Runtime/Motion/UnitMotionNetworkController.cs) — role-aware `ApplyDashLocally`
- [UnitDriverNetworkRemote.cs](../Runtime/Motion/UnitDriverNetworkRemote.cs) — `BeginPredictedDash`
