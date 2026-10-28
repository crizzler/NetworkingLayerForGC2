#if UNITY_EDITOR
using Arawn.GameCreator2.Networking.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;

namespace Arawn.GameCreator2.Networking.Tests
{
    /// <summary>
    /// Guards the Android player configuration that Fusion needs at runtime. A missing
    /// INTERNET permission is invisible in the Editor and only fails after installing an APK.
    /// </summary>
    [Category("GC2Networking.Android")]
    public sealed class NetworkAndroidPlayerSettingsTests
    {
        [Test]
        public void AndroidPlayerSettings_EnableFusionNetworkingRequirements()
        {
            Assert.That(
                PlayerSettings.Android.forceInternetPermission,
                Is.True,
                "Android APKs need the INTERNET permission for Fusion sockets. Open the " +
                "Scene Setup Wizard > Scene Objects > Optional Integrations and choose " +
                "Configure Android settings.");

            Assert.That(
                (int)(PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64),
                Is.Not.Zero,
                "Android VR/Quest builds require ARM64.");

            Assert.That(
                PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android),
                Is.EqualTo(ScriptingImplementation.IL2CPP),
                "The GC2 Networking Layer Android build is validated with IL2CPP.");

            Assert.That(
                (int)PlayerSettings.Android.minSdkVersion,
                Is.GreaterThanOrEqualTo(NetworkAndroidPlatformSetup.RequiredMinSdk),
                "Fusion Android builds require a supported minimum SDK.");
        }

        [Test]
        public void AndroidPlatformSetup_IsIdempotentAndValidates()
        {
            string report = NetworkAndroidPlatformSetup.ApplyAndroidSettings();
            Assert.That(report, Is.Not.Null.And.Not.Empty);

            Assert.That(
                NetworkAndroidPlatformSetup.TryValidateAndroidSettings(out string error),
                Is.True,
                error);

            string secondReport = NetworkAndroidPlatformSetup.ApplyAndroidSettings();
            Assert.That(secondReport, Is.EqualTo("Android player settings were already configured."));
        }
    }
}
#endif
