---
description: >-
  Orchestrates per-subsystem state snapshots for clients that join an
  in-progress session. Network-agnostic, server-authoritative.
---

# Late-Join State Synchronization

## The Problem

When a client connects to an already-running session, every networked subsystem has accumulated state that the new client doesn't have:

* Players have **inventory items**, **equipped gear**, **stat modifiers**
* **Triggers** have fired (doors opened, cutscenes completed)
* **Variables** have changed from their defaults
* NPCs have **moved**, changed **combat state**, gained **status effects**

Without a coordinated late-join flow, the new client sees a stale world.

## Architecture

The system uses a **thin orchestrator + pluggable providers** pattern rather than a monolithic world-state serializer:

```
Client Connects
      │
      ▼
┌─────────────────────────────────┐
│    NetworkLateJoinCoordinator   │  Server-side singleton
│    (orchestrator)               │
└──────────────┬──────────────────┘
               │ queries each provider in priority order
               │
    ┌──────────┼──────────────────────────────────────┐
    │          │          │          │          │      │
    ▼          ▼          ▼          ▼          ▼      ▼
 Spawns    Character  Inventory   Stats    Triggers  Camera
 (pri 0)   (pri 10)  (pri 100)  (pri 110) (pri 200) (pri 300)
    │          │          │          │          │      │
    └──────────┴──────────┴──────────┴──────────┴──────┘
               │
               ▼
        LateJoinBundle
        (ordered entries)
               │
               ▼
    OnSendBundleToClient(clientId, bundle)
               │
               ▼  ← your networking solution sends this
               │
        Joining Client
               │
               ▼
    ReceiveLateJoinBundle(bundle)
               │
               ▼
    Dispatch entries → providers (in priority order)
               │
               ▼
    OnLateJoinComplete fires
```

### Why Not a Save System?

| Approach | Problem |
|----------|---------|
| GC2 Memory/Token | Designed for disk I/O, not network. JSON-only, full-save-only, triggers scene loads. |
| CrystalSave Sync | Has networking infra but no GC2 bridge. v1.7 sync isn't production-complete. |
| Monolithic world snapshot | Conflicts with server authority — server should send only what the client needs. |
| **Per-subsystem orchestration** | **Each subsystem already knows how to snapshot its own state. Just coordinate the sends.** |

## Core Components

### ILateJoinSnapshotProvider

The interface every subsystem implements to participate in late-join sync:

```csharp
public interface ILateJoinSnapshotProvider
{
    string ProviderId { get; }      // e.g., "Inventory", "Stats"
    int Priority { get; }           // lower = sent first
    bool HasSnapshot { get; }       // false to skip
    
    LateJoinSnapshotEntry[] CollectSnapshots(ulong clientId);  // server: capture
    void ApplySnapshots(LateJoinSnapshotEntry[] entries);       // client: apply
}
```

### LateJoinSnapshotEntry

A single payload within the bundle:

```csharp
public struct LateJoinSnapshotEntry
{
    public string ProviderId;      // which provider created this
    public string Key;             // sub-key (e.g., network ID, object name)
    public string Payload;         // string-serialized data (JSON, type-prefixed, etc.)
    public byte[] BinaryPayload;   // alternative: raw bytes for binary subsystems
    public float Timestamp;        // server time when captured
}
```

### LateJoinBundle

The complete package sent to the joining client:

```csharp
public struct LateJoinBundle
{
    public ulong ClientId;
    public LateJoinSnapshotEntry[] Entries;  // ordered by provider priority
    public int ProviderCount;
    public float Timestamp;
}
```

### NetworkLateJoinCoordinator

The singleton orchestrator. Server collects, client applies:

```csharp
// Server: wire to your transport's client-connected callback
SubscribeToClientConnected(clientId =>
{
    NetworkLateJoinCoordinator.Instance.SendLateJoinBundle(clientId);
});

// Server: wire the send delegate to your transport adapter
coordinator.OnSendBundleToClient = (clientId, bundle) =>
{
    // Serialize bundle and send via your networking solution
    SendBundleToClient(clientId, bundle);
};

// Client: call when you receive the bundle
coordinator.ReceiveLateJoinBundle(receivedBundle);

void SubscribeToClientConnected(System.Action<ulong> callback) { /* transport hookup */ }
void SendBundleToClient(ulong clientId, LateJoinBundle bundle) { /* transport send */ }
```

## Priority System

Providers are queried in ascending priority order. This ensures dependencies are resolved correctly — spawns arrive before inventory, inventory before stats, etc.

| Range | Category | Examples |
|-------|----------|----------|
| 0–99 | **Spawns & Identity** | NetworkSpawnListener, NetworkCharacter state |
| 100–199 | **Core Gameplay** | Inventory, Stats, Abilities |
| 200–299 | **Visual Scripting** | Triggers (persistent state), Variables |
| 300+ | **Cosmetic** | Camera sync, visual effects |

On the receiving client, entries are also dispatched in provider priority order, so a Stats provider can safely reference objects that the Spawns provider already created.

## Implementing a Provider

### Example: Stats Provider

```csharp
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Stats;
using UnityEngine;

public class StatsLateJoinProvider : MonoBehaviour, ILateJoinSnapshotProvider
{
    public string ProviderId => "Stats";
    public int Priority => 110;
    public bool HasSnapshot => NetworkStatsManager.Instance != null;

    private void OnEnable()
    {
        if (NetworkLateJoinCoordinator.Instance != null)
            NetworkLateJoinCoordinator.Instance.RegisterProvider(this);
    }

    private void OnDisable()
    {
        if (NetworkLateJoinCoordinator.Instance != null)
            NetworkLateJoinCoordinator.Instance.UnregisterProvider(this);
    }

    public LateJoinSnapshotEntry[] CollectSnapshots(ulong clientId)
    {
        // Gather snapshot from every tracked stats controller
        var controllers = FindObjectsByType<NetworkStatsController>(
            FindObjectsSortMode.None);
        
        var entries = new LateJoinSnapshotEntry[controllers.Length];
        for (int i = 0; i < controllers.Length; i++)
        {
            var snapshot = controllers[i].GetFullSnapshot();
            entries[i] = new LateJoinSnapshotEntry
            {
                ProviderId = ProviderId,
                Key = snapshot.NetworkId.ToString(),
                Payload = JsonUtility.ToJson(snapshot),
                Timestamp = Time.time
            };
        }
        return entries;
    }

    public void ApplySnapshots(LateJoinSnapshotEntry[] entries)
    {
        foreach (var entry in entries)
        {
            var snapshot = JsonUtility.FromJson<NetworkStatsSnapshot>(entry.Payload);
            
            // Find the matching controller by network ID
            var controllers = FindObjectsByType<NetworkStatsController>(
                FindObjectsSortMode.None);
            
            foreach (var ctrl in controllers)
            {
                if (ctrl.NetworkId == snapshot.NetworkId)
                {
                    ctrl.ReceiveFullSnapshot(snapshot);
                    break;
                }
            }
        }
    }
}
```

### Example: Trigger Provider

```csharp
using Arawn.GameCreator2.Networking;
using UnityEngine;

public class TriggerLateJoinProvider : MonoBehaviour, ILateJoinSnapshotProvider
{
    public string ProviderId => "Triggers";
    public int Priority => 200;
    public bool HasSnapshot => true;

    private void OnEnable()
    {
        NetworkLateJoinCoordinator.Instance?.RegisterProvider(this);
    }

    private void OnDisable()
    {
        NetworkLateJoinCoordinator.Instance?.UnregisterProvider(this);
    }

    public LateJoinSnapshotEntry[] CollectSnapshots(ulong clientId)
    {
        var triggers = FindObjectsByType<NetworkTriggerController>(
            FindObjectsSortMode.None);
        
        var entries = new System.Collections.Generic.List<LateJoinSnapshotEntry>();
        
        foreach (var trigger in triggers)
        {
            string[] states = trigger.GetPersistentStates();
            if (states.Length == 0) continue;
            
            entries.Add(new LateJoinSnapshotEntry
            {
                ProviderId = ProviderId,
                Key = trigger.gameObject.name,
                Payload = string.Join("|", states),
                Timestamp = Time.time
            });
        }
        
        return entries.ToArray();
    }

    public void ApplySnapshots(LateJoinSnapshotEntry[] entries)
    {
        var triggers = FindObjectsByType<NetworkTriggerController>(
            FindObjectsSortMode.None);
        
        foreach (var entry in entries)
        {
            foreach (var trigger in triggers)
            {
                if (trigger.gameObject.name == entry.Key)
                {
                    string[] states = entry.Payload.Split('|');
                    trigger.ReplayPersistentStates(states);
                    break;
                }
            }
        }
    }
}
```

### Example: Variable Provider

```csharp
using Arawn.GameCreator2.Networking;
using UnityEngine;

public class VariableLateJoinProvider : MonoBehaviour, ILateJoinSnapshotProvider
{
    public string ProviderId => "Variables";
    public int Priority => 210;
    public bool HasSnapshot => true;

    private void OnEnable()
    {
        NetworkLateJoinCoordinator.Instance?.RegisterProvider(this);
    }

    private void OnDisable()
    {
        NetworkLateJoinCoordinator.Instance?.UnregisterProvider(this);
    }

    public LateJoinSnapshotEntry[] CollectSnapshots(ulong clientId)
    {
        var syncs = FindObjectsByType<NetworkVariableSync>(
            FindObjectsSortMode.None);
        
        var entries = new System.Collections.Generic.List<LateJoinSnapshotEntry>();
        
        foreach (var sync in syncs)
        {
            // Name variables
            var nameSnap = sync.GetNameSnapshot();
            if (nameSnap.Length > 0)
            {
                entries.Add(new LateJoinSnapshotEntry
                {
                    ProviderId = ProviderId,
                    Key = $"{sync.gameObject.name}:names",
                    Payload = JsonUtility.ToJson(
                        new NameSnapshotWrapper { Items = nameSnap }),
                    Timestamp = Time.time
                });
            }
            
            // List variables
            var listSnap = sync.GetListSnapshot();
            if (listSnap.Length > 0)
            {
                entries.Add(new LateJoinSnapshotEntry
                {
                    ProviderId = ProviderId,
                    Key = $"{sync.gameObject.name}:list",
                    Payload = string.Join("|", listSnap),
                    Timestamp = Time.time
                });
            }
        }
        
        return entries.ToArray();
    }

    public void ApplySnapshots(LateJoinSnapshotEntry[] entries)
    {
        var syncs = FindObjectsByType<NetworkVariableSync>(
            FindObjectsSortMode.None);
        
        foreach (var entry in entries)
        {
            // Parse "ObjectName:names" or "ObjectName:list"
            int colonIdx = entry.Key.LastIndexOf(':');
            if (colonIdx < 0) continue;
            
            string objName = entry.Key.Substring(0, colonIdx);
            string type = entry.Key.Substring(colonIdx + 1);
            
            foreach (var sync in syncs)
            {
                if (sync.gameObject.name != objName) continue;
                
                if (type == "names")
                {
                    var wrapper = JsonUtility.FromJson<NameSnapshotWrapper>(
                        entry.Payload);
                    sync.ApplyNameSnapshot(wrapper.Items);
                }
                else if (type == "list")
                {
                    string[] items = entry.Payload.Split('|');
                    sync.ApplyListSnapshot(items);
                }
                break;
            }
        }
    }
    
    // JsonUtility needs a wrapper for arrays
    [System.Serializable]
    private class NameSnapshotWrapper
    {
        public NetworkVariableSync.NameVariableChange[] Items;
    }
}
```

## Chunking (Large Payloads)

When the estimated bundle size exceeds `ChunkSizeBytes` (default: 32 KB), the coordinator automatically splits the bundle into chunks:

```
Bundle (120 KB)
    │
    ├─► Chunk 0  (32 KB)  ──► OnSendChunkToClient
    ├─► Chunk 1  (32 KB)  ──► OnSendChunkToClient
    ├─► Chunk 2  (32 KB)  ──► OnSendChunkToClient
    └─► Chunk 3  (24 KB)  ──► OnSendChunkToClient
                                      │
                                      ▼
                              Client reassembles
                              via ReceiveChunk()
                                      │
                                      ▼
                              OnLateJoinComplete
```

### Server-side setup for chunking:

```csharp
coordinator.ChunkSizeBytes = 32768; // 32 KB per chunk

coordinator.OnSendChunkToClient = (clientId, chunk) =>
{
    // Send via reliable-ordered channel
    SendChunkToClientReliableOrdered(clientId, chunk);
};

void SendChunkToClientReliableOrdered(ulong clientId, LateJoinChunk chunk) { /* transport send */ }
```

### Client-side reassembly:

```csharp
// In your network message handler:
void OnChunkReceived(LateJoinChunk chunk)
{
    // Coordinator auto-reassembles and dispatches when all chunks arrive
    NetworkLateJoinCoordinator.Instance.ReceiveChunk(chunk);
}
```

Set `ChunkSizeBytes = 0` to disable chunking entirely (sends the full bundle in one message).

## Full Integration Example

### Server Setup

```csharp
using Arawn.GameCreator2.Networking;
using UnityEngine;

public class MyNetworkManager : MonoBehaviour
{
    [SerializeField] private NetworkLateJoinCoordinator m_Coordinator;
    
    private void Start()
    {
        // Wire coordinator delegates
        m_Coordinator.OnSendBundleToClient = SendBundleRpc;
        m_Coordinator.OnSendChunkToClient = SendChunkRpc;
        
        m_Coordinator.OnBundleSent += (clientId, size) =>
        {
            Debug.Log($"Sent {size} bytes of late-join data to client {clientId}");
        };
        
        SubscribeToClientConnected(OnClientConnected);
    }
    
    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer()) return;
        
        // The coordinator respects SendDelay (default 0.5s)
        // to let the client's objects spawn first
        m_Coordinator.SendLateJoinBundle(clientId);
    }
    
    private void SendBundleRpc(ulong clientId, LateJoinBundle bundle)
    {
        string json = JsonUtility.ToJson(bundle);
        TransportSendReliable(clientId, json);
    }
    
    private void SendChunkRpc(ulong clientId, LateJoinChunk chunk)
    {
        string json = JsonUtility.ToJson(chunk);
        TransportSendReliableOrdered(clientId, json);
    }

    private void TransportSendReliable(ulong clientId, string payloadJson)
    {
        // Serialize to bytes and send with your transport's reliable channel.
    }

    private void TransportSendReliableOrdered(ulong clientId, string payloadJson)
    {
        // Serialize to bytes and send with your transport's reliable-ordered channel.
    }

    private void SubscribeToClientConnected(System.Action<ulong> callback)
    {
        // Hook into your transport's client-connected event here.
    }

    private bool IsServer()
    {
        // Return current session role from your transport bootstrap.
        return true;
    }
}
```

### Client Setup

```csharp
// In your network message handler:
void OnLateJoinBundleReceived(LateJoinBundle bundle)
{
    NetworkLateJoinCoordinator.Instance.ReceiveLateJoinBundle(bundle);
}

// Subscribe to completion
NetworkLateJoinCoordinator.Instance.OnLateJoinComplete += () =>
{
    Debug.Log("Late-join sync complete — enabling gameplay");
    EnablePlayerInput();
    FadeInCamera();
};
```

## Inspector Settings

| Property | Default | Description |
|----------|---------|-------------|
| **Chunk Size Bytes** | 32768 | Max bytes per chunk. 0 = no chunking. |
| **Send Delay** | 0.5s | Wait time after connect before sending. Allows client objects to spawn. |
| **Debug Mode** | false | Logs every provider collection and dispatch. |

## Diagnostics

```csharp
var coordinator = NetworkLateJoinCoordinator.Instance;

// List registered providers
string[] ids = coordinator.GetProviderIds();
Debug.Log($"Providers: {string.Join(", ", ids)}");

// Estimate bundle size without sending
int bytes = coordinator.EstimateBundleSizeForClient(clientId);
Debug.Log($"Estimated late-join payload: {bytes} bytes");

// Build bundle for inspection
LateJoinBundle bundle = coordinator.BuildBundle(clientId);
Debug.Log($"Bundle: {bundle.Entries.Length} entries from {bundle.ProviderCount} providers");

// Cleanup on disconnect
coordinator.ClearPendingChunks();
```

## Error Handling

The coordinator catches exceptions from individual providers and continues — one broken subsystem won't block the others:

```csharp
coordinator.OnError += (message) =>
{
    // Log to your analytics / error reporting
    Debug.LogError(message);
};
```

Unmatched entries (provider ID in the bundle but no registered provider on the client) produce a warning log with the count of dropped entries.

## Relationship to CrystalSave

The coordinator is a **complementary** system, not a replacement:

| Concern | Solution |
|---------|----------|
| **Late-join state sync** (runtime) | `NetworkLateJoinCoordinator` — per-subsystem snapshots |
| **Persistent save/load** (disk) | GC2 Memory/Token or CrystalSave |
| **Networked save/load** (future) | CrystalSave 2.x with GC2 bridge (when available) |

When CrystalSave 2.x ships with full networking + GC2 bridge support, it could replace the per-subsystem providers with a single CrystalSave provider that captures everything via `ISaveable`. Until then, the coordinator gives you production-ready late-join sync today.
