# Network Quests

The Quests module syncs selected Game Creator 2 `Journal` quest state through explicit networking profiles and instructions. It does not automatically sync every GC2 quest, because quest visibility, ownership, and sharing rules are usually game-specific.

## Goals

- Keep normal GC2 Quests and Journals for editor compatibility.
- Add networking only for quests explicitly listed in a `NetworkQuestProfile`.
- Support personal quest journals and shared quest states.
- Avoid editing GC2 Quests source code for normal operation.
- Use the same server-authoritative request, response, and broadcast pattern as the other networking modules.

## Components

Add these to the session/network infrastructure:

- `NetworkQuestsManager`
- `PurrNetQuestsTransportBridge` when using PurrNet

Add this to each player Journal that can participate in networked quests:

- `Journal`
- `NetworkQuestsController`
- `NetworkCharacter`

The PurrNet bridge scans for `NetworkQuestsController` components and initializes them based on the active `NetworkCharacter` role.

## NetworkQuestProfile

Create a `NetworkQuestProfile` asset from:

`Create > Game Creator > Network > Quests > Network Quest Profile`

Each binding contains:

- `Quest`: the GC2 Quest asset.
- `Share Mode`: `Personal`, `Global`, or `Party`.
- `Allow Client Writes`: whether clients can request state changes for this quest.
- `Auto Forward Journal Changes`: whether default GC2 Journal changes should be intercepted and sent as network requests.
- `Allowed Actions`: the exact quest/task operations clients may request.

Use one profile per quest authority model. For example, a player can have one profile for personal quests and another shared profile for party or world story quests.

## Share Modes

`Personal` means the quest state belongs to one player's networked Journal. Requests are validated against target ownership.

`Global` means a valid request is broadcast to all matching profiled Journals. This fits Baldur's Gate-style shared party/story quest state.

`Party` marks the request with a shared scope id. The base module carries and validates the scope, and project-specific party membership should be enforced through `NetworkQuestsManager.CustomQuestValidator`.

## Instructions

Use network-specific instructions when a quest state should sync:

- `Network Quest Activate`
- `Network Quest Share`
- `Network Task Complete`
- `Network Task Value`

The default GC2 quest instructions can still be used for local-only quests. If `Auto Forward Journal Changes` is enabled in the profile, default GC2 Journal changes for profiled quests are forwarded automatically.

## PurrNet Demo Scene

`Game Creator > Networking Layer > PurrNet Transport > Demo Scenes > Quests` creates a quest-ready setup when the GC2 Quests examples/UI packages are installed:

- the generated player prefab gets a GC2 `Journal`
- the player prefab gets a `NetworkQuestsController`
- a generated `PurrNetDemoNetworkQuestProfile` binds `Quest Simple` from `Quests.Examples@1.3.5`
- the scene includes visible quest demo objects with GC2 `Trigger` components and `EventOnInputButton` events
- the scene includes `HUD_Quests` and `Journal_Quests` from `Quests.UI@1.3.4`
- `PurrNetDemoQuestUIBinder` refreshes those UI prefabs after the local network player spawns
- `Q` runs a scene-owned visual scripting trigger that sends a global network quest share request to the local player's Journal
- `E` runs a scene-owned visual scripting trigger that completes the demo quest task through the network quest path

This makes the demo scene a first-pass validation target: if the generated scene does not sync the quest, debug the networking code first; if it does sync, compare custom scene setup against the generated profile, Journal, controller, and scene visual scripting instructions.

## Side Effects

By default, remote clients apply the resulting authoritative quest/task state directly to `QuestEntries` and `TaskEntries`. This updates UI and Journal state without replaying Quest `On Activate`, `On Complete`, or task instruction lists on every client.

Enable `Replay Quest Methods On Remote Clients` in the profile only when those GC2 instruction side effects are intentionally local presentation effects and are safe to run on every receiving client.

## Late Join

`NetworkQuestProfile.Snapshot On Late Join` marks the profile as eligible for snapshot sync. Personal snapshots target the owning Journal. Shared quest state should normally be driven by server broadcasts and project-specific world/party state policies.

For production shared quests, distinguish live session state from persisted campaign state:

- Live session state belongs in `NetworkQuestsManager`. The server should keep authoritative `Global` and `Party` quest state while the session is running and replay matching shared state to newly registered Journals.
- Persisted campaign state belongs in a save system. Crystal Save is the intended integration point for saving and restoring shared quest state across server restarts, save/load, or hosted campaigns.
- The PurrNet demo scene does not require Crystal Save. Its `Q` trigger is enough to validate the live request, validation, broadcast, and Journal application path for currently connected players.

Crystal Save is the better persistence target for shared network quest state than GC2's basic save system. GC2 Save/Load is designed around local `IGameSave` subscriptions, `Remember` components, slots, and JSON/PlayerPrefs-style storage. It is not built as a networked, server-authoritative save layer: it has no built-in network transport, snapshot/diff sync, server reconciliation, or shared campaign-state authority model. That is useful for ordinary local game state, but not for multiplayer quest authority.

Using GC2's basic save system as the authoritative persistence layer for shared network quests would require changing or deeply wrapping GC2's save internals. In practice, anyone who wants that route would have to edit Game Creator 2 save source code, which this networking layer avoids.

Crystal Save is the intended route because it is already designed for richer runtime persistence. It works with byte payload components, save slots, prefab/runtime object state, local/cloud save backends, and a network-aware sync runtime with snapshots, diffs, transport assets, and reconciliation. A future quest persistence adapter can therefore save the authoritative `NetworkQuestsManager` shared-state table through Crystal Save without editing GC2's save source code.

## Security

Quest requests are validated by:

- `SecurityIntegration.ValidateModuleRequest(...)`
- target ownership for `Personal` quests
- `NetworkQuestProfile` action allow-lists
- optional `NetworkQuestsManager.CustomQuestValidator`

Use `CustomQuestValidator` for project rules such as:

- party membership
- quest prerequisites
- level requirements
- distance to quest giver
- faction/team ownership

## Recommended Patterns

For WoW-style quest sharing:

1. Activate personal quests with `Share Mode = Personal`.
2. Use `Network Quest Share` with `Share Mode = Party`.
3. Validate party membership in `CustomQuestValidator`.
4. Let each accepting player receive or activate the quest on their personal Journal.

For Baldur's Gate-style shared story quests:

1. Add the quest to a profile with `Share Mode = Global`.
2. Use `Network Quest Share` or the other network quest instructions with `Global`.
3. Keep `Replay Quest Methods On Remote Clients` disabled unless client-side replay is intentional.

## Debugging

Enable logging on:

- `NetworkQuestsManager`
- `NetworkQuestsController`
- `PurrNetQuestsTransportBridge`

Useful symptoms:

- Rejected request: check profile action allow-list, ownership, and `CustomQuestValidator`.
- Local-only quest change: check that the quest is in the profile and the Instruction List uses a network quest instruction or profile auto-forwarding.
- Remote side effects duplicate: keep raw-state application enabled and avoid remote replay.
