# Game Creator 2 Networking Layer

Transport-agnostic, server-authoritative multiplayer layer for Game Creator 2.

<img width="892" height="1129" alt="image" src="https://github.com/user-attachments/assets/8be1025e-8da0-4638-89b4-fe22df222b3f" />


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

## License

This networking layer is MIT licensed.

See:

- `Assets/Plugins/GameCreator2NetworkingLayer/LICENSE.md`
