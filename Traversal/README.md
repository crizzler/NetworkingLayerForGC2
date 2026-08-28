# Network Traversal Module (GC2 Traversal)

This module adds server-authoritative networking for **Game Creator 2 Traversal** (`TraverseLink`, `TraverseInteractive`, `TraversalStance`).

Compile symbol: `GC2_TRAVERSAL` (auto-enabled when `com.gamecreator.traversal` is present).

Supported source lines: Game Creator 2 Traversal `2.0.x` and `2.1.x`.

## Required Authority Patch

The Traversal source patch is mandatory for networked Characters. The transport
fails closed when its hooks are absent so a missing controller or route cannot
silently fall through to local-only GC2 traversal.

Patch menu:

- `Game Creator > Networking Layer > Patches > Traversal > Patch (Server Authority)`

The PurrNet setup wizard applies and validates the compatible patch. It also
reports an actionable error after a GC2 Traversal update changes the expected
source signatures. Characters without `NetworkCharacter` remain ordinary local
GC2 Characters and do not require a network route. Patch `2.9.0-traversal`
also rejects superseded starts after an async GC2 yield, routes authored
`ContinueA`/`ContinueB` edges before native transitions, supplies the
presentation-safe local-owner snapshot motion loop, and guards
`Traverse.RefreshCollisions` against destroyed or malformed ignored-collider
references.

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

A live `EnterTraverseInteractive` broadcast now creates that same presentation
shell on a remote observer. It must not run a second `MotionInteractive` loop:
the observer has no owner input, and its synthetic position/direction writes can
race the replicated owner pose and blend parameters. Keeping live entry and
late-join/reconnect restoration on one lifecycle is particularly important for
Ledge Rail Climb: a clamped right edge requires `Speed-XY = 0` and
`Intent-X = +1` to select **Edge Right** instead of **Move Right**.

When a live remote observer changes from one `TraverseInteractive` to another,
the presentation shell locally selects and plays GC2's authored source-exit and
target-enter gestures with root motion disabled. It does not call
`Traverse.ChangeTo`, does not run Traverse/Motion instruction lists, and does
not start a second input loop. The movement transport remains the only writer
of the observer's root pose, so Fusion/PurrNet interpolation supplies the
physical ledge-to-ledge movement while the local gesture supplies the authored
visual transition.

Authority may internally clear A before asynchronously entering B. That
intermediate detach is not a durable network state: its fallback snapshot is
deferred through the replacement frame and cancelled when B enters. A genuine
detach still publishes immediately after that bounded debounce. This preserves
the source identity required to choose A's exit gesture without delaying the
authoritative B broadcast.

On a Fusion Host, or for a player locally owned by the Shared authority, GC2
writes each eased transition root before Fusion captures it into native state.
Those samples are accepted only when the actor is simultaneously authority,
authenticated local player owner, and the local Traversal controller.

A joining Shared player's transition takes the inverse path: its input RPC is
accepted only when Fusion identifies the real sender as that actor's admitted
logical owner. The Shared authority stamps that `PlayerRef` onto the queued
sample and exposes a non-networked authenticated-application scope only around
the matching simulation call. Traversal accepts an eased pose within that
scope only for an authenticated `PlayerOwned` server replica. Ordinary Host
clients, unauthenticated actors, and NPCs cannot use it. Reliable owner samples
may arrive after authority's local GC2 replica has already cleared
`InInteractiveTransition`, so a bounded authorization retains the exact request
correlation, target traverse, and authored start-to-target root corridor. The
server owner-motion operation must still match that correlation, finite-value
and reconciliation-distance checks remain in force, and unrelated or
off-corridor samples remain constrained to the authored traversal surface.
Draining transition A closes A's broad exit grace before transition B opens its
own window, so the previous operation cannot authorize the replacement gap.
Validation alone does not consume the authorization: only a target-root sample
committed by the backend clears it through `NotifyPositionAccepted`; replacement,
exit, and timeout also clear it. Accepted intermediate or delayed samples leave
GC2's destination `TraversalStance.RelativePosition` unchanged until the
transition completes.

If a periodic full snapshot for the new hold arrives before its matching live
broadcast, the observer retains the selected transition presentation for a
short bounded window and consumes it only when that exact version and traversal
identity arrives live. A snapshot by itself never replays an historical
transition for a late joiner.

Persistent traversal snapshots still update a remote shell's stable traverse
identity and relative stance pose, but they never call `SetPosition(..., true)`
on a remote observer. A traversal snapshot teleport would race the same root
already replicated by Fusion or PurrNet and turn an otherwise smooth connection
into a snap. An authenticated local owner restoring durable state may still use
the snapshot root as a fallback; late-joining observers converge through normal
transport pose replication.
On curved/authored rails the retained priority-9 direction may contain both
Traverse-local Y and Z components. At authored boundary A or B, an outward Z
component takes precedence over a slightly larger Y component; otherwise a
remote observer can mistake held-right input for **Edge Forward**.

If an authoritative GC2 traversal task faults after it has already entered its
stance, the controller recovers only the exact traverse/token generation owned
by that task. It invalidates the matching pending enter and clears the
half-entered snapshot state; a stale completion cannot cancel a newer
traversal. This is a defensive recovery path for third-party instruction,
animation, or scene failures and does not turn a failed action into an accepted
request.

## Continuous motion and Host traffic

GC2 interactive motions call priority-9 `MoveToDirection` every render frame.
The authoritative local server or Host still applies that direction locally on
every call, but identical semantic network commands are coalesced. Direction
changes and stop/start transitions send immediately, and sustained motion uses
a bounded heartbeat. This keeps Free Climb, Cover, and other continuous
Traversal motions responsive without allowing Host-only reliable command
traffic to starve Fusion pose snapshots or repeatedly restart observer blends.

This changes transport traffic only. It does not extrapolate traversal poses,
add another transform writer, or bypass request, sender, or owner validation.

## Interactive traversal and other network characters

GC2 interactive Traversal clamps a character to its authored ledge, rail,
ladder, cover, or climb surface and applies that result as an absolute root
pose. After the server has accepted the traversal and opened its bounded owner-
motion window, Fusion Native preserves those same absolute semantics when it
records authoritative state. It must not sweep the authenticated pose a second
time through `CharacterController.Move`: another player occupying the same rail
could stop only the authority copy while the owner's authored pose continues,
leaving observers behind on a Move animation when the owner has reached Edge.

This exception is scoped to an active, validated interactive traversal. Normal
locomotion and non-authorized owner poses remain collision-constrained. Player
controllers and colliders also remain enabled and queryable, so Shooter, Melee,
lag compensation, triggers, and world collision keep their normal behavior.
Authority reconstructs the GC2 Crown/Center/Feet anchor for every submitted
interactive pose and requires it to remain within 2 cm of the authored local
plane, width, and Position A/B bounds. Off-surface poses and malformed authored
bounds fail closed before the absolute-root allowance is evaluated.

## Traversal Examples 1.0.1

Fusion and PurrNet Traversal Examples `1.0.1` restore the authored nested wall
under the Climb demo's `Cover_High`. Version `1.0.0` removed that prefab child
while `Traverse.m_IgnoreColliders` still referenced its collider, so authority
could fault during the `Cover_Low` to `Cover_High` continuation and leave a
character half-entered. Import the current installer rather than preserving a
locally modified `1.0.0` demo folder.

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

## Automated Fusion traversal smoke

Use an isolated two-process smoke for Fusion traversal changes. Supply licensed
pristine GC2 sources either with `--pristine-gc2-root` or the equivalent
`GC2_NETWORK_PRISTINE_ROOT` environment variable; the runner overlays those
sources before applying the current patches. Keep the Photon application ID in
the local environment or private CI secret.

```bash
GC2_NETWORK_PRISTINE_ROOT=/path/to/pristine-gc2 \
GC2_NETWORK_SMOKE_PHOTON_APP_ID=... \
  Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --topology fusion-host-climb

GC2_NETWORK_SMOKE_PHOTON_APP_ID=... \
  Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root /path/to/pristine-gc2 \
  --topology fusion-host-cover

GC2_NETWORK_SMOKE_PHOTON_APP_ID=... \
  Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root /path/to/pristine-gc2 \
  --topology fusion-host-pullup

GC2_NETWORK_SMOKE_PHOTON_APP_ID=... \
  Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root /path/to/pristine-gc2 \
  --topology fusion-host-ledge-transition

GC2_NETWORK_SMOKE_PHOTON_APP_ID=... \
  Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root /path/to/pristine-gc2 \
  --topology fusion-shared-ledge-transition

GC2_NETWORK_SMOKE_PHOTON_APP_ID=... \
  Tools/run_unity_network_smoke.sh \
  --unity /path/to/Unity \
  --pristine-gc2-root /path/to/pristine-gc2 \
  --topology fusion-host-ledge-reconnect
```

`fusion-host-climb` enters the real `Free_Climb`, climbs, cancels, falls, and
checks both observer presentation and the connected owner's input. The
`fusion-host-cover` topology follows the exact `Cover_Low` to `Cover_High`
continuation, detaches, verifies post-cover movement and jumping on both peers,
and fails on an authoritative traversal-task exception or destroyed-collider
error. `fusion-host-pullup` repeats the exact root `Free_Climb -> PullUp`
connection three times and rejects a canceled outgoing edge pose, pre-entry
rollback, ground teleport, air launch, snap-back, duplicate TraverseLink
animation/lifecycle events, endpoint divergence, or loss of connected-owner
input during Host traversal. It treats the first `MotionLink` warp point and
the clip's grounded platform landing as distinct phases, verifies the Host root
is aligned when GC2 reaches boundary B, acknowledges every attempt from the
observer, and compares the final Host/observer poses. The traversal harnesses
run at an authored 60 FPS and write peer logs and JSON results beneath the
isolated `.unity-ci` project.

`fusion-host-ledge-transition` drives the Host through the exact center-low,
center-middle, and center-top `Ledge_Hold_Climb` fixtures using two normal GC2
`TryJump` connections. The connected peer requires the authored exit and enter
gesture presentation for both changes, at least eight distinct intermediate
transport-pose samples per segment, no material reverse correction or
frame step above 0.25 m, the actual `Traverse@Climb_ExitU` then
`Traverse@Climb_EnterD` start order twice, stable Fusion teleport keys after
source settle, final target convergence, and independent movement of its
authenticated local owner. The harness samples Fusion's last committed Render
pose, avoiding the phase-dependent child transform seen between simulation and
`onBeforeRender`. GC2's short authored target-alignment pre-roll is excluded
only from rollback classification before forward travel begins; every absolute
frame step and every later reverse correction remains subject to the same
strict limits.

`fusion-shared-ledge-transition` exercises the opposite ownership direction.
Player B joins and performs the same two `TryJump` connections while the player
who created Shared observes B. The smoke waits for both Player B's local
snapshot acknowledgement and the Shared authority's admitted-client readiness,
then correlates every coordination marker with B's network ID. It requires two
authenticated non-continuous Shared pose applications, the same authored
`ExitU -> EnterD` pairs and intermediate-sample limits, stable teleport keys,
zero reverse or large-teleport corrections, no frame step above 0.25 m, final
target convergence, no owner-pose validation failure, and uninterrupted
movement of the Shared creator's own local character. The authored clip-pair
trace is emitted on Player B's actual authority-side
`EnterTraverseInteractive` route before `Traverse.ChangeTo`. Rejection checks
begin only after `observer-source-ready`: a stale setup sample may correctly
fail closed while source attachment negotiates readiness, but neither measured
transition may be rejected.

`fusion-host-ledge-reconnect` first attaches the connected owner's player at a
fixed mid-rail point, starts the Host to its left, and requires both processes
to prove that the Host crossed the occupied capsule and reached Edge Right
without moving the blocker. It then disconnects that client and preserves the
existing fresh-process, snapshot-first reconnect proof. This topology catches
both the dynamic-player collision divergence and live-versus-snapshot edge
animation lifecycle regressions.

## Opt-in Fusion traversal diagnostics

Fusion Host/Client climbing and cover diagnostics are available only in the
Unity Editor or a **Development Build**. They are dormant until explicitly
enabled and are compiled out of non-development customer players.

Launch both peers from the same clean Development Build and give each process a
separate log file:

```bash
./Game.x86_64 --gc2-network-ci-trace --gc2-network-trace-role host \
  -logFile Host-Climb.log
./Game.x86_64 --gc2-network-ci-trace --gc2-network-trace-role client \
  -logFile Client-Climb.log
```

An opted-in Development player is capped to 60 FPS automatically while this
trace is active. This prevents two local players and verbose logging from
starving Fusion's packet/interpolation pumps. Use
`--gc2-network-trace-frame-rate 120` to select another controlled value from
30–240 FPS, or pass `0` to deliberately disable the diagnostic cap. The trace
does not change Editor frame pacing and all frame-pacing code is absent from a
non-development customer player.

Reproduce the problem, then keep trying to move the connected player for at
least ten seconds after leaving the wall before closing either peer. Attach the
complete Host and Client logs from the same session. The trace remains active
for 30 seconds after the final climb ends so exit and recovery failures remain
visible.

The correlated `[GC2NetworkCI]` channels distinguish:

- `traversal-window` and `traversal-state`: climb entry, exit, actor, request,
  correlation, and authoritative state lifecycle;
- `fusion-runner`: Unity-frame heartbeat, Fusion tick/input tick, readiness,
  local player mapping, and explicit tick-stalled/tick-recovered events;
- `fusion-input`: owner payload collection, rejected/missing endpoints, and
  authority-side missing input;
- `gc2-player-input`: local `IsPlayer`, controllable, `ShortcutPlayer`, raw
  input, selected driver/input sink, and the exact reason input was blocked;
- `fusion-native-tick`: State/Input Authority simulation, consumed input,
  owner pose, native/engine pose, velocity, and pending external writes;
- `fusion-native-pose`: observer snapshot endpoints, interpolation alpha,
  teleport keys, render writer delta, later root mutations, and explicit
  `snapshot-underrun-entered`/`snapshot-underrun-recovered` transitions.

Fusion Native presents every remote character through a safe visual-only child
when one is available. This keeps the `CharacterController` root on the current
trusted tick on both a Host and a connected observer, so the two directions do
not use different visible interpolation strategies. Prefabs without a safe
visual child retain Character-root interpolation on non-authoritative peers; an
authoritative Host root is never moved onto Fusion's past render timeline.

Fusion Native also protects ordinary customer presentation when a remote
Traversal proxy temporarily reaches the newest authoritative snapshot before a
second interpolation endpoint is available. It keys that detection to the
Traversal flag in the same Fusion snapshot pair, retains it briefly through the
detach/gravity handoff, decays a render-only error toward each received
snapshot, and hands back to Fusion interpolation when the pair recovers. It
never extrapolates from velocity or changes request/authority validation. The
visual error is never fed back into simulation. During active or lingering
Traversal continuity, a non-teleport sample inside the normal reconciliation
envelope advances the visible root by at most 0.2 m per presentation update and
retains the remaining offset for later convergence. A first pose, ordinary
non-Traversal motion, a real teleport boundary, or an out-of-envelope sample
bypasses that presentation-only cap. For ordinary proxies outside Fusion's
local simulation set, the hidden Character root is restored only to the newest
trusted `NativeState` while the visual child remains interpolated.

Do not enable the trace for routine play or performance measurements. The
extra sampling is diagnostic evidence, not gameplay behavior.
