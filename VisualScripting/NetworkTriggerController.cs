using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-agnostic trigger synchronization for GC2 Triggers.
    /// Intercepts trigger execution and broadcasts to all clients via events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attach this to any GameObject with GC2 Triggers that need network sync.
    /// Assign triggers with unique names. When the owner fires a trigger locally,
    /// this component raises <see cref="OnTriggerBroadcastRequested"/> so your
    /// networking solution can relay it to other clients.
    /// </para>
    /// <para>
    /// Persistent triggers are tracked and can be replayed to late joiners via
    /// <see cref="GetPersistentStates"/> and <see cref="ReplayPersistentStates"/>.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Networked Trigger")]
    [DisallowMultipleComponent]
    public class NetworkTriggerController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════
        // NESTED TYPES
        // ════════════════════════════════════════════════════════════════════

        [Serializable]
        public class TriggerEntry
        {
            [Tooltip("Unique name for this trigger (e.g., 'Equip', 'Attack', 'Jump')")]
            public string Name;

            [Tooltip("The GC2 Trigger component")]
            public Trigger Trigger;

            [Tooltip("If true, state is tracked and replayed to late joiners")]
            public bool IsPersistent;

            [Tooltip("Another trigger name that clears this persistent state " +
                     "(e.g., 'Unequip' clears 'Equip')")]
            public string ClearedBy;
        }

        /// <summary>
        /// Payload broadcast when a trigger fires.
        /// Send this over your networking solution.
        /// </summary>
        public struct TriggerBroadcast
        {
            /// <summary>Name of the trigger that fired.</summary>
            public string TriggerName;

            /// <summary>Whether this is a persistent trigger.</summary>
            public bool IsPersistent;

            /// <summary>Name of the persistent trigger this clears (or null).</summary>
            public string ClearsTrigger;

            public const int APPROX_HEADER_BYTES = 6; // flags + lengths
        }

        // ════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════

        [Header("Triggers")]
        [SerializeField] private List<TriggerEntry> m_Triggers = new List<TriggerEntry>();

        [Header("Settings")]
        [Tooltip("If true, this component is the owner (can fire triggers to network)")]
        [SerializeField] private bool m_IsOwner;

        [Header("Debug")]
        [SerializeField] private bool m_DebugMode;

        // ════════════════════════════════════════════════════════════════════
        // FIELDS
        // ════════════════════════════════════════════════════════════════════

        private readonly Dictionary<Trigger, TriggerEntry> m_TriggerLookup
            = new Dictionary<Trigger, TriggerEntry>();

        private readonly Dictionary<string, TriggerEntry> m_NameLookup
            = new Dictionary<string, TriggerEntry>();

        private readonly HashSet<string> m_ActivePersistentStates = new HashSet<string>();

        private readonly Dictionary<Trigger, Action> m_Subscriptions
            = new Dictionary<Trigger, Action>();

        private bool m_IsNetworkExecution;

        // ════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Raised when the owner fires a trigger that needs network broadcast.
        /// Hook this to your networking solution's RPC / message system.
        /// </summary>
        public event Action<TriggerBroadcast> OnTriggerBroadcastRequested;

        /// <summary>
        /// Raised when a persistent trigger state changes (added or cleared).
        /// Useful for syncing SyncLists or similar.
        /// </summary>
        public event Action<string, bool> OnPersistentStateChanged;

        // ════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════

        /// <summary>All configured trigger entries.</summary>
        public IReadOnlyList<TriggerEntry> Triggers => m_Triggers;

        /// <summary>Whether this instance is the owner (can fire triggers).</summary>
        public bool IsOwner
        {
            get => m_IsOwner;
            set => m_IsOwner = value;
        }

        // ════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            RebuildLookups();
        }

        private void OnEnable()
        {
            foreach (var entry in m_Triggers)
            {
                if (entry.Trigger == null) continue;

                string name = entry.Name;
                Action callback = () => OnTriggerBeforeExecute(name);
                m_Subscriptions[entry.Trigger] = callback;
                entry.Trigger.EventBeforeExecute += callback;
            }
        }

        private void OnDisable()
        {
            foreach (var kvp in m_Subscriptions)
            {
                if (kvp.Key != null)
                    kvp.Key.EventBeforeExecute -= kvp.Value;
            }
            m_Subscriptions.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // TRIGGER INTERCEPTION
        // ════════════════════════════════════════════════════════════════════

        private void OnTriggerBeforeExecute(string triggerName)
        {
            // Avoid re-entrance from network execution
            if (m_IsNetworkExecution) return;
            if (!m_IsOwner) return;

            if (!m_NameLookup.TryGetValue(triggerName, out var entry)) return;

            // Track persistent state
            if (entry.IsPersistent)
            {
                m_ActivePersistentStates.Add(triggerName);
                OnPersistentStateChanged?.Invoke(triggerName, true);
            }

            // Handle clearing
            if (!string.IsNullOrEmpty(entry.ClearedBy))
            {
                // This trigger clears another — but ClearedBy means
                // "another trigger whose persistent state I clear"
            }

            // Check if this trigger clears other persistent triggers
            string clears = FindClearedByTrigger(triggerName);

            if (!string.IsNullOrEmpty(clears) && m_ActivePersistentStates.Contains(clears))
            {
                m_ActivePersistentStates.Remove(clears);
                OnPersistentStateChanged?.Invoke(clears, false);
            }

            var broadcast = new TriggerBroadcast
            {
                TriggerName = triggerName,
                IsPersistent = entry.IsPersistent,
                ClearsTrigger = clears
            };

            if (m_DebugMode)
                Debug.Log($"[NetworkTrigger] Broadcasting: {triggerName} " +
                          $"(persistent={entry.IsPersistent}, clears={clears ?? "none"})");

            OnTriggerBroadcastRequested?.Invoke(broadcast);
        }

        // ════════════════════════════════════════════════════════════════════
        // NETWORK RECEIVE (call from your networking callbacks)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Execute a trigger received from the network.
        /// Call this from your networking solution's RPC handler.
        /// </summary>
        public void ExecuteFromNetwork(string triggerName)
        {
            if (!m_NameLookup.TryGetValue(triggerName, out var entry))
            {
                if (m_DebugMode)
                    Debug.LogWarning($"[NetworkTrigger] Trigger not found: {triggerName}");
                return;
            }

            if (entry.Trigger == null)
            {
                if (m_DebugMode)
                    Debug.LogWarning($"[NetworkTrigger] Trigger component null: {triggerName}");
                return;
            }

            if (m_DebugMode)
                Debug.Log($"[NetworkTrigger] ExecuteFromNetwork: {triggerName}");

            m_IsNetworkExecution = true;
            entry.Trigger.Invoke();
            m_IsNetworkExecution = false;
        }

        /// <summary>
        /// Apply a received broadcast (handles persistent state tracking + execution).
        /// Call this from your networking solution on non-owner clients.
        /// </summary>
        public void ApplyBroadcast(TriggerBroadcast broadcast)
        {
            // Track persistent state
            if (broadcast.IsPersistent)
            {
                m_ActivePersistentStates.Add(broadcast.TriggerName);
            }

            if (!string.IsNullOrEmpty(broadcast.ClearsTrigger))
            {
                m_ActivePersistentStates.Remove(broadcast.ClearsTrigger);
            }

            ExecuteFromNetwork(broadcast.TriggerName);
        }

        // ════════════════════════════════════════════════════════════════════
        // PERSISTENT STATE (for late joiners)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get all currently active persistent trigger names.
        /// Send this to late joiners so they can replay state.
        /// </summary>
        public string[] GetPersistentStates()
        {
            var states = new string[m_ActivePersistentStates.Count];
            m_ActivePersistentStates.CopyTo(states);
            return states;
        }

        /// <summary>
        /// Replay persistent states received from the server.
        /// Call this for late joiners.
        /// </summary>
        public void ReplayPersistentStates(string[] activeStates)
        {
            if (activeStates == null) return;

            foreach (string triggerName in activeStates)
            {
                m_ActivePersistentStates.Add(triggerName);
                ExecuteFromNetwork(triggerName);
            }

            if (m_DebugMode)
                Debug.Log($"[NetworkTrigger] Replayed {activeStates.Length} persistent states");
        }

        /// <summary>
        /// Check if a persistent trigger is currently active.
        /// </summary>
        public bool IsPersistentActive(string triggerName)
        {
            return m_ActivePersistentStates.Contains(triggerName);
        }

        /// <summary>
        /// Server-only: manually clear a persistent state.
        /// </summary>
        public void ClearPersistentState(string triggerName)
        {
            if (m_ActivePersistentStates.Remove(triggerName))
            {
                OnPersistentStateChanged?.Invoke(triggerName, false);
            }
        }

        /// <summary>
        /// Server-only: clear all persistent states.
        /// </summary>
        public void ClearAllPersistentStates()
        {
            m_ActivePersistentStates.Clear();
        }

        // ════════════════════════════════════════════════════════════════════
        // LOOKUP
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get a Trigger component by its registered name.
        /// </summary>
        public Trigger GetTriggerByName(string triggerName)
        {
            return m_NameLookup.TryGetValue(triggerName, out var entry) ? entry.Trigger : null;
        }

        /// <summary>
        /// Get a TriggerEntry by name.
        /// </summary>
        public TriggerEntry GetEntryByName(string triggerName)
        {
            return m_NameLookup.TryGetValue(triggerName, out var entry) ? entry : null;
        }

        // ════════════════════════════════════════════════════════════════════
        // PRIVATE
        // ════════════════════════════════════════════════════════════════════

        private void RebuildLookups()
        {
            m_TriggerLookup.Clear();
            m_NameLookup.Clear();

            foreach (var entry in m_Triggers)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;

                m_NameLookup[entry.Name] = entry;
                if (entry.Trigger != null)
                    m_TriggerLookup[entry.Trigger] = entry;
            }
        }

        /// <summary>
        /// Find which persistent trigger is cleared by the given trigger name.
        /// </summary>
        private string FindClearedByTrigger(string triggerName)
        {
            foreach (var entry in m_Triggers)
            {
                if (!string.IsNullOrEmpty(entry.ClearedBy) && entry.ClearedBy == triggerName)
                    return entry.Name;
            }
            return null;
        }
    }
}
