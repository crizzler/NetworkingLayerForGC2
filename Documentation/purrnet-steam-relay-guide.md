# PurrNet Steam P2P and Relay Guide

This guide explains how to run the Game Creator 2 Networking Layer over
PurrNet's `SteamTransport`. It covers listen-host games in which one Steam
player hosts and the other players connect by Steam ID or through a lobby.

> This revision reflects the current server-authoritative Core and Inventory
> workflows and the PurrNet Steam transport included with this project. Every
> peer in a session must use the same Networking Layer package version, GC2
> patches, registered assets, and compatible PurrNet version.

## Steam transport, lobbies, and relay

These are separate responsibilities:

- `SteamTransport` carries PurrNet messages through Steam Networking Sockets.
- A Steam lobby provides discovery, invites, member lists, and metadata such as
  the host's Steam ID.
- Steam decides whether suitable P2P traffic uses a direct route or Steam
  Datagram Relay. The current `SteamTransport` has no GC2 relay toggle.
- The GC2 Networking Layer remains responsible for input, movement, animation,
  Core state, and selected module state after PurrNet connects.

Enabling `Peer To Peer` therefore does not mean "direct connection only." Do
not configure Unity Relay or a second PurrNet relay for this flow.

## Requirements

- A standalone Windows, Linux, or macOS build. The current Steam add-on is not
  supported on WebGL or mobile targets.
- Game Creator 2 Core and every GC2 module used by the game.
- PurrNet and its Steam add-on compiling in the project.
- Steamworks.NET installed. The Steam transport Inspector can add the supported
  package when it is missing.
- A Steam bootstrap that initializes the Steam API and pumps callbacks before
  PurrNet starts. `SteamTransport` does not perform this application-level
  bootstrap for you.
- Steam running on every test machine, with each tester signed into a different
  Steam account.
- A valid Steam App ID. Steam's `480` Spacewar ID is suitable only for early
  development; ship with your own App ID.
- The same game build and Networking Layer package version on every peer.

If your connection code is in a custom assembly definition and directly uses
Steamworks.NET types, reference both the PurrNet Steam runtime assembly and the
Steamworks.NET assembly from that asmdef.

Do not add a PurrNet `NetworkTransform` to the same player root managed by
`NetworkCharacter`. GC2 character position and rotation are synchronized by
the Networking Layer's movement pipeline.

## Prepare the GC2 integration

Apply required GC2 source patches outside Play Mode before configuring Steam.
Use the patch status reported by the PurrNet Scene Setup Wizard as the source
of truth for the modules installed in your project.

Inventory requires special attention after upgrading:

1. Open **Game Creator > Networking Layer > Patches > Inventory > Patch
   (Server Authority)**.
2. Confirm the status is **Patched**, **Backups Available**, and
   `3.0.0-inventory`.
3. Do not connect an old Inventory client to a new server. Its packet and
   snapshot layouts are incompatible.

It is useful to verify the scene over PurrNet UDP first. That separates a GC2
module setup issue from Steam initialization, account, lobby, or App ID issues.

## Configure the scene

1. Open **Game Creator > Networking Layer > PurrNet Scene Setup Wizard**.
2. Select every GC2 module the scene uses.
3. On the Transport page, select **ExistingOrManual**.
4. Let the wizard create or update the managers, player setup, and bridges.
5. Add **PurrNet > Transport > Steam Transport** to the PurrNet
   `NetworkManager` GameObject.
6. Assign that `SteamTransport` to `NetworkManager.transport`.
7. Remove or disable the old UDP, WebTransport, or Local transport component.
8. Re-run the wizard's validation and resolve every transport or bridge
   reference warning.

The scene must have exactly one enabled concrete transport, and
`NetworkManager.transport` must reference it. `ExistingOrManual` preserves a
manual Steam setup; it does not select the component on your behalf.

Keep the wizard-created infrastructure, including:

- `PurrNetTransportBridge`
- `PurrNetCoreTransportBridge`
- `PurrNetAnimationMotionTransportBridge`
- `PurrNetVariableTransportBridge` when Variables are enabled
- Each selected module manager and matching PurrNet bridge
- `NetworkIdentity`, `NetworkCharacter`, and
  `PurrNetNetworkCharacterAuto` on the registered player prefab
- The controller required by every enabled module on the player prefab

## Required Steam channel settings

This step is essential with the Steam transport included in the current
project. It supports `Unreliable` and `ReliableOrdered`; it does not advertise
support for `UnreliableSequenced` or `ReliableUnordered`.

Set the following channels in the Inspector:

| Traffic | Component | Channel |
|---|---|---|
| High-frequency character input | `PurrNetTransportBridge` Input Channel | `Unreliable` |
| High-frequency character state | `PurrNetTransportBridge` State Channel | `Unreliable` |
| Core, Variables, Animation/Motion, and module commands/state | Their matching PurrNet bridges | `ReliableOrdered` |
| Inventory requests, responses, deltas, and snapshots | `PurrNetInventoryTransportBridge` | `ReliableOrdered` |

New base bridges normally default to `UnreliableSequenced`. In the current
Steam implementation that value is sent reliably, which can create a backlog
and make movement or combat appear delayed. Change both base bridge fields to
`Unreliable` when using Steam.

Do not solve this by making high-frequency movement traffic
`ReliableOrdered`. Reliable delivery is appropriate for semantic operations
such as grants, equipment, transactions, and snapshots, not continuously
superseded pose samples.

## SteamTransport settings

For a normal player-hosted game:

| Setting | Value |
|---|---|
| Peer To Peer | Enabled |
| Dedicated Server | Disabled |
| Address when hosting | The local host Steam ID |
| Address when joining | The host Steam ID |
| Server Port | Leave at the project default for P2P |

The address is a decimal Steam ID, not an IP address. The current
`NetworkManager.StartHost()` starts a server and then a local client, so set
the address to the local host's Steam ID before starting the host. The current
transport also accepts `localhost` for that local P2P connection, but passing
the explicit ID makes the flow clearer.

There is no separate relay or relay-fallback field on the current component.
Do not look for one in the GC2 bridges.

## Minimal Steam ID connection flow

The following component deliberately receives Steam IDs from your Steam
bootstrap or lobby layer. It does not depend directly on Steamworks.NET, and it
does not initialize Steam.

```csharp
using PurrNet;
using PurrNet.Steam;
using UnityEngine;

public sealed class GC2PurrNetSteamConnect : MonoBehaviour
{
    [SerializeField] private NetworkManager m_NetworkManager;
    [SerializeField] private SteamTransport m_SteamTransport;

    private void Awake()
    {
        if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;

        if (m_SteamTransport == null && m_NetworkManager != null)
        {
            m_SteamTransport =
                m_NetworkManager.transport as SteamTransport;
        }
    }

    public void StartSteamHost(string localSteamId)
    {
        if (!TryConfigureP2P(localSteamId)) return;
        m_NetworkManager.StartHost();
    }

    public void StartSteamClient(string hostSteamId)
    {
        if (!TryConfigureP2P(hostSteamId)) return;
        m_NetworkManager.StartClient();
    }

    public void Disconnect()
    {
        if (m_NetworkManager == null) return;

        m_NetworkManager.StopClient();
        m_NetworkManager.StopServer();
    }

    private bool TryConfigureP2P(string hostSteamId)
    {
        if (m_NetworkManager == null || m_SteamTransport == null)
        {
            Debug.LogError(
                "Assign the PurrNet NetworkManager and SteamTransport.",
                this
            );
            return false;
        }

        string address = hostSteamId?.Trim();
        if (!ulong.TryParse(address, out _))
        {
            Debug.LogError("A valid decimal Steam ID is required.", this);
            return false;
        }

        if (m_NetworkManager.transport != m_SteamTransport)
        {
            Debug.LogError(
                "NetworkManager.transport is not the assigned SteamTransport.",
                this
            );
            return false;
        }

        m_SteamTransport.peerToPeer = true;
        m_SteamTransport.dedicatedServer = false;
        m_SteamTransport.address = address;
        return true;
    }
}
```

Initialize Steam and begin pumping callbacks before invoking either start
method. If the transport Inspector reports that Steam is unsupported, fix the
Steamworks.NET package, build target, and assembly references first.

## Steam lobby flow

A lobby is the usual production discovery layer:

```text
Host
  -> initialize Steam and callback pumping
  -> create a Steam lobby
  -> store the host Steam ID in lobby metadata
  -> configure SteamTransport for P2P
  -> call NetworkManager.StartHost()

Client
  -> initialize Steam and callback pumping
  -> join or accept an invite to the lobby
  -> read the host Steam ID from lobby metadata
  -> assign it to SteamTransport.address
  -> call NetworkManager.StartClient()
```

Some lobby APIs raise the same lobby-entered callback for the player who
created the lobby. Do not let that callback start the host as a second client;
`StartHost()` already includes the local client.

The lobby is not the authoritative inventory, combat, or character state. It
only helps peers discover and join the PurrNet session.

## Authority and local presentation

Changing from UDP to Steam does not change the gameplay authority model:

- The server validates gameplay and owns persistent replicated state.
- The owning client sends input and semantic requests.
- Each client creates its own HUD, Canvas, tracer, muzzle flash, hit effect,
  and other transient presentation.
- Persistent attachment descriptors are reconstructed from authoritative
  state.
- Interactive runtime objects use registered PurrNet prefabs with
  `NetworkIdentity`.

See [Spawning Prefabs and UI in Multiplayer](spawning-prefabs-and-ui-in-multiplayer.md)
for the full decision guide.

## Inventory 3 authority workflow

Inventory operations use `ReliableOrdered` and are server-authoritative:

- A server or host operation executes and broadcasts its confirmed result.
- An owning client sends one semantic request and waits for the confirmed bag
  revision instead of mutating locally and being rolled back.
- A remote proxy cannot mutate another player's bag.
- Identical snapshots reconcile structurally, preserving runtime item objects,
  UI selection, and description panels.

An arbitrary client-side **Add Item** is denied by default. This protects the
server from fabricated rewards. Use one of these supported paths:

- A trusted server grant such as `TryServerGrantItem`.
- A server-validated quest, merchant, crafting, or gameplay transaction.
- `CustomAddValidator` for a project-defined client request and source hash.
- `AllowUnvalidatedOwnedClientAdds` only for a deliberately trusted co-op game.

The wizard warns when the unsafe compatibility option is enabled.

Static scene pickups require a stable `NetworkInventoryPickupSource`. When
Inventory is selected, the wizard can convert stock fixed-item pickup templates
and reports duplicate IDs or unresolved Items. A runtime pickup must be a
server-spawned registered PurrNet prefab with `NetworkIdentity`,
`NetworkInventoryPickupSource`, and the PurrNet runtime pickup identity adapter.

See [Inventory Networking](../Inventory/README.md) for grant, pickup, merchant,
crafting, and runtime-item examples.

## Demo UI limitations

`PurrNetDemoCanvasUI` and `PurrNetHostJoinUI` are direct-connect test helpers.
They can write a transport's public address, but their labels and host checks
are designed around IP/port and UDP workflows.

For a Steam build, replace or disable them and use a lobby/invite menu that:

- Initializes Steam before networking.
- Supplies the correct host Steam ID.
- Prevents duplicate startup callbacks.
- Shows Steam/PurrNet connection errors to the player.
- Calls `StopClient()` and `StopServer()` as appropriate when leaving.

The GC2 managers and PurrNet bridges remain unchanged.

## Test checklist

- Steam reports initialized before PurrNet starts.
- Steam callbacks continue to run while connected.
- Every tester uses the same App ID and game build.
- Every remote tester uses a different Steam account.
- `steam_appid.txt` is beside the development executable when required by the
  Steamworks.NET setup.
- The target is standalone Windows, Linux, or macOS.
- Exactly one transport is enabled.
- `NetworkManager.transport` references `SteamTransport`.
- `Peer To Peer` is enabled and `Dedicated Server` is disabled.
- The host and client addresses are decimal host Steam IDs.
- Base Input and State channels are both `Unreliable`.
- Core and module bridges use `ReliableOrdered`.
- Required GC2 patches report current.
- The same Networking Layer package version is installed on every peer.
- The player prefab is registered in PurrNet `NetworkPrefabs`.
- The scene has one set of core and selected module bridges.
- The production Steam UI has replaced the IP/port demo UI.

## Troubleshooting

### SteamTransport reports unsupported

Confirm Steamworks.NET is installed, the target is standalone Windows, Linux,
or macOS, and the PurrNet Steam runtime assembly compiled with its package
version define. Check custom asmdef references if only your connection script
fails to compile.

### A client cannot connect

Confirm Steam is initialized on both peers and callbacks are running. Check the
App ID, account, lobby metadata, and exact decimal host Steam ID. An IP address
is not the remote address in P2P mode.

### Host starts but no player spawns

Confirm the player prefab is registered in PurrNet `NetworkPrefabs` and the
scene contains `PurrNetDemoPlayerSpawner` or the project's production spawn
flow. Check that a stale transport or duplicate startup UI did not start a
second session.

### Movement is delayed, freezes, or catches up in bursts

Set `PurrNetTransportBridge` Input Channel and State Channel to `Unreliable`.
With the current Steam transport, leaving the generated
`UnreliableSequenced` defaults maps high-frequency traffic to reliable sends.

### A GC2 module works over UDP but not Steam

First confirm PurrNet is connected. Then confirm the matching manager,
controller, and PurrNet module bridge exist and reference the same
`NetworkManager`. Module operations should remain `ReliableOrdered`.

### Client Add Item is rejected

This is normally Inventory security policy, not Steam packet loss. Apply
`3.0.0-inventory`, configure a server grant or `CustomAddValidator`, and inspect
the rejection reason. Enable `AllowUnvalidatedOwnedClientAdds` only when the
game intentionally trusts its clients.

### A scene pickup works only for the host

Run the setup wizard's Inventory validation. Confirm the pickup has a unique
stable `NetworkInventoryPickupSource` ID and a resolved Item. Runtime pickups
must be server-spawned PurrNet network prefabs with the runtime identity
adapter.

### Inventory items flicker or bag organization reverts

Verify that every peer uses the same Networking Layer version, the Inventory
patch reports `3.0.0-inventory`, and the Inventory bridge uses
`ReliableOrdered`. Mixed versions and legacy patches do not provide the new
versioned structural reconciliation.

### The wizard replaces SteamTransport

Select **ExistingOrManual** before applying the wizard again. Afterwards,
verify that Steam remains assigned and that no old concrete transport is still
enabled.

### The lobby connects but gameplay does not synchronize

Lobby membership is not a PurrNet gameplay connection. Confirm the lobby
callback supplied the host Steam ID and that `StartHost()` or `StartClient()`
actually completed.

## Dedicated-server boundary

This guide targets Steam P2P listen hosts. A dedicated Steam server requires a
Steam Game Server API bootstrap, server callback pumping, authentication,
deployment credentials, and project-specific matchmaking. It normally calls
`StartServer()` rather than using a player-host lobby flow.

Do not enable `Dedicated Server` and assume the listen-host instructions above
will initialize the Steam game-server environment.

## References

- [PurrNet Steam Transport](https://purrnet.gitbook.io/docs/systems-and-modules/transports/steam-transport)
- [PurrNet: Connect with Steam](https://purrnet.dev/docs/guides/steam-setup/connect-with-steam)
- [Steam Datagram Relay](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay)
- [Steam Networking overview](https://partner.steamgames.com/doc/features/multiplayer/networking)
- [Steamworks SDK Spacewar example](https://partner.steamgames.com/doc/sdk/api/example)
- [GC2 PurrNet transport README](../Runtime/Transport/PurrNet/README.md)
