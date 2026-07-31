#if GC2_INVENTORY
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    // [LOCAL-EDIT] #INVENTORY-SERVER-LOOT
    // Adds server-authoritative GC2 loot-container generation on top of Arawn's inventory networking layer.
    // Must be attached to any GameObject that will send requests for Loot Table generation.
    // Works alongside InstructionNetworkLootRequest.cs
    [AddComponentMenu("Game Creator/Network/Inventory/Network Loot Container")]
    [DisallowMultipleComponent]
    public sealed class NetworkLootContainer : MonoBehaviour
    {
        [Header("Loot")]
        [SerializeField] private LootTable m_LootTable;
        [SerializeField] private bool m_GenerateOnce = true;

        [Header("Debug")]
        [SerializeField] private bool m_LogDiagnostics;

        private bool m_HasGenerated;

        public LootTable LootTable => m_LootTable;
        public bool GenerateOnce => m_GenerateOnce;
        public bool HasGenerated => m_HasGenerated;
        public bool LogDiagnostics => m_LogDiagnostics;

        public bool CanGenerate()
        {
            if (m_LootTable == null) return false;
            return !m_GenerateOnce || !m_HasGenerated;
        }

        public void MarkGenerated()
        {
            m_HasGenerated = true;
        }
    }
}
#endif
