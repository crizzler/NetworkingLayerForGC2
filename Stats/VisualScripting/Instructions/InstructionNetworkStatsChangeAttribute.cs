#if GC2_STATS
using System;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using GCAttribute = GameCreator.Runtime.Stats.Attribute;

namespace Arawn.GameCreator2.Networking.Stats
{
    [Version(1, 0, 0)]
    [Title("Network Change Attribute")]
    [Description("Requests a server-authoritative change to a GC2 Attribute")]
    [Category("Network/Stats/Change Attribute")]
    [Parameter("Target", "Network Character whose Attribute is changed")]
    [Parameter("Attribute", "GC2 Attribute to change")]
    [Parameter("Operation", "How the supplied value changes the Attribute")]
    [Parameter("Value", "Value sent to the authoritative Stats controller")]
    [Parameter("Source", "Gameplay source used by server validation")]
    [Keywords("Network", "Stats", "Health", "HP", "Mana", "Stamina", "Authority")]
    [Image(typeof(IconAttr), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkStatsChangeAttribute : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectLocalNetworkPlayer.Create();
        [SerializeField] private GCAttribute m_Attribute;
        [SerializeField] private AttributeModificationType m_Operation =
            AttributeModificationType.Add;
        [SerializeField] private PropertyGetDecimal m_Value = new PropertyGetDecimal(-10f);
        [SerializeField] private StatModificationSource m_Source =
            StatModificationSource.Direct;

        [NonSerialized] private float m_NextWarningTime;

        public InstructionNetworkStatsChangeAttribute()
        { }

        public InstructionNetworkStatsChangeAttribute(
            GCAttribute attribute,
            AttributeModificationType operation,
            float value)
        {
            m_Attribute = attribute;
            m_Operation = operation;
            m_Value = new PropertyGetDecimal(value);
        }

        public override string Title =>
            $"Network {m_Operation} {m_Attribute?.name ?? "(none)"} by {m_Value}";

        protected override Task Run(Args args)
        {
            GameObject target = m_Target.Get(args);
            if (target == null || m_Attribute == null) return DefaultResult;

            Traits traits = target.Get<Traits>();
            NetworkStatsController controller = target.Get<NetworkStatsController>();
            NetworkCharacter networkCharacter = target.Get<NetworkCharacter>();

            if (networkCharacter == null && controller == null)
            {
                ApplyOffline(traits, args);
                return DefaultResult;
            }

            if (networkCharacter != null && !networkCharacter.IsOwnerInstance &&
                (controller == null || !controller.IsServer))
            {
                WarnUnavailableRoute(target, networkCharacter, controller);
                return DefaultResult;
            }

            if (controller == null || controller.NetworkId == 0 ||
                (!controller.IsServer && !controller.IsLocalClient))
            {
                WarnUnavailableRoute(target, networkCharacter, controller);
                return DefaultResult;
            }

            controller.RequestAttributeModify(
                m_Attribute.ID,
                m_Operation,
                (float)m_Value.Get(args),
                m_Source);

            return DefaultResult;
        }

        private void ApplyOffline(Traits traits, Args args)
        {
            RuntimeAttributeData attribute = traits?.RuntimeAttributes.Get(m_Attribute.ID);
            if (attribute == null) return;

            float value = (float)m_Value.Get(args);
            switch (m_Operation)
            {
                case AttributeModificationType.Set:
                    attribute.Value = value;
                    break;
                case AttributeModificationType.Add:
                    attribute.Value += value;
                    break;
                case AttributeModificationType.SetPercent:
                    attribute.Value = attribute.MinValue +
                                      (attribute.MaxValue - attribute.MinValue) * value;
                    break;
                case AttributeModificationType.AddPercent:
                    attribute.Value += (attribute.MaxValue - attribute.MinValue) * value;
                    break;
            }
        }

        private void WarnUnavailableRoute(
            GameObject target,
            NetworkCharacter networkCharacter,
            NetworkStatsController controller)
        {
            if (UnityEngine.Time.unscaledTime < m_NextWarningTime) return;
            m_NextWarningTime = UnityEngine.Time.unscaledTime + 5f;

            Debug.LogWarning(
                $"[NetworkStats] Attribute change for '{target.name}' was not sent because " +
                $"its authoritative Stats route is not ready (networkId=" +
                $"{networkCharacter?.NetworkId ?? 0}, controller={(controller != null)}, " +
                $"server={controller?.IsServer ?? false}, local=" +
                $"{controller?.IsLocalClient ?? false}).",
                target);
        }
    }
}
#endif
