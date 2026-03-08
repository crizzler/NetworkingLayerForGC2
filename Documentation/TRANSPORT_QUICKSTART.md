# Transport Wiring Quickstart

This quickstart shows how to connect **any networking stack** (NGO, FishNet, Mirror, custom) to the Game Creator 2 Networking Layer.

For the canonical transport contract and public runtime API reference, see:

- `Assets/Plugins/GameCreator2NetworkingLayer/Documentation/PUBLIC_API.md`

The GC2 layer is transport-agnostic. You provide:

1. A `NetworkTransportBridge` implementation.
2. Packet serialization + routing in your transport.
3. Ownership mapping (`characterNetworkId -> ownerClientId`).

## 1) Scene Prerequisites

Use `Game Creator > Networking Layer > Scene Setup Wizard` once, then verify your scene has:

- One `NetworkTransportBridge` implementation (your class, not a placeholder).
- One `NetworkSecurityManager` (server/host scenes).
- Relevant module managers/controllers you use (`NetworkCoreManager`, `NetworkInventoryManager`, `NetworkStatsManager`, `NetworkShooterManager`, `NetworkMeleeManager`, `NetworkQuestsManager`, `NetworkDialogueManager`, `NetworkTraversalManager`, `NetworkAbilitiesController`).  
  The setup wizard can create/reuse these directly in **Step 3: Scene Objects** when their toggles are enabled.
- If **Create / Ensure GC2 Network Player object** is enabled, the wizard also adds selected per-character controllers to that player template:
  `NetworkInventoryController`, `NetworkStatsController`, `NetworkShooterController`, `NetworkMeleeController`, `NetworkQuestsController`, `NetworkTraversalController`.
- `NetworkDialogueController` is usually placed on dialogue actors (not automatically on the player template) and can reference `Dialogue` + `NetworkCharacter` on different GameObjects.

Optional modules compile only when their symbols are enabled:

- `GC2_INVENTORY`
- `GC2_STATS`
- `GC2_SHOOTER`
- `GC2_MELEE`
- `GC2_QUESTS`
- `GC2_DIALOGUE`
- `GC2_TRAVERSAL`
- `GC2_ABILITIES`

These are auto-synchronized by `GC2NetworkingDefineSymbols` based on installed modules.  
Manual refresh: `Game Creator > Networking Layer > Refresh Define Symbols`.

## 2) Create Your Bridge

Create a class inheriting `NetworkTransportBridge` and implement the abstract API.

```csharp
using Arawn.GameCreator2.Networking;
using UnityEngine;

public sealed class MyTransportBridge : NetworkTransportBridge
{
    public override bool IsServer => /* transport server state */;
    public override bool IsClient => /* transport client state */;
    public override bool IsHost => IsServer && IsClient;
    public override float ServerTime => Time.time; // Replace with transport-synced time if available.

    public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
    {
        // Serialize + send to server channel.
    }

    public override void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime)
    {
        // Serialize + send to one target client.
    }

    public override void Broadcast(
        uint characterNetworkId,
        NetworkPositionState state,
        float serverTime,
        uint excludeClientId = uint.MaxValue,
        NetworkRecipientFilter relevanceFilter = null)
    {
        // Loop transport clients and send when ShouldSendToClient(...) is true.
    }

    // Optional: if transport exposes authoritative owner data, override these.
    protected override bool TryResolveOwnerClientId(NetworkCharacter networkCharacter, out uint ownerClientId)
    {
        ownerClientId = 0;
        return false;
    }

    protected override bool TryResolveServerIssuedNetworkId(NetworkCharacter networkCharacter, out uint networkId)
    {
        networkId = 0;
        return false;
    }

    // Called from your transport packet handlers on server:
    public void OnTransportInput(ulong rawSenderClientId, uint characterNetworkId, NetworkInputState[] inputs)
    {
        if (!NetworkTransportBridge.TryConvertSenderClientId(rawSenderClientId, out uint senderClientId)) return;
        if (!TryAcceptInputFromSender(senderClientId, characterNetworkId)) return;

        RaiseInputReceivedServer(senderClientId, characterNetworkId, inputs);
    }

    // Called from your transport packet handlers on clients:
    public void OnTransportState(uint characterNetworkId, NetworkPositionState state, float serverTime)
    {
        RaiseStateReceivedClient(characterNetworkId, state, serverTime);
    }
}
```

Important:

- `clientId = 0` is valid.
- Reject only `NetworkTransportBridge.InvalidClientId`.
- Keep ownership mappings current with `SetCharacterOwner(...)` / `ClearCharacterOwner(...)`.

## 3) Initialize Managers Per Session Role

Create a bootstrap script and initialize managers after your transport is ready.

```csharp
using Arawn.GameCreator2.Networking;
using UnityEngine;

#if GC2_INVENTORY
using Arawn.GameCreator2.Networking.Inventory;
#endif
#if GC2_STATS
using Arawn.GameCreator2.Networking.Stats;
#endif
#if GC2_SHOOTER
using Arawn.GameCreator2.Networking.Shooter;
#endif
#if GC2_MELEE
using Arawn.GameCreator2.Networking.Melee;
#endif
#if GC2_QUESTS
using Arawn.GameCreator2.Networking.Quests;
#endif
#if GC2_DIALOGUE
using Arawn.GameCreator2.Networking.Dialogue;
#endif
#if GC2_TRAVERSAL
using Arawn.GameCreator2.Networking.Traversal;
#endif

public sealed class MyNetworkBootstrap : MonoBehaviour
{
    [SerializeField] private MyTransportBridge m_Bridge;

    public void InitializeSession(bool isServer, bool isClient)
    {
        if (NetworkCoreManager.HasInstance) NetworkCoreManager.Instance.Initialize(isServer, isClient);

#if GC2_INVENTORY
        if (NetworkInventoryManager.Instance != null) NetworkInventoryManager.Instance.IsServer = isServer;
#endif
#if GC2_STATS
        if (NetworkStatsManager.Instance != null) NetworkStatsManager.Instance.IsServer = isServer;
#endif

#if GC2_SHOOTER
        if (NetworkShooterManager.HasInstance) NetworkShooterManager.Instance.Initialize(isServer, isClient);
#endif

#if GC2_MELEE
        if (NetworkMeleeManager.HasInstance) NetworkMeleeManager.Instance.Initialize(isServer, isClient);
#endif

#if GC2_QUESTS
        if (NetworkQuestsManager.Instance != null) NetworkQuestsManager.Instance.IsServer = isServer;
#endif

#if GC2_DIALOGUE
        if (NetworkDialogueManager.Instance != null) NetworkDialogueManager.Instance.IsServer = isServer;
#endif

#if GC2_TRAVERSAL
        if (NetworkTraversalManager.Instance != null) NetworkTraversalManager.Instance.IsServer = isServer;
#endif

#if GC2_ABILITIES
        NetworkAbilitiesManager.Initialize(() => m_Bridge.ServerTime, ResolveLocalPlayerNetworkId);

        if (NetworkAbilitiesController.HasInstance)
        {
            if (isServer && isClient) NetworkAbilitiesController.Instance.InitializeAsHost();
            else if (isServer) NetworkAbilitiesController.Instance.InitializeAsServer();
            else if (isClient) NetworkAbilitiesController.Instance.InitializeAsClient();
        }
#endif
    }

    private uint ResolveLocalPlayerNetworkId()
    {
        // Return your local player's network character id.
        return 0;
    }
}
```

## 4) Wire Outbound Delegates To Transport Send

Managers/controllers emit requests/responses/broadcasts through delegates. Wire those delegates to your transport sender methods.

Example (`NetworkCoreManager`):

```csharp
void WireCoreDelegates(NetworkCoreManager core)
{
    core.SendRagdollRequestToServer = req =>
        TransportSendToServer(NetworkCoreManager.MessageTypes.RagdollRequest, req);

    core.SendRagdollResponseToClient = (clientId, res) =>
        TransportSendToClient(clientId, NetworkCoreManager.MessageTypes.RagdollResponse, res);

    core.BroadcastRagdoll = bc =>
        TransportBroadcast(NetworkCoreManager.MessageTypes.RagdollBroadcast, bc);

    // Repeat same pattern for Prop / Invincibility / Poise / Busy / Interaction.
}

void TransportSendToServer<T>(byte messageType, T payload) { /* your transport send */ }
void TransportSendToClient<T>(uint clientId, byte messageType, T payload) { /* your transport send */ }
void TransportBroadcast<T>(byte messageType, T payload) { /* your transport send */ }
```

Do the same for Inventory/Stats/Shooter/Melee/Quests/Dialogue/Traversal/Abilities managers you enable.

## 5) Route Inbound Packets To `Receive*` APIs

When a packet arrives, deserialize it and call the module manager `Receive*` API.

Examples:

```csharp
// Core (expects uint senderClientId)
coreManager.ReceiveRagdollRequest(senderClientId, request);

// Inventory / Stats (expects ulong raw client id)
inventoryManager.ReceiveContentAddRequest(request, rawSenderClientId);
statsManager.ReceiveStatModifyRequest(request, rawSenderClientId);

// Shooter / Melee (expects uint senderClientId)
shooterManager.ReceiveShotRequest(senderClientId, request);
meleeManager.ReceiveHitRequest(senderClientId, request);

// Quests (expects ulong raw client id)
_ = questsManager.ReceiveQuestRequest(request, rawSenderClientId);

// Dialogue (expects ulong raw client id)
_ = dialogueManager.ReceiveDialogueRequest(request, rawSenderClientId);

// Traversal (expects ulong raw client id)
_ = traversalManager.ReceiveTraversalRequest(request, rawSenderClientId);

// Client-side responses/broadcasts
coreManager.ReceiveRagdollResponse(response);
coreManager.ReceiveRagdollBroadcast(broadcast);
traversalManager.ReceiveTraversalResponse(traversalResponse, targetNetworkId);
traversalManager.ReceiveTraversalChangeBroadcast(traversalBroadcast);

// Abilities server path
abilitiesController.ProcessCastRequest(senderClientId, castRequest);

// Abilities client path
abilitiesController.ReceiveCastResponse(castResponse);
abilitiesController.ReceiveCastBroadcast(castBroadcast);
```

For every enabled module, map each network message ID to exactly one matching `Receive*`/`Process*` method.

## 6) Spawn, Role, and Ownership

For each spawned `NetworkCharacter`:

1. Call `InitializeNetworkRole(isServer, isOwner, isHost)`.
2. Set ownership mapping in the bridge immediately on server:
   - `SetCharacterOwner(character.NetworkId, ownerClientId)`
3. Clear mapping on despawn:
   - `ClearCharacterOwner(character.NetworkId)`

Strict ownership validation is enforced in request paths. Late ownership mapping causes valid requests to be rejected.

## 7) Competitive-Ready Checklist

Before shipping:

- Keep `NetworkSecurityManager` active on server/host scenes.
- Ensure all inbound requests pass authoritative sender ID.
- Keep client and server on the same protocol version (v2).
- Confirm each enabled module has both outbound delegate wiring and inbound `Receive*` routing.
- Validate with host + dedicated server + 2 clients and forged-request tests.

## 8) Where To Go Next

- Core module details: `Assets/Plugins/GameCreator2NetworkingLayer/Core/README.md`
- Inventory module details: `Assets/Plugins/GameCreator2NetworkingLayer/Inventory/README.md`
- Stats module details: `Assets/Plugins/GameCreator2NetworkingLayer/Stats/README.md`
- Shooter module details: `Assets/Plugins/GameCreator2NetworkingLayer/Shooter/README.md`
- Melee module details: `Assets/Plugins/GameCreator2NetworkingLayer/Melee/README.md`
- Quests module details: `Assets/Plugins/GameCreator2NetworkingLayer/Quests/README.md`
- Dialogue module details: `Assets/Plugins/GameCreator2NetworkingLayer/Dialogue/README.md`
- Traversal module details: `Assets/Plugins/GameCreator2NetworkingLayer/Traversal/README.md`
- Abilities module details: `Assets/Plugins/GameCreator2NetworkingLayer/Abilities/README.md`

## 9) Patching Policy (Recommended)

- Start unpatched first. Interception/fallback mode is the default recommendation.
- For coop and most flows, this is typically enough (including Quests/Dialogue/Traversal).
- Move to patch mode when your game gains traction and abuse risk appears (confirmed cheaters, ranked pressure, economy exploits).
- When patching, prioritize combat/economy-critical modules first, then expand as needed.

Patch menu:

- `Game Creator > Networking Layer > Patches > <Module> > Patch (Server Authority)`

Detailed strategy:

- `Assets/Plugins/GameCreator2NetworkingLayer/Documentation/PATCHING_STRATEGY.md`

## 10) License

Game Creator 2 Networking Layer is MIT licensed.

See:

- `Assets/Plugins/GameCreator2NetworkingLayer/LICENSE.md`
