#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Applies and validates the Android player settings required by Photon Fusion and
    /// transport-neutral GC2 networking code. Unity does not add the INTERNET permission
    /// unless the player setting is enabled, which makes a release APK fail at connection time.
    /// </summary>
    public static class NetworkAndroidPlatformSetup
    {
        public const int RequiredMinSdk = 26;
        public const string InternetPermission = "android.permission.INTERNET";
        public const string NetworkStatePermission = "android.permission.ACCESS_NETWORK_STATE";

        [InitializeOnLoadMethod]
        private static void ConfigureDetectedJdkOnLoad()
        {
            EditorApplication.delayCall += () =>
            {
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) return;
                if (TryValidateAndroidJdk(out _)) return;

                if (TryEnsureAndroidJdk(out string report))
                {
                    Debug.Log("[GC2 Networking Android] " + report);
                }
            };
        }

        private const string AndroidExternalToolsTypeName =
            "UnityEditor.Android.AndroidExternalToolsSettings";
        private const string AndroidExtensionsAssemblyName =
            "UnityEditor.Android.Extensions";
        private const string JdkPathPropertyName = "jdkRootPath";

        /// <summary>
        /// Applies the recommended Android player settings and returns a human-readable change
        /// report. Calling this repeatedly is safe.
        /// </summary>
        public static string ApplyAndroidSettings()
        {
            var changes = new List<string>();

            if (!PlayerSettings.Android.forceInternetPermission)
            {
                PlayerSettings.Android.forceInternetPermission = true;
                changes.Add("enabled the Android INTERNET permission");
            }

            AndroidArchitecture architectures = PlayerSettings.Android.targetArchitectures;
            if ((architectures & AndroidArchitecture.ARM64) == 0)
            {
                PlayerSettings.Android.targetArchitectures =
                    architectures | AndroidArchitecture.ARM64;
                changes.Add("enabled the ARM64 architecture");
            }

            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) !=
                ScriptingImplementation.IL2CPP)
            {
                PlayerSettings.SetScriptingBackend(
                    NamedBuildTarget.Android,
                    ScriptingImplementation.IL2CPP);
                changes.Add("selected IL2CPP scripting backend");
            }

            if ((int)PlayerSettings.Android.minSdkVersion < RequiredMinSdk)
            {
                PlayerSettings.Android.minSdkVersion =
                    (AndroidSdkVersions)RequiredMinSdk;
                changes.Add($"raised minimum Android SDK to {RequiredMinSdk}");
            }

            return changes.Count == 0
                ? "Android player settings were already configured."
                : "Applied Android player settings: " + string.Join("; ", changes) + ".";
        }

        /// <summary>
        /// Validates the settings that must be true for a Fusion Android build. The check is
        /// read-only; use <see cref="ApplyAndroidSettings"/> to repair a failing project.
        /// </summary>
        public static bool TryValidateAndroidSettings(out string error)
        {
            var failures = new List<string>();

            if (!PlayerSettings.Android.forceInternetPermission)
            {
                failures.Add(
                    "Require Internet Permission is disabled, so the APK will not receive " +
                    $"{InternetPermission}.");
            }

            if ((PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) == 0)
            {
                failures.Add("ARM64 is not enabled, which is required by current Android VR devices.");
            }

            if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) !=
                ScriptingImplementation.IL2CPP)
            {
                failures.Add("The Android scripting backend is not IL2CPP.");
            }

            if ((int)PlayerSettings.Android.minSdkVersion < RequiredMinSdk)
            {
                failures.Add(
                    $"Android minimum SDK is below {RequiredMinSdk} " +
                    $"({PlayerSettings.Android.minSdkVersion}).");
            }

            error = failures.Count == 0
                ? string.Empty
                : string.Join(" ", failures);
            return failures.Count == 0;
        }

        /// <summary>
        /// Checks Unity's Android External Tools JDK path. Unity raises a modal "JDK not found"
        /// dialog during an Android build when this setting is missing; surface it in the
        /// wizard and CI before the user reaches the build button.
        /// </summary>
        public static bool TryValidateAndroidJdk(out string error)
        {
            if (TryGetAndroidJdkRoot(out string jdkRoot) && IsJdkRootValid(jdkRoot))
            {
                error = string.Empty;
                return true;
            }

            error =
                "Unity's Android JDK is not configured. Install the OpenJDK/Android Build " +
                "Support module in Unity Hub, or set Edit > Preferences > External Tools > " +
                "JDK to a JDK 17+ directory containing bin/java.";
            return false;
        }

        /// <summary>
        /// Finds the Unity-bundled or JAVA_HOME/JDK_HOME JDK and writes it to Unity's External
        /// Tools preference when the current path is empty or invalid. Returns false when no
        /// usable JDK can be found; the caller must show the actionable install/preference
        /// instructions from <see cref="TryValidateAndroidJdk"/>.
        /// </summary>
        public static bool TryEnsureAndroidJdk(out string report)
        {
            if (TryGetAndroidJdkRoot(out string current) && IsJdkRootValid(current))
            {
                report = "Android JDK is configured: " + current;
                return true;
            }

            foreach (string candidate in EnumerateJdkCandidates())
            {
                if (!IsJdkRootValid(candidate) || !TrySetAndroidJdkRoot(candidate))
                {
                    continue;
                }

                report = "Configured the Unity Android JDK: " + candidate;
                return true;
            }

            TryValidateAndroidJdk(out string error);
            report = error;
            return false;
        }

        private static bool TryGetAndroidJdkRoot(out string jdkRoot)
        {
            jdkRoot = string.Empty;
            PropertyInfo property = FindJdkRootProperty();
            if (property == null) return false;

            jdkRoot = property.GetValue(null) as string ?? string.Empty;
            return true;
        }

        private static bool TrySetAndroidJdkRoot(string jdkRoot)
        {
            PropertyInfo property = FindJdkRootProperty();
            if (property == null || !property.CanWrite) return false;

            property.SetValue(null, jdkRoot);
            return true;
        }

        private static PropertyInfo FindJdkRootProperty()
        {
            Type settingsType = FindAndroidExternalToolsType();
            return settingsType?.GetProperty(
                JdkPathPropertyName,
                BindingFlags.Public | BindingFlags.Static);
        }

        private static Type FindAndroidExternalToolsType()
        {
            Type type = Type.GetType(
                AndroidExternalToolsTypeName + ", " + AndroidExtensionsAssemblyName,
                false);
            if (type != null) return type;

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (assembly == null || assembly.IsDynamic) continue;

                type = assembly.GetType(AndroidExternalToolsTypeName, false);
                if (type != null) return type;
            }

            return null;
        }

        private static bool IsJdkRootValid(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;

            string executable = Application.platform == RuntimePlatform.WindowsEditor
                ? "java.exe"
                : "java";
            return File.Exists(Path.Combine(root, "bin", executable));
        }

        private static IEnumerable<string> EnumerateJdkCandidates()
        {
            string contents = EditorApplication.applicationContentsPath;
            string playback = Path.Combine(
                contents,
                "PlaybackEngines",
                "AndroidPlayer");

            yield return Path.Combine(playback, "OpenJDK");
            yield return Path.Combine(playback, "Tools", "OpenJDK");
            yield return Path.Combine(playback, "jdk");

            string userProfile = Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                yield return Path.Combine(userProfile, ".local", "opt", "jdk-17");
                yield return Path.Combine(userProfile, ".local", "opt", "temurin-17");
            }

            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome)) yield return javaHome;

            string jdkHome = Environment.GetEnvironmentVariable("JDK_HOME");
            if (!string.IsNullOrWhiteSpace(jdkHome)) yield return jdkHome;
        }

        /// <summary>
        /// Shared Android/VR status and configuration section for the Fusion and PurrNet
        /// scene setup wizards.
        /// </summary>
        public static void DrawWizardSection()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Android / Android VR", EditorStyles.boldLabel);

            bool valid = TryValidateAndroidSettings(out string error);
            bool jdkValid = TryValidateAndroidJdk(out string jdkError);
            bool androidTarget =
                EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android;

            if (valid && jdkValid)
            {
                EditorGUILayout.HelpBox(
                    "Android player and External Tools settings are ready for Fusion/PurrNet: " +
                    "INTERNET permission, ARM64, IL2CPP, minimum SDK 26, and a valid JDK.",
                    MessageType.Info);
            }
            else
            {
                string details = valid
                    ? jdkError
                    : error + (jdkValid ? string.Empty : "\n" + jdkError);
                EditorGUILayout.HelpBox(
                    "Android build prerequisites need attention: " + details +
                    (androidTarget
                        ? string.Empty
                        : " Switch the active build target to Android before building; " +
                          "the settings can be prepared now."),
                    MessageType.Warning);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                string buttonLabel = valid && jdkValid
                    ? "Re-apply Android settings"
                    : "Configure Android settings";
                if (GUILayout.Button(buttonLabel, GUILayout.Width(230f)))
                {
                    string report = ApplyAndroidSettings();
                    bool jdkReady = TryEnsureAndroidJdk(out string jdkReport);
                    bool configured = TryValidateAndroidSettings(out string configuredError);
                    Debug.Log($"[GC2NetworkingSetupWizard] {report} {jdkReport}");

                    string message = report + "\n" + jdkReport;
                    if (!configured)
                    {
                        message += "\n\nPlayer settings still invalid:\n" + configuredError;
                    }
                    else if (!jdkReady)
                    {
                        message += "\n\nInstall/configure the JDK before building an APK.";
                    }
                    else
                    {
                        message += "\n\nAndroid builds will validate this before packing an APK.";
                    }

                    EditorUtility.DisplayDialog("GC2 Networking - Android", message, "OK");
                }
            }
        }
    }

    /// <summary>
    /// Applies the same Android safeguards before every Android player build, including builds
    /// started from a build server. This prevents a project that was configured before this
    /// package was imported from silently shipping an APK without the INTERNET permission.
    /// </summary>
    internal sealed class NetworkAndroidBuildPreprocessor : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;

            string changes = NetworkAndroidPlatformSetup.ApplyAndroidSettings();
            if (!NetworkAndroidPlatformSetup.TryValidateAndroidSettings(out string error))
            {
                throw new BuildFailedException(
                    "GC2 Networking Layer Android build validation failed: " + error);
            }

            if (!NetworkAndroidPlatformSetup.TryEnsureAndroidJdk(out string jdkReport))
            {
                throw new BuildFailedException(
                    "GC2 Networking Layer Android build validation failed: " + jdkReport);
            }

            Debug.Log($"[GC2 Networking Android] Build preprocessor: {changes} {jdkReport}");
        }
    }
}
#endif
