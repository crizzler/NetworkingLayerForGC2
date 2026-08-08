#if GC2_STATS
using System;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats
{
    [Version(1, 0, 0)]
    [Title("Network Change Stat")]
    [Description("Requests a server-authoritative change to a GC2 Stat")]
    [Category("Network/Stats/Change Stat")]
    [Parameter("Target", "Network Character whose Stat is changed")]
    [Parameter("Stat", "GC2 Stat to change")]
    [Parameter("Operation", "How the supplied value changes the Stat base value")]
    [Parameter("Value", "Value sent to the authoritative Stats controller")]
    [Parameter("Source", "Gameplay source used by server validation")]
    [Keywords("Network", "Stats", "Strength", "Experience", "XP", "Authority")]
    [Image(typeof(IconStat), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkStatsChangeStat : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectLocalNetworkPlayer.Create();
        [SerializeField] private Stat m_Stat;
        [SerializeField] private StatModificationType m_Operation =
            StatModificationType.AddToBase;
        [SerializeField] private PropertyGetDecimal m_Value = new PropertyGetDecimal(1f);
        [SerializeField] private StatModificationSource m_Source =
            StatModificationSource.Direct;

        [NonSerialized] private float m_NextWarningTime;

        public InstructionNetworkStatsChangeStat()
        { }

        public InstructionNetworkStatsChangeStat(
            Stat stat,
            StatModificationType operation,
            float value)
        {
            m_Stat = stat;
            m_Operation = operation;
            m_Value = new PropertyGetDecimal(value);
        }

        public override string Title =>
            $"Network {m_Operation} {m_Stat?.name ?? "(none)"} by {m_Value}";

        protected override Task Run(Args args)
        {
            GameObject target = m_Target.Get(args);
            if (target == null || m_Stat == null) return DefaultResult;

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

            controller.RequestStatModify(
                m_Stat.ID,
                m_Operation,
                (float)m_Value.Get(args),
                m_Source);

            return DefaultResult;
        }

        private void ApplyOffline(Traits traits, Args args)
        {
            RuntimeStatData stat = traits?.RuntimeStats.Get(m_Stat.ID);
            if (stat == null) return;

            float value = (float)m_Value.Get(args);
            switch (m_Operation)
            {
                case StatModificationType.SetBase:
                    stat.Base = value;
                    break;
                case StatModificationType.AddToBase:
                    stat.Base += value;
                    break;
                case StatModificationType.MultiplyBase:
                    stat.Base *= value;
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
                $"[NetworkStats] Stat change for '{target.name}' was not sent because its " +
                $"authoritative Stats route is not ready (networkId=" +
                $"{networkCharacter?.NetworkId ?? 0}, controller={(controller != null)}, " +
                $"server={controller?.IsServer ?? false}, local=" +
                $"{controller?.IsLocalClient ?? false}).",
                target);
        }
    }
}
#endif
