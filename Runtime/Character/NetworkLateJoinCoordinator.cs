using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Interface for subsystems that can provide a late-join state snapshot.
    /// Implement this in each networking subsystem controller and register
    /// with <see cref="NetworkLateJoinCoordinator.RegisterProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator calls providers in priority order (ascending).
    /// Lower priority = earlier in the sequence. Spawns and character state
    /// should be low priority (sent first), cosmetics and camera high priority
    /// (sent last).
    /// </para>
    /// </remarks>
    public interface ILateJoinSnapshotProvider
    {
        /// <summary>
        /// Unique identifier for this snapshot provider (e.g., "Inventory", "Stats").
        /// Used for logging and deduplication.
        /// </summary>
        string ProviderId { get; }

        /// <summary>
        /// Priority order. Lower values are processed first.
        /// Recommended ranges:
        ///   0-99   = Spawns, character state
        ///   100-199 = Core gameplay (inventory, stats, abilities)
        ///   200-299 = Visual scripting (triggers, variables)
        ///   300+    = Cosmetic (camera, effects)
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Whether this provider currently has data to send.
        /// Return false to skip (e.g., subsystem not initialized or empty).
        /// </summary>
        bool HasSnapshot { get; }

        /// <summary>
        /// Collect all snapshot payloads for a specific joining client.
        /// Each payload is returned as a named entry that the coordinator
        /// passes to your networking solution's send delegate.
        /// </summary>
        /// <param name="clientId">The network ID of the joining client.</param>
        /// <returns>
        /// One or more snapshot entries. Return an empty array to skip.
        /// </returns>
        LateJoinSnapshotEntry[] CollectSnapshots(ulong clientId);

        /// <summary>
        /// Apply snapshot entries received from the server.
        /// Called on the joining client.
        /// </summary>
        /// <param name="entries">The snapshot entries for this provider.</param>
        void ApplySnapshots(LateJoinSnapshotEntry[] entries);
    }

    /// <summary>
    /// A single snapshot payload within a late-join transfer.
    /// </summary>
    [Serializable]
    public struct LateJoinSnapshotEntry
    {
        /// <summary>Provider ID that created this entry.</summary>
        public string ProviderId;

        /// <summary>Sub-key within the provider (e.g., a network ID or object name).</summary>
        public string Key;

        /// <summary>
        /// Serialized payload as a string. Use JSON, type-prefixed strings,
        /// or Base64-encoded binary — your choice per provider.
        /// </summary>
        public string Payload;

        /// <summary>
        /// Optional: byte payload for binary-heavy subsystems.
        /// Mutually exclusive with <see cref="Payload"/>.
        /// </summary>
        public byte[] BinaryPayload;

        /// <summary>Server timestamp when this snapshot was captured.</summary>
        public float Timestamp;
    }

    /// <summary>
    /// The full bundle of snapshots sent to a late-joining client.
    /// Serialize this as a single message or chunk it per-provider.
    /// </summary>
    [Serializable]
    public struct LateJoinBundle
    {
        /// <summary>Target client ID.</summary>
        public ulong ClientId;

        /// <summary>All snapshot entries, ordered by provider priority.</summary>
        public LateJoinSnapshotEntry[] Entries;

        /// <summary>Number of providers that contributed.</summary>
        public int ProviderCount;

        /// <summary>Server time when the bundle was assembled.</summary>
        public float Timestamp;
    }

    /// <summary>
    /// Orchestrates late-join state synchronization by collecting snapshots
    /// from all registered subsystem providers and sending them to joining clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Server side:</b> When a client connects, call
    /// <see cref="SendLateJoinBundle(ulong)"/>. The coordinator queries
    /// each registered <see cref="ILateJoinSnapshotProvider"/> in priority order,
    /// assembles a <see cref="LateJoinBundle"/>, and invokes
    /// <see cref="OnSendBundleToClient"/> for your networking solution to transmit.
    /// </para>
    /// <para>
    /// <b>Client side:</b> When the bundle arrives, call
    /// <see cref="ReceiveLateJoinBundle(LateJoinBundle)"/>. The coordinator
    /// dispatches entries to each registered provider's <see cref="ILateJoinSnapshotProvider.ApplySnapshots"/>.
    /// </para>
    /// <para>
    /// <b>Large bundles:</b> If the bundle exceeds <see cref="ChunkSizeBytes"/>,
    /// it is automatically split and sent via <see cref="OnSendChunkToClient"/>
    /// instead. The receiving side reassembles chunks via <see cref="ReceiveChunk"/>.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Late Join Coordinator")]
    [DisallowMultipleComponent]
    public class NetworkLateJoinCoordinator : NetworkSingleton<NetworkLateJoinCoordinator>
    {

        // ════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════

        [Header("Settings")]
        [Tooltip("Maximum bytes per chunk when splitting large bundles. " +
                 "Set to 0 to disable chunking (send full bundle).")]
        [SerializeField] private int m_ChunkSizeBytes = 32768; // 32 KB

        [Tooltip("Delay (seconds) after client connect before sending the bundle. " +
                 "Allows time for the client's objects to spawn.")]
        [SerializeField] private float m_SendDelay = 0.5f;

        [Tooltip("If true, logs every provider collection and send operation.")]
        [SerializeField] private bool m_DebugMode;

        // ════════════════════════════════════════════════════════════════════
        // FIELDS
        // ════════════════════════════════════════════════════════════════════

        private readonly List<ILateJoinSnapshotProvider> m_Providers
            = new List<ILateJoinSnapshotProvider>();

        private readonly Dictionary<ulong, List<LateJoinChunk>> m_PendingChunks
            = new Dictionary<ulong, List<LateJoinChunk>>();

        private bool m_ProvidersSorted;

        // ════════════════════════════════════════════════════════════════════
        // EVENTS / DELEGATES (wire to your networking solution)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Server] Invoked when a complete late-join bundle should be sent
        /// to a specific client. Wire this to your RPC/message system.
        /// Only invoked when chunking is disabled or the bundle fits in one chunk.
        /// </summary>
        public Action<ulong, LateJoinBundle> OnSendBundleToClient;

        /// <summary>
        /// [Server] Invoked for each chunk when a large bundle is split.
        /// Wire this to your reliable-ordered message channel.
        /// </summary>
        public Action<ulong, LateJoinChunk> OnSendChunkToClient;

        /// <summary>
        /// [Client] Invoked after all snapshot entries have been applied.
        /// Use this to trigger post-join logic (e.g., fade-in, enable input).
        /// </summary>
        public event Action OnLateJoinComplete;

        /// <summary>
        /// [Server] Invoked after a bundle has been fully assembled and sent.
        /// Reports the client ID and total byte size.
        /// </summary>
        public event Action<ulong, int> OnBundleSent;

        /// <summary>
        /// Invoked when an error occurs during collection or application.
        /// </summary>
        public event Action<string> OnError;

        // ════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Number of registered providers.</summary>
        public int ProviderCount => m_Providers.Count;

        /// <summary>Maximum chunk size in bytes. 0 = no chunking.</summary>
        public int ChunkSizeBytes
        {
            get => m_ChunkSizeBytes;
            set => m_ChunkSizeBytes = Mathf.Max(0, value);
        }

        /// <summary>Delay before sending bundle after client connects.</summary>
        public float SendDelay
        {
            get => m_SendDelay;
            set => m_SendDelay = Mathf.Max(0f, value);
        }

        /// <summary>All registered provider IDs (for diagnostics).</summary>
        public string[] GetProviderIds()
        {
            var ids = new string[m_Providers.Count];
            for (int i = 0; i < m_Providers.Count; i++)
                ids[i] = m_Providers[i].ProviderId;
            return ids;
        }



        // ════════════════════════════════════════════════════════════════════
        // PROVIDER REGISTRATION
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a snapshot provider. Call from each subsystem's initialization.
        /// Duplicate provider IDs are rejected.
        /// </summary>
        public void RegisterProvider(ILateJoinSnapshotProvider provider)
        {
            if (provider == null) return;

            // Deduplicate
            for (int i = 0; i < m_Providers.Count; i++)
            {
                if (m_Providers[i].ProviderId == provider.ProviderId) return;
            }

            m_Providers.Add(provider);
            m_ProvidersSorted = false;

            if (m_DebugMode)
                Debug.Log($"[LateJoin] Registered provider: {provider.ProviderId} " +
                          $"(priority={provider.Priority})");
        }

        /// <summary>
        /// Unregister a snapshot provider. Call from each subsystem's cleanup.
        /// </summary>
        public void UnregisterProvider(ILateJoinSnapshotProvider provider)
        {
            if (provider == null) return;
            m_Providers.RemoveAll(p => p.ProviderId == provider.ProviderId);

            if (m_DebugMode)
                Debug.Log($"[LateJoin] Unregistered provider: {provider.ProviderId}");
        }

        /// <summary>
        /// Unregister a provider by ID.
        /// </summary>
        public void UnregisterProvider(string providerId)
        {
            m_Providers.RemoveAll(p => p.ProviderId == providerId);
        }

        // ════════════════════════════════════════════════════════════════════
        // SERVER: SEND LATE-JOIN BUNDLE
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Server] Collect snapshots from all providers and send to the joining client.
        /// Call this when a client connects (e.g., from OnClientConnectedCallback).
        /// Respects <see cref="SendDelay"/> before collecting.
        /// </summary>
        public void SendLateJoinBundle(ulong clientId)
        {
            if (m_SendDelay > 0f)
            {
                StartCoroutine(SendBundleDelayed(clientId));
            }
            else
            {
                SendBundleImmediate(clientId);
            }
        }

        /// <summary>
        /// [Server] Collect and send immediately (no delay).
        /// </summary>
        public void SendBundleImmediate(ulong clientId)
        {
            EnsureSorted();

            var allEntries = new List<LateJoinSnapshotEntry>();
            int providerContributions = 0;

            for (int i = 0; i < m_Providers.Count; i++)
            {
                var provider = m_Providers[i];

                if (!provider.HasSnapshot)
                {
                    if (m_DebugMode)
                        Debug.Log($"[LateJoin] Skipping {provider.ProviderId} (no snapshot)");
                    continue;
                }

                try
                {
                    var entries = provider.CollectSnapshots(clientId);
                    if (entries != null && entries.Length > 0)
                    {
                        allEntries.AddRange(entries);
                        providerContributions++;

                        if (m_DebugMode)
                            Debug.Log($"[LateJoin] Collected {entries.Length} entries " +
                                      $"from {provider.ProviderId}");
                    }
                }
                catch (Exception ex)
                {
                    string msg = $"[LateJoin] Error collecting from {provider.ProviderId}: {ex.Message}";
                    Debug.LogError(msg);
                    Debug.LogException(ex);
                    OnError?.Invoke(msg);
                }
            }

            if (allEntries.Count == 0)
            {
                if (m_DebugMode)
                    Debug.Log($"[LateJoin] No snapshots to send to client {clientId}");
                return;
            }

            var bundle = new LateJoinBundle
            {
                ClientId = clientId,
                Entries = allEntries.ToArray(),
                ProviderCount = providerContributions,
                Timestamp = Time.time
            };

            int estimatedSize = EstimateBundleSize(bundle);

            if (m_ChunkSizeBytes > 0 && estimatedSize > m_ChunkSizeBytes)
            {
                SendChunked(clientId, bundle, estimatedSize);
            }
            else
            {
                if (m_DebugMode)
                    Debug.Log($"[LateJoin] Sending bundle to client {clientId}: " +
                              $"{allEntries.Count} entries from {providerContributions} providers " +
                              $"(~{estimatedSize} bytes)");

                OnSendBundleToClient?.Invoke(clientId, bundle);
            }

            OnBundleSent?.Invoke(clientId, estimatedSize);
        }

        private System.Collections.IEnumerator SendBundleDelayed(ulong clientId)
        {
            yield return new WaitForSeconds(m_SendDelay);
            SendBundleImmediate(clientId);
        }

        // ════════════════════════════════════════════════════════════════════
        // CLIENT: RECEIVE LATE-JOIN BUNDLE
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Client] Apply a received late-join bundle. Call from your
        /// networking solution's message handler.
        /// </summary>
        public void ReceiveLateJoinBundle(LateJoinBundle bundle)
        {
            if (m_DebugMode)
                Debug.Log($"[LateJoin] Receiving bundle: {bundle.Entries?.Length ?? 0} entries " +
                          $"from {bundle.ProviderCount} providers");

            if (bundle.Entries == null || bundle.Entries.Length == 0)
            {
                OnLateJoinComplete?.Invoke();
                return;
            }

            DispatchEntriesToProviders(bundle.Entries);

            OnLateJoinComplete?.Invoke();
        }

        // ════════════════════════════════════════════════════════════════════
        // CHUNKING
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// A chunk of a late-join bundle for large payloads.
        /// </summary>
        [Serializable]
        public struct LateJoinChunk
        {
            /// <summary>Target client.</summary>
            public ulong ClientId;

            /// <summary>Zero-based chunk index.</summary>
            public int ChunkIndex;

            /// <summary>Total number of chunks.</summary>
            public int TotalChunks;

            /// <summary>Entries in this chunk.</summary>
            public LateJoinSnapshotEntry[] Entries;

            /// <summary>Server timestamp.</summary>
            public float Timestamp;
        }

        private void SendChunked(ulong clientId, LateJoinBundle bundle, int totalSize)
        {
            // Split entries into chunks by estimated size
            var chunks = new List<LateJoinChunk>();
            var currentEntries = new List<LateJoinSnapshotEntry>();
            int currentSize = 0;

            for (int i = 0; i < bundle.Entries.Length; i++)
            {
                int entrySize = EstimateEntrySize(bundle.Entries[i]);

                if (currentSize + entrySize > m_ChunkSizeBytes && currentEntries.Count > 0)
                {
                    // Flush current chunk
                    chunks.Add(new LateJoinChunk
                    {
                        ClientId = clientId,
                        ChunkIndex = chunks.Count,
                        Entries = currentEntries.ToArray(),
                        Timestamp = bundle.Timestamp
                    });
                    currentEntries.Clear();
                    currentSize = 0;
                }

                currentEntries.Add(bundle.Entries[i]);
                currentSize += entrySize;
            }

            // Flush remaining
            if (currentEntries.Count > 0)
            {
                chunks.Add(new LateJoinChunk
                {
                    ClientId = clientId,
                    ChunkIndex = chunks.Count,
                    Entries = currentEntries.ToArray(),
                    Timestamp = bundle.Timestamp
                });
            }

            // Set total chunk count
            int totalChunks = chunks.Count;
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                chunk.TotalChunks = totalChunks;
                chunks[i] = chunk;
            }

            if (m_DebugMode)
                Debug.Log($"[LateJoin] Sending {totalChunks} chunks to client {clientId} " +
                          $"(~{totalSize} bytes total)");

            for (int i = 0; i < chunks.Count; i++)
            {
                OnSendChunkToClient?.Invoke(clientId, chunks[i]);
            }
        }

        /// <summary>
        /// [Client] Receive a chunk. Automatically reassembles and applies
        /// when all chunks arrive.
        /// </summary>
        public void ReceiveChunk(LateJoinChunk chunk)
        {
            if (!m_PendingChunks.TryGetValue(chunk.ClientId, out var chunkList))
            {
                chunkList = new List<LateJoinChunk>();
                m_PendingChunks[chunk.ClientId] = chunkList;
            }

            chunkList.Add(chunk);

            if (m_DebugMode)
                Debug.Log($"[LateJoin] Received chunk {chunk.ChunkIndex + 1}/{chunk.TotalChunks}");

            if (chunkList.Count >= chunk.TotalChunks)
            {
                // Sort by chunk index and reassemble
                chunkList.Sort((a, b) => a.ChunkIndex.CompareTo(b.ChunkIndex));

                var allEntries = new List<LateJoinSnapshotEntry>();
                for (int i = 0; i < chunkList.Count; i++)
                {
                    if (chunkList[i].Entries != null)
                        allEntries.AddRange(chunkList[i].Entries);
                }

                m_PendingChunks.Remove(chunk.ClientId);

                if (m_DebugMode)
                    Debug.Log($"[LateJoin] Reassembled {chunkList.Count} chunks → " +
                              $"{allEntries.Count} entries");

                DispatchEntriesToProviders(allEntries.ToArray());
                OnLateJoinComplete?.Invoke();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // DISPATCH
        // ════════════════════════════════════════════════════════════════════

        private void DispatchEntriesToProviders(LateJoinSnapshotEntry[] entries)
        {
            // Group entries by provider ID
            var grouped = new Dictionary<string, List<LateJoinSnapshotEntry>>();
            for (int i = 0; i < entries.Length; i++)
            {
                string pid = entries[i].ProviderId;
                if (!grouped.TryGetValue(pid, out var list))
                {
                    list = new List<LateJoinSnapshotEntry>();
                    grouped[pid] = list;
                }
                list.Add(entries[i]);
            }

            EnsureSorted();

            // Dispatch in provider priority order
            for (int i = 0; i < m_Providers.Count; i++)
            {
                var provider = m_Providers[i];
                if (!grouped.TryGetValue(provider.ProviderId, out var providerEntries))
                    continue;

                try
                {
                    provider.ApplySnapshots(providerEntries.ToArray());

                    if (m_DebugMode)
                        Debug.Log($"[LateJoin] Applied {providerEntries.Count} entries " +
                                  $"to {provider.ProviderId}");
                }
                catch (Exception ex)
                {
                    string msg = $"[LateJoin] Error applying to {provider.ProviderId}: {ex.Message}";
                    Debug.LogError(msg);
                    Debug.LogException(ex);
                    OnError?.Invoke(msg);
                }
            }

            // Warn about unmatched entries
            foreach (var kvp in grouped)
            {
                bool found = false;
                for (int i = 0; i < m_Providers.Count; i++)
                {
                    if (m_Providers[i].ProviderId == kvp.Key) { found = true; break; }
                }
                if (!found)
                {
                    Debug.LogWarning($"[LateJoin] No provider registered for '{kvp.Key}' " +
                                     $"({kvp.Value.Count} entries dropped)");
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // SIZE ESTIMATION
        // ════════════════════════════════════════════════════════════════════

        private static int EstimateBundleSize(LateJoinBundle bundle)
        {
            int size = 16; // header
            if (bundle.Entries == null) return size;

            for (int i = 0; i < bundle.Entries.Length; i++)
                size += EstimateEntrySize(bundle.Entries[i]);

            return size;
        }

        private static int EstimateEntrySize(LateJoinSnapshotEntry entry)
        {
            int size = 24; // struct overhead + timestamp
            if (entry.ProviderId != null) size += entry.ProviderId.Length * 2;
            if (entry.Key != null) size += entry.Key.Length * 2;
            if (entry.Payload != null) size += entry.Payload.Length * 2;
            if (entry.BinaryPayload != null) size += entry.BinaryPayload.Length;
            return size;
        }

        // ════════════════════════════════════════════════════════════════════
        // SORTING
        // ════════════════════════════════════════════════════════════════════

        private void EnsureSorted()
        {
            if (m_ProvidersSorted) return;
            m_Providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
            m_ProvidersSorted = true;
        }

        // ════════════════════════════════════════════════════════════════════
        // DIAGNOSTICS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Server] Build a bundle without sending it. Useful for diagnostics 
        /// or size estimation.
        /// </summary>
        public LateJoinBundle BuildBundle(ulong clientId)
        {
            EnsureSorted();

            var allEntries = new List<LateJoinSnapshotEntry>();
            int count = 0;

            for (int i = 0; i < m_Providers.Count; i++)
            {
                var provider = m_Providers[i];
                if (!provider.HasSnapshot) continue;

                try
                {
                    var entries = provider.CollectSnapshots(clientId);
                    if (entries != null && entries.Length > 0)
                    {
                        allEntries.AddRange(entries);
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            return new LateJoinBundle
            {
                ClientId = clientId,
                Entries = allEntries.ToArray(),
                ProviderCount = count,
                Timestamp = Time.time
            };
        }

        /// <summary>
        /// Estimate the byte size of a bundle that would be sent to this client.
        /// </summary>
        public int EstimateBundleSizeForClient(ulong clientId)
        {
            return EstimateBundleSize(BuildBundle(clientId));
        }

        /// <summary>
        /// Clear all pending chunk reassembly state.
        /// Call on disconnect or session end.
        /// </summary>
        public void ClearPendingChunks()
        {
            m_PendingChunks.Clear();
        }
    }
}
