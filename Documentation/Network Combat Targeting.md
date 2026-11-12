# Network combat targeting and facing

Install **Melee Examples** for your transport through Game Creator's Install menu.
The existing Melee/Stats example remains available. The additional closest-target
scene is based on Melee Examples 1.2.8's `10_Target_Closest` arena:

- Fusion: `Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerFusionTransport.MeleeExamples@1.0.0/Requires Melee Examples - FusionTargetClosestDemo.unity`
- PurrNet: `Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerPurrNetTransport.MeleeExamples@1.0.0/Requires Melee Examples - PurrNetTargetClosestDemo.unity`

Start a Host and join from a second player. Move with WASD, press TAB for GC2's
native **Cycle Closest Target** action, or select an NPC or the other player in
the demo panel. R clears selection. Each peer's panel shows the confirmed target
network ID and its observed facing error. The nine scene NPCs are explicitly
server authoritative; each human controls only their own player.

For a custom player, add **Network Combat Target**, use **Network Look at Combat
Target** as the Character's Facing unit, and keep **Network Motion Controller**
as its Motion unit. Populate GC2's candidate list with spawned network Characters
and use the ordinary GC2 selection actions. The new Facing unit reads the target
from that exact Character, including on a dedicated server without a local
`ShortcutPlayer`. Network Pivot alone normally follows movement and does not
provide the stock Look at Target behavior.

Native selection becomes a reliable Core command through the Animation/Motion
transport bridge. Authority verifies the authenticated sender/actor and the
existing Core rate/replay rules, then validates the selected registered actor.
Targets must be living, active server NPCs or authenticated players, must differ
from the selecting actor, and must be within the component's configured maximum
distance (30 metres by default). Authority commits a revision and broadcasts the
confirmed selection; movement transport state carries the resulting yaw. Native
changes on an observer cannot overwrite the confirmed target. Death, despawn,
or leaving range clears selection on authority.

Late-join snapshots include the current target and revision. A receiver retains
an unresolved target ID until that actor spawns. Clears, duplicate deliveries,
and stale revisions are handled without replaying input or combat actions.
This is an opt-in player-targeting component; it does not change Melee damage
validation or Free Flow Combat's PvE restrictions.

The command uses a new `SetCombatTarget` semantic value in the existing motion
packet layout. Update every peer together so every peer understands targeting
and target snapshots. Existing prefabs keep their authored facing behavior.

## Rebuilding and validation

The transport-specific **Rebuild ... Closest Target Melee Installer** menu items
reconstruct the new scene/player alongside the original payload and export the
corresponding `Demo/<Transport>/Packages/Melee/Package.unitypackage`. The Game
Creator descriptors retain the stock Melee, Sword, Stats, and transport-assets
dependencies. The source Melee scene and existing player prefab remain external
fixtures. Run rebuilds in an isolated project. Uploadable installers require a
signed-in Unity 6000.6+ Editor and the Arawn publisher attestation.

The focused EditMode gate extracts the checked-in archives within their exact
install roots without rewriting them:

```bash
Tools/run_unity_editmode_tests.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root /path/to/pristine-project \
  --prepare-targeting-fixtures \
  --test-category GC2Networking.Targeting \
  --minimum-passed 23 \
  --require-suite Arawn.GameCreator2.Networking.Tests.NetworkCombatTargetTests \
  --require-suite Arawn.GameCreator2.Networking.Tests.NetworkTargetingInstallerTests \
  --release-build
```

`Tools/run_unity_network_smoke.sh` supports `purrnet-targeting-host-client`,
`fusion-targeting-host-client`, and `fusion-targeting-shared-two-peer`. Fusion
uses the existing private application-ID environment variable. The probes call
GC2's real `CycleTargets.Closest`, validate both owner directions against NPCs
and the other player, and require matching actor/target/revision tuples plus
root and Animator-facing measurements on both peers. A fresh third process then
verifies the authority player's persisted target and facing without issuing a
selection request. Logs, JSON, and Test
Framework XML remain under the machine-local `.unity-ci` evidence directory.
