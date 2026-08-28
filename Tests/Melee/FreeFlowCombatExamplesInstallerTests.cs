#if GC2_MELEE
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using PurrNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Melee.Tests
{
    /// <summary>
    /// Guards the generated Free Flow Combat installers as security-relevant fixtures. The
    /// package archive must remain a canonical-current, single-root export of the installed
    /// demo; prefab and scene assertions protect the explicit player/NPC authority split.
    /// </summary>
    [NUnit.Framework.Category("GC2Networking.FreeFlow")]
    public sealed class FreeFlowCombatExamplesInstallerTests
    {
        private const string InstallsRoot = "Assets/Plugins/GameCreator/Installs/";
        private const string CanonicalFreeFlowWeapon =
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/" +
            "FreeFlow_Brawl_FreeFlowWeapon.asset";
        private const string FreeFlowAttackType =
            "Arawn.FreeFlowCombat.VisualScripting.InstructionFreeFlowAttack";
        private const string FreeFlowCounterType =
            "Arawn.FreeFlowCombat.VisualScripting.InstructionFreeFlowCounter";
        private const string FreeFlowWeaponType =
            "Arawn.FreeFlowCombat.FreeFlowMeleeWeapon";
        private const string FreeFlowEnemyTelegraphType =
            "Arawn.FreeFlowCombat.VisualScripting.AI.InstructionFreeFlowEnemyTelegraph";
        private const string LegacyMeleeInputType =
            "GameCreator.Runtime.Melee.InstructionMeleeInputExecute";
        private const string MeleeEquipType =
            "GameCreator.Runtime.Melee.InstructionMeleeEquipWeapon";
        private const string EventOnStartType =
            "GameCreator.Runtime.VisualScripting.EventOnStart";
        private const string CharacterAttachPropType =
            "GameCreator.Runtime.VisualScripting.InstructionCharacterAttachProp";
        private const string FreeFlowAdapterType =
            "Arawn.GameCreator2.Networking.Melee.NetworkFreeFlowCombatAdapter";
        private const string AuthorityOnlyAiRoot = "Authority Only Free Flow AI";
        private const string PersistentEnemiesRoot = "Free Flow Network Enemies";

        private static readonly HashSet<string> s_PresentationOnlyOnEquipInstructions =
            new(StringComparer.Ordinal)
            {
                CharacterAttachPropType
            };

        private static readonly InstallerSpec[] Cases =
        {
            new(
                "Fusion",
                "GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples",
                "GC2NetworkingLayerFusionTransport.MeleeExamples",
                "FusionDemoPlayer-FreeFlowCombat.prefab",
                "FusionDemoEnemy-FreeFlowCombat.prefab",
                "Requires Melee & Free Flow Combat - FusionFreeFlowCombatDemo.unity",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/" +
                "FreeFlowCombat/Package.unitypackage",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/FreeFlowCombat/" +
                "GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples.asset",
                null),
            new(
                "PurrNet",
                "GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples",
                "GC2NetworkingLayerPurrNetTransport.MeleeExamples",
                "PurrNetDemoPlayer-FreeFlowCombat.prefab",
                "PurrNetDemoEnemy-FreeFlowCombat.prefab",
                "Requires Melee & Free Flow Combat - PurrNetFreeFlowCombatDemo.unity",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/" +
                "FreeFlowCombat/Package.unitypackage",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/FreeFlowCombat/" +
                "GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples.asset",
                "Profiles/PurrNetFreeFlowCombatNetworkPrefabs.asset")
        };

        public static IEnumerable<InstallerSpec> InstallerCases => Cases;

        [Test]
        public void CanonicalWeapon_HasTargetingIndicatorsEnemySetupAndMotionWarpFallback()
        {
            UnityEngine.Object weapon =
                AssetDatabase.LoadMainAssetAtPath(CanonicalFreeFlowWeapon);
            Assert.That(weapon, Is.Not.Null,
                "The networking examples require the canonical Brawl Free Flow weapon.");
            Assert.That(weapon.GetType().FullName, Is.EqualTo(FreeFlowWeaponType));

            var serialized = new SerializedObject(weapon);
            float attackRadius = RequireProperty(serialized, "m_AttackRadius").floatValue;
            float scanRadius = RequireProperty(serialized, "m_ScanRadius").floatValue;
            Assert.That(float.IsNaN(attackRadius) || float.IsInfinity(attackRadius), Is.False);
            Assert.That(float.IsNaN(scanRadius) || float.IsInfinity(scanRadius), Is.False);
            Assert.That(attackRadius, Is.GreaterThan(0f));
            Assert.That(scanRadius, Is.GreaterThan(attackRadius),
                "Scan Radius must extend beyond Attack Radius so the fallback action can receive " +
                "a distant candidate for motion warping.");

            AssertAssetReference<GameObject>(serialized, "m_TargetIndicatorPrefab");
            AssertAssetReference<GameObject>(serialized, "m_FallbackCandidateIndicatorPrefab");
            AssertAssetReference<GameObject>(serialized, "m_EnemyCounterIndicatorPrefab");
            Assert.That(
                RequireProperty(serialized, "m_AutoSetupNonPlayerCharacters").boolValue,
                Is.True,
                "The canonical weapon must install the Free Flow target, enemy agent, and " +
                "Behavior Processor when an NPC equips it on simulation authority.");
            UnityEngine.Object behaviorGraph =
                AssertAssetReference<UnityEngine.Object>(serialized, "m_EnemyBehaviorGraph");
            Assert.That(GetManagedReferenceTypes(behaviorGraph).Any(type =>
                    type.IndexOf("GameCreator.Runtime.Behavior", StringComparison.Ordinal) >= 0),
                Is.True,
                "The configured enemy Behavior graph must contain an authored GC2 Behavior node.");
            SerializedProperty telegraph = FindManagedReference(
                behaviorGraph,
                FreeFlowEnemyTelegraphType);
            Assert.That(telegraph, Is.Not.Null,
                "The canonical enemy Behavior graph must retain its counter telegraph task.");
            SerializedProperty telegraphDuration = RequireRelativeProperty(
                RequireRelativeProperty(
                    RequireRelativeProperty(telegraph, "m_Duration"),
                    "m_Property"),
                "m_Value");
            Assert.That(
                telegraphDuration.doubleValue,
                Is.EqualTo(0.8d).Within(0.0001d),
                "The network demo telegraph must leave enough time for prompt replication, " +
                "human reaction, and the revision-bound counter request round trip.");

            SerializedProperty onEquip = RequireProperty(serialized, "m_OnEquip");
            SerializedProperty onEquipInstructions = RequireRelativeProperty(
                RequireRelativeProperty(onEquip, "m_Instructions"),
                "m_Instructions");
            Instruction[] authoredOnEquip = Enumerable.Range(0, onEquipInstructions.arraySize)
                .Select(index => onEquipInstructions
                    .GetArrayElementAtIndex(index)
                    .managedReferenceValue as Instruction)
                .Where(instruction => instruction != null)
                .ToArray();
            Assert.That(authoredOnEquip, Is.Not.Empty,
                "The canonical weapon must retain its presentation setup on equip.");
            string[] unsafeOnEquip = authoredOnEquip
                .Select(instruction => instruction.GetType().FullName)
                .Where(typeName => typeName == null ||
                    !s_PresentationOnlyOnEquipInstructions.Contains(typeName))
                .ToArray();
            Assert.That(unsafeOnEquip, Is.Empty,
                "The replica-safe equip Trigger runs on every peer, so the canonical weapon's " +
                "On Equip list may contain only explicitly allow-listed presentation actions. " +
                $"Unsafe instruction(s): {string.Join(", ", unsafeOnEquip)}");

            SerializedProperty direct =
                RequireProperty(serialized, "m_OnTargetSelectedInstructions");
            SerializedProperty fallback = RequireProperty(serialized, "m_NoTargetInstructions");
            Skill[] authoredSkills = EnumerateObjectReferences(direct)
                .Concat(EnumerateObjectReferences(fallback))
                .OfType<Skill>()
                .Distinct()
                .ToArray();
            Assert.That(authoredSkills, Is.Not.Empty,
                "The direct/fallback action must reference at least one authored Melee Skill.");

            Skill[] motionWarpSkills = authoredSkills
                .Where(SkillContainsMotionWarping)
                .ToArray();
            Assert.That(motionWarpSkills, Is.Not.Empty,
                "At least one direct/fallback Skill must contain a GC2 Melee motion-warping " +
                "track or clip so a scanned enemy can be approached by the authored action.");
        }

        [TestCaseSource(nameof(InstallerCases))]
        public void Descriptor_IsVersion100WithExactDependencies(InstallerSpec spec)
        {
            Assert.That(File.Exists(ProjectPath(spec.DescriptorPath)), Is.True,
                $"Missing {spec.Transport} Free Flow Combat installer descriptor.");

            string yaml = File.ReadAllText(ProjectPath(spec.DescriptorPath))
                .Replace("\r\n", "\n");
            Assert.That(
                yaml,
                Does.Match(@"m_Version:\s*\n\s*major:\s*1\s*\n\s*minor:\s*0\s*\n\s*patch:\s*0"),
                $"{spec.Transport} Free Flow Combat descriptor must remain version 1.0.0.");

            string[] dependencyIds = Regex.Matches(
                    yaml,
                    @"(?m)^\s*-\s+m_ID:\s*(\S+)\s*$")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToArray();
            CollectionAssert.AreEquivalent(
                new[] { spec.MeleeDependencyId, "Melee.Brawl" },
                dependencyIds,
                $"{spec.Transport} descriptor must declare only its transport Melee fixture " +
                "and the Free Flow Brawl dependency.");

            AssertDependencyVersion(yaml, spec.MeleeDependencyId, 1, 0, 0);
            AssertDependencyVersion(yaml, "Melee.Brawl", 1, 1, 4);
        }

        [TestCaseSource(nameof(InstallerCases))]
        public void Package_IsCanonicalCurrentSingleRootPayload(InstallerSpec spec)
        {
            Assert.That(Directory.Exists(ProjectPath(spec.InstallRoot)), Is.True,
                $"Missing generated install root '{spec.InstallRoot}'.");
            Assert.That(File.Exists(ProjectPath(spec.PackagePath)), Is.True,
                $"Missing generated package '{spec.PackagePath}'.");
            string[] installedVersions = Directory.GetDirectories(
                    ProjectPath(InstallsRoot),
                    spec.InstallId + "@*")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { spec.InstallId + "@1.0.0" },
                installedVersions,
                $"{spec.Transport} Free Flow Combat examples contain a stale install root.");

            UnityPackageContents package = ReadUnityPackage(ProjectPath(spec.PackagePath));
            string[] expectedPaths = EnumerateInstallRootPaths(spec.InstallRoot);

            Assert.That(package.Pathnames.All(path =>
                    string.Equals(path, spec.InstallRoot, StringComparison.Ordinal) ||
                    path.StartsWith(spec.InstallRoot + "/", StringComparison.Ordinal)), Is.True,
                "The installer must not export a licensed dependency or another install root.");
            Assert.That(package.Pathnames.Any(path =>
                    path.StartsWith("Assets/Arawn/FreeFlowCombat/", StringComparison.Ordinal)),
                Is.False,
                "The optional Free Flow dependency must remain external to the installer.");
            CollectionAssert.AreEquivalent(
                expectedPaths,
                package.Pathnames,
                $"{spec.Transport} Package.unitypackage is not the exact current install root.");

            PayloadCanonicalizer canonicalizer = PayloadCanonicalizer.Create(
                package,
                expectedPaths);

            foreach (string assetPath in expectedPaths)
            {
                string currentPath = ProjectPath(assetPath);
                string metaPath = currentPath + ".meta";
                Assert.That(File.Exists(metaPath), Is.True,
                    $"Generated payload entry '{assetPath}' has no Unity metadata.");
                canonicalizer.AssertCurrentMetadata(assetPath, metaPath);

                if (Directory.Exists(currentPath)) continue;
                canonicalizer.AssertCurrentAsset(assetPath, currentPath);
            }

            string navMeshFolder = Path.ChangeExtension(spec.ScenePath, null)
                ?.Replace('\\', '/');
            Assert.That(navMeshFolder, Is.Not.Null.And.Not.Empty);
            string navMeshAsset = navMeshFolder + "/NavMesh-FreeFlowCombat.asset";
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    spec.InstallRoot,
                    spec.ScenePath,
                    spec.PlayerPath,
                    spec.EnemyPath,
                    navMeshFolder,
                    navMeshAsset
                },
                package.Pathnames,
                $"{spec.Transport} installer is missing a required Free Flow demo asset.");
        }

        [TestCaseSource(nameof(InstallerCases))]
        public void CharacterPrefabs_UseExplicitServerAuthorityClassification(InstallerSpec spec)
        {
            GameObject player = LoadPrefab(spec.PlayerPath);
            Character playerCharacter = player.GetComponent<Character>();
            NetworkCharacter playerNetworkCharacter = player.GetComponent<NetworkCharacter>();

            Assert.That(playerCharacter, Is.Not.Null);
            Assert.That(playerCharacter.IsPlayer, Is.False,
                "PlayerOwned is a network classification; the prefab's GC2 IsPlayer flag must " +
                "stay false until an authenticated local owner is assigned.");
            Assert.That(playerNetworkCharacter, Is.Not.Null);
            Assert.That(playerNetworkCharacter.ActorType,
                Is.EqualTo(NetworkCharacterActorType.PlayerOwned));
            AssertSingleRootComponent(player, FreeFlowAdapterType);
            Assert.That(ContainsManagedReference(player, LegacyMeleeInputType), Is.False,
                "The player prefab must not retain the stock Melee input Trigger beside the " +
                "Free Flow input fixture.");

            GameObject enemy = LoadPrefab(spec.EnemyPath);
            Character enemyCharacter = enemy.GetComponent<Character>();
            NetworkCharacter enemyNetworkCharacter = enemy.GetComponent<NetworkCharacter>();
            NetworkCharacterAuthorityGate gate =
                enemy.GetComponent<NetworkCharacterAuthorityGate>();

            Assert.That(enemyCharacter, Is.Not.Null);
            Assert.That(enemyCharacter.IsPlayer, Is.False);
            Assert.That(enemyNetworkCharacter, Is.Not.Null);
            Assert.That(enemyNetworkCharacter.ActorType,
                Is.EqualTo(NetworkCharacterActorType.NPC));
            Assert.That(enemyNetworkCharacter.NPCMode,
                Is.EqualTo(NetworkCharacter.NPCSyncMode.ServerAuthoritative));
            AssertSingleRootComponent(enemy, FreeFlowAdapterType);
            Assert.That(enemy.GetComponents<NetworkNpcTargetSelector>(), Has.Length.EqualTo(1));
            Assert.That(gate, Is.Not.Null);
            Assert.That(gate.AuthorityOnlyRoots, Has.Length.EqualTo(1));
            Assert.That(gate.AuthorityOnlyRoots[0], Is.Not.Null);
            Assert.That(gate.AuthorityOnlyRoots[0].name, Is.EqualTo(AuthorityOnlyAiRoot));
            Assert.That(gate.AuthorityOnlyRoots[0].transform.IsChildOf(enemy.transform), Is.True);
            Assert.That(gate.AuthorityOnlyRoots[0].activeSelf, Is.True,
                "The authority gate needs the authored active state so it can enable AI only " +
                "after server/Shared-master authority resolves.");

            MonoBehaviour[] npcTriggers = enemy.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(component => component != null && string.Equals(
                    component.GetType().FullName,
                    "GameCreator.Runtime.VisualScripting.Trigger",
                    StringComparison.Ordinal))
                .ToArray();
            Assert.That(npcTriggers, Is.Not.Empty,
                "The NPC prefab must retain its replica initialization Trigger.");

            MonoBehaviour[] equipTriggers = npcTriggers
                .Where(trigger => GetManagedReferenceTypes(trigger)
                    .Any(type => IsManagedType(type, MeleeEquipType)))
                .ToArray();
            Assert.That(equipTriggers, Has.Length.EqualTo(1),
                "The NPC must have one deterministic On Start Melee equip Trigger on every " +
                "replica, never multiple equipment bootstraps.");
            Assert.That(equipTriggers.All(trigger =>
                    !trigger.transform.IsChildOf(gate.AuthorityOnlyRoots[0].transform)),
                Is.True,
                "Free Flow weapon equip/bootstrap is replica initialization and must also run " +
                "on observers. Only Behavior/AI simulation belongs behind the authority gate.");
            Assert.That(npcTriggers.Except(equipTriggers).All(trigger =>
                    trigger.transform.IsChildOf(gate.AuthorityOnlyRoots[0].transform)),
                Is.True,
                "Any authored NPC Trigger other than replica-safe equipment setup must remain " +
                "below the explicit authority-only root.");
            UnityEngine.Object canonicalWeapon =
                AssetDatabase.LoadMainAssetAtPath(CanonicalFreeFlowWeapon);
            Assert.That(canonicalWeapon, Is.Not.Null);
            foreach (MonoBehaviour equipTrigger in equipTriggers)
            {
                Assert.That(equipTrigger.enabled, Is.True,
                    "The replica equipment bootstrap Trigger must be enabled.");
                var serializedTrigger = new SerializedObject(equipTrigger);
                SerializedProperty triggerEvent = RequireProperty(
                    serializedTrigger,
                    "m_TriggerEvent");
                Assert.That(
                    triggerEvent.managedReferenceValue?.GetType().FullName,
                    Is.EqualTo(EventOnStartType),
                    "Replica equipment must use a pure Event On Start Trigger regardless of " +
                    "the copied player fixture's original event.");

                SerializedProperty triggerInstructions = RequireRelativeProperty(
                    RequireProperty(serializedTrigger, "m_Instructions"),
                    "m_Instructions");
                Assert.That(triggerInstructions.arraySize, Is.EqualTo(1),
                    "The replica equipment Trigger must not execute copied player or AI " +
                    "instructions on observers.");
                var equipInstruction = triggerInstructions
                    .GetArrayElementAtIndex(0)
                    .managedReferenceValue as InstructionMeleeEquipWeapon;
                Assert.That(equipInstruction, Is.Not.Null,
                    "The replica equipment Trigger's only instruction must equip the weapon.");
                Assert.That(equipInstruction.IsEnabled, Is.True,
                    "The replica equipment bootstrap instruction must be enabled.");

                UnityEngine.Object[] references = EnumerateObjectReferences(
                        serializedTrigger)
                    .ToArray();
                Assert.That(references.Any(reference => reference == canonicalWeapon),
                    Is.True,
                    "Every replica-safe NPC equip Trigger must equip the exact canonical " +
                    "Free Flow weapon.");
                Assert.That(references.Any(reference => reference == enemyCharacter), Is.True,
                    "Every replica-safe NPC equip Trigger must target its own Character, " +
                    "not the authenticated player or a copied prefab reference.");

                FieldInfo characterField = typeof(InstructionMeleeEquipWeapon).GetField(
                    "m_Character",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(characterField, Is.Not.Null,
                    "GC2 changed the Melee equip instruction's Character field.");
                var characterProperty =
                    characterField.GetValue(equipInstruction) as PropertyGetGameObject;
                Assert.That(characterProperty, Is.Not.Null);
                Assert.That(
                    characterProperty.Get(new Args(enemy)),
                    Is.SameAs(enemy),
                    "The replica-safe equip instruction must resolve the NPC itself. " +
                    "A Character Target getter only references the NPC asset while actually " +
                    "resolving its selected human combat target at runtime.");
            }
        }

        [TestCaseSource(nameof(InstallerCases))]
        public void Scene_HasFivePersistentEnemiesAndOnlyFreeFlowCombatInputs(InstallerSpec spec)
        {
            Assert.That(File.Exists(ProjectPath(spec.ScenePath)), Is.True,
                $"Missing generated scene '{spec.ScenePath}'.");
            Scene scene = EditorSceneManager.OpenScene(spec.ScenePath, OpenSceneMode.Additive);
            try
            {
                GameObject enemyRoot = FindGameObject(scene, PersistentEnemiesRoot);
                Assert.That(enemyRoot, Is.Not.Null);
                Assert.That(enemyRoot.transform.parent, Is.Null,
                    "Persistent network enemies must remain scene roots. Parenting them below " +
                    "the transport session lets a DontDestroyOnLoad bootstrap remove them from " +
                    "the scene before Fusion/PurrNet can register their scene identities.");
                Transform[] enemies = Enumerable.Range(0, enemyRoot.transform.childCount)
                    .Select(enemyRoot.transform.GetChild)
                    .ToArray();
                Assert.That(enemies, Has.Length.EqualTo(5));
                CollectionAssert.AreEquivalent(
                    Enumerable.Range(1, 5).Select(index =>
                        $"Free Flow Network Enemy {index}").ToArray(),
                    enemies.Select(enemy => enemy.name).ToArray());

                foreach (Transform enemy in enemies)
                {
                    GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(
                        enemy.gameObject) as GameObject;
                    Assert.That(source, Is.Not.Null,
                        $"'{enemy.name}' must remain a prefab-backed persistent network NPC.");
                    Assert.That(AssetDatabase.GetAssetPath(source), Is.EqualTo(spec.EnemyPath));
                }

                Transform[] allEnemyInstances = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                    .Where(candidate =>
                    {
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(candidate.gameObject))
                            return false;
                        GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(
                            candidate.gameObject) as GameObject;
                        return source != null && string.Equals(
                            AssetDatabase.GetAssetPath(source),
                            spec.EnemyPath,
                            StringComparison.Ordinal);
                    })
                    .ToArray();
                Assert.That(allEnemyInstances, Has.Length.EqualTo(5),
                    "The scene must not contain an extra persistent enemy outside its fixture root.");

                string[] managedTypes = GetManagedReferenceTypes(scene).ToArray();
                Assert.That(managedTypes.Any(type => IsManagedType(type, FreeFlowAttackType)),
                    Is.True, "The generated scene must retain the Free Flow attack Instruction.");
                Assert.That(managedTypes.Any(type => IsManagedType(type, FreeFlowCounterType)),
                    Is.True, "The generated scene must retain the Free Flow counter Instruction.");
                Assert.That(managedTypes.Any(type => IsManagedType(type, LegacyMeleeInputType)),
                    Is.False,
                    "The stock Melee input Instruction would duplicate the Free Flow request.");

                AssertNoBotSlotFixture(scene);
                AssertCanonicalWeaponRegistration(scene);

                string navMeshPath = Path.ChangeExtension(spec.ScenePath, null)
                    ?.Replace('\\', '/') + "/NavMesh-FreeFlowCombat.asset";
                Assert.That(SceneReferencesAsset(scene, navMeshPath), Is.True,
                    "The generated scene must reference its packaged NavMesh data.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void PurrNetRegistry_ContainsExactlyTheGeneratedPlayerAndEnemy()
        {
            InstallerSpec spec = Cases.Single(item => item.Transport == "PurrNet");
            string registryPath = spec.InstallRoot + "/" + spec.RegistryRelativePath;
            NetworkPrefabs registry =
                AssetDatabase.LoadAssetAtPath<NetworkPrefabs>(registryPath);
            Assert.That(registry, Is.Not.Null);
            Assert.That(registry.autoGenerate, Is.False);
            Assert.That(registry.networkOnly, Is.True);
            Assert.That(registry.poolByDefault, Is.False);
            Assert.That(registry.folder, Is.Null);
            Assert.That(registry.searchAllIfNoFolder, Is.False);
            Assert.That(registry.linkedNetworkPrefabs, Is.Empty);
            Assert.That(registry.prefabs, Has.Count.EqualTo(2));

            CollectionAssert.AreEquivalent(
                new[] { spec.PlayerPath, spec.EnemyPath },
                registry.prefabs.Select(entry =>
                    AssetDatabase.GetAssetPath(entry.prefab)).ToArray());
            foreach (NetworkPrefabs.UserPrefabData entry in registry.prefabs)
            {
                string path = AssetDatabase.GetAssetPath(entry.prefab);
                Assert.That(entry.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
                Assert.That(entry.pooled, Is.False);
                Assert.That(entry.warmupCount, Is.Zero);
            }

            Scene scene = EditorSceneManager.OpenScene(spec.ScenePath, OpenSceneMode.Additive);
            try
            {
                NetworkManager[] managers = FindComponents<NetworkManager>(scene).ToArray();
                Assert.That(managers, Has.Length.EqualTo(1));
                SerializedProperty configured = new SerializedObject(managers[0])
                    .FindProperty("_networkPrefabs");
                Assert.That(configured, Is.Not.Null);
                Assert.That(configured.objectReferenceValue, Is.SameAs(registry));
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void FusionPrefabs_HaveExactPrefabTableDiscoveryLabels()
        {
            InstallerSpec spec = Cases.Single(item => item.Transport == "Fusion");
            GameObject player = LoadPrefab(spec.PlayerPath);
            GameObject enemy = LoadPrefab(spec.EnemyPath);

            Assert.That(AssetDatabase.GetLabels(player), Does.Contain("FusionPrefab"));
            Assert.That(AssetDatabase.GetLabels(enemy), Does.Contain("FusionPrefab"));

            string[] labelledPaths = AssetDatabase.FindAssets(
                    "l:FusionPrefab",
                    new[] { spec.InstallRoot })
                .Select(AssetDatabase.GUIDToAssetPath)
                .ToArray();
            CollectionAssert.AreEquivalent(
                new[] { spec.PlayerPath, spec.EnemyPath },
                labelledPaths,
                "Fusion's prefab-table rebuild must discover exactly the generated combatants " +
                "inside this installer root.");
        }

        private static void AssertDependencyVersion(
            string yaml,
            string dependencyId,
            int major,
            int minor,
            int patch)
        {
            string pattern =
                @"-\s+m_ID:\s*" + Regex.Escape(dependencyId) +
                @"\s*\n\s*m_MinVersion:\s*\n\s*major:\s*" + major +
                @"\s*\n\s*minor:\s*" + minor +
                @"\s*\n\s*patch:\s*" + patch + @"(?:\s|$)";
            Assert.That(yaml, Does.Match(pattern),
                $"Dependency '{dependencyId}' must require at least {major}.{minor}.{patch}.");
        }

        private static string[] EnumerateInstallRootPaths(string installRoot)
        {
            string absoluteRoot = ProjectPath(installRoot);
            var paths = new HashSet<string>(StringComparer.Ordinal) { installRoot };
            foreach (string directory in Directory.EnumerateDirectories(
                         absoluteRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                paths.Add(ToAssetPath(directory));
            }

            foreach (string file in Directory.EnumerateFiles(
                         absoluteRoot,
                         "*",
                         SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                paths.Add(ToAssetPath(file));
            }
            return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        private static void AssertNoBotSlotFixture(Scene scene)
        {
            string[] forbiddenTypes =
            {
                "Arawn.GameCreator2.Networking.NetworkBotSlotCoordinator",
                "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetBotSlotCoordinator",
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionBotSlotCoordinator",
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionBotSlotStateReplicator"
            };

            foreach (Component component in EnumerateComponents(scene))
            {
                string typeName = component.GetType().FullName ?? string.Empty;
                Assert.That(forbiddenTypes, Does.Not.Contain(typeName),
                    $"The persistent-enemy fixture must not contain '{typeName}'.");

                var serialized = new SerializedObject(component);
                SerializedProperty botSlots = serialized.FindProperty("m_BotSlotCoordinator");
                if (botSlots != null)
                {
                    Assert.That(botSlots.objectReferenceValue, Is.Null,
                        $"'{component.name}' still references a bot-slot replacement fixture.");
                }
            }

            string[] fixtureNames = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(item => item.name)
                .Where(name => name.IndexOf("Bot Slot", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();
            Assert.That(fixtureNames, Is.Empty,
                "This demo uses five persistent enemies, not human-replacement bot slots.");
        }

        private static void AssertCanonicalWeaponRegistration(Scene scene)
        {
            int registryCount = 0;
            foreach (Component component in EnumerateComponents(scene))
            {
                var serialized = new SerializedObject(component);
                SerializedProperty weapons = serialized.FindProperty("m_RegisterWeapons");
                if (weapons == null) continue;

                registryCount++;
                Assert.That(weapons.isArray, Is.True);
                Assert.That(weapons.arraySize, Is.EqualTo(1));
                Assert.That(
                    AssetDatabase.GetAssetPath(
                        weapons.GetArrayElementAtIndex(0).objectReferenceValue),
                    Is.EqualTo(CanonicalFreeFlowWeapon));
            }
            Assert.That(registryCount, Is.GreaterThanOrEqualTo(1),
                "No Melee transport bridge registered the canonical Free Flow weapon.");
        }

        private static bool SceneReferencesAsset(Scene scene, string assetPath)
        {
            foreach (Component component in EnumerateComponents(scene))
            {
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType == SerializedPropertyType.ObjectReference &&
                        property.objectReferenceValue != null &&
                        string.Equals(
                            AssetDatabase.GetAssetPath(property.objectReferenceValue),
                            assetPath,
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool ContainsManagedReference(GameObject root, string fullTypeName)
        {
            foreach (Component component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                foreach (string type in GetManagedReferenceTypes(component))
                {
                    if (IsManagedType(type, fullTypeName)) return true;
                }
            }
            return false;
        }

        private static SerializedProperty RequireProperty(
            SerializedObject serialized,
            string propertyName)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            Assert.That(property, Is.Not.Null,
                $"'{serialized.targetObject.name}' is missing required serialized field " +
                $"'{propertyName}'. The optional Free Flow authoring contract changed.");
            return property;
        }

        private static SerializedProperty RequireRelativeProperty(
            SerializedProperty parent,
            string propertyName)
        {
            SerializedProperty property = parent?.FindPropertyRelative(propertyName);
            Assert.That(property, Is.Not.Null,
                $"Missing required serialized field '{parent?.propertyPath}.{propertyName}'. " +
                "The optional Free Flow authoring contract changed.");
            return property;
        }

        private static T AssertAssetReference<T>(
            SerializedObject serialized,
            string propertyName) where T : UnityEngine.Object
        {
            SerializedProperty property = RequireProperty(serialized, propertyName);
            Assert.That(property.propertyType,
                Is.EqualTo(SerializedPropertyType.ObjectReference));
            Assert.That(property.objectReferenceValue, Is.InstanceOf<T>(),
                $"'{propertyName}' must reference a {typeof(T).Name} asset.");
            Assert.That(AssetDatabase.Contains(property.objectReferenceValue), Is.True,
                $"'{propertyName}' must reference a persistent project asset.");
            return property.objectReferenceValue as T;
        }

        private static IEnumerable<UnityEngine.Object> EnumerateObjectReferences(
            SerializedProperty root)
        {
            SerializedProperty iterator = root.Copy();
            SerializedProperty end = root.GetEndProperty();
            bool includeRoot = true;
            bool enterChildren = true;
            while (includeRoot || iterator.Next(enterChildren))
            {
                includeRoot = false;
                if (SerializedProperty.EqualContents(iterator, end)) yield break;
                enterChildren = true;
                if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                    iterator.objectReferenceValue != null)
                {
                    yield return iterator.objectReferenceValue;
                }
            }
        }

        private static IEnumerable<UnityEngine.Object> EnumerateObjectReferences(
            SerializedObject serialized)
        {
            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType == SerializedPropertyType.ObjectReference &&
                    iterator.objectReferenceValue != null)
                {
                    yield return iterator.objectReferenceValue;
                }
            }
        }

        private static bool SkillContainsMotionWarping(Skill skill)
        {
            if (skill == null) return false;
            return GetManagedReferenceTypes(skill).Any(typeName =>
                typeName.IndexOf("MeleeMotionWarping", StringComparison.Ordinal) >= 0);
        }

        private static IEnumerable<string> GetManagedReferenceTypes(Scene scene)
        {
            foreach (Component component in EnumerateComponents(scene))
            foreach (string type in GetManagedReferenceTypes(component))
                yield return type;
        }

        private static IEnumerable<string> GetManagedReferenceTypes(Component component)
        {
            return GetManagedReferenceTypes((UnityEngine.Object)component);
        }

        private static IEnumerable<string> GetManagedReferenceTypes(
            UnityEngine.Object target)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyType != SerializedPropertyType.ManagedReference) continue;
                string type = property.managedReferenceFullTypename;
                if (!string.IsNullOrEmpty(type)) yield return type;
            }
        }

        private static SerializedProperty FindManagedReference(
            UnityEngine.Object target,
            string fullTypeName)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyType != SerializedPropertyType.ManagedReference) continue;
                string serializedType = property.managedReferenceFullTypename;
                if (!string.IsNullOrEmpty(serializedType) &&
                    IsManagedType(serializedType, fullTypeName))
                {
                    return property.Copy();
                }
            }

            return null;
        }

        private static bool IsManagedType(string serializedType, string fullTypeName) =>
            string.Equals(serializedType, fullTypeName, StringComparison.Ordinal) ||
            serializedType.EndsWith(" " + fullTypeName, StringComparison.Ordinal);

        private static IEnumerable<Component> EnumerateComponents(Scene scene) =>
            scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .Where(component => component != null);

        private static IEnumerable<T> FindComponents<T>(Scene scene) where T : Component =>
            scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true));

        private static GameObject FindGameObject(Scene scene, string name) =>
            scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .FirstOrDefault(item => item.name == name)
                ?.gameObject;

        private static GameObject LoadPrefab(string assetPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            Assert.That(prefab, Is.Not.Null, $"Missing generated prefab '{assetPath}'.");
            return prefab;
        }

        private static void AssertSingleRootComponent(GameObject root, string fullTypeName)
        {
            Component[] matches = root.GetComponents<Component>()
                .Where(component => component != null && string.Equals(
                    component.GetType().FullName,
                    fullTypeName,
                    StringComparison.Ordinal))
                .ToArray();
            Assert.That(matches, Has.Length.EqualTo(1),
                $"'{root.name}' must contain exactly one root {fullTypeName} component.");
        }

        private static string ToAssetPath(string absolutePath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.That(projectRoot, Is.Not.Null.And.Not.Empty);
            return Path.GetRelativePath(projectRoot, absolutePath).Replace('\\', '/');
        }

        private static string ProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.That(projectRoot, Is.Not.Null.And.Not.Empty);
            return Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static UnityPackageContents ReadUnityPackage(string packagePath)
        {
            var pathnames = new Dictionary<string, string>(StringComparer.Ordinal);
            var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var assetMetas = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using FileStream file = File.OpenRead(packagePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var header = new byte[512];

            while (true)
            {
                int headerBytes = ReadFully(gzip, header, header.Length);
                if (headerBytes == 0 || header.All(value => value == 0)) break;
                if (headerBytes != header.Length)
                    throw new InvalidDataException("Incomplete Unity package TAR header.");

                string entryName = Encoding.UTF8.GetString(header, 0, 100)
                    .TrimEnd('\0')
                    .TrimStart('.', '/');
                string sizeText = Encoding.ASCII.GetString(header, 124, 12)
                    .Trim('\0', ' ');
                long size = string.IsNullOrEmpty(sizeText)
                    ? 0L
                    : Convert.ToInt64(sizeText, 8);
                string[] segments = entryName.Split('/');
                bool capture = segments.Length == 2 &&
                    (segments[1] == "pathname" ||
                     segments[1] == "asset" ||
                     segments[1] == "asset.meta");

                if (capture)
                {
                    Assert.That(size, Is.LessThanOrEqualTo(int.MaxValue));
                    var content = new byte[(int)size];
                    if (ReadFully(gzip, content, content.Length) != content.Length)
                        throw new EndOfStreamException($"Incomplete '{entryName}' entry.");

                    if (segments[1] == "pathname")
                    {
                        pathnames[segments[0]] = Encoding.UTF8.GetString(content)
                            .TrimEnd('\0', '\r', '\n');
                    }
                    else if (segments[1] == "asset") assets[segments[0]] = content;
                    else assetMetas[segments[0]] = content;
                }
                else
                {
                    Skip(gzip, size);
                }

                Skip(gzip, (512L - size % 512L) % 512L);
            }

            var assetsByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var metasByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in pathnames)
            {
                if (assets.TryGetValue(entry.Key, out byte[] asset))
                    assetsByPath[entry.Value] = asset;
                if (assetMetas.TryGetValue(entry.Key, out byte[] meta))
                    metasByPath[entry.Value] = meta;
            }

            return new UnityPackageContents(
                pathnames.Values.ToArray(),
                assetsByPath,
                metasByPath);
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[8192];
            while (count > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                    throw new EndOfStreamException("Incomplete Unity package entry.");
                count -= read;
            }
        }

        /// <summary>
        /// Compares a freshly generated install root with its checked-in Unity package without
        /// treating Unity's generated identity values as authored content. Path membership stays
        /// exact, metadata ignores only the asset's own top-level GUID, and non-Unity-YAML assets
        /// remain byte-for-byte checks. Unity YAML references are rewritten through an ordered
        /// semantic document graph and the documents are then sorted, so harmless serialization
        /// order changes compare equal while changed hierarchy/component references still fail.
        /// </summary>
        private sealed class PayloadCanonicalizer
        {
            private const string OwnGuidToken = "<GC2_OWN_ASSET_GUID>";

            private static readonly Regex MetaGuidRegex = new(
                @"(?m)^(guid:[ \t]*)([0-9a-fA-F]{32})(?=[ \t]*\r?$)",
                RegexOptions.CultureInvariant);

            private static readonly Regex UnityDocumentRegex = new(
                @"(?m)^(---[ \t]+!u!\d+[ \t]+&)(-?\d+)([^\r\n]*)$",
                RegexOptions.CultureInvariant);

            private static readonly Regex CrossAssetReferenceRegex = new(
                @"(\bfileID:[ \t]*)(-?\d+)(?!\d)([ \t]*,[ \t]*guid:[ \t]*)" +
                @"([0-9a-fA-F]{32})",
                RegexOptions.CultureInvariant);

            private static readonly Regex LocalFileIdRegex = new(
                @"(\bfileID:[ \t]*)(-?\d+)(?!\d)(?![ \t]*,[ \t]*guid:)",
                RegexOptions.CultureInvariant);

            private static readonly Regex GuidTokenRegex = new(
                @"\b[0-9a-fA-F]{32}\b",
                RegexOptions.CultureInvariant);

            private static readonly Regex ManagedReferenceIdRegex = new(
                @"(\brid:[ \t]*)(-?\d+)(?!\d)",
                RegexOptions.CultureInvariant);

            private static readonly Regex FusionSortKeyRegex = new(
                @"(?m)^([ \t]*SortKey:[ \t]*)\d+([ \t]*\r?)$",
                RegexOptions.CultureInvariant);

            private static readonly Regex FusionSortKeyOverrideRegex = new(
                @"(?m)(^[ \t]*propertyPath:[ \t]*SortKey[ \t]*\r?\n" +
                @"[ \t]*value:[ \t]*)\d+(?=[ \t]*\r?$)",
                RegexOptions.CultureInvariant);

            private readonly UnityPackageContents m_Package;
            private readonly PayloadSide m_PackagedSide;
            private readonly PayloadSide m_CurrentSide;

            private PayloadCanonicalizer(
                UnityPackageContents package,
                PayloadSide packagedSide,
                PayloadSide currentSide)
            {
                m_Package = package;
                m_PackagedSide = packagedSide;
                m_CurrentSide = currentSide;
            }

            public static PayloadCanonicalizer Create(
                UnityPackageContents package,
                IEnumerable<string> assetPaths)
            {
                string[] paths = assetPaths.ToArray();
                var currentAssets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var currentMetas = new Dictionary<string, byte[]>(StringComparer.Ordinal);

                foreach (string assetPath in paths)
                {
                    Assert.That(
                        package.AssetMetas.TryGetValue(assetPath, out _),
                        Is.True,
                        $"Installer is missing metadata for '{assetPath}'.");

                    string currentPath = ProjectPath(assetPath);
                    string currentMetaPath = currentPath + ".meta";
                    Assert.That(File.Exists(currentMetaPath), Is.True,
                        $"Generated payload entry '{assetPath}' has no Unity metadata.");
                    currentMetas[assetPath] = File.ReadAllBytes(currentMetaPath);

                    if (!Directory.Exists(currentPath))
                    {
                        Assert.That(File.Exists(currentPath), Is.True,
                            $"Generated payload entry '{assetPath}' is missing.");
                        Assert.That(package.Assets.TryGetValue(assetPath, out _), Is.True,
                            $"Installer is missing asset data for '{assetPath}'.");
                        currentAssets[assetPath] = File.ReadAllBytes(currentPath);
                    }
                }

                return new PayloadCanonicalizer(
                    package,
                    PayloadSide.Create(package.AssetMetas, package.Assets, paths, "installer"),
                    PayloadSide.Create(currentMetas, currentAssets, paths, "generated root"));
            }

            public void AssertCurrentMetadata(string assetPath, string currentMetaPath)
            {
                Assert.That(
                    m_Package.AssetMetas.TryGetValue(assetPath, out byte[] packagedBytes),
                    Is.True,
                    $"Installer is missing metadata for '{assetPath}'.");
                byte[] currentBytes = File.ReadAllBytes(currentMetaPath);

                string packaged = CanonicalizeMetadata(packagedBytes, assetPath, "installer");
                string current = CanonicalizeMetadata(currentBytes, assetPath, "generated root");
                Assert.That(packaged, Is.EqualTo(current),
                    $"Installer contains stale metadata for '{assetPath}'. Only that metadata " +
                    "file's own top-level GUID is generation-dependent.");
            }

            public void AssertCurrentAsset(string assetPath, string currentPath)
            {
                Assert.That(
                    m_Package.Assets.TryGetValue(assetPath, out byte[] packagedBytes),
                    Is.True,
                    $"Installer is missing asset data for '{assetPath}'.");
                byte[] currentBytes = File.ReadAllBytes(currentPath);

                bool packagedIsYaml = IsUnityYaml(packagedBytes);
                bool currentIsYaml = IsUnityYaml(currentBytes);
                if (!packagedIsYaml || !currentIsYaml)
                {
                    Assert.That(packagedBytes.SequenceEqual(currentBytes), Is.True,
                        $"Installer contains stale binary or non-Unity-YAML asset " +
                        $"'{assetPath}'.");
                    return;
                }

                string packaged = CanonicalizeUnityYaml(
                    assetPath,
                    packagedBytes,
                    m_PackagedSide);
                string current = CanonicalizeUnityYaml(
                    assetPath,
                    currentBytes,
                    m_CurrentSide);
                Assert.That(packaged, Is.EqualTo(current),
                    $"Installer contains materially stale Unity YAML for '{assetPath}'. " +
                    "Generated internal GUIDs, document IDs, managed-reference IDs, and " +
                    "Fusion SortKey values plus YAML document serialization order were " +
                    "canonicalized before this comparison.");
            }

            private static string CanonicalizeMetadata(
                byte[] bytes,
                string assetPath,
                string sourceName)
            {
                string text = Encoding.UTF8.GetString(bytes);
                MatchCollection matches = MetaGuidRegex.Matches(text);
                Assert.That(matches, Has.Count.EqualTo(1),
                    $"The {sourceName} metadata for '{assetPath}' must contain exactly one " +
                    "top-level Unity GUID.");
                return MetaGuidRegex.Replace(
                    text,
                    match => match.Groups[1].Value + OwnGuidToken,
                    1);
            }

            private static string CanonicalizeUnityYaml(
                string assetPath,
                byte[] bytes,
                PayloadSide side)
            {
                UnityYamlAsset yaml = side.GetYamlAsset(assetPath);
                Assert.That(
                    yaml.RawText,
                    Is.EqualTo(Encoding.UTF8.GetString(bytes)),
                    $"The indexed Unity YAML for '{assetPath}' changed during comparison.");

                IReadOnlyDictionary<string, string> ownDocuments =
                    side.GetDocumentTokens(assetPath);
                var canonicalDocuments = new List<string>(yaml.Documents.Count);
                foreach (UnityYamlDocument document in yaml.Documents)
                {
                    string text = document.RawText;

                    text = CrossAssetReferenceRegex.Replace(text, match =>
                    {
                        string guid = match.Groups[4].Value;
                        if (!side.TryGetPathByGuid(guid, out string targetPath))
                            return match.Value;

                        string fileId = match.Groups[2].Value;
                        IReadOnlyDictionary<string, string> targetDocuments =
                            side.GetDocumentTokens(targetPath);
                        string canonicalFileId = targetDocuments.TryGetValue(
                            fileId,
                            out string documentToken)
                            ? documentToken
                            : fileId;
                        return match.Groups[1].Value + canonicalFileId +
                               match.Groups[3].Value + GuidToken(targetPath);
                    });

                    text = UnityDocumentRegex.Replace(text, match =>
                    {
                        string fileId = match.Groups[2].Value;
                        Assert.That(ownDocuments.TryGetValue(fileId, out string token), Is.True,
                            $"Unity YAML document '{fileId}' in '{assetPath}' was not indexed.");
                        return match.Groups[1].Value + token + match.Groups[3].Value;
                    });

                    text = LocalFileIdRegex.Replace(text, match =>
                    {
                        string fileId = match.Groups[2].Value;
                        return ownDocuments.TryGetValue(fileId, out string token)
                            ? match.Groups[1].Value + token
                            : match.Value;
                    });

                    text = GuidTokenRegex.Replace(text, match =>
                        side.TryGetPathByGuid(match.Value, out string targetPath)
                            ? GuidToken(targetPath)
                            : match.Value);

                    text = CanonicalizeManagedReferenceIds(text);
                    text = NormalizeFusionSortKeys(text);
                    canonicalDocuments.Add(text);
                }

                canonicalDocuments.Sort(StringComparer.Ordinal);
                return yaml.Preamble + string.Concat(canonicalDocuments);
            }

            private static string CanonicalizeManagedReferenceIds(string text)
            {
                var managedReferenceTokens =
                    new Dictionary<string, string>(StringComparer.Ordinal);
                text = ManagedReferenceIdRegex.Replace(text, match =>
                {
                    string rid = match.Groups[2].Value;
                    if (rid == "0") return match.Value;
                    if (!managedReferenceTokens.TryGetValue(rid, out string token))
                    {
                        token = $"<GC2_RID_{managedReferenceTokens.Count + 1:D4}>";
                        managedReferenceTokens.Add(rid, token);
                    }
                    return match.Groups[1].Value + token;
                });
                return text;
            }

            private static string NormalizeFusionSortKeys(string text)
            {
                text = FusionSortKeyOverrideRegex.Replace(
                    text,
                    match => match.Groups[1].Value + "<GC2_FUSION_SORT_KEY>");
                text = FusionSortKeyRegex.Replace(
                    text,
                    match => match.Groups[1].Value +
                             "<GC2_FUSION_SORT_KEY>" + match.Groups[2].Value);
                return text;
            }

            private static bool IsUnityYaml(byte[] bytes)
            {
                string prefix = Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 16));
                return prefix.TrimStart('\uFEFF').StartsWith("%YAML", StringComparison.Ordinal);
            }

            private static string GuidToken(string assetPath) =>
                $"<GC2_GUID:{assetPath}>";

            private sealed class UnityYamlAsset
            {
                private UnityYamlAsset(
                    string rawText,
                    string preamble,
                    IReadOnlyList<UnityYamlDocument> documents)
                {
                    RawText = rawText;
                    Preamble = preamble;
                    Documents = documents;
                }

                public string RawText { get; }
                public string Preamble { get; }
                public IReadOnlyList<UnityYamlDocument> Documents { get; }

                public static UnityYamlAsset Parse(
                    string assetPath,
                    byte[] bytes,
                    string sourceName)
                {
                    string text = Encoding.UTF8.GetString(bytes);
                    MatchCollection headers = UnityDocumentRegex.Matches(text);
                    Assert.That(headers.Count, Is.GreaterThan(0),
                        $"The {sourceName} Unity YAML '{assetPath}' contains no documents.");

                    var documents = new List<UnityYamlDocument>(headers.Count);
                    var fileIds = new HashSet<string>(StringComparer.Ordinal);
                    for (int index = 0; index < headers.Count; ++index)
                    {
                        Match header = headers[index];
                        string fileId = header.Groups[2].Value;
                        Assert.That(fileIds.Add(fileId), Is.True,
                            $"The {sourceName} Unity YAML '{assetPath}' declares duplicate " +
                            $"document ID '{fileId}'.");
                        int end = index + 1 < headers.Count
                            ? headers[index + 1].Index
                            : text.Length;
                        documents.Add(new UnityYamlDocument(
                            assetPath,
                            fileId,
                            text.Substring(header.Index, end - header.Index)));
                    }

                    return new UnityYamlAsset(
                        text,
                        text.Substring(0, headers[0].Index),
                        documents);
                }
            }

            private sealed class UnityYamlDocument
            {
                public UnityYamlDocument(string assetPath, string fileId, string rawText)
                {
                    AssetPath = assetPath;
                    FileId = fileId;
                    RawText = rawText;
                    NodeKey = assetPath + "\u001f" + fileId;
                    FingerprintTemplate = string.Empty;
                    ReferencedNodeKeys = Array.Empty<string>();
                    SemanticToken = string.Empty;
                }

                public string AssetPath { get; }
                public string FileId { get; }
                public string RawText { get; }
                public string NodeKey { get; }
                public string FingerprintTemplate { get; set; }
                public IReadOnlyList<string> ReferencedNodeKeys { get; set; }
                public string SemanticToken { get; set; }
            }

            private sealed class PayloadSide
            {
                private readonly IReadOnlyDictionary<string, string> m_PathByGuid;
                private readonly IReadOnlyDictionary<
                    string,
                    IReadOnlyDictionary<string, string>> m_DocumentsByPath;
                private readonly IReadOnlyDictionary<string, UnityYamlAsset> m_YamlByPath;
                private readonly IReadOnlyDictionary<
                    string,
                    IReadOnlyDictionary<string, UnityYamlDocument>> m_DocumentObjectsByPath;
                private readonly IReadOnlyDictionary<string, UnityYamlDocument>
                    m_DocumentsByNodeKey;

                private PayloadSide(
                    IReadOnlyDictionary<string, string> pathByGuid,
                    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
                        documentsByPath,
                    IReadOnlyDictionary<string, UnityYamlAsset> yamlByPath,
                    IReadOnlyDictionary<string, IReadOnlyDictionary<string, UnityYamlDocument>>
                        documentObjectsByPath,
                    IReadOnlyDictionary<string, UnityYamlDocument> documentsByNodeKey)
                {
                    m_PathByGuid = pathByGuid;
                    m_DocumentsByPath = documentsByPath;
                    m_YamlByPath = yamlByPath;
                    m_DocumentObjectsByPath = documentObjectsByPath;
                    m_DocumentsByNodeKey = documentsByNodeKey;
                }

                public static PayloadSide Create(
                    IReadOnlyDictionary<string, byte[]> metas,
                    IReadOnlyDictionary<string, byte[]> assets,
                    IEnumerable<string> assetPaths,
                    string sourceName)
                {
                    var pathByGuid = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                    string[] paths = assetPaths.ToArray();

                    foreach (string assetPath in paths)
                    {
                        Assert.That(metas.TryGetValue(assetPath, out byte[] metaBytes), Is.True,
                            $"The {sourceName} is missing metadata for '{assetPath}'.");
                        string guid = ReadOwnGuid(metaBytes, assetPath, sourceName);
                        bool duplicateGuid = pathByGuid.TryGetValue(
                            guid,
                            out string existingPath);
                        Assert.That(duplicateGuid, Is.False,
                            $"The {sourceName} reuses Unity GUID '{guid}' for " +
                            $"'{existingPath}' and '{assetPath}'.");
                        pathByGuid.Add(guid, assetPath);
                    }

                    var yamlByPath = new Dictionary<string, UnityYamlAsset>(
                        StringComparer.Ordinal);
                    var documentObjectsByPath = new Dictionary<
                        string,
                        IReadOnlyDictionary<string, UnityYamlDocument>>(StringComparer.Ordinal);
                    var documentsByNodeKey = new Dictionary<string, UnityYamlDocument>(
                        StringComparer.Ordinal);
                    foreach (string assetPath in paths)
                    {
                        if (!assets.TryGetValue(assetPath, out byte[] assetBytes) ||
                            !IsUnityYaml(assetBytes))
                        {
                            continue;
                        }

                        UnityYamlAsset yaml = UnityYamlAsset.Parse(
                            assetPath,
                            assetBytes,
                            sourceName);
                        yamlByPath.Add(assetPath, yaml);
                        var byFileId = new Dictionary<string, UnityYamlDocument>(
                            StringComparer.Ordinal);
                        foreach (UnityYamlDocument document in yaml.Documents)
                        {
                            byFileId.Add(document.FileId, document);
                            documentsByNodeKey.Add(document.NodeKey, document);
                        }
                        documentObjectsByPath.Add(assetPath, byFileId);
                    }

                    var side = new PayloadSide(
                        pathByGuid,
                        new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                        yamlByPath,
                        documentObjectsByPath,
                        documentsByNodeKey);
                    side.AssignSemanticDocumentTokens();

                    var documentsByPath = new Dictionary<
                        string,
                        IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
                    foreach (KeyValuePair<
                                 string,
                                 IReadOnlyDictionary<string, UnityYamlDocument>> entry
                             in documentObjectsByPath)
                    {
                        documentsByPath.Add(
                            entry.Key,
                            entry.Value.ToDictionary(
                                item => item.Key,
                                item => item.Value.SemanticToken,
                                StringComparer.Ordinal));
                    }

                    return new PayloadSide(
                        pathByGuid,
                        documentsByPath,
                        yamlByPath,
                        documentObjectsByPath,
                        documentsByNodeKey);
                }

                public bool TryGetPathByGuid(string guid, out string assetPath) =>
                    m_PathByGuid.TryGetValue(guid, out assetPath);

                public IReadOnlyDictionary<string, string> GetDocumentTokens(
                    string assetPath) =>
                    m_DocumentsByPath.TryGetValue(
                        assetPath,
                        out IReadOnlyDictionary<string, string> documents)
                        ? documents
                        : EmptyDocumentTokens;

                public UnityYamlAsset GetYamlAsset(string assetPath)
                {
                    Assert.That(m_YamlByPath.TryGetValue(
                        assetPath,
                        out UnityYamlAsset yaml), Is.True,
                        $"Unity YAML '{assetPath}' was not indexed.");
                    return yaml;
                }

                private void AssignSemanticDocumentTokens()
                {
                    if (m_DocumentsByNodeKey.Count == 0) return;

                    foreach (UnityYamlDocument document in m_DocumentsByNodeKey.Values)
                    {
                        BuildFingerprintTemplate(document);
                    }

                    Dictionary<string, string> colors = AssignColorClasses(
                        m_DocumentsByNodeKey.Values.ToDictionary(
                            document => document.NodeKey,
                            document => document.FingerprintTemplate,
                            StringComparer.Ordinal));
                    int classCount = colors.Values.Distinct(StringComparer.Ordinal).Count();

                    // The document graph is refined until no equivalence class splits. Including
                    // the previous class in every signature makes this partition monotonic, while
                    // ordered reference colors retain hierarchy and component-list semantics.
                    for (int iteration = 0;
                         iteration < m_DocumentsByNodeKey.Count;
                         ++iteration)
                    {
                        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (UnityYamlDocument document in m_DocumentsByNodeKey.Values)
                        {
                            var signature = new StringBuilder(colors[document.NodeKey]);
                            foreach (string referencedNodeKey in document.ReferencedNodeKeys)
                            {
                                signature.Append('|');
                                signature.Append(colors[referencedNodeKey]);
                            }
                            signatures.Add(document.NodeKey, signature.ToString());
                        }

                        Dictionary<string, string> refined = AssignColorClasses(signatures);
                        int refinedClassCount = refined.Values
                            .Distinct(StringComparer.Ordinal)
                            .Count();
                        colors = refined;
                        if (refinedClassCount == classCount) break;
                        classCount = refinedClassCount;
                    }

                    foreach (UnityYamlDocument document in m_DocumentsByNodeKey.Values)
                    {
                        document.SemanticToken =
                            $"<GC2_DOC_CLASS_{colors[document.NodeKey]}>";
                    }
                }

                private void BuildFingerprintTemplate(UnityYamlDocument document)
                {
                    var referencedNodeKeys = new List<string>();
                    string RegisterReference(UnityYamlDocument target)
                    {
                        referencedNodeKeys.Add(target.NodeKey);
                        return $"<GC2_NODE_REF_{referencedNodeKeys.Count:D4}>";
                    }

                    string text = document.RawText;
                    text = CrossAssetReferenceRegex.Replace(text, match =>
                    {
                        string guid = match.Groups[4].Value;
                        if (!TryGetPathByGuid(guid, out string targetPath))
                            return match.Value;

                        string fileId = match.Groups[2].Value;
                        string canonicalFileId = TryGetDocument(
                            targetPath,
                            fileId,
                            out UnityYamlDocument target)
                            ? RegisterReference(target)
                            : fileId;
                        return match.Groups[1].Value + canonicalFileId +
                               match.Groups[3].Value + GuidToken(targetPath);
                    });

                    MatchCollection headers = UnityDocumentRegex.Matches(text);
                    Assert.That(headers, Has.Count.EqualTo(1),
                        $"Indexed Unity YAML document '{document.FileId}' in " +
                        $"'{document.AssetPath}' must contain exactly one header.");
                    text = UnityDocumentRegex.Replace(
                        text,
                        match => match.Groups[1].Value +
                                 "<GC2_SELF_DOCUMENT>" + match.Groups[3].Value,
                        1);

                    text = LocalFileIdRegex.Replace(text, match =>
                    {
                        string fileId = match.Groups[2].Value;
                        return TryGetDocument(
                            document.AssetPath,
                            fileId,
                            out UnityYamlDocument target)
                            ? match.Groups[1].Value + RegisterReference(target)
                            : match.Value;
                    });
                    text = GuidTokenRegex.Replace(text, match =>
                        TryGetPathByGuid(match.Value, out string targetPath)
                            ? GuidToken(targetPath)
                            : match.Value);
                    text = CanonicalizeManagedReferenceIds(text);
                    text = NormalizeFusionSortKeys(text);

                    document.FingerprintTemplate = text;
                    document.ReferencedNodeKeys = referencedNodeKeys;
                }

                private bool TryGetDocument(
                    string assetPath,
                    string fileId,
                    out UnityYamlDocument document)
                {
                    if (m_DocumentObjectsByPath.TryGetValue(
                            assetPath,
                            out IReadOnlyDictionary<string, UnityYamlDocument> documents))
                    {
                        return documents.TryGetValue(fileId, out document);
                    }
                    document = null;
                    return false;
                }

                private static Dictionary<string, string> AssignColorClasses(
                    IReadOnlyDictionary<string, string> signatures)
                {
                    string[] uniqueSignatures = signatures.Values
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(signature => signature, StringComparer.Ordinal)
                        .ToArray();
                    var colorBySignature = new Dictionary<string, string>(
                        uniqueSignatures.Length,
                        StringComparer.Ordinal);
                    for (int index = 0; index < uniqueSignatures.Length; ++index)
                    {
                        colorBySignature.Add(
                            uniqueSignatures[index],
                            $"C{index + 1:D8}");
                    }

                    return signatures.ToDictionary(
                        item => item.Key,
                        item => colorBySignature[item.Value],
                        StringComparer.Ordinal);
                }

                private static string ReadOwnGuid(
                    byte[] bytes,
                    string assetPath,
                    string sourceName)
                {
                    string text = Encoding.UTF8.GetString(bytes);
                    MatchCollection matches = MetaGuidRegex.Matches(text);
                    Assert.That(matches, Has.Count.EqualTo(1),
                        $"The {sourceName} metadata for '{assetPath}' must contain exactly one " +
                        "top-level Unity GUID.");
                    return matches[0].Groups[2].Value;
                }

                private static readonly IReadOnlyDictionary<string, string>
                    EmptyDocumentTokens = new Dictionary<string, string>();
            }
        }

        public sealed class InstallerSpec
        {
            public InstallerSpec(
                string transport,
                string installId,
                string meleeDependencyId,
                string playerName,
                string enemyName,
                string sceneName,
                string packagePath,
                string descriptorPath,
                string registryRelativePath)
            {
                Transport = transport;
                InstallId = installId;
                MeleeDependencyId = meleeDependencyId;
                PlayerName = playerName;
                EnemyName = enemyName;
                SceneName = sceneName;
                PackagePath = packagePath;
                DescriptorPath = descriptorPath;
                RegistryRelativePath = registryRelativePath;
            }

            public string Transport { get; }
            public string InstallId { get; }
            public string MeleeDependencyId { get; }
            public string PlayerName { get; }
            public string EnemyName { get; }
            public string SceneName { get; }
            public string PackagePath { get; }
            public string DescriptorPath { get; }
            public string RegistryRelativePath { get; }
            public string InstallRoot => InstallsRoot + InstallId + "@1.0.0";
            public string PlayerPath => InstallRoot + "/" + PlayerName;
            public string EnemyPath => InstallRoot + "/" + EnemyName;
            public string ScenePath => InstallRoot + "/" + SceneName;

            public override string ToString() => Transport;
        }

        private sealed class UnityPackageContents
        {
            public UnityPackageContents(
                IReadOnlyCollection<string> pathnames,
                IReadOnlyDictionary<string, byte[]> assets,
                IReadOnlyDictionary<string, byte[]> assetMetas)
            {
                Pathnames = pathnames;
                Assets = assets;
                AssetMetas = assetMetas;
            }

            public IReadOnlyCollection<string> Pathnames { get; }
            public IReadOnlyDictionary<string, byte[]> Assets { get; }
            public IReadOnlyDictionary<string, byte[]> AssetMetas { get; }
        }
    }
}
#endif
