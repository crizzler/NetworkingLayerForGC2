# PurrNet Transport for Game Creator 2 Networking Layer

A concrete `NetworkTransportBridge` implementation that wires the
Game Creator 2 Networking Layer on top of **PurrNet**.

For a compact project setup guide, see
`Assets/Arawn/NetworkingLayerForGC2/Documentation/purrnet-quickstart.md`.

For a manual character-selection setup, see
`Assets/Arawn/NetworkingLayerForGC2/Documentation/purrnet-character-selection-manual-setup.md`.

## Layout

- `PurrNetTransportBridge.cs` — `NetworkTransportBridge` implementation.
- `PurrNetNetworkMessages.cs` — `GC2InputBroadcast` / `GC2StateBroadcast` packets
  (both `IPackedAuto`, auto-serialized by PurrNet).
- `PurrNetDemoCanvasUI.cs` — generated UGUI host/join/disconnect overlay used by
  the PurrNet wizard and demo scenes. Reflectively reads/writes `address` /
  `serverPort` on the active transport (UDP, WebTransport, …), so no transport
  plumbing is needed.
- `PurrNetChatBoxUI.cs` — optional lower-left UGUI chat panel that relays
  player messages through PurrNet reliable broadcast packets. The wizard creates
  the chat Canvas and child controls in the scene so the layout can be styled at
  design time. Because it talks only to `NetworkManager`, it works with direct
  UDP, WebTransport, Local, Steam Relay/SteamTransport, or any other configured
  PurrNet transport.
- `PurrNetHostJoinUI.cs` — older lightweight IMGUI host/join overlay for manual
  scenes that do not want generated UGUI.
- `Arawn.GameCreator2.Networking.Transport.PurrNet.asmdef` — isolated asmdef
  so the core GC2 networking assembly stays transport-agnostic.
- Editor companion: `Assets/Arawn/NetworkingLayerForGC2/Editor/Transport/PurrNet/` contains
  `PurrNetSceneSetupWizard` (menu: `Game Creator > Networking Layer > PurrNet
  Scene Setup Wizard`) and an isolated editor asmdef.

## Scene setup (automated)

Open `Game Creator > Networking Layer > PurrNet Scene Setup Wizard`. It will:

- Create or reuse a PurrNet `NetworkManager`.
- Add the selected transport (UDP / WebTransport / Local / keep existing) and
  write the default address/port into supported transports.
- Create or reuse the core GC2 networking managers:
  `NetworkSecurityManager`, `NetworkCoreManager`, `NetworkAnimationManager`,
  `NetworkMotionManager`, and `NetworkVariableManager`.
- Create or reuse the core PurrNet bridges:
  `PurrNetTransportBridge`, `PurrNetVariableTransportBridge`, and
  `PurrNetAnimationMotionTransportBridge`.
- Create or reuse selected module managers and PurrNet module bridges for
  Stats, Inventory, Melee, Shooter, Quests, Dialogue, Traversal, and Abilities.
- When Shooter is selected, require the GC2 Shooter Sight hook patch before
  setup continues. This patch can also be applied manually from
  `Game Creator > Networking Layer > Patches > Shooter Sight > Patch (Remote Camera Safety)`.
- Optionally create a `NetworkSessionProfile`, `NetworkPrefabs` asset,
  `PurrNetDemoPlayerSpawner`, demo runtime UI, demo controls UI, and a PurrNet
  chat box UI.
- Optionally prepare an assigned Player Prefab with `NetworkIdentity`,
  `NetworkCharacter`, `PurrNetNetworkCharacterAuto`, selected module
  controllers, local variable sync, and pre-registered Network Dash/Gesture
  animation clips.
- Parent generated scene helpers under a single session root for scene hygiene.

The wizard has six pages:

1. **Project** - choose a project template and expected player count. Non-Custom
   templates immediately apply recommended modules, tick-rate, session preset,
   and helper defaults. Custom keeps current manual settings unchanged.
2. **Modules** - choose which GC2 modules should replicate over PurrNet. Core,
   Variables, Animation, and Motion are always included.
3. **Transport** - choose UDP, WebTransport, Local, or existing/manual transport
   setup.
4. **Core** - review generated managers, bridges, NetworkManager settings,
   session profile generation, and Custom session fields.
5. **Scene** - assign and optionally prepare the Player Prefab, create
   NetworkPrefabs, register network instruction animation clips, and choose
   helper UI.
6. **Review** - inspect the final setup before applying it.

## Scene setup (manual)

1. Add a `NetworkManager` (PurrNet) to the scene with its transport of choice
   (UDP, Steam, UTP, WebTransport, etc.).
2. Add `PurrNetTransportBridge`, `PurrNetVariableTransportBridge`, and
   `PurrNetAnimationMotionTransportBridge` to the scene.
3. Add the shared managers used by your project, at minimum
   `NetworkSecurityManager`, `NetworkCoreManager`, `NetworkAnimationManager`,
   `NetworkMotionManager`, and `NetworkVariableManager`.
4. Add each selected module manager and matching PurrNet module bridge
   (`PurrNetMeleeTransportBridge`, `PurrNetShooterTransportBridge`, etc.).
5. Initialize the GC2 managers when PurrNet starts. The bridge components do
   this automatically when configured like the wizard output; custom manual
   setups should follow `Documentation/TRANSPORT_QUICKSTART.md`.
6. Prepare spawned player prefabs with `NetworkIdentity`, `NetworkCharacter`,
   `PurrNetNetworkCharacterAuto`, and selected per-player module controllers.
   Add `NetworkVariableController` only when the prefab uses local GC2
   variables that must synchronize.

## Host / Join overlay

The wizard-created `PurrNetDemoCanvasUI` builds a UGUI panel at runtime with
Host, Join by IP, Disconnect, role, and connection-state controls. This is the
default quick-start UI for direct-connect tests: Player A clicks Host, Player B
enters Player A's LAN/public IP and port, then clicks Join.

The optional `PurrNetHostJoinUI` component remains available for manual scenes
that prefer a tiny IMGUI overlay with no generated Canvas. Replace either helper
with your own menu UI for production builds; both are validation/front-end
starters, not a finished game menu.

## Packet flow

- Client input:
  `SendToServer(characterId, inputs[])` → `GC2InputBroadcast` over
  `UnreliableSequenced` (configurable) → server subscribes and raises
  `OnInputReceivedServer(senderClientId, characterId, inputs)`.
- Server state:
  `SendToOwner(...)` / `Broadcast(...)` → `GC2StateBroadcast` over
  `UnreliableSequenced` → clients raise `OnStateReceivedClient(...)`.

Host-local input is short-circuited: when running as host, the bridge dispatches
input through the server pipeline directly instead of round-tripping the
transport.

## Motion smoothing and localhost tests

Most first tests run as two local game instances connected to `127.0.0.1`.
That setup has very low latency, but remote characters can still look uneven if
the state stream is sampled too sparsely or if snapshots are filtered twice.
The PurrNet demo scene uses the `Duel` session preset and a 60 Hz PurrNet tick
rate so local testing has a high-quality baseline.

For `NetworkCharacter` players, let the GC2 networking driver own motion
presentation. Do not add a PurrNet `NetworkTransform` to the same player root,
because both systems will try to write position/rotation. Use the
`NetworkSessionProfile` to tune `serverStateBroadcastRate`,
`stateApplyRate`, and `interpolationDelay` for your target game.

Owner-side reconciliation corrects the authoritative root immediately and
smooths the visible GC2 model through `UnitDriverNetworkClient`. If a custom
camera follows the character root directly and still shows correction bumps,
offset the camera target using `ReconciliationVisualOffset` while
`IsReconciling` is true, or follow the model/mannequin instead of the root.

Lag compensation is separate from visual smoothing. The
`LagCompensationManager` records historical server-side positions so hitscan,
projectile, melee, and ability hit validation can rewind targets to the time a
client acted. It does not make remote transforms render more smoothly.

## Ownership

- Automatic: if the spawned `NetworkCharacter` (or any parent) has a
  PurrNet `NetworkIdentity`, ownership is read from `identity.owner` and mapped
  via `SetCharacterOwner(networkId, ownerClientId)`.
- Manual: call `SetCharacterOwner(networkId, ownerClientId)` yourself if you
  spawn characters outside PurrNet's ownership system. `PlayerID.id` values
  fit in `uint` in practice, so they can be used directly as
  `ownerClientId`.

## Channels

Defaults are `UnreliableSequenced` for both input and state streams. Flip them
to `ReliableOrdered` from the inspector if your project needs guaranteed delivery
at the cost of latency.

## Module wiring

The PurrNet integration includes bridge components for the core layer and each
supported optional module. When a module is selected in the PurrNet Scene Setup
Wizard, the wizard creates/reuses both the GC2 module manager and the PurrNet
bridge for that module.

Custom transports or hand-written PurrNet setups should still follow the public
transport contract in `Documentation/TRANSPORT_QUICKSTART.md`: wire outbound
manager/controller delegates to transport sends and route inbound packets to the
matching `Receive*` APIs.
