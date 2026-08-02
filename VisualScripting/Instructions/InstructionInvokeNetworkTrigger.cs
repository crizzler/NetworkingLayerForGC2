using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Legacy GC2 Instruction that invokes a registered trigger locally. A custom transport
    /// relay may subscribe to <see cref="NetworkTriggerController.OnTriggerBroadcastRequested"/>,
    /// but the bundled transports intentionally do not execute arbitrary remote triggers.
    /// </summary>
    [Obsolete("Legacy local/custom-transport hook. Use typed network Instructions for gameplay.")]
    [Title("Invoke Legacy Network Trigger (Local Only)")]
    [Description("Invokes a legacy registered trigger locally; requires custom code to relay it")]

    [Category("Network/Legacy/Invoke Registered Trigger (Local Only)")]

    [Parameter("Trigger Name", "The unique name of the trigger to fire")]
    [Parameter("Target", "GameObject with the NetworkTriggerController (defaults to Self)")]

    [Keywords("Network", "Legacy", "Trigger", "Custom Transport", "Local")]

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
        [NonSerialized] private bool m_HasWarnedMissingRelay;

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

            if (!controller.HasBroadcastRelay && !m_HasWarnedMissingRelay)
            {
                m_HasWarnedMissingRelay = true;
                Debug.LogWarning(
                    "[InstructionInvokeNetworkTrigger] No custom broadcast relay is attached. " +
                    "The trigger will execute locally only. Use typed network Instructions for " +
                    "authoritative gameplay.",
                    controller);
            }

            // Execute locally. A custom relay can observe EventBeforeExecute through the
            // controller, but built-in transports deliberately do not relay this operation.
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
