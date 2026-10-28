using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Reads desktop smoke arguments and their Android intent-extra equivalents.
    /// Android players cannot receive command-line switches through adb, so the same
    /// <c>--gc2-network-smoke-*</c> names are passed with <c>am start --es</c>.
    /// </summary>
    public static class NetworkSmokeArguments
    {
        private static Dictionary<string, string> s_AndroidExtras;

        public static bool TryGetArgument(string name, out string value)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length; i++)
            {
                string argument = arguments[i] ?? string.Empty;
                if (string.Equals(argument, name, StringComparison.Ordinal) &&
                    i + 1 < arguments.Length)
                {
                    value = arguments[i + 1];
                    return true;
                }

                string prefix = name + "=";
                if (argument.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = argument.Substring(prefix.Length);
                    return true;
                }
            }

            value = string.Empty;
#if UNITY_ANDROID && !UNITY_EDITOR
            return TryGetAndroidExtra(NormalizeAndroidKey(name), out value);
#else
            return false;
#endif
        }

        public static int GetIntArgument(string name, int fallback)
        {
            return TryGetArgument(name, out string value) &&
                   int.TryParse(value, out int result)
                ? result
                : fallback;
        }

        public static float GetFloatArgument(string name, float fallback)
        {
            return TryGetArgument(name, out string value) &&
                   float.TryParse(
                       value,
                       System.Globalization.NumberStyles.Float,
                       System.Globalization.CultureInfo.InvariantCulture,
                       out float result)
                ? result
                : fallback;
        }

        public static bool HasFlag(string name)
        {
            if (!TryGetArgument(name, out string value)) return false;
            return string.IsNullOrWhiteSpace(value) ||
                   string.Equals(value, "1", StringComparison.Ordinal) ||
                   string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeAndroidKey(string name)
        {
            return name.TrimStart('-');
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static bool TryGetAndroidExtra(string key, out string value)
        {
            LoadAndroidExtras();
            return s_AndroidExtras.TryGetValue(key, out value) && value != null;
        }

        private static void LoadAndroidExtras()
        {
            if (s_AndroidExtras != null) return;

            s_AndroidExtras = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (var unityPlayer =
                       new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (AndroidJavaObject activity =
                       unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (AndroidJavaObject intent =
                       activity?.Call<AndroidJavaObject>("getIntent"))
                using (AndroidJavaObject extras =
                       intent?.Call<AndroidJavaObject>("getExtras"))
                {
                    if (extras == null) return;

                    using (AndroidJavaObject keySet =
                           extras.Call<AndroidJavaObject>("keySet"))
                    using (AndroidJavaObject iterator =
                           keySet?.Call<AndroidJavaObject>("iterator"))
                    {
                        if (iterator == null) return;

                        while (iterator.Call<bool>("hasNext"))
                        {
                            using (AndroidJavaObject keyObject =
                                   iterator.Call<AndroidJavaObject>("next"))
                            {
                                string key = keyObject?.Call<string>("toString");
                                if (string.IsNullOrEmpty(key)) continue;

                                string extraValue =
                                    extras.Call<string>("getString", key);
                                s_AndroidExtras[key] = extraValue;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "[GC2 Network Smoke] Could not read Android intent extras: " +
                    exception.Message);
            }
        }
#endif
    }
}
