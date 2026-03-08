using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Deterministic hash helpers for network IDs.
    /// </summary>
    public static class StableHashUtility
    {
        // 32-bit FNV-1a constants.
        private const uint FNV_OFFSET_BASIS = 2166136261u;
        private const uint FNV_PRIME = 16777619u;

        public static int GetStableHash(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;

            uint hash = FNV_OFFSET_BASIS;
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= FNV_PRIME;
            }

            return unchecked((int)hash);
        }

        public static int GetStableHash(UnityEngine.Object value)
        {
            if (value == null) return 0;
            
            if (value is Component component)
            {
                return GetStableHash(BuildTransformKey(component.transform, component.GetType().FullName));
            }
            
            if (value is GameObject gameObject)
            {
                return GetStableHash(BuildTransformKey(gameObject.transform, "GameObject"));
            }
            
            string key = $"{value.GetType().FullName}|{value.name}";
            return GetStableHash(key);
        }
        
        private static string BuildTransformKey(Transform transform, string suffix)
        {
            if (transform == null)
            {
                return suffix ?? string.Empty;
            }
            
            var builder = new StringBuilder(128);
            builder.Append(transform.gameObject.scene.path);
            builder.Append('|');
            
            var chain = new Stack<string>(8);
            Transform current = transform;
            while (current != null)
            {
                chain.Push(current.name);
                current = current.parent;
            }
            
            while (chain.Count > 0)
            {
                if (builder[builder.Length - 1] != '|') builder.Append('/');
                builder.Append(chain.Pop());
            }
            
            builder.Append('|');
            builder.Append(suffix);
            return builder.ToString();
        }
    }
}
