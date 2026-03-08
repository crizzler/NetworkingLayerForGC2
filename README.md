# Game Creator 2 Networking Layer

Transport-agnostic, server-authoritative multiplayer layer for Game Creator 2.

<img width="880" height="1163" alt="image" src="https://github.com/user-attachments/assets/53739b39-dbab-4222-9d33-98d0b3c18254" />

## What This Package Is

- A runtime networking layer for GC2 that is **not bound to one networking SDK**.
- A strict authority/security model designed for coop and competitive multiplayer.
- A module system that lets you wire one transport stack and keep GC2 gameplay integration consistent.

This package intentionally does **not** ship a production transport implementation (NGO/FishNet/Mirror/etc).  
You wire your own transport adapter to the exposed manager/controller send/receive APIs.

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

Use `Game Creator > Networking Layer > Scene Setup Wizard` to scaffold:

- Session profile asset
- Off-mesh registry
- Animation registry (optional)
- Custom bridge placeholder
- Security manager
- Optional network player template

## Patch System

Use `Game Creator > Networking Layer > Patches` for optional source patching.

- Default recommendation: start unpatched (interception/fallback mode).
- Move to patch mode when your game has traction and abuse/cheat pressure increases.
- Core combat/inventory modules are usually first for competitive hardening.
- Quests / Dialogue / Traversal patchers remain optional for most coop flows.

See:

- `Assets/Plugins/GameCreator2NetworkingLayer/Documentation/PATCHING_STRATEGY.md`

## Quickstart

Start here for transport wiring:

- `Assets/Plugins/GameCreator2NetworkingLayer/Documentation/TRANSPORT_QUICKSTART.md`
- `Assets/Plugins/GameCreator2NetworkingLayer/Documentation/PUBLIC_API.md`
- `Assets/Plugins/GameCreator2NetworkingLayer/Documentation/PATCHING_STRATEGY.md`

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
- For release packaging/sync, only `Assets/Plugins/GameCreator2NetworkingLayer/` should be included.

## License

This networking layer is MIT licensed.

See:

- `Assets/Plugins/GameCreator2NetworkingLayer/LICENSE.md`
