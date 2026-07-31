#if GC2_INVENTORY
using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    // [LOCAL-EDIT] #INVENTORY-SERVER-LOOT
    // Adds server-authoritative GC2 loot-container generation on top of Arawn's inventory networking layer.
    [Title("Network Loot Request")]
    [Description("Requests server-authoritative loot generation for a Network Loot Container")]
    [Category("Network/Inventory/Network Loot Request")]
    [Parameter("Loot Container", "GameObject with the NetworkInventoryController and NetworkLootContainer. Usually Self.")]
    [Parameter("Actor", "Player or character GameObject with the NetworkInventoryController that owns the request.")]
    [Parameter("Log Diagnostics", "Print diagnostic logs when this instruction sends or rejects a loot request.")]
    [Keywords("Network", "Inventory", "Loot", "Container", "Server")]
    [Serializable]
    public sealed class InstructionNetworkLootRequest : Instruction
    {
        [Header("Loot Container")]
        [SerializeField]
        [Tooltip("GameObject with the NetworkInventoryController and NetworkLootContainer. Usually Self.")]
        private PropertyGetGameObject m_LootContainer = GetGameObjectSelf.Create();

        [Header("Actor")]
        [SerializeField]
        [Tooltip("Player or character GameObject with the NetworkInventoryController that owns the request.")]
        private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Print diagnostic logs when this instruction sends or rejects a loot request.")]
        private bool m_LogDiagnostics;

        public override string Title => $"Network Loot {m_LootContainer}";

        protected override Task Run(Args args)
        {
            GameObject containerObject = m_LootContainer.Get(args);
            GameObject actorObject = m_Actor.Get(args);

            if (containerObject == null)
            {
                LogWarning("No loot container resolved.");
                return DefaultResult;
            }

            if (actorObject == null)
            {
                LogWarning("No actor resolved.");
                return DefaultResult;
            }

            NetworkInventoryController containerInventory =
                containerObject.GetComponentInParent<NetworkInventoryController>() ??
                containerObject.GetComponentInChildren<NetworkInventoryController>();

            NetworkInventoryController actorInventory =
                actorObject.GetComponentInParent<NetworkInventoryController>() ??
                actorObject.GetComponentInChildren<NetworkInventoryController>();

            if (containerInventory == null)
            {
                LogWarning($"Loot container '{containerObject.name}' has no NetworkInventoryController.");
                return DefaultResult;
            }

            if (actorInventory == null)
            {
                LogWarning($"Actor '{actorObject.name}' has no NetworkInventoryController.");
                return DefaultResult;
            }

            actorInventory.RequestLootGeneration(containerInventory);
            Log($"sent loot request actor={actorObject.name} actorBag={actorInventory.NetworkId} container={containerObject.name} containerBag={containerInventory.NetworkId}");

            return DefaultResult;
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[InstructionNetworkLootRequest] {message}");
        }

        private void LogWarning(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.LogWarning($"[InstructionNetworkLootRequest] {message}");
        }
    }
}
#endif
