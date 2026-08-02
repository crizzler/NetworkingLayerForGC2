using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Authority-only GC2 Instruction that builds a snapshot from the current profiled
    /// variable manager and broadcasts it through the active transport adapter.
    /// </summary>
    [Title("Sync Network Variables")]
    [Description("Broadcasts the current profiled network-variable state from the logical authority")]

    [Category("Network/Variables/Sync Network Variables")]

    [Parameter("Manager", "Optional GameObject with the Network Variable Manager; otherwise uses the active manager")]

    [Keywords("Network", "Variable", "Sync", "Snapshot", "Broadcast", "Force")]

    [Image(typeof(IconRefresh), ColorTheme.Type.Blue)]
    [Serializable]
    public class InstructionSyncNetworkVariables : Instruction
    {
        // MEMBERS: -------------------------------------------------------------------------------

        [SerializeField]
        [Tooltip("Optional target with the current Network Variable Manager")]
        private PropertyGetGameObject m_Target = new PropertyGetGameObject();

        // PROPERTIES: ----------------------------------------------------------------------------

        public override string Title => "Sync Network Variables";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override Task Run(Args args)
        {
            GameObject target = m_Target.Get(args);
            NetworkVariableManager manager = target != null
                ? target.GetComponent<NetworkVariableManager>()
                : null;
            if (manager == null) manager = NetworkVariableManager.Instance;

            if (manager == null)
            {
                Debug.LogWarning(
                    "[InstructionSyncNetworkVariables] No NetworkVariableManager is available.");
                return DefaultResult;
            }

            if (!manager.BroadcastFullSnapshot())
            {
                Debug.LogWarning(
                    "[InstructionSyncNetworkVariables] The snapshot was not broadcast. " +
                    "Run this instruction on the logical network authority and verify the Variables transport adapter.",
                    manager);
            }

            return DefaultResult;
        }
    }
}
