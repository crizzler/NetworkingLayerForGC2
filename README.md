# Game Creator 2 Networking Layer

Transport-agnostic, server-authoritative multiplayer support for Game Creator 2.

**[Online documentation](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2)** · **[Releases](https://github.com/crizzler/NetworkingLayerForGC2/releases)**

> **Release status: Alpha**  
> The package is in alpha and its APIs, behavior, and documentation may change. The new Photon Fusion integration is specifically an **early alpha** feature.

<img width="880" height="1163" alt="Game Creator 2 Networking Layer" src="https://github.com/user-attachments/assets/53739b39-dbab-4222-9d33-98d0b3c18254" />

## What This Package Is

- A runtime networking layer for GC2 that is not bound to one networking SDK.
- A server-authoritative security model for cooperative and competitive multiplayer.
- A shared module contract that keeps GC2 gameplay integration consistent across transports.
- Setup, validation, migration, patching, demo, lobby, and GC2 Inspector visual-scripting tooling.

## Supported Transports

| Transport | Status | Included workflows |
| --- | --- | --- |
| PurrNet | Alpha | Host/client transport bridge, LAN discovery and direct join, staging/ready-room lobby with chat, and an optional Steamworks.NET lobby/invite demo |
| Photon Fusion 2 | **Early alpha** | Host/Client and Shared topologies, Photon matchmaking/lobby discovery, session bootstrap and diagnostics, character selection, chat, and Steam authentication/invite extension points |

The Fusion integration uses Fusion/Photon connectivity. It does not replace Fusion's transport with Steam Datagram Relay. In Host/Client mode Fusion can use direct UDP or Photon Relay; Shared Mode uses Photon Relay.

Version 1.9.0 was validated against PurrNet 1.21.0 and Photon Fusion 2.1.1. The complete public source snapshot contains both transport integrations, so install the corresponding SDK assemblies before importing or compiling their transport folders.

## Supported Modules

- Core, Variables, Animation, and Motion
- Inventory
- Stats
- Shooter
- Melee
- Quests
- Dialogue
- Traversal
- Abilities (DaimahouGames third-party module integration)

Both transport integrations include bridges for the shared core and supported optional modules. The Fusion integration also includes GC2 Inspector Instructions, Conditions, Events, and Properties for session control and transport state.

## Setup Wizards

- PurrNet: `Game Creator > Networking Layer > PurrNet Scene Setup Wizard`
- Fusion: `Game Creator > Networking Layer > Fusion Scene Setup Wizard`

The wizards create or reuse the transport session objects, shared GC2 managers, transport bridges, selected module bridges, player-prefab components, session profiles, registration assets, and optional demo UI. Use each wizard's Review and validation pages before applying changes to an existing scene.

## Lobby Workflows

- The transport-neutral lobby API and canvas UI provide a common front end for hosting, discovery, joining, compatibility checks, and session capacity.
- PurrNet includes LAN discovery/direct-address joining plus an authoritative staging room with player list, ready states, configurable launch policies, capacity enforcement, join-in-progress rules, and chat.
- Fusion includes Photon session discovery and joining through its lobby/matchmaking service.
- The optional PurrNet Steamworks.NET demo adds Steam lobbies and invites and remains compile-safe when Steamworks.NET is absent.
- Fusion exposes region, authentication, visibility, capacity, session-property, and force-Photon-Relay start options. Steam authentication and invite metadata can be supplied by project-level adapters without coupling the core integration to a Steamworks wrapper.

## Patch System

Use `Game Creator > Networking Layer > Patches` outside Play Mode. Each transport wizard validates the required patch for every selected module.

- Inventory, Melee, Shooter, and Traversal networking require their current server-authority patches.
- Shooter also requires the remote-camera-safety Sight patch.
- Updating or reinstalling a patched GC2 module can overwrite its hooks. Rerun Patch Status and reapply anything reported missing or stale.
- The Networking Layer fails closed when required hooks are unavailable.

## Compatibility

Servers and clients must use the same Networking Layer version. Version 1.9.0 uses the v2 position-state wire layout, which older clients cannot decode.

## Documentation

- [General quickstart](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/getting-started/quickstart)
- [PurrNet overview](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/purrnet-overview)
- [Fusion overview](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/fusion-overview)

## Contributing

The recommended flow is fork, branch, and pull request.

1. Fork the repository and create a branch from `main`.
2. Keep changes scoped and atomic.
3. Verify Unity compiles cleanly for every affected module.
4. Open a pull request describing what changed, why, and how to test it.

Preserve Unity `.meta` files and GUIDs. The repository root is a flattened mirror of `Assets/Arawn/NetworkingLayerForGC2/`, plus the repository README and license.

## License

This networking layer is MIT licensed. See [LICENSE.md](LICENSE.md).
