using System;
using System.Globalization;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public static class NetworkVariableSerializer
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static bool TrySerialize(object value, out string serialized)
        {
            serialized = Serialize(value);
            return serialized != null;
        }

        public static string Serialize(object value)
        {
            if (value == null) return "null:";

            switch (value)
            {
                case float f:
                    return $"float:{f.ToString(Invariant)}";
                case double d:
                    return $"double:{d.ToString(Invariant)}";
                case int i:
                    return $"int:{i}";
                case long l:
                    return $"long:{l}";
                case bool b:
                    return $"bool:{b}";
                case string s:
                    return $"string:{s}";
                case Vector2 v2:
                    return $"vector2:{v2.x.ToString(Invariant)},{v2.y.ToString(Invariant)}";
                case Vector3 v3:
                    return $"vector3:{v3.x.ToString(Invariant)},{v3.y.ToString(Invariant)},{v3.z.ToString(Invariant)}";
                case Quaternion q:
                    return $"quaternion:{q.x.ToString(Invariant)},{q.y.ToString(Invariant)},{q.z.ToString(Invariant)},{q.w.ToString(Invariant)}";
                case Color c:
                    return $"color:{c.r.ToString(Invariant)},{c.g.ToString(Invariant)},{c.b.ToString(Invariant)},{c.a.ToString(Invariant)}";
                default:
                    Debug.LogWarning($"[NetworkVariables] Unsupported variable value type: {value.GetType().Name}");
                    return null;
            }
        }

        public static bool TryDeserialize(string serialized, out object value)
        {
            value = Deserialize(serialized);
            return value != null || string.Equals(serialized, "null:", StringComparison.Ordinal);
        }

        public static object Deserialize(string serialized)
        {
            if (string.IsNullOrEmpty(serialized) || serialized == "null:") return null;

            int colonIndex = serialized.IndexOf(':');
            if (colonIndex < 0) return serialized;

            string type = serialized.Substring(0, colonIndex);
            string data = serialized.Substring(colonIndex + 1);

            switch (type)
            {
                case "float":
                    return float.TryParse(data, NumberStyles.Float, Invariant, out float f) ? f : 0f;
                case "double":
                    return double.TryParse(data, NumberStyles.Float, Invariant, out double d) ? d : 0.0;
                case "int":
                    return int.TryParse(data, NumberStyles.Integer, Invariant, out int i) ? i : 0;
                case "long":
                    return long.TryParse(data, NumberStyles.Integer, Invariant, out long l) ? l : 0L;
                case "bool":
                    return bool.TryParse(data, out bool b) && b;
                case "string":
                    return data;
                case "vector2":
                    return ParseVector2(data);
                case "vector3":
                    return ParseVector3(data);
                case "quaternion":
                    return ParseQuaternion(data);
                case "color":
                    return ParseColor(data);
                default:
                    Debug.LogWarning($"[NetworkVariables] Unknown variable value type prefix: {type}");
                    return data;
            }
        }

        private static Vector2 ParseVector2(string data)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 2) return Vector2.zero;
            float.TryParse(parts[0], NumberStyles.Float, Invariant, out float x);
            float.TryParse(parts[1], NumberStyles.Float, Invariant, out float y);
            return new Vector2(x, y);
        }

        private static Vector3 ParseVector3(string data)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 3) return Vector3.zero;
            float.TryParse(parts[0], NumberStyles.Float, Invariant, out float x);
            float.TryParse(parts[1], NumberStyles.Float, Invariant, out float y);
            float.TryParse(parts[2], NumberStyles.Float, Invariant, out float z);
            return new Vector3(x, y, z);
        }

        private static Quaternion ParseQuaternion(string data)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 4) return Quaternion.identity;
            float.TryParse(parts[0], NumberStyles.Float, Invariant, out float x);
            float.TryParse(parts[1], NumberStyles.Float, Invariant, out float y);
            float.TryParse(parts[2], NumberStyles.Float, Invariant, out float z);
            float.TryParse(parts[3], NumberStyles.Float, Invariant, out float w);
            return new Quaternion(x, y, z, w);
        }

        private static Color ParseColor(string data)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 4) return Color.white;
            float.TryParse(parts[0], NumberStyles.Float, Invariant, out float r);
            float.TryParse(parts[1], NumberStyles.Float, Invariant, out float g);
            float.TryParse(parts[2], NumberStyles.Float, Invariant, out float b);
            float.TryParse(parts[3], NumberStyles.Float, Invariant, out float a);
            return new Color(r, g, b, a);
        }
    }
}
