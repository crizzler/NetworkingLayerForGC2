using GameCreator.Runtime.Characters;
using UnityEngine;

/// <summary>
/// Keeps Unity object identity source-compatible across supported Unity and GC2 versions.
/// Legacy 32-bit keys are process-local cache and diagnostic identifiers; exact API forwarding
/// retains Unity's native identifier and is never used as a replicated or persistent identity.
/// </summary>
public static class UnityObjectIdentityExtensions
{
    public static int GetLegacyInstanceId(this Object value)
    {
#if UNITY_6000_5_OR_NEWER
        return value.GetEntityId().GetHashCode();
#else
        return value.GetInstanceID();
#endif
    }

    /// <summary>
    /// Removes one exact attached prefab instance across both GC2 Props API shapes. Current GC2
    /// accepts <c>EntityId</c> on Unity 6000.5, while earlier GC2 sources accept an int.
    /// The private adapter lets overload resolution select the available API without using
    /// Unity's obsolete EntityId/int conversion operators.
    /// </summary>
    public static void RemovePrefabInstance(
        this Props props,
        GameObject prefab,
        GameObject instance)
    {
        if (props == null) throw new System.ArgumentNullException(nameof(props));
        if (instance == null) throw new System.ArgumentNullException(nameof(instance));

        props.RemovePrefab(prefab, new CompatibleObjectId(instance));
    }

    private readonly struct CompatibleObjectId
    {
        private readonly int m_LegacyId;

#if UNITY_6000_5_OR_NEWER
        private readonly EntityId m_EntityId;
#endif

        public CompatibleObjectId(Object value)
        {
#if UNITY_6000_5_OR_NEWER
            m_EntityId = value.GetEntityId();
            m_LegacyId = m_EntityId.GetHashCode();
#else
            m_LegacyId = value.GetInstanceID();
#endif
        }

        public static implicit operator int(CompatibleObjectId value)
        {
            return value.m_LegacyId;
        }

#if UNITY_6000_5_OR_NEWER
        public static implicit operator EntityId(CompatibleObjectId value)
        {
            return value.m_EntityId;
        }
#endif
    }
}
