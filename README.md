# Game Creator 2 Networking Layer

Transport-agnostic, server-authoritative multiplayer layer for Game Creator 2.

**Online documentation: <https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2>**

**Download: [https://github.com/crizzler/NetworkingLayerForGC2/releases/
](https://github.com/crizzler/NetworkingLayerForGC2/releases/)**

> **Release Status: Alpha**  
> This package is currently in **Alpha**. APIs, behavior, and documentation may change between releases.

<img width="880" height="1163" alt="image" src="https://github.com/user-attachments/assets/53739b39-dbab-4222-9d33-98d0b3c18254" />

## What This Package Is

- A runtime networking layer for GC2 that is **not bound to one networking SDK**.
- A first-class **PurrNet transport integration** with scene setup, demo scenes, player spawning, module bridges, and runtime helper UI.
- A strict authority/security model designed for coop and competitive multiplayer.
- A module system that lets you wire one transport stack and keep GC2 gameplay integration consistent.

The core layer remains transport-agnostic. PurrNet is included as the currently supported concrete transport, and future transport folders can plug into the same manager/controller send/receive APIs.

## Supported Modules

- Core
- Inventory
- Stats
- Shooter
- Melee
- Quests
- Dialogue
- Traversal
- Abilities (DaimahouGames third-party module integration)

## Core Runtime Entry Points

- `NetworkTransportBridge` / `INetworkTransportBridge`
- `NetworkCharacter`
- Module managers/controllers (`Core`, `Inventory`, `Stats`, `Shooter`, `Melee`, `Quests`, `Dialogue`, `Traversal`, `Abilities`)
- `NetworkSecurityManager` + `SecurityIntegration`

## Integration Model

1. Implement your bridge by inheriting `NetworkTransportBridge`.
2. Wire outbound delegates from managers/controllers to your transport sender.
3. Route inbound transport packets to the matching manager/controller `Receive*` APIs.
4. Register ownership mappings early (`characterNetworkId -> ownerClientId`) so strict validation succeeds from first request.
5. Normalize sender IDs through `NetworkTransportBridge.TryConvertSenderClientId(...)` (`clientId = 0` is valid).

## Setup Wizard

Use `Game Creator > Networking Layer > PurrNet Scene Setup Wizard` for PurrNet projects. The generic scene setup wizard is hidden automatically when a transport-specific wizard is installed.

The PurrNet wizard is split into six pages:

1. **Project** - choose a project template and expected player count. Non-Custom templates apply recommended modules, tick rate, session preset, and helper settings immediately.
2. **Modules** - select which GC2 modules should run over PurrNet. Core, Variables, Animation, and Motion are always included.
3. **Transport** - choose UDP, WebTransport, Local, or an existing/manual PurrNet transport and set default address/port where relevant.
4. **Core** - review NetworkManager, core managers, PurrNet bridges, selected module managers/bridges, tick rate, and session profile generation. Custom session presets expose editable profile fields on this page.
5. **Scene** - assign an optional Player Prefab, prepare it with required networking components, register Network State/Dash/Gesture clips, create NetworkPrefabs, and add optional demo UI.
6. **Review** - inspect the final setup before applying changes to the active scene.

When a Player Prefab is assigned and preparation is enabled, the wizard can add `NetworkIdentity`, `NetworkCharacter`, `PurrNetNetworkCharacterAuto`, selected module controllers, optional `NetworkVariableController` for local GC2 variables, and pre-registered animation clips used by Network State, Dash, or Gesture instructions.

## Patch System

Use `Game Creator > Networking Layer > Patches` for optional source patching.

- Default recommendation: start unpatched (interception/fallback mode).
- Move to patch mode when your game has traction and abuse/cheat pressure increases.
- Core combat/inventory modules are usually first for competitive hardening.
- Quests / Dialogue / Traversal patchers remain optional for most coop flows.

See:

- [Patching Strategy](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/misc/optional-patching-strategy)

## Quickstart

Start here for transport wiring:

- [Quickstart](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/getting-started/quickstart)
- [Public API](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/getting-started/publish-your-docs)
- [Patching Strategy](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/misc/optional-patching-strategy)
- [PurrNet Transport]([https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/purrnet-overview))
- [Network Animation States](Documentation/network-animation-states.md)
- [Network Dialogue](Documentation/network-dialogue.md)
- [Network Quests](Documentation/network-quests.md)

## Contributing

Recommended default flow: **fork -> branch -> pull request**.

1. Fork the repository.
2. Create a branch from `main` (example: `fix/melee-hit-validation`).
3. Keep changes scoped and atomic (one concern per PR when possible).
4. Verify Unity compiles cleanly for affected modules (no new errors).
5. Open a PR with:
   - What changed
   - Why it changed
   - How to test it

Notes:

- If you have direct write access, branch + PR in the main repo is still preferred over direct pushes to `main`.
- For release packaging/sync, include `Assets/Arawn/NetworkingLayerForGC2/` and its generated documentation/assets as required by the package release.

## License

This networking layer is MIT licensed.

See:

- [License](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/getting-started/license-mit)
