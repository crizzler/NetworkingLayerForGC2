using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>Shared, transport-neutral authoring and validation for server-owned NPC prefabs.</summary>
    public static class NetworkNpcSetupEditorUtility
    {
        public static bool ConfigureServerNpc(
            GameObject prefabRoot,
            NetworkPredictionBackend predictionBackend,
            IReadOnlyList<string> authorityRootPaths,
            IReadOnlyList<Type> requiredComponentTypes,
            List<string> changes,
            List<string> errors)
        {
            if (prefabRoot == null) throw new ArgumentNullException(nameof(prefabRoot));
            changes ??= new List<string>();
            errors ??= new List<string>();

            Character character = EnsureComponent<Character>(prefabRoot, changes);
            if (character.IsPlayer)
            {
                character.IsPlayer = false;
                EditorUtility.SetDirty(character);
                changes.Add("Character.IsPlayer=false");
            }

            if (predictionBackend == NetworkPredictionBackend.BuiltIn &&
                character.Driver is not UnitDriverNavmesh &&
                character.Driver is not UnitDriverNavmeshNetworkServer)
            {
                var serializedCharacter = new SerializedObject(character);
                SerializedProperty driver = serializedCharacter
                    .FindProperty("m_Kernel")
                    ?.FindPropertyRelative("m_Driver");
                if (driver != null &&
                    driver.propertyType == SerializedPropertyType.ManagedReference)
                {
                    driver.managedReferenceValue = new UnitDriverNavmeshNetworkServer();
                    serializedCharacter.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(character);
                    changes.Add("server-authoritative NavMesh driver");
                }
                else
                {
                    errors.Add("The GC2 Character driver could not be configured for server NavMesh movement.");
                }
            }

            NetworkCharacter networkCharacter =
                EnsureComponent<NetworkCharacter>(prefabRoot, changes);
            var serializedNetworkCharacter = new SerializedObject(networkCharacter);
            bool networkChanged = false;
            networkChanged |= SetEnum(
                serializedNetworkCharacter,
                "m_ActorType",
                (int)NetworkCharacterActorType.NPC);
            networkChanged |= SetEnum(
                serializedNetworkCharacter,
                "m_NPCMode",
                (int)NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            networkChanged |= SetEnum(
                serializedNetworkCharacter,
                "m_PredictionBackend",
                (int)predictionBackend);
            networkChanged |= SetBool(serializedNetworkCharacter, "m_UseNetworkMotion", true);
            networkChanged |= SetBool(serializedNetworkCharacter, "m_UseAnimationSync", true);
            networkChanged |= SetBool(serializedNetworkCharacter, "m_UseCoreNetworking", true);
            networkChanged |= SetBool(
                serializedNetworkCharacter,
                "m_HostOwnerUsesClientPrediction",
                false);
            if (networkChanged)
            {
                serializedNetworkCharacter.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(networkCharacter);
                changes.Add("explicit server-owned NPC authority");
            }

            NetworkCharacterAuthorityGate gate =
                EnsureComponent<NetworkCharacterAuthorityGate>(prefabRoot, changes);
            EnsureComponent<NetworkNpcTargetSelector>(prefabRoot, changes);
            ConfigureAuthorityRoots(prefabRoot, gate, authorityRootPaths, errors, changes);

            if (requiredComponentTypes != null)
            {
                for (int i = 0; i < requiredComponentTypes.Count; i++)
                {
                    Type type = requiredComponentTypes[i];
                    if (type == null || !typeof(Component).IsAssignableFrom(type)) continue;
                    if (prefabRoot.GetComponent(type) != null) continue;
                    prefabRoot.AddComponent(type);
                    changes.Add(type.Name);
                }
            }

            return errors.Count == 0;
        }

        public static void ValidateServerNpc(
            GameObject prefab,
            Type requiredIdentityType,
            IReadOnlyList<string> authorityRootPaths,
            ICollection<string> errors,
            ICollection<string> warnings)
        {
            if (errors == null) throw new ArgumentNullException(nameof(errors));
            if (warnings == null) throw new ArgumentNullException(nameof(warnings));
            if (prefab == null)
            {
                errors.Add("Assign an NPC prefab before preparing server-owned NPCs or bot slots.");
                return;
            }

            Character character = prefab.GetComponent<Character>();
            NetworkCharacter networkCharacter = prefab.GetComponent<NetworkCharacter>();
            if (character == null) errors.Add($"NPC prefab '{prefab.name}' has no GC2 Character component.");
            if (requiredIdentityType != null && prefab.GetComponent(requiredIdentityType) == null)
            {
                errors.Add(
                    $"NPC prefab '{prefab.name}' has no root {requiredIdentityType.Name} transport identity.");
            }
            if (networkCharacter == null)
            {
                errors.Add($"NPC prefab '{prefab.name}' has no NetworkCharacter component.");
                return;
            }

            if (networkCharacter.ActorType != NetworkCharacterActorType.NPC)
            {
                errors.Add(
                    $"NPC prefab '{prefab.name}' must use Actor Type NPC; Character.IsPlayer is not an authority setting.");
            }
            if (!networkCharacter.IsServerAuthoritativeNPC)
            {
                bool hasDurableCombat = HasDurableGameplayController(prefab);
                string message =
                    $"NPC prefab '{prefab.name}' is client-deterministic. Client-deterministic NPCs are cosmetic" +
                    (hasDurableCombat
                        ? " and cannot contain Shooter, Melee, Stats, Inventory, Abilities, or Traversal controllers."
                        : " and cannot author durable gameplay state.");
                if (hasDurableCombat) errors.Add(message);
                else warnings.Add(message);
            }

            NetworkCharacterAuthorityGate gate =
                prefab.GetComponent<NetworkCharacterAuthorityGate>();
            if (gate == null)
            {
                errors.Add(
                    $"NPC prefab '{prefab.name}' needs NetworkCharacterAuthorityGate so AI stops immediately when authority is unresolved or migrates.");
            }

            if (prefab.GetComponent<NetworkNpcTargetSelector>() == null)
            {
                warnings.Add(
                    $"NPC prefab '{prefab.name}' has no NetworkNpcTargetSelector; authored AI must provide its own authenticated player targeting.");
            }

            bool hasTriggers = HasGc2Triggers(prefab);
            if (hasTriggers && (authorityRootPaths == null || authorityRootPaths.Count == 0))
            {
                errors.Add(
                    $"NPC prefab '{prefab.name}' contains GC2 Triggers but no explicit authority-only AI roots were selected.");
            }
            else if (authorityRootPaths != null)
            {
                for (int i = 0; i < authorityRootPaths.Count; i++)
                {
                    string path = authorityRootPaths[i]?.Trim();
                    if (string.IsNullOrEmpty(path) || prefab.transform.Find(path) == null)
                    {
                        errors.Add(
                            $"NPC authority root path '{authorityRootPaths[i]}' does not exist below '{prefab.name}'.");
                    }
                }
            }

            MonoBehaviour[] behaviours = prefab.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                string fullName = behaviour.GetType().FullName ?? string.Empty;
                if (fullName.Contains("PurrDiction", StringComparison.Ordinal) ||
                    fullName.Contains("FusionKcc", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(
                        $"NPC prefab '{prefab.name}' contains owner-predicted component '{behaviour.GetType().Name}'. " +
                        "Server-owned NPCs must use a supported server authority backend.");
                }
            }
        }

        public static bool HasGc2Triggers(GameObject root)
        {
            if (root == null) return false;
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                Type type = behaviours[i]?.GetType();
                if (type != null &&
                    string.Equals(
                        type.FullName,
                        "GameCreator.Runtime.VisualScripting.Trigger",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void ConfigureAuthorityRoots(
            GameObject root,
            NetworkCharacterAuthorityGate gate,
            IReadOnlyList<string> paths,
            ICollection<string> errors,
            ICollection<string> changes)
        {
            var selected = new List<GameObject>();
            if (paths != null)
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    string path = paths[i]?.Trim();
                    if (string.IsNullOrEmpty(path)) continue;
                    Transform target = root.transform.Find(path);
                    if (target == null)
                    {
                        errors.Add($"Authority-only AI root '{path}' was not found below '{root.name}'.");
                        continue;
                    }
                    if (!selected.Contains(target.gameObject)) selected.Add(target.gameObject);
                }
            }

            var serializedGate = new SerializedObject(gate);
            SerializedProperty roots = serializedGate.FindProperty("m_AuthorityOnlyRoots");
            if (roots == null || !roots.isArray) return;
            bool changed = roots.arraySize != selected.Count;
            roots.arraySize = selected.Count;
            for (int i = 0; i < selected.Count; i++)
            {
                SerializedProperty element = roots.GetArrayElementAtIndex(i);
                if (element.objectReferenceValue != selected[i]) changed = true;
                element.objectReferenceValue = selected[i];
            }
            if (!changed) return;
            serializedGate.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(gate);
            changes.Add("explicit authority-only AI roots");
        }

        private static bool HasDurableGameplayController(GameObject root)
        {
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                string name = behaviours[i]?.GetType().Name ?? string.Empty;
                if (name is "NetworkShooterController" or
                    "NetworkMeleeController" or
                    "NetworkStatsController" or
                    "NetworkInventoryController" or
                    "NetworkAbilitiesController" or
                    "NetworkTraversalController")
                {
                    return true;
                }
            }
            return false;
        }

        private static T EnsureComponent<T>(GameObject root, ICollection<string> changes)
            where T : Component
        {
            T component = root.GetComponent<T>();
            if (component != null) return component;
            component = root.AddComponent<T>();
            changes?.Add(typeof(T).Name);
            return component;
        }

        private static bool SetBool(SerializedObject serialized, string name, bool value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null || property.boolValue == value) return false;
            property.boolValue = value;
            return true;
        }

        private static bool SetEnum(SerializedObject serialized, string name, int value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null || property.enumValueIndex == value) return false;
            property.enumValueIndex = value;
            return true;
        }
    }
}
