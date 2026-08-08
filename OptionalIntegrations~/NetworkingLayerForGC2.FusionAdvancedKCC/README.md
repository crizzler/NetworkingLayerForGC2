# Fusion Advanced KCC Companion

This optional source root contains the strongly typed Photon Fusion Advanced KCC adapter for the Game Creator 2 Networking Layer.

Fusion Native remains the built-in, recommended movement backend. Install this companion only when a project specifically wants Photon Advanced KCC for collision, prediction, resimulation, and render presentation.

## Requirements

- Game Creator 2 Networking Layer 2.1.0
- Photon Fusion 2.1.1
- Photon Advanced KCC 2.1.0, or a compatible API version recognized by the Fusion setup wizard

Photon Advanced KCC is a separate dependency. This repository does not redistribute or modify its source or binaries.

## Install

1. Install the main Networking Layer at `Assets/Arawn/NetworkingLayerForGC2`.
2. Install Photon Fusion and let Unity compile it.
3. Import Photon Advanced KCC separately and let Unity compile it.
4. Copy this companion to `Assets/Arawn/NetworkingLayerForGC2.FusionAdvancedKCC`, beside the main Networking Layer folder. Keep its `.meta` file.
5. Let Unity finish compiling. The Networking Layer detects the compatible KCC API and manages `ARAWN_GC2_FUSION_KCC` automatically.
6. Open `Game Creator > Networking Layer > Fusion Scene Setup Wizard`.
7. Select **Fusion Advanced KCC (Optional Addon)**, configure the player prefab, apply the setup, and resolve every validation error before testing.

When downloading the public repository source, this companion is stored below `OptionalIntegrations~` so Unity ignores it in the flattened repository layout. Move the `NetworkingLayerForGC2.FusionAdvancedKCC` child folder and its `.meta` file to the sibling Unity path described above.

## Demo

After Photon Fusion and Advanced KCC compile successfully, open `Game Creator > Install...` and install **Advanced KCC Examples**. The movement-course scene includes a configured GC2 player prefab, slope, steps, jump obstacle, and collision slalom. The installer references Advanced KCC but does not contain the Photon addon.

## Multiplayer Compatibility

Every peer in a session must use the same Networking Layer version, the same movement backend, and compatible Photon Fusion and Advanced KCC versions. Do not mix Fusion Native and Advanced KCC player configurations for the same network prefab.

## Assembly Layout

It is intentionally installed beside `Assets/Arawn/NetworkingLayerForGC2`, not inside it. Photon Advanced KCC runtime sources do not have an assembly definition and compile into `Assembly-CSharp`. The typed adapter must compile into that same predefined assembly because Unity assembly-definition assemblies cannot reference `Assembly-CSharp` types.

All source files are guarded by `ARAWN_GC2_FUSION_KCC`. The Networking Layer enables that symbol only after recognizing a compatible Advanced KCC API, so projects without the addon continue to compile normally.

Do not add an assembly definition to this folder or move it below the main Networking Layer assembly definition. Keep this folder and its `.meta` file together when copying or packaging the integration.
