#if GC2_INVENTORY
using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    [Title("Network Pickup Request")]
    [Description("Requests a server-authoritative pickup of a Network World Object")]
    [Category("Network/Inventory/Network Pickup Request")]
    [Parameter("Pickup Source", "GameObject with the NetworkWorldObject to pick up. Usually Self.")]
    [Parameter("Picker", "Player or character GameObject with the NetworkInventoryController that receives the item.")]
    [Parameter("Destination Position", "Inventory destination cell. Use (-1, -1) to let the bag auto-place the item.")]
    [Parameter("Log Diagnostics", "Print diagnostic logs when this instruction sends or rejects a pickup request.")]
    [Keywords("Network", "Inventory", "Pickup", "Item", "World")]
    [Serializable]
    public sealed class InstructionNetworkPickupRequest : Instruction
    {
        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT
        [Header("Pickup")]
        [SerializeField]
        [Tooltip("GameObject with the NetworkWorldObject to pick up. Usually Self.")]
        private PropertyGetGameObject m_PickupSource = GetGameObjectSelf.Create();

        [SerializeField]
        [Tooltip("Player or character GameObject with the NetworkInventoryController that receives the item.")]
        private PropertyGetGameObject m_Picker = GetGameObjectPlayer.Create();

        [SerializeField]
        [Tooltip("Inventory destination cell. Use (-1, -1) to let the bag auto-place the item.")]
        private Vector2Int m_DestinationPosition = TBagContent.INVALID;

        [Header("Debug")]
        [SerializeField]
        [Tooltip("Print diagnostic logs when this instruction sends or rejects a pickup request.")]
        private bool m_LogDiagnostics;

        public override string Title => $"Network Pickup {m_PickupSource}";

        protected override Task Run(Args args)
        {
            GameObject sourceObject = m_PickupSource.Get(args);
            GameObject pickerObject = m_Picker.Get(args);

            if (sourceObject == null)
            {
                LogWarning("No pickup source resolved.");
                return DefaultResult;
            }

            if (pickerObject == null)
            {
                LogWarning("No picker resolved.");
                return DefaultResult;
            }

            NetworkWorldObject worldObject = sourceObject.GetComponentInParent<NetworkWorldObject>();
            if (worldObject == null)
            {
                LogWarning($"Pickup source '{sourceObject.name}' has no NetworkWorldObject.");
                return DefaultResult;
            }

            NetworkInventoryController pickerInventory = pickerObject.GetComponent<NetworkInventoryController>();
            if (pickerInventory == null)
            {
                LogWarning($"Picker '{pickerObject.name}' has no NetworkInventoryController.");
                return DefaultResult;
            }

            pickerInventory.RequestWorldObjectPickup(worldObject, m_DestinationPosition);
            Log(
                $"sent pickup source={sourceObject.name} picker={pickerObject.name} " +
                $"worldObject={worldObject.NetworkId} item={worldObject.Item?.ID.String} destination={m_DestinationPosition}");

            return DefaultResult;
        }

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[InstructionNetworkPickupRequest] {message}");
        }

        private void LogWarning(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.LogWarning($"[InstructionNetworkPickupRequest] {message}");
        }
    }
}
#endif
