using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// GC2 Instruction that forces a full variable snapshot broadcast via
    /// <see cref="NetworkVariableSync"/>. Useful after batch-setting
    /// variables or during ownership transfer.
    /// </summary>
    [Title("Sync Network Variables")]
    [Description("Forces a full snapshot broadcast of all networked variables on the target")]

    [Category("Network/Variables/Sync Network Variables")]

    [Parameter("Target", "GameObject with the NetworkVariableSync (defaults to Self)")]

    [Keywords("Network", "Variable", "Sync", "Snapshot", "Broadcast", "Force")]

    [Image(typeof(IconRefresh), ColorTheme.Type.Blue)]
    [Serializable]
    public class InstructionSyncNetworkVariables : Instruction
    {
        // MEMBERS: -------------------------------------------------------------------------------

        [SerializeField]
        [Tooltip("The target with the NetworkVariableSync (defaults to Self)")]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        // PROPERTIES: ----------------------------------------------------------------------------

        public override string Title => "Sync Network Variables";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override Task Run(Args args)
        {
            GameObject target = m_Target.Get(args);
            if (target == null) target = args.Self;
            if (target == null) return DefaultResult;

            var sync = target.GetComponent<NetworkVariableSync>();
            if (sync == null)
            {
                Debug.LogWarning($"[InstructionSyncNetworkVariables] " +
                                 $"No NetworkVariableSync on {target.name}");
                return DefaultResult;
            }

            sync.BroadcastFullSnapshot();
            return DefaultResult;
        }
    }
}
