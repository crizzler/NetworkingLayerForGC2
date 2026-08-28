using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Keeps unordered scene-object discovery warning-free on Unity 6000.5 while retaining the
    /// older Unity API used by supported package consumers. Networking lookups never rely on
    /// Unity instance-ID ordering; stable network IDs resolve any meaningful ambiguity.
    /// </summary>
    public static class UnityObjectSearch
    {
        public static T FindAny<T>() where T : Object
        {
            return Object.FindAnyObjectByType<T>();
        }

        public static T FindAny<T>(FindObjectsInactive inactive) where T : Object
        {
            return Object.FindAnyObjectByType<T>(inactive);
        }

        public static Object FindAny(System.Type type)
        {
            return Object.FindAnyObjectByType(type);
        }

        public static Object FindAny(System.Type type, FindObjectsInactive inactive)
        {
            return Object.FindAnyObjectByType(type, inactive);
        }

        public static T[] FindAll<T>(FindObjectsInactive inactive) where T : Object
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType<T>(inactive);
#else
            return Object.FindObjectsByType<T>(inactive, FindObjectsSortMode.None);
#endif
        }

        public static Object[] FindAll(System.Type type, FindObjectsInactive inactive)
        {
#if UNITY_6000_5_OR_NEWER
            return Object.FindObjectsByType(type, inactive);
#else
            return Object.FindObjectsByType(type, inactive, FindObjectsSortMode.None);
#endif
        }
    }
}
