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

Version 2.4.1 was validated against PurrNet 1.22.1-beta.3 and Photon Fusion 2.1.1. The optional Advanced KCC driver was validated with Photon Advanced KCC 2.1.0. The complete public source snapshot contains both transport integrations, so install the corresponding SDK assemblies before importing or compiling their transport folders.

## Optional Fusion Advanced KCC Driver

The optional Game Creator 2 movement driver for Photon Fusion Advanced KCC, introduced in version 2.1.0, remains available in version 2.4.1. Fusion Native remains the built-in, recommended default. Choose Advanced KCC when your project specifically wants its collision, prediction, resimulation, and render-presentation workflow.

- Photon Advanced KCC is a separate dependency and is not redistributed by this repository.
- The Fusion Scene Setup Wizard detects a compatible KCC API, manages `ARAWN_GC2_FUSION_KCC`, configures the nested KCC motor and GC2 driver, and validates authority and Fusion weaving setup.
- The optional adapter source is stored at [`OptionalIntegrations~/NetworkingLayerForGC2.FusionAdvancedKCC`](OptionalIntegrations~/NetworkingLayerForGC2.FusionAdvancedKCC) so Unity ignores it in this repository's flattened source layout.
- In a Unity project, install that folder as `Assets/Arawn/NetworkingLayerForGC2.FusionAdvancedKCC`, beside `Assets/Arawn/NetworkingLayerForGC2`. Keep the companion folder's `.meta` file and do not add an assembly definition to it.
- After installing Photon Advanced KCC, the optional movement-course demo can be installed from `Game Creator > Install...` as **Advanced KCC Examples**.

## Network Actions and Replicated Object State

Version 2.2.0 adds transport-neutral Network Actions for authority-validated gameplay requests, confirmed transient events, and persistent replicated object state.

Version 2.2.1 fixes the Fusion client-readiness race that could reject a Network Action before initial snapshot acknowledgement, adds immediate local transport-failure responses and visible door-demo request results, and renders endpoint Conditions and Instructions through GC2's native UI Toolkit list tools.

- `NetworkActionDefinition` assets provide the trusted payload, authority, recipient, validation, reliability, and persistence contract.
- `NetworkActionEndpoint` components allow-list actions on admitted Fusion or PurrNet objects and bind authority checks plus local GC2 apply and snapshot Instructions.
- Persistent Boolean, Number, String, and Vector3 values are revisioned, broadcast through the selected transport, and reconstructed for late joiners and clients entering relevance.
- GC2 Instructions, Conditions, Events, and Properties cover requests, results, object-state reads, interaction context, and local state application for transforms, active objects, renderers, behaviours, colliders, layers, tags, visual variants, Animator parameters/states, and character controllability presentation.
- The Fusion and PurrNet Core Examples installers include an authority-validated replicated door-state example.

Network Actions replicate a trusted semantic value; they do not serialize or remotely execute arbitrary GC2 Instruction Lists. Authority commits the value once, then each receiving replica applies its locally authored presentation. Use absolute, idempotent snapshot Instructions for persistent state.

## Network Ragdoll Safety

Version 2.4.0 aligned every Networking Layer movement driver with GC2's ragdoll collision lifecycle. Version 2.4.1 fixes ragdoll-body discovery after GC2 temporarily detaches the configured Animator hierarchy.

- The active root movement collider is disabled before ragdoll physics begins and restored to its exact previous enabled, trigger, and Rigidbody state after recovery.
- Authority transitions, driver swaps, component disable, and destruction release any outstanding guard without leaving a character collider disabled.
- Fusion, PurrNet, and optional PurrDiction lifecycle tests cover ragdoll entry and recovery.
- The Fusion and PurrNet Core Examples 1.1.0 installers add dedicated two-peer ragdoll scenes with correlated request/result diagnostics and visible root-physics proof.
- The three-cycle proof panel and optional ragdoll-force path now follow `Character.Animim.Animator`, so detached dynamic bodies are counted correctly and receive their requested impulse.

## Server-Authoritative NPCs and Free Flow Combat

Version 2.3.0 separates GC2's local `Character.IsPlayer` presentation flag from authenticated network ownership through explicit `PlayerOwned` and `NPC` actor types.

- Server-authoritative NPCs run AI, navigation, Shooter, Stats, Melee, and other durable gameplay only on the server or current Fusion Shared master; observer replicas interpolate the authoritative pose.
- Authority gates, deterministic target selection, transport-neutral pose sampling, and setup-wizard validation prevent client replicas or runtime `IsPlayer` changes from granting gameplay authority.
- Optional bot-backed player slots atomically replace bots with joining players and restore fresh bots after disconnects while replicating durable slot membership.
- Separate Fusion and PurrNet Enemy Shooter installers demonstrate server-owned enemies and bot slots without replacing the existing PvP Shooter+Stats demos.
- The optional Free Flow Combat integration routes attacks, counters, Motion Warp context, telegraphs, hits, and late-join state through the server-authoritative Melee pipeline. Its example installers contain only Networking Layer-owned demo content and require the separately installed Free Flow Combat and GC2 Melee dependencies.

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

Install examples through `Game Creator > Install...`. The former aggregate Fusion and PurrNet demo `.unitypackage` files were retired in version 2.2.0; the per-feature installers are the supported source of demo content and preserve explicit dependencies and upgrade versions.

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

Matching Networking Layer versions are recommended for every peer. Versions 2.4.0 and 2.4.1 do not change the 2.3.0 wire protocol, but version 2.3.0 changed role initialization, trusted combat handling, Melee Skill and Free Flow state payloads, bot-slot membership replication, and persistent-state paths and must not be mixed with builds older than 2.3.0. Every peer in an Advanced KCC session must also use the compatible optional adapter and Photon addon version.

## Documentation

- [General quickstart](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/getting-started/quickstart)
- [PurrNet overview](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/purrnet-overview)
- [Fusion overview](https://arawn-software-publishing.gitbook.io/networking-layer-for-gc2/fusion-overview)
- [Server-authoritative NPCs and bot slots](Documentation/Server%20Authoritative%20NPCs%20and%20Bot%20Slots.md)
- [Server-authoritative Free Flow Combat](Documentation/Server%20Authoritative%20Free%20Flow%20Combat.md)
- [Fusion Advanced KCC companion guide](OptionalIntegrations~/NetworkingLayerForGC2.FusionAdvancedKCC/README.md)

## Contributing

The recommended flow is fork, branch, and pull request.

1. Fork the repository and create a branch from `main`.
2. Keep changes scoped and atomic.
3. Verify Unity compiles cleanly for every affected module.
4. Open a pull request describing what changed, why, and how to test it.

Preserve Unity `.meta` files and GUIDs. The repository root is a flattened mirror of `Assets/Arawn/NetworkingLayerForGC2/`, plus the repository README, license, and optional integrations stored below `OptionalIntegrations~`.

## License

This networking layer is MIT licensed. See [LICENSE.md](LICENSE.md).
