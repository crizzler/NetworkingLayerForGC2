using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// GC2 Instruction that fires a networked trigger by name via
    /// <see cref="NetworkTriggerController"/>. The trigger is broadcast
    /// to all connected peers.
    /// </summary>
    [Title("Invoke Network Trigger")]
    [Description("Fires a named trigger on the NetworkTriggerController, broadcasting to all peers")]

    [Category("Network/Triggers/Invoke Network Trigger")]

    [Parameter("Trigger Name", "The unique name of the trigger to fire")]
    [Parameter("Target", "GameObject with the NetworkTriggerController (defaults to Self)")]

    [Keywords("Network", "Trigger", "Broadcast", "Fire", "Invoke", "RPC")]

    [Image(typeof(IconTriggers), ColorTheme.Type.Blue)]
    [Serializable]
    public class InstructionInvokeNetworkTrigger : Instruction
    {
        // MEMBERS: -------------------------------------------------------------------------------

        [SerializeField]
        [Tooltip("The unique name of the trigger (must match a TriggerEntry name)")]
        private PropertyGetString m_TriggerName = new PropertyGetString("MyTrigger");

        [SerializeField]
        [Tooltip("The target with the NetworkTriggerController (defaults to Self)")]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        // PROPERTIES: ----------------------------------------------------------------------------

        public override string Title => $"Invoke Network Trigger \"{m_TriggerName}\"";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override Task Run(Args args)
        {
            string triggerName = m_TriggerName.Get(args);
            if (string.IsNullOrEmpty(triggerName)) return DefaultResult;

            GameObject target = m_Target.Get(args);
            if (target == null) target = args.Self;
            if (target == null) return DefaultResult;

            var controller = target.GetComponent<NetworkTriggerController>();
            if (controller == null)
            {
                Debug.LogWarning($"[InstructionInvokeNetworkTrigger] " +
                                 $"No NetworkTriggerController on {target.name}");
                return DefaultResult;
            }

            // Execute locally — the controller's EventBeforeExecute interception
            // will broadcast to the network automatically
            var trigger = controller.GetTriggerByName(triggerName);
            if (trigger != null)
            {
                trigger.Invoke();
            }
            else
            {
                Debug.LogWarning($"[InstructionInvokeNetworkTrigger] " +
                                 $"Trigger '{triggerName}' not found on {target.name}");
            }

            return DefaultResult;
        }
    }
}
