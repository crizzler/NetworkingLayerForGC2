# Android and Android VR Setup

Fusion needs Android socket permissions that the Unity Editor does not require.
A project can connect correctly in Play Mode and still fail immediately after
installing an APK when the player has no `android.permission.INTERNET`.

## Configure the player

1. Switch Unity's active build target to **Android**.
2. Open either transport setup wizard:
   - **Game Creator > Networking Layer > Fusion Scene Setup Wizard** →
     **Step 3: Fusion Session**
   - **Game Creator > Networking Layer > PurrNet Scene Setup Wizard** →
     **Step 3: Transport**
3. Find the **Android / Android VR** section and choose
   **Configure Android settings**.
4. Let Unity finish the script reload.

The command enables:

- **Require Internet Permission** (`PlayerSettings.Android.forceInternetPermission`);
- the ARM64 target architecture;
- the IL2CPP scripting backend;
- a minimum Android SDK of 26 where the project was lower.

A build preprocessor applies and validates the same settings before every
Android player build, including CLI and build-server builds, so a project that
was configured before importing the package cannot silently ship an APK without
the permission.

The configure action also checks Unity's External Tools JDK path and tries to
adopt a bundled JDK, `JAVA_HOME`/`JDK_HOME`, or a user-local JDK; when the Android
target is active it repeats this automatically on editor load. If Unity still reports
**JDK not found**, install **Android Build Support > OpenJDK** in Unity Hub, or
open **Edit > Preferences > External Tools** and point **JDK** at a JDK 17+
folder containing `bin/java`. This is a machine/editor setting, not a
project/package asset, so it cannot be stored in the repository.

If you prefer to configure Player Settings manually, use **Edit > Project
Settings > Player > Android > Other Settings** and enable **Require Internet
Permission**. Do not remove `android.permission.INTERNET` from a custom Android
manifest.

## Photon Fusion

Configure the Photon application ID in the normal Fusion/GC2 setup location for
the build. The Editor and every Android player must use the same Photon
application ID and region. Do not commit an application ID or secret to source
control.

For a Meta Quest/Android VR build, also use Unity's Android XR/OpenXR player
configuration required by the target device. The Networking Layer does not
replace the XR provider setup.

## Verify an APK

After building:

```bash
# Point these at your installed Android SDK build-tools.
aapt dump permissions YourGame.apk
aapt dump badging YourGame.apk
```

The first command must list `android.permission.INTERNET`; the second must list
`arm64-v8a`.

If the Android app stops immediately, capture the startup log before changing
networking code:

```bash
adb logcat -c
adb install -r YourGame.apk
adb logcat -d > android-startup.log
```

Look for `AndroidRuntime`, `FATAL EXCEPTION`, and Unity network/Fusion errors.
A missing INTERNET permission normally appears as a socket/permission failure
rather than a Unity Editor error.

## CI validation

The private development repository includes:

```bash
Tools/run_unity_android_build.sh --unity /path/to/Unity
```

The runner creates an isolated project, runs the `GC2Networking.Android`
EditMode checks, builds an APK through the Unity CLI, validates the manifest and
ARM64 ABI with `aapt`, and can optionally install/launch the APK on an attached
`adb` device with `--require-device`.

## Network sync smoke

Build and run the Fusion Core Variables demo as an automated host/client sync
check with `Tools/run_unity_android_device_smoke.sh`. The runner launches the
APK with `gc2-network-smoke-*` intent extras, then requires both peers to log
`[GC2 Network Sync Smoke] role=... passed=true ... humans=2 local=1 remote=1`.
It exits non-zero when either peer is missing the marker or times out. The same
step is exposed as the `network_sync` input on the Android CI workflow.

When two `adb` devices are online, both peers are Android. When only one phone
is connected, the runner builds the same scene as a Linux development player
and tests both directions: phone Host/desktop Client, then desktop Host/phone
Client. This keeps an Android transport regression automatic on an x86_64
development host. The desktop peer is transport/scene coverage only; Android
UI, lifecycle, and XR runtime behavior still need the phone.

Unity 6000.6 no longer builds x86_64 or ARMv7 Android players. The x86_64
Android emulator cannot run an ARM64 APK, and an ARM64 AVD cannot boot on an
x86_64 host because the Android emulator's QEMU2 backend requires a matching
host architecture. Use a physical phone, an ARM64 CI runner/cloud device, or the
phone-plus-desktop fallback. A run where the Android process never logs its own
sync marker is not Android network evidence.

The current validated pair is a Xiaomi `M2103K19PG` (Android 13, arm64) and a
Linux x86_64 desktop player, with both phone-host and phone-client directions
passing `humans=2 local=1 remote=1`. The smoke also exercises the Android
IL2CPP path that previously crashed while registering a remote player prefab;
animation clips are now discovered from Animator controllers instead of
reflecting over every serialized Game Creator component field.
