#if GC2_STATS
using System;
using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Stats;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats
{
    public partial class NetworkStatsController
    {
        private static readonly BindingFlags INSTANCE_FLAGS =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly PropertyInfo RUNTIME_STATS_KEYS_PROPERTY =
            typeof(RuntimeStats).GetProperty("StatsKeys", INSTANCE_FLAGS);

        private static readonly MethodInfo RUNTIME_STATS_GET_HASH_METHOD =
            typeof(RuntimeStats).GetMethod("Get", INSTANCE_FLAGS, null, new[] { typeof(int) }, null);

        private static readonly MethodInfo RUNTIME_ATTRIBUTES_GET_HASH_METHOD =
            typeof(RuntimeAttributes).GetMethod("Get", INSTANCE_FLAGS, null, new[] { typeof(int) }, null);

        private static readonly MethodInfo STAT_SET_BASE_WITHOUT_NOTIFY_METHOD =
            typeof(RuntimeStatData).GetMethod("SetBaseWithoutNotify", INSTANCE_FLAGS, null, new[] { typeof(double) }, null);

        private static readonly MethodInfo ATTRIBUTE_SET_VALUE_WITHOUT_NOTIFY_METHOD =
            typeof(RuntimeAttributeData).GetMethod("SetValueWithoutNotify", INSTANCE_FLAGS, null, new[] { typeof(double) }, null);

        private RuntimeStatData GetRuntimeStatByHash(int statHash)
        {
            if (m_Traits?.RuntimeStats == null) return null;

            if (RUNTIME_STATS_GET_HASH_METHOD != null)
            {
                return RUNTIME_STATS_GET_HASH_METHOD.Invoke(m_Traits.RuntimeStats, new object[] { statHash }) as RuntimeStatData;
            }

            return null;
        }

        private RuntimeAttributeData GetRuntimeAttributeByHash(int attributeHash)
        {
            if (m_Traits?.RuntimeAttributes == null) return null;

            if (RUNTIME_ATTRIBUTES_GET_HASH_METHOD != null)
            {
                return RUNTIME_ATTRIBUTES_GET_HASH_METHOD.Invoke(m_Traits.RuntimeAttributes, new object[] { attributeHash }) as RuntimeAttributeData;
            }

            return null;
        }

        private IEnumerable<int> EnumerateRuntimeStatHashes()
        {
            if (m_Traits?.RuntimeStats == null) yield break;

            if (RUNTIME_STATS_KEYS_PROPERTY?.GetValue(m_Traits.RuntimeStats) is IEnumerable<int> reflectedKeys)
            {
                foreach (int statHash in reflectedKeys)
                {
                    yield return statHash;
                }

                yield break;
            }

            // Fallback: enumerate class definition IDs.
            Class traitsClass = m_Traits.Class;
            if (traitsClass == null) yield break;

            for (int i = 0; i < traitsClass.StatsLength; i++)
            {
                StatItem statItem = traitsClass.GetStat(i);
                if (statItem?.Stat == null) continue;
                yield return statItem.Stat.ID.Hash;
            }
        }

        private IEnumerable<int> EnumerateRuntimeAttributeHashes()
        {
            if (m_Traits?.RuntimeAttributes == null) yield break;

            List<int> attributes = m_Traits.RuntimeAttributes.AttributesKeys;
            if (attributes == null) yield break;

            for (int i = 0; i < attributes.Count; i++)
            {
                yield return attributes[i];
            }
        }

        private static void SetStatBaseSilently(RuntimeStatData stat, float value)
        {
            if (stat == null) return;

            if (STAT_SET_BASE_WITHOUT_NOTIFY_METHOD != null)
            {
                STAT_SET_BASE_WITHOUT_NOTIFY_METHOD.Invoke(stat, new object[] { (double)value });
                return;
            }

            stat.Base = value;
        }

        private static void SetAttributeValueSilently(RuntimeAttributeData attribute, float value)
        {
            if (attribute == null) return;

            if (ATTRIBUTE_SET_VALUE_WITHOUT_NOTIFY_METHOD != null)
            {
                ATTRIBUTE_SET_VALUE_WITHOUT_NOTIFY_METHOD.Invoke(attribute, new object[] { (double)value });
                return;
            }

            attribute.Value = value;
        }
    }
}
#endif
