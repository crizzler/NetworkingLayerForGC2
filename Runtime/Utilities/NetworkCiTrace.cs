using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Correlated diagnostics for opt-in multi-process CI jobs. Calls are omitted from
    /// non-development players and the logger remains dormant unless the dedicated command-line
    /// switch is present, so customer builds never emit this trace accidentally.
    /// </summary>
    public static class NetworkCiTrace
    {
        public const string EnableArgument = "--gc2-network-ci-trace";
        public const string RoleArgument = "--gc2-network-trace-role";
        public const string FrameRateArgument = "--gc2-network-trace-frame-rate";
        public const string FrameRateEnvironmentVariable = "GC2_NETWORK_CI_FRAME_RATE";
        public const int DefaultTraceFrameRate = 60;
        public const float TraversalTraceLingerSeconds = 30f;

        private static bool? s_Enabled;
        private static string s_Role;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static bool s_FramePacingApplied;
        private static readonly HashSet<uint> s_ActiveTraversalActors = new();
        private static readonly Dictionary<string, float> s_LastSamples = new(128);
        private static readonly Dictionary<string, string> s_LastValues = new(64);
        private static float s_TraversalTraceUntil;
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRuntimeState()
        {
            s_Enabled = null;
            s_Role = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            s_FramePacingApplied = false;
            s_ActiveTraversalActors.Clear();
            s_LastSamples.Clear();
            s_LastValues.Clear();
            s_TraversalTraceUntil = 0f;
#endif
        }

        /// <summary>
        /// Two Development players on one workstation can otherwise run at thousands of frames
        /// per second and starve each other's networking pumps while the verbose trace is active.
        /// Keep the diagnostic environment controlled by default. This never runs in the Editor
        /// or a non-development customer player, and an explicit value of 0 disables the cap.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyTraceFramePacing()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Application.isEditor || s_FramePacingApplied || !Enabled) return;

            int targetFrameRate = ResolveTraceFrameRate();
            if (targetFrameRate <= 0) return;

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = targetFrameRate;
            s_FramePacingApplied = true;
            Log(
                "trace-runtime",
                "frame-pacing",
                0,
                0,
                $"targetFrameRate={targetFrameRate} vSyncCount={QualitySettings.vSyncCount}");
#endif
        }

        public static bool Enabled
        {
            get
            {
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
                return false;
#else
                if (s_Enabled.HasValue) return s_Enabled.Value;

                string[] arguments = Environment.GetCommandLineArgs();
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (!string.Equals(
                            arguments[i],
                            EnableArgument,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    s_Enabled = true;
                    return true;
                }

                s_Enabled = string.Equals(
                    Environment.GetEnvironmentVariable("GC2_NETWORK_CI_TRACE"),
                    "1",
                    StringComparison.Ordinal);
                return s_Enabled.Value;
#endif
            }
        }

        public static bool HasTraversalActivity
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return Enabled && s_ActiveTraversalActors.Count > 0;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Remains active briefly after the final traversal ends. A stalled Fusion runner or
        /// input route often becomes visible only during exit/recovery, so ending the trace on
        /// the same frame as the GC2 stance would discard the most useful evidence.
        /// </summary>
        public static bool TraversalTraceWindowActive
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                return Enabled &&
                       (s_ActiveTraversalActors.Count > 0 ||
                        Time.realtimeSinceStartup <= s_TraversalTraceUntil);
#else
                return false;
#endif
            }
        }

        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void SetTraversalActivity(uint actorNetworkId, bool active)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (actorNetworkId == 0) return;

            bool changed = active
                ? s_ActiveTraversalActors.Add(actorNetworkId)
                : s_ActiveTraversalActors.Remove(actorNetworkId);
            s_TraversalTraceUntil = Mathf.Max(
                s_TraversalTraceUntil,
                Time.realtimeSinceStartup + TraversalTraceLingerSeconds);

            if (!changed) return;
            Log(
                "traversal-window",
                active ? "actor-entered" : "actor-exited",
                actorNetworkId,
                0,
                $"activeActors={s_ActiveTraversalActors.Count} " +
                $"lingerSeconds={TraversalTraceLingerSeconds:F0}");
#endif
        }

        public static bool ShouldSample(string key, float interval = 0.5f)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!TraversalTraceWindowActive || string.IsNullOrEmpty(key)) return false;

            float now = Time.realtimeSinceStartup;
            if (s_LastSamples.TryGetValue(key, out float previous) &&
                now - previous < Mathf.Max(0.01f, interval))
            {
                return false;
            }

            s_LastSamples[key] = now;
            return true;
#else
            return false;
#endif
        }

        public static bool HasChanged(string key, string value)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!TraversalTraceWindowActive || string.IsNullOrEmpty(key)) return false;

            value ??= string.Empty;
            if (s_LastValues.TryGetValue(key, out string previous) && previous == value)
            {
                return false;
            }

            s_LastValues[key] = value;
            return true;
#else
            return false;
#endif
        }

        [Conditional("UNITY_EDITOR")]
        [Conditional("DEVELOPMENT_BUILD")]
        public static void Log(
            string channel,
            string stage,
            uint actorNetworkId,
            uint correlationId,
            string detail,
            UnityEngine.Object context = null)
        {
            if (!Enabled) return;

            Debug.LogFormat(
                LogType.Log,
                LogOption.NoStacktrace,
                context,
                "{0}",
                $"[GC2NetworkCI] role={Role} channel={Safe(channel)} stage={Safe(stage)} " +
                $"actor={actorNetworkId} correlation={correlationId} " +
                $"frame={Time.frameCount} realtime={Time.realtimeSinceStartup:F3} {detail ?? string.Empty}");
        }

        private static string Role
        {
            get
            {
                if (s_Role != null) return s_Role;
                s_Role = GetArgument(RoleArgument);
                if (string.IsNullOrWhiteSpace(s_Role))
                {
                    s_Role = GetArgument("--gc2-network-smoke-role");
                }
                if (string.IsNullOrWhiteSpace(s_Role)) s_Role = "unknown";
                return s_Role;
            }
        }

        private static string GetArgument(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < arguments.Length; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.Ordinal))
                {
                    return arguments[i + 1] ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static int ResolveTraceFrameRate()
        {
            string value = GetArgument(FrameRateArgument);
            if (string.IsNullOrWhiteSpace(value))
            {
                value = Environment.GetEnvironmentVariable(
                    FrameRateEnvironmentVariable);
            }

            if (string.IsNullOrWhiteSpace(value)) return DefaultTraceFrameRate;
            if (!int.TryParse(value, out int parsed)) return DefaultTraceFrameRate;
            if (parsed <= 0) return 0;
            return Mathf.Clamp(parsed, 30, 240);
        }

        private static string Safe(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value.Replace(' ', '-');
        }
    }
}
