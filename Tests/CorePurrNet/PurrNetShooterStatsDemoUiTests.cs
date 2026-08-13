using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Arawn.GameCreator2.Networking.Transport.PurrNet;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    /// <summary>
    /// Guards the responsive UI contract of the installable PurrNet Shooter + Stats demo.
    /// Optional Shooter and Stats assemblies are deliberately not referenced here; the scene,
    /// runtime session card, and Game Creator installer payload are the stable integration edge.
    /// </summary>
    public sealed class PurrNetShooterStatsDemoUiTests
    {
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.ShooterExamples@1.0.2";

        private const string ScenePath = InstallRoot +
            "/Requires Shooter Demos - PurrNetShooterStatsDemo.unity";
        private const string PlayerPrefabPath = InstallRoot +
            "/PurrNetDemoPlayer-ShooterAndStats.prefab";
        private const string ProfilesPath = InstallRoot + "/Profiles";
        private const string NetworkPrefabsPath = ProfilesPath +
            "/PurrNetDemoNetworkPrefabs 37.asset";
        private const string VariableProfilePath = ProfilesPath +
            "/PurrNetDemoNetworkVariableProfile 21.asset";
        private const string SceneVariableProfilePath = ProfilesPath +
            "/PurrNetDemoSceneNetworkVariableProfile 20.asset";
        private const string MonitorPath = InstallRoot +
            "/PurrNetShooterStatsDemoMonitor.cs";
        private const string WeaponFolderPath = InstallRoot + "/Weapons";
        private const string CustomWeaponPath = WeaponFolderPath +
            "/PurrNetDemo_AK_Weapon.asset";

        private const string StockWeaponPath =
            "Assets/Plugins/GameCreator/Installs/" +
            "Shooter.Weapons@1.1.4/Weapons/AK_Weapon.asset";
        private const string HealthAttributePath =
            "Assets/Plugins/GameCreator/Installs/" +
            "Stats.Classes@1.3.7/_Stats/Health/HP.asset";
        private const string ColliderFreeImpactPrefabPath =
            "Assets/Plugins/GameCreator/Installs/" +
            "Shooter.Weapons@1.1.4/Effects/Hits/Hit_Gun.prefab";
        private const string FootstepImpactPrefabPath =
            "Assets/Plugins/GameCreator/Installs/" +
            "GameCreator.Characters@1.8.25/Assets/Particles/FX_Footstep.prefab";

        private const string DemoUiPath =
            "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/" +
            "PurrNetDemoCanvasUI.cs";
        private const string WizardPath =
            "Assets/Arawn/NetworkingLayerForGC2/Editor/Transport/PurrNet/" +
            "PurrNetSceneSetupWizard.cs";
        private const string InstallerDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Shooter/" +
            "GC2NetworkingLayerPurrNetTransport.ShooterExamples.asset";
        private const string PackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Shooter/" +
            "Package.unitypackage";

        private const float Gap = 12f;

        [Test]
        public void ShooterStatsDemo_UsesUniqueCustomWeaponEverywhere()
        {
            AssertAssetExists(ScenePath);
            AssertAssetExists(PlayerPrefabPath);
            AssertAssetExists(CustomWeaponPath);
            AssertAssetExists(StockWeaponPath);

            string customGuid = AssetDatabase.AssetPathToGUID(CustomWeaponPath);
            string stockGuid = AssetDatabase.AssetPathToGUID(StockWeaponPath);
            Assert.That(customGuid, Is.Not.Empty);
            Assert.That(stockGuid, Is.Not.Empty);
            Assert.That(customGuid, Is.Not.EqualTo(stockGuid),
                "The PurrNet demo AK must have a unique Unity GUID.");
            Assert.That(
                AssetDatabase.GUIDToAssetPath(customGuid),
                Is.EqualTo(CustomWeaponPath));

            string customWeapon = ReadAsset(CustomWeaponPath);
            string stockWeapon = ReadAsset(StockWeaponPath);
            string customId = ReadGc2AssetId(customWeapon);
            string stockId = ReadGc2AssetId(stockWeapon);
            Assert.That(customId, Is.Not.Empty);
            Assert.That(stockId, Is.Not.Empty);
            Assert.That(customId, Is.Not.EqualTo(stockId),
                "Cloning the AK must regenerate its GC2 ID as well as its Unity GUID.");
            StringAssert.Contains("m_Name: PurrNetDemo_AK_Weapon", customWeapon);

            string playerPrefab = ReadAsset(PlayerPrefabPath);
            string scene = ReadAsset(ScenePath);
            Assert.That(
                CountGuidReferences(playerPrefab, customGuid),
                Is.EqualTo(9),
                "All nine equip, aim, shoot, and reload references must use the PurrNet demo AK.");
            Assert.That(
                CountGuidReferences(scene, customGuid),
                Is.EqualTo(1),
                "The scene registry must contain the PurrNet demo AK exactly once.");
            Assert.That(
                CountGuidReferences(playerPrefab, stockGuid),
                Is.Zero,
                "The player prefab must not retain the stock Shooter example AK.");
            Assert.That(
                CountGuidReferences(scene, stockGuid),
                Is.Zero,
                "The scene must not register the stock Shooter example AK.");
        }

        [Test]
        public void ShooterStatsDemo_CustomWeaponHasDeterministicNetworkHpDamage()
        {
            string weapon = ReadAsset(CustomWeaponPath);
            string healthGuid = AssetDatabase.AssetPathToGUID(HealthAttributePath);
            Assert.That(healthGuid, Is.Not.Empty, "The GC2 HP Attribute dependency is missing.");

            string onHit = Slice(weapon, "  m_OnHit:", "  m_Animations:");
            MatchCollection onHitReferences = Regex.Matches(
                onHit,
                @"(?m)^\s+- rid: ([0-9]+)\s*$");
            Assert.That(onHitReferences.Count, Is.EqualTo(1),
                "The weapon should execute exactly one authoritative On Hit instruction.");

            string instructionRid = onHitReferences[0].Groups[1].Value;
            string instruction = ReadManagedReference(weapon, instructionRid);
            StringAssert.Contains(
                "type: {class: InstructionNetworkStatsChangeAttribute, " +
                "ns: Arawn.GameCreator2.Networking.Stats, " +
                "asm: Arawn.GameCreator2.Networking.Stats}",
                instruction);
            StringAssert.Contains(
                $"m_Attribute: {{fileID: 11400000, guid: {healthGuid}, type: 2}}",
                instruction);
            StringAssert.Contains("m_Operation: 1", instruction,
                "Serialized AttributeModificationType.Add must remain selected.");
            StringAssert.Contains("m_Source: 1", instruction,
                "Serialized StatModificationSource.Combat must remain selected.");

            string targetRid = ReadNestedPropertyRid(instruction, "m_Target");
            string target = ReadManagedReference(weapon, targetRid);
            StringAssert.Contains(
                "type: {class: GetGameObjectFindComponentInParents, " +
                "ns: GameCreator.Runtime.Common, " +
                "asm: GameCreator.Runtime.Core}",
                target,
                "Shooter hits can originate on child colliders, so the target must resolve " +
                "the parent NetworkStatsController.");
            StringAssert.Contains(
                "m_TypeName: Arawn.GameCreator2.Networking.Stats.NetworkStatsController,",
                target);
            StringAssert.Contains(
                "Arawn.GameCreator2.Networking.Stats, Version=0.0.0.0, Culture=neutral,",
                target);

            string hitTargetRid = ReadNestedPropertyRid(target, "m_From");
            string hitTarget = ReadManagedReference(weapon, hitTargetRid);
            StringAssert.Contains(
                "type: {class: GetGameObjectTarget, ns: GameCreator.Runtime.Common, " +
                "asm: GameCreator.Runtime.Core}",
                hitTarget,
                "The parent lookup must begin at GC2's Shooter hit target.");

            string valueRid = ReadNestedPropertyRid(instruction, "m_Value");
            string value = ReadManagedReference(weapon, valueRid);
            StringAssert.Contains(
                "type: {class: GetDecimalDecimal, ns: GameCreator.Runtime.Common, " +
                "asm: GameCreator.Runtime.Core}",
                value);
            StringAssert.IsMatch(@"(?m)^\s*m_Value: -10(?:\.0+)?\s*$", value,
                "Every authoritative hit must subtract exactly 10 HP.");

            Match powerReference = Regex.Match(
                weapon,
                @"(?m)^\s+m_Power:\r?\n\s+m_Property:\r?\n\s+rid: ([0-9]+)\s*$");
            Assert.That(powerReference.Success, Is.True,
                "The demo weapon needs a deterministic serialized power value.");
            string power = ReadManagedReference(
                weapon,
                powerReference.Groups[1].Value);
            StringAssert.Contains("type: {class: GetDecimalDecimal", power);
            StringAssert.IsMatch(@"(?m)^\s*m_Value: 10(?:\.0+)?\s*$", power,
                "The bridge receives Shooter power as damage, so it must remain 10.");

            string playerPrefab = ReadAsset(PlayerPrefabPath);
            string validation = Slice(
                playerPrefab,
                "  m_ValidationConfig:",
                "  m_LogDiagnostics:");
            StringAssert.Contains("DefaultBaseDamage: 10", validation,
                "Fallback server validation damage must match the authored On Hit damage.");
            StringAssert.Contains("HeadDamageMultiplier: 1", validation);
            StringAssert.Contains("TorsoDamageMultiplier: 1", validation);
            StringAssert.Contains("LegsDamageMultiplier: 1", validation);
            StringAssert.Contains("DefaultFalloffStartDistance: 100", validation);
            StringAssert.Contains("MinDamageFalloff: 1", validation,
                "The deterministic demo must not vary damage by distance.");

            string statsTypes = ReadAsset(
                "Assets/Arawn/NetworkingLayerForGC2/Stats/NetworkStatsTypes.cs");
            StringAssert.Contains("Add = 1", statsTypes);
            StringAssert.Contains("Combat = 1", statsTypes);
        }

        [Test]
        public void ShooterStatsDemo_CustomWeaponUsesColliderFreeGunImpact()
        {
            AssertAssetExists(CustomWeaponPath);
            AssertAssetExists(ColliderFreeImpactPrefabPath);
            AssertAssetExists(FootstepImpactPrefabPath);

            string weapon = ReadAsset(CustomWeaponPath);
            string impactGuid = AssetDatabase.AssetPathToGUID(ColliderFreeImpactPrefabPath);
            string footstepGuid = AssetDatabase.AssetPathToGUID(FootstepImpactPrefabPath);
            Assert.That(impactGuid, Is.Not.Empty);
            Assert.That(footstepGuid, Is.Not.Empty);
            Assert.That(impactGuid, Is.Not.EqualTo(footstepGuid));

            Match impactReference = Regex.Match(
                weapon,
                @"(?m)^\s+m_ImpactEffect:\r?\n\s+m_Property:\r?\n\s+rid: ([0-9]+)\s*$");
            Assert.That(impactReference.Success, Is.True,
                "The Shooter weapon must serialize an impact-effect property.");

            string impactProperty = ReadManagedReference(
                weapon,
                impactReference.Groups[1].Value);
            StringAssert.Contains(
                "type: {class: GetGameObjectInstance, ns: GameCreator.Runtime.Common, " +
                "asm: GameCreator.Runtime.Core}",
                impactProperty);
            StringAssert.Contains($"guid: {impactGuid}", impactProperty,
                "The demo AK must use Shooter's collider-free Hit_Gun impact prefab.");
            StringAssert.DoesNotContain($"guid: {footstepGuid}", impactProperty,
                "FX_Footstep owns a physical collider that can intercept subsequent rounds.");
            Assert.That(CountGuidReferences(weapon, impactGuid), Is.EqualTo(1));
            Assert.That(CountGuidReferences(weapon, footstepGuid), Is.Zero);

            GameObject impactPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                ColliderFreeImpactPrefabPath);
            Assert.That(impactPrefab, Is.Not.Null);
            Collider[] colliders = impactPrefab.GetComponentsInChildren<Collider>(true);
            Assert.That(colliders, Is.Empty,
                "Impact VFX must never introduce colliders into the projectile hit path.");
        }

        [Test]
        public void ShooterStatsDemo_SceneContainsDamageBridgeAndReplicatedHealthMonitor()
        {
            AssertAssetExists(MonitorPath);

            string scene = ReadAsset(ScenePath);
            string monitor = ReadAsset(MonitorPath);
            string healthGuid = AssetDatabase.AssetPathToGUID(HealthAttributePath);
            string monitorGuid = AssetDatabase.AssetPathToGUID(MonitorPath);

            const string bridgeType =
                "Arawn.GameCreator2.Networking.Stats.Shooter::" +
                "Arawn.GameCreator2.Networking.Stats.Shooter." +
                "NetworkShooterStatsDamageBridge";
            Assert.That(CountOccurrences(scene, bridgeType), Is.EqualTo(1),
                "The scene needs exactly one server-side Shooter to Stats damage bridge.");
            StringAssert.Contains(
                $"m_HealthAttribute: {{fileID: 11400000, guid: {healthGuid}, type: 2}}",
                scene);
            StringAssert.Contains("m_FallbackHealthAttributeId: hp", scene);

            Assert.That(monitorGuid, Is.Not.Empty);
            Assert.That(
                CountGuidReferences(scene, monitorGuid),
                Is.EqualTo(1),
                "The scene should contain one replicated-health monitor component.");
            string monitorComponent = ReadSceneComponentByScriptGuid(scene, monitorGuid);
            StringAssert.Contains(
                $"m_Health: {{fileID: 11400000, guid: {healthGuid}, type: 2}}",
                monitorComponent);
            StringAssert.Contains(
                "m_ReservedControlsPanel: {fileID: 1069764099}",
                monitorComponent,
                "The health monitor must reserve the authored Controls Panel.");
            StringAssert.Contains(
                "m_SessionUI: {fileID: 618092877}",
                monitorComponent,
                "The monitor must also reserve the generated PurrNet session card.");
            StringAssert.Contains("--- !u!224 &1069764099", scene);
            StringAssert.Contains("--- !u!114 &618092877", scene);

            StringAssert.Contains(
                "Each validated hit from PurrNetDemo_AK_Weapon deals 10 HP damage.",
                monitor);
            StringAssert.Contains("FindObjectsByType<NetworkStatsController>", monitor,
                "The panel must discover the replicated network Stats controllers.");
            StringAssert.Contains("controller.NetworkId != 0", monitor);
            StringAssert.Contains("RuntimeAttributeData", monitor);
            StringAssert.Contains("Rect reservedArea = GetReservedControlsGuiRect();", monitor);
            StringAssert.Contains("m_ReservedControlsPanel.GetWorldCorners(m_WorldCorners);", monitor);
            StringAssert.Contains("RectTransformUtility.WorldToScreenPoint(", monitor);
            StringAssert.Contains(
                "m_SessionUI != null && m_SessionUI.IsRuntimeOverlayVisible",
                monitor);
            StringAssert.Contains("m_SessionUI.RuntimeOverlayRect", monitor);
            StringAssert.Contains("sessionArea);", monitor,
                "Both occupied rectangles must feed the monitor layout calculation.");
        }

        [Test]
        public void ShooterStatsDemo_WiresControlsReservationIntoGeneratedSessionCard()
        {
            string scene = ReadAsset(ScenePath);
            string demoUi = ReadAsset(DemoUiPath);

            Assert.That(
                CountOccurrences(
                    scene,
                    "Arawn.GameCreator2.Networking.Transport.PurrNet::" +
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetDemoCanvasUI"),
                Is.EqualTo(1),
                "The scene should have exactly one generated PurrNet session card.");
            StringAssert.Contains("--- !u!114 &618092877", scene);
            StringAssert.Contains(
                "m_ReservedControlsPanel: {fileID: 1069764099}",
                scene,
                "The session card must explicitly reserve the authored Controls Panel.");
            StringAssert.Contains("--- !u!224 &1069764099", scene);

            StringAssert.Contains(
                "[SerializeField] private RectTransform m_ReservedControlsPanel;",
                demoUi);
            StringAssert.Contains("public bool IsRuntimeOverlayVisible", demoUi);
            StringAssert.Contains("public Rect RuntimeOverlayRect", demoUi);
            StringAssert.Contains("rectTransform.GetWorldCorners", demoUi);
            StringAssert.Contains("GetComponent<RectTransform>()", demoUi,
                "The generated card bounds must come from its actual scaled RectTransform.");
            StringAssert.Contains("RectTransformUtility.WorldToScreenPoint", demoUi);
            StringAssert.Contains("CalculatePanelRect(", demoUi);
            StringAssert.DoesNotContain("GameObject.Find(\"Controls Panel", demoUi,
                "Collision avoidance must remain opt-in through an explicit scene reference.");
            StringAssert.DoesNotContain("m_Panel.SetActive(false)", demoUi,
                "The primary host/join card must never be hidden to make room for optional help.");
        }

        [Test]
        public void DemoCanvasLayout_PreservesPreferredRectWhenCardsAreSeparate()
        {
            var viewport = new Rect(0f, 0f, 1920f, 1080f);
            var preferred = new Rect(24f, 24f, 360f, 280f);
            var controls = new Rect(1396f, 24f, 500f, 202f);

            Rect result = CalculatePanelRect(preferred, controls, viewport);

            AssertRectApproximately(result, preferred);
            Assert.That(result.Overlaps(controls), Is.False);
            AssertRectInside(result, viewport);
        }

        [Test]
        public void DemoCanvasLayout_ChoosesClosestFittingNonOverlappingCandidate()
        {
            var viewport = new Rect(0f, 0f, 1000f, 700f);
            var preferred = new Rect(350f, 100f, 300f, 240f);
            var controls = new Rect(500f, 50f, 200f, 300f);

            Rect result = CalculatePanelRect(preferred, controls, viewport);

            var expectedLeftCandidate = new Rect(
                controls.xMin - Gap - preferred.width,
                preferred.y,
                preferred.width,
                preferred.height);
            AssertRectApproximately(result, expectedLeftCandidate);
            Assert.That(result.Overlaps(controls), Is.False);
            AssertRectInside(result, viewport);
        }

        [Test]
        public void DemoCanvasLayout_DocksBelowWhenHorizontalCandidatesCannotFit()
        {
            var viewport = new Rect(0f, 0f, 640f, 600f);
            var preferred = new Rect(20f, 20f, 300f, 240f);
            var controls = new Rect(200f, 20f, 420f, 180f);

            Rect result = CalculatePanelRect(preferred, controls, viewport);

            var expectedBelowCandidate = new Rect(
                preferred.x,
                controls.yMax + Gap,
                preferred.width,
                preferred.height);
            AssertRectApproximately(result, expectedBelowCandidate);
            Assert.That(result.Overlaps(controls), Is.False);
            AssertRectInside(result, viewport);
        }

        [Test]
        public void DemoCanvasLayout_ClampsPreferredRectInsideViewport()
        {
            var viewport = new Rect(0f, 0f, 640f, 480f);
            var preferred = new Rect(-20f, -10f, 300f, 240f);
            var controls = new Rect(500f, 10f, 100f, 100f);

            Rect result = CalculatePanelRect(preferred, controls, viewport);

            AssertRectApproximately(result, new Rect(0f, 0f, 300f, 240f));
            AssertRectInside(result, viewport);
            Assert.That(result.Overlaps(controls), Is.False);
        }

        [Test]
        public void DemoCanvasLayout_ReturnsNoFitSignalWhenNoPlacementExists()
        {
            var viewport = new Rect(0f, 0f, 320f, 200f);
            var preferred = new Rect(10f, 10f, 300f, 180f);
            var controls = new Rect(0f, 0f, 320f, 200f);

            Rect result = CalculatePanelRect(preferred, controls, viewport);

            Assert.That(result, Is.EqualTo(Rect.zero),
                "No-fit must be explicit so runtime can keep the session card and temporarily " +
                "suppress only the optional authored controls panel.");
        }

        [Test]
        public void ShooterStatsDemo_HealthMonitorAvoidsControlsAndSessionCards()
        {
            const float screenWidth = 640f;
            const float screenHeight = 480f;
            var controls = new Rect(472f, 8f, 156f, 66f);
            var session = new Rect(16f, 16f, 300f, 266f);

            Rect result = CalculateMonitorPanelRect(
                screenWidth,
                screenHeight,
                controls,
                session);

            Assert.That(result.height, Is.GreaterThanOrEqualTo(72f));
            AssertRectInsideScreen(result, screenWidth, screenHeight);
            Assert.That(result.Overlaps(controls), Is.False);
            Assert.That(result.Overlaps(session), Is.False);
            Assert.That(result.yMin, Is.GreaterThanOrEqualTo(session.yMax + Gap - 0.001f),
                "On a narrow view, the replicated-health card must dock below the session UI.");
        }

        [Test]
        public void ShooterStatsDemo_HealthMonitorCollapsesInsteadOfOverlappingCards()
        {
            const float screenWidth = 640f;
            const float screenHeight = 360f;
            var controls = new Rect(472f, 8f, 156f, 66f);
            var session = new Rect(16f, 16f, 300f, 266f);

            Rect result = CalculateMonitorPanelRect(
                screenWidth,
                screenHeight,
                controls,
                session);

            Assert.That(result.height, Is.Zero,
                "When no safe vertical space remains, the optional monitor must hide rather " +
                "than covering the session or controls UI.");
            AssertRectInsideScreen(result, screenWidth, screenHeight);
            Assert.That(result.Overlaps(controls), Is.False);
            Assert.That(result.Overlaps(session), Is.False);
        }

        [Test]
        public void ShooterStatsDemo_UnityPackageContainsExactVersion102Payload()
        {
            string descriptor = ReadAsset(InstallerDescriptorPath).Replace("\r\n", "\n");
            StringAssert.IsMatch(
                @"(?m)^    m_Version:\n      major: 1\n      minor: 0\n      patch: 2$",
                descriptor,
                "Existing 1.0.1 installs need version 1.0.2 so Game Creator offers the " +
                "collider-free impact fix.");

            string packageFile = ProjectPath(PackagePath);
            Assert.That(File.Exists(packageFile), Is.True,
                "The PurrNet Shooter demo Game Creator package is missing.");

            string[] pathnames = ReadUnityPackagePathnames(packageFile);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    InstallRoot,
                    ProfilesPath,
                    NetworkPrefabsPath,
                    VariableProfilePath,
                    SceneVariableProfilePath,
                    PlayerPrefabPath,
                    ScenePath,
                    MonitorPath,
                    WeaponFolderPath,
                    CustomWeaponPath
                },
                pathnames,
                "The package must contain the exact ten-entry install payload, including the " +
                "custom weapon and replicated-health monitor, with no stale assets.");
            Assert.That(pathnames.Length, Is.EqualTo(10));

            Dictionary<string, byte[]> packagedAssets =
                ReadUnityPackageAssetPayloads(packageFile);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, NetworkPrefabsPath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, VariableProfilePath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, SceneVariableProfilePath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, PlayerPrefabPath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, ScenePath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, MonitorPath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, CustomWeaponPath);
        }

        [Test]
        public void PurrNetWizard_ExposesAndCreatesStatsDamageBridgesWithHp()
        {
            string wizard = ReadAsset(WizardPath).Replace("\r\n", "\n");

            StringAssert.Contains(
                "private bool m_CreateMeleeStatsDamageBridge = true;",
                wizard);
            StringAssert.Contains(
                "private bool m_CreateShooterStatsDamageBridge = true;",
                wizard);
            StringAssert.Contains("Add Melee -> Stats damage bridge", wizard);
            StringAssert.Contains("Add Shooter -> Stats damage bridge", wizard);

            StringAssert.Contains(
                "if (m_ModuleMelee && m_ModuleStats && m_CreateMeleeStatsDamageBridge)",
                wizard);
            StringAssert.Contains("NETWORK_MELEE_STATS_DAMAGE_BRIDGE_TYPE", wizard);
            StringAssert.Contains("Network Melee Stats Damage Bridge", wizard);
            StringAssert.Contains(
                "if (m_ModuleShooter && m_ModuleStats && m_CreateShooterStatsDamageBridge)",
                wizard);
            StringAssert.Contains("NETWORK_SHOOTER_STATS_DAMAGE_BRIDGE_TYPE", wizard);
            StringAssert.Contains("Network Shooter Stats Damage Bridge", wizard);

            StringAssert.Contains(
                "private const string HEALTH_ATTRIBUTE_PATH =\n" +
                $"            \"{HealthAttributePath}\";",
                wizard);
            Assert.That(
                CountOccurrences(wizard, "LoadAsset(HEALTH_ATTRIBUTE_PATH)"),
                Is.EqualTo(2),
                "Both Melee and Shooter damage bridges must load the GC2 HP Attribute.");
            Assert.That(
                CountOccurrences(
                    wizard,
                    "AssignObjectReference(so, \"m_HealthAttribute\", healthAttribute)"),
                Is.EqualTo(2),
                "Both bridge types must receive the selected HP Attribute asset.");
            Assert.That(
                CountOccurrences(
                    wizard,
                    "SetString(so, \"m_FallbackHealthAttributeId\", \"hp\")"),
                Is.EqualTo(2),
                "Both bridge types need an explicit lowercase HP fallback ID.");
        }

        private static Rect CalculatePanelRect(
            Rect preferred,
            Rect reserved,
            Rect viewport)
        {
            MethodInfo method = typeof(PurrNetDemoCanvasUI).GetMethod(
                "CalculatePanelRect",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Rect), typeof(Rect), typeof(Rect), typeof(float) },
                null);
            Assert.That(method, Is.Not.Null,
                "PurrNetDemoCanvasUI must expose its pure four-argument layout contract.");

            object value = method.Invoke(null, new object[]
            {
                preferred,
                reserved,
                viewport,
                Gap
            });
            Assert.That(value, Is.TypeOf<Rect>());
            return (Rect)value;
        }

        private static Rect CalculateMonitorPanelRect(
            float screenWidth,
            float screenHeight,
            Rect firstReserved,
            Rect secondReserved)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(MonitorPath);
            Assert.That(script, Is.Not.Null,
                "The PurrNet Shooter + Stats monitor script is not importable.");

            Type monitorType = script.GetClass();
            Assert.That(monitorType, Is.Not.Null,
                "The monitor did not compile. Check the GC2 Shooter and Stats dependencies.");

            MethodInfo method = monitorType.GetMethod(
                "CalculatePanelRect",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(float), typeof(float), typeof(Rect), typeof(Rect) },
                null);
            Assert.That(method, Is.Not.Null,
                "The monitor must expose its pure four-argument layout contract.");

            object value = method.Invoke(null, new object[]
            {
                screenWidth,
                screenHeight,
                firstReserved,
                secondReserved
            });
            Assert.That(value, Is.TypeOf<Rect>());
            return (Rect)value;
        }

        private static void AssertRectInside(Rect rect, Rect viewport)
        {
            const float tolerance = 0.001f;
            Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(viewport.xMin - tolerance));
            Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(viewport.yMin - tolerance));
            Assert.That(rect.xMax, Is.LessThanOrEqualTo(viewport.xMax + tolerance));
            Assert.That(rect.yMax, Is.LessThanOrEqualTo(viewport.yMax + tolerance));
        }

        private static void AssertRectApproximately(Rect actual, Rect expected)
        {
            const float tolerance = 0.001f;
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance));
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance));
            Assert.That(actual.width, Is.EqualTo(expected.width).Within(tolerance));
            Assert.That(actual.height, Is.EqualTo(expected.height).Within(tolerance));
        }

        private static void AssertRectInsideScreen(
            Rect rect,
            float screenWidth,
            float screenHeight)
        {
            const float tolerance = 0.001f;
            Assert.That(rect.xMin, Is.GreaterThanOrEqualTo(-tolerance));
            Assert.That(rect.yMin, Is.GreaterThanOrEqualTo(-tolerance));
            Assert.That(rect.xMax, Is.LessThanOrEqualTo(screenWidth + tolerance));
            Assert.That(rect.yMax, Is.LessThanOrEqualTo(screenHeight + tolerance));
        }

        private static void AssertAssetExists(string assetPath)
        {
            Assert.That(File.Exists(ProjectPath(assetPath)), Is.True,
                $"Required demo asset is missing: {assetPath}");
            Assert.That(AssetDatabase.LoadMainAssetAtPath(assetPath), Is.Not.Null,
                $"Required demo asset is not importable: {assetPath}");
        }

        private static string ReadGc2AssetId(string source)
        {
            Match match = Regex.Match(
                source,
                @"(?ms)^  m_Id:\r?$.*?^      m_String: ([^\r\n]+)$");
            Assert.That(match.Success, Is.True, "Could not read the GC2 asset ID.");
            return match.Groups[1].Value.Trim();
        }

        private static string ReadNestedPropertyRid(string source, string propertyName)
        {
            Match match = Regex.Match(
                source,
                Regex.Escape(propertyName) +
                @":\s*\r?\n\s+m_Property:\s*\r?\n\s+rid: ([0-9]+)");
            Assert.That(match.Success, Is.True,
                $"Could not resolve {propertyName}'s managed-reference property.");
            return match.Groups[1].Value;
        }

        private static string ReadManagedReference(string source, string rid)
        {
            Match reference = Regex.Match(
                source,
                @"(?m)^    - rid: " + Regex.Escape(rid) + @"\r?$");
            Assert.That(reference.Success, Is.True,
                $"Managed reference {rid} is missing.");
            int start = reference.Index;

            int end = source.IndexOf(
                "\n    - rid: ",
                start + reference.Length,
                StringComparison.Ordinal);
            return end >= 0 ? source.Substring(start, end - start) : source.Substring(start);
        }

        private static string Slice(string source, string startMarker, string endMarker)
        {
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0),
                $"Missing serialized section: {startMarker}");
            int end = source.IndexOf(
                endMarker,
                start + startMarker.Length,
                StringComparison.Ordinal);
            Assert.That(end, Is.GreaterThan(start),
                $"Missing serialized section terminator: {endMarker}");
            return source.Substring(start, end - start);
        }

        private static string ReadSceneComponentByScriptGuid(string scene, string scriptGuid)
        {
            Match script = Regex.Match(
                scene,
                @"(?m)^  m_Script: \{fileID: 11500000, guid: " +
                Regex.Escape(scriptGuid) +
                @", type: 3\}\r?$");
            Assert.That(script.Success, Is.True,
                $"No scene component uses script GUID {scriptGuid}.");

            int start = scene.LastIndexOf("--- !u!114 &", script.Index, StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            int end = scene.IndexOf("\n--- !u!", script.Index, StringComparison.Ordinal);
            return end >= 0 ? scene.Substring(start, end - start) : scene.Substring(start);
        }

        private static int CountGuidReferences(string source, string guid)
        {
            return Regex.Matches(
                source,
                @"guid:\s*" + Regex.Escape(guid) + @"(?:,|\s|})").Count;
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }

        private static string ReadAsset(string assetPath)
        {
            string path = ProjectPath(assetPath);
            Assert.That(File.Exists(path), Is.True, $"Asset source is missing: {assetPath}");
            return File.ReadAllText(path);
        }

        private static string ProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.That(projectRoot, Is.Not.Null.And.Not.Empty);
            return Path.Combine(projectRoot, assetPath);
        }

        private static string[] ReadUnityPackagePathnames(string packagePath)
        {
            var pathnames = new List<string>();
            using FileStream file = File.OpenRead(packagePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var header = new byte[512];

            while (true)
            {
                int headerBytes = ReadFully(gzip, header, 0, header.Length);
                if (headerBytes == 0) break;
                if (headerBytes != header.Length)
                {
                    throw new InvalidDataException(
                        "The Unity package ended inside a TAR header.");
                }
                if (header.All(value => value == 0)) break;

                string entryName = Encoding.UTF8
                    .GetString(header, 0, 100)
                    .TrimEnd('\0');
                string sizeText = Encoding.ASCII
                    .GetString(header, 124, 12)
                    .Trim('\0', ' ');
                long size = string.IsNullOrEmpty(sizeText)
                    ? 0L
                    : Convert.ToInt64(sizeText, 8);

                if (entryName.EndsWith("/pathname", StringComparison.Ordinal))
                {
                    Assert.That(size, Is.LessThanOrEqualTo(int.MaxValue));
                    var content = new byte[(int)size];
                    if (ReadFully(gzip, content, 0, content.Length) != content.Length)
                    {
                        throw new EndOfStreamException(
                            "The Unity package ended inside a pathname entry.");
                    }
                    pathnames.Add(Encoding.UTF8
                        .GetString(content)
                        .TrimEnd('\0', '\r', '\n'));
                }
                else
                {
                    SkipStreamBytes(gzip, size);
                }

                long padding = (512L - size % 512L) % 512L;
                SkipStreamBytes(gzip, padding);
            }

            return pathnames
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static Dictionary<string, byte[]> ReadUnityPackageAssetPayloads(
            string packagePath)
        {
            var pathnamesByEntry = new Dictionary<string, string>(StringComparer.Ordinal);
            var assetsByEntry = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            using FileStream file = File.OpenRead(packagePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var header = new byte[512];

            while (true)
            {
                int headerBytes = ReadFully(gzip, header, 0, header.Length);
                if (headerBytes == 0) break;
                if (headerBytes != header.Length)
                {
                    throw new InvalidDataException(
                        "The Unity package ended inside a TAR header.");
                }
                if (header.All(value => value == 0)) break;

                string entryName = Encoding.UTF8
                    .GetString(header, 0, 100)
                    .TrimEnd('\0')
                    .TrimStart('.', '/');
                string sizeText = Encoding.ASCII
                    .GetString(header, 124, 12)
                    .Trim('\0', ' ');
                long size = string.IsNullOrEmpty(sizeText)
                    ? 0L
                    : Convert.ToInt64(sizeText, 8);

                string[] segments = entryName.Split('/');
                bool isPayload =
                    segments.Length == 2 &&
                    (segments[1] == "pathname" || segments[1] == "asset");

                byte[] content = null;
                if (isPayload)
                {
                    Assert.That(size, Is.LessThanOrEqualTo(int.MaxValue));
                    content = new byte[(int)size];
                    if (ReadFully(gzip, content, 0, content.Length) != content.Length)
                    {
                        throw new EndOfStreamException(
                            $"The Unity package ended inside '{entryName}'.");
                    }

                    if (segments[1] == "pathname")
                    {
                        pathnamesByEntry[segments[0]] = Encoding.UTF8
                            .GetString(content)
                            .TrimEnd('\0', '\r', '\n');
                    }
                    else
                    {
                        assetsByEntry[segments[0]] = content;
                    }
                }
                else
                {
                    SkipStreamBytes(gzip, size);
                }

                long padding = (512L - size % 512L) % 512L;
                SkipStreamBytes(gzip, padding);
            }

            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in pathnamesByEntry)
            {
                if (assetsByEntry.TryGetValue(entry.Key, out byte[] asset))
                {
                    result[entry.Value] = asset;
                }
            }

            return result;
        }

        private static void AssertPackageAssetMatchesCurrentSource(
            IReadOnlyDictionary<string, byte[]> packagedAssets,
            string assetPath)
        {
            Assert.That(
                packagedAssets.TryGetValue(assetPath, out byte[] packaged),
                Is.True,
                $"The Unity package has no asset payload for '{assetPath}'.");

            byte[] current = File.ReadAllBytes(ProjectPath(assetPath));
            Assert.That(
                packaged.Length,
                Is.EqualTo(current.Length),
                $"The packaged '{assetPath}' is stale (byte length differs from its source).");
            Assert.That(
                packaged.SequenceEqual(current),
                Is.True,
                $"The packaged '{assetPath}' is stale (bytes differ from source). " +
                "Rebuild Package.unitypackage with the Game Creator InstallManager.");
        }

        private static int ReadFully(
            Stream stream,
            byte[] buffer,
            int offset,
            int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void SkipStreamBytes(Stream stream, long count)
        {
            if (count <= 0) return;
            var buffer = new byte[8192];
            long remaining = count;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        "The Unity package ended inside a TAR entry.");
                }
                remaining -= read;
            }
        }
    }
}
