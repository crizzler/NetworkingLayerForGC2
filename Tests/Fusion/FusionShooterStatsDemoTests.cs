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

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    /// <summary>
    /// Guards the installable Fusion Shooter + Stats example without adding optional Shooter or
    /// Stats assemblies to the Fusion test assembly. The demo intentionally lives below Plugins,
    /// so its serialized assets and package payload are the most stable integration boundary.
    /// </summary>
    public sealed class FusionShooterStatsDemoTests
    {
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.ShooterExamples@1.0.2";

        private const string ScenePath = InstallRoot +
            "/Requires Shooter Demos - FusionShooterStatsDemo.unity";
        private const string PlayerPrefabPath = InstallRoot +
            "/FusionDemoPlayer-ShooterAndStats.prefab";
        private const string MonitorPath = InstallRoot +
            "/FusionShooterStatsDemoMonitor.cs";
        private const string SessionUiPath =
            "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
            "FusionDemoSessionUI.cs";
        private const string WeaponFolderPath = InstallRoot + "/Weapons";
        private const string CustomWeaponPath = WeaponFolderPath +
            "/FusionDemo_AK_Weapon.asset";

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
        private const string PackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Shooter/" +
            "Package.unitypackage";
        private const string InstallerDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Shooter/" +
            "GC2NetworkingLayerFusionTransport.ShooterExamples.asset";

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
                "The demo copy must have a unique Unity GUID.");
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
                "Cloning the AK must also regenerate its GC2 ID, not only its Unity GUID.");
            StringAssert.Contains("m_Name: FusionDemo_AK_Weapon", customWeapon);

            string playerPrefab = ReadAsset(PlayerPrefabPath);
            string scene = ReadAsset(ScenePath);
            Assert.That(
                CountGuidReferences(playerPrefab, customGuid),
                Is.EqualTo(9),
                "The player prefab's equip, aim, shoot, and reload instructions must all use " +
                "the demo weapon.");
            Assert.That(
                CountGuidReferences(scene, customGuid),
                Is.EqualTo(1),
                "The scene registry must contain the custom weapon exactly once.");
            Assert.That(
                CountGuidReferences(playerPrefab, stockGuid),
                Is.Zero,
                "The prefab must not fall back to the stock Shooter example AK.");
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
                "Shooter On Hit must resolve the network character from child hit colliders.");
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
                "The parent lookup must begin at the Shooter hit target supplied by GC2.");

            string valueRid = ReadNestedPropertyRid(instruction, "m_Value");
            string value = ReadManagedReference(weapon, valueRid);
            StringAssert.Contains(
                "type: {class: GetDecimalDecimal, ns: GameCreator.Runtime.Common, " +
                "asm: GameCreator.Runtime.Core}",
                value);
            StringAssert.IsMatch(@"(?m)^\s*m_Value: -10(?:\.0+)?\s*$", value,
                "Each authoritative On Hit must subtract exactly 10 HP.");

            Match powerReference = Regex.Match(
                weapon,
                @"(?m)^\s+m_Power:\r?\n\s+m_Property:\r?\n\s+rid: ([0-9]+)\s*$");
            Assert.That(powerReference.Success, Is.True,
                "The custom weapon must have a serialized constant reaction/damage power.");
            string power = ReadManagedReference(
                weapon,
                powerReference.Groups[1].Value);
            StringAssert.Contains("type: {class: GetDecimalDecimal", power);
            StringAssert.IsMatch(@"(?m)^\s*m_Value: 10(?:\.0+)?\s*$", power,
                "The demo weapon power must stay deterministic at 10.");

            // These raw enum assertions keep the serialized numeric checks meaningful without
            // adding the optional Stats runtime assembly as a test reference.
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
        public void ShooterStatsDemo_SceneContainsDamageBridgeAndHealthMonitor()
        {
            string scene = ReadAsset(ScenePath);
            string monitor = ReadAsset(MonitorPath);
            string healthGuid = AssetDatabase.AssetPathToGUID(HealthAttributePath);

            const string bridgeType =
                "Arawn.GameCreator2.Networking.Stats.Shooter::" +
                "Arawn.GameCreator2.Networking.Stats.Shooter." +
                "NetworkShooterStatsDamageBridge";
            Assert.That(CountOccurrences(scene, bridgeType), Is.EqualTo(1));
            StringAssert.Contains(
                $"m_HealthAttribute: {{fileID: 11400000, guid: {healthGuid}, type: 2}}",
                scene);
            StringAssert.Contains("m_FallbackHealthAttributeId: hp", scene);

            Assert.That(
                CountOccurrences(scene, "m_Name: Fusion Shooter Stats Monitor"),
                Is.EqualTo(1));
            Assert.That(
                CountOccurrences(
                    scene,
                    "Arawn.GameCreator2.Networking.Demo.Fusion." +
                    "FusionShooterStatsDemoMonitor"),
                Is.EqualTo(1));
            StringAssert.Contains(
                $"m_Health: {{fileID: 11400000, guid: {healthGuid}, type: 2}}",
                scene);
            StringAssert.Contains(
                "m_ReservedControlsPanel: {fileID: 1069764099}",
                scene,
                "The runtime health panel must reserve the scene's top-right Controls Panel.");
            StringAssert.Contains(
                "m_SessionUI: {fileID: 2097728536}",
                scene,
                "The health panel must also reserve the fallback Fusion session overlay.");
            StringAssert.Contains("--- !u!224 &1069764099", scene);
            StringAssert.Contains("--- !u!114 &2097728536", scene);
            StringAssert.Contains(
                "Each validated hit from FusionDemo_AK_Weapon deals 10 HP damage.",
                monitor);
            StringAssert.Contains(
                "Rect reservedArea = GetReservedControlsGuiRect();",
                monitor);
            StringAssert.Contains(
                "Rect area = CalculatePanelRect(",
                monitor);
            StringAssert.Contains("reservedArea,", monitor);
            StringAssert.Contains("sessionArea);", monitor);
            StringAssert.Contains(
                "m_ReservedControlsPanel.GetWorldCorners(m_WorldCorners);",
                monitor);
            StringAssert.Contains(
                "RectTransformUtility.WorldToScreenPoint(",
                monitor,
                "The uGUI controls bounds must be converted to IMGUI screen coordinates.");
            StringAssert.Contains(
                "m_SessionUI != null && m_SessionUI.IsRuntimeOverlayVisible",
                monitor);
            StringAssert.Contains("m_SessionUI.RuntimeOverlayRect", monitor);

            string sessionUi = ReadAsset(SessionUiPath);
            StringAssert.Contains("public bool IsRuntimeOverlayVisible =>", sessionUi);
            StringAssert.Contains("public Rect RuntimeOverlayRect => new Rect(", sessionUi);
            StringAssert.Contains("if (!IsRuntimeOverlayVisible) return;", sessionUi);
            StringAssert.Contains("RuntimeOverlayRect,", sessionUi,
                "The public bounds must be the exact rectangle used to render the session card.");
        }

        [TestCase(1920f, 1080f, 1396f, 24f, 500f, 202f)]
        [TestCase(570f, 380f, 400f, 8f, 160f, 66f)]
        [TestCase(285f, 190f, 201f, 4f, 80f, 33f)]
        public void ShooterStatsDemo_HealthMonitorDocksWithoutOverlapAtRepresentativeSizes(
            float screenWidth,
            float screenHeight,
            float reservedX,
            float reservedY,
            float reservedWidth,
            float reservedHeight)
        {
            var reserved = new Rect(
                reservedX,
                reservedY,
                reservedWidth,
                reservedHeight);

            Rect result = CalculateMonitorPanelRect(screenWidth, screenHeight, reserved);

            Assert.That(result.height, Is.GreaterThanOrEqualTo(72f),
                "These supported view sizes must retain a visible, scrollable health panel.");
            AssertRectInsideScreen(result, screenWidth, screenHeight);
            Assert.That(result.Overlaps(reserved), Is.False,
                $"The health panel {result} overlaps the reserved controls panel {reserved}.");
            Assert.That(result.yMin, Is.GreaterThanOrEqualTo(reserved.yMax + 12f - 0.001f),
                "The health panel must dock below the controls with the configured gap.");
        }

        [Test]
        public void ShooterStatsDemo_HealthMonitorCollapsesWhenNoNonOverlappingHeightExists()
        {
            const float screenWidth = 320f;
            const float screenHeight = 80f;
            var reserved = new Rect(200f, 0f, 120f, 70f);

            Rect result = CalculateMonitorPanelRect(screenWidth, screenHeight, reserved);

            Assert.That(result.height, Is.Zero,
                "An impossibly short view must hide the health panel instead of overlapping UI.");
            AssertRectInsideScreen(result, screenWidth, screenHeight);
            Assert.That(result.Overlaps(reserved), Is.False);
        }

        [Test]
        public void ShooterStatsDemo_HealthMonitorAvoidsBothCardsOnNarrowScreen()
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
            Assert.That(result.yMin, Is.GreaterThanOrEqualTo(session.yMax + 12f - 0.001f));
        }

        [Test]
        public void ShooterStatsDemo_HealthMonitorCollapsesWhenSessionCardLeavesNoRoom()
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
                "The monitor must collapse instead of covering either narrow-screen overlay.");
            AssertRectInsideScreen(result, screenWidth, screenHeight);
            Assert.That(result.Overlaps(controls), Is.False);
            Assert.That(result.Overlaps(session), Is.False);
        }

        [Test]
        public void ShooterStatsDemo_UnityPackageContainsExactInstallPayload()
        {
            string descriptor = ReadAsset(InstallerDescriptorPath).Replace("\r\n", "\n");
            StringAssert.IsMatch(
                @"(?m)^    m_Version:\n      major: 1\n      minor: 0\n      patch: 2$",
                descriptor,
                "Existing 1.0.1 installs need version 1.0.2 so Game Creator offers the " +
                "collider-free impact fix.");

            string packageFile = ProjectPath(PackagePath);
            Assert.That(File.Exists(packageFile), Is.True,
                "The Shooter demo Game Creator install package is missing.");

            string[] pathnames = ReadUnityPackagePathnames(packageFile);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    InstallRoot,
                    ScenePath,
                    PlayerPrefabPath,
                    MonitorPath,
                    WeaponFolderPath,
                    CustomWeaponPath
                },
                pathnames,
                "The repacked demo must contain its scene, player, monitor, custom weapon, " +
                "and both install folders—and no stale or unrelated assets.");
            Assert.That(pathnames.Length, Is.EqualTo(6));

            Dictionary<string, byte[]> packagedAssets =
                ReadUnityPackageAssetPayloads(packageFile);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, ScenePath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, MonitorPath);
            AssertPackageAssetMatchesCurrentSource(packagedAssets, CustomWeaponPath);
        }

        [Test]
        public void FusionWizard_ExposesAndCreatesStatsDamageBridgesWithHp()
        {
            string wizard = ReadAsset(
                "Assets/Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");

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
            StringAssert.Contains(
                "NetworkMeleeStatsDamageBridgeType",
                wizard);
            StringAssert.Contains("Network Melee Stats Damage Bridge", wizard);
            StringAssert.Contains(
                "if (m_ModuleShooter && m_ModuleStats && m_CreateShooterStatsDamageBridge)",
                wizard);
            StringAssert.Contains(
                "NetworkShooterStatsDamageBridgeType",
                wizard);
            StringAssert.Contains("Network Shooter Stats Damage Bridge", wizard);

            StringAssert.Contains(
                $"private const string HealthAttributePath =\n" +
                $"            \"{HealthAttributePath}\";",
                wizard.Replace("\r\n", "\n"));
            StringAssert.Contains(
                "AssetDatabase.LoadMainAssetAtPath(HealthAttributePath)",
                wizard);
            StringAssert.Contains(
                "SetObject(serialized, \"m_HealthAttribute\", health);",
                wizard);
            StringAssert.Contains(
                "SetString(serialized, \"m_FallbackHealthAttributeId\", \"hp\");",
                wizard);
        }

        private static void AssertAssetExists(string assetPath)
        {
            Assert.That(File.Exists(ProjectPath(assetPath)), Is.True,
                $"Required demo asset is missing: {assetPath}");
            Assert.That(AssetDatabase.LoadMainAssetAtPath(assetPath), Is.Not.Null,
                $"Required demo asset is not importable: {assetPath}");
        }

        private static Rect CalculateMonitorPanelRect(
            float screenWidth,
            float screenHeight,
            Rect reserved)
        {
            return InvokeMonitorPanelRect(
                new[] { typeof(float), typeof(float), typeof(Rect) },
                new object[] { screenWidth, screenHeight, reserved });
        }

        private static Rect CalculateMonitorPanelRect(
            float screenWidth,
            float screenHeight,
            Rect firstReserved,
            Rect secondReserved)
        {
            return InvokeMonitorPanelRect(
                new[] { typeof(float), typeof(float), typeof(Rect), typeof(Rect) },
                new object[] { screenWidth, screenHeight, firstReserved, secondReserved });
        }

        private static Rect InvokeMonitorPanelRect(Type[] parameterTypes, object[] arguments)
        {
            MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(MonitorPath);
            Assert.That(script, Is.Not.Null,
                "The Fusion Shooter + Stats monitor script is not importable.");

            Type monitorType = script.GetClass();
            Assert.That(monitorType, Is.Not.Null,
                "The monitor did not compile. Check the GC2 Shooter and Stats dependencies.");

            MethodInfo method = monitorType.GetMethod(
                "CalculatePanelRect",
                BindingFlags.Public | BindingFlags.Static,
                null,
                parameterTypes,
                null);
            Assert.That(method, Is.Not.Null,
                "The monitor must expose its pure layout overload for regression testing.");

            object value = method.Invoke(null, arguments);
            Assert.That(value, Is.TypeOf<Rect>());
            return (Rect)value;
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
            string projectRoot = Directory.GetParent(UnityEngine.Application.dataPath)?.FullName;
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
                $"The packaged '{assetPath}' is stale (its bytes differ from its source). " +
                "Rebuild Package.unitypackage with the Game Creator InstallManager.");
        }

        private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
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
