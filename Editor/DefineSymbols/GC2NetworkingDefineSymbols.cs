#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    [InitializeOnLoad]
    [DefaultExecutionOrder(100)]
    public static class GC2NetworkingDefineSymbols
    {
        private const string SYMBOL_INVENTORY = "GC2_INVENTORY";
        public const string SYMBOL_INVENTORY_AUTHORITY_PATCH = "GC2_NETWORK_INVENTORY_PATCHED";
        private const string SYMBOL_STATS = "GC2_STATS";
        private const string SYMBOL_SHOOTER = "GC2_SHOOTER";
        private const string SYMBOL_MELEE = "GC2_MELEE";
        public const string SYMBOL_MELEE_AUTHORITY_PATCH = "GC2_NETWORK_MELEE_PATCHED";
        private const string SYMBOL_QUESTS = "GC2_QUESTS";
        private const string SYMBOL_DIALOGUE = "GC2_DIALOGUE";
        private const string SYMBOL_TRAVERSAL = "GC2_TRAVERSAL";
        public const string SYMBOL_TRAVERSAL_AUTHORITY_PATCH = "GC2_NETWORK_TRAVERSAL_PATCHED";
        private const string SYMBOL_ABILITIES = "GC2_ABILITIES";
        private const string SYMBOL_TRANSPORT_INTEGRATION = "ARAWN_GC2_TRANSPORT_INTEGRATION";
        private const string OBSOLETE_SYMBOL_PURRNET_TRANSPORT = "ARAWN_GC2_PURRNET_TRANSPORT";

        private const string GC2_PACKAGES_ROOT = "Assets/Plugins/GameCreator/Packages";
        private const string GC2_INVENTORY_DIR = GC2_PACKAGES_ROOT + "/Inventory";
        private const string GC2_INVENTORY_PATCH_ABI_FILE =
            GC2_INVENTORY_DIR + "/Runtime/Classes/Bag/Content/TBagContent.cs";
        private const string GC2_STATS_DIR = GC2_PACKAGES_ROOT + "/Stats";
        private const string GC2_SHOOTER_DIR = GC2_PACKAGES_ROOT + "/Shooter";
        private const string GC2_MELEE_DIR = GC2_PACKAGES_ROOT + "/Melee";
        private const string GC2_QUESTS_DIR = GC2_PACKAGES_ROOT + "/Quests";
        private const string GC2_DIALOGUE_DIR = GC2_PACKAGES_ROOT + "/Dialogue";
        private const string GC2_TRAVERSAL_DIR = GC2_PACKAGES_ROOT + "/Traversal";

        private const string ABILITIES_MODULE_DIR = "Assets/Plugins/DaimahouGames/Packages/Abilities";
        private const string TRANSPORT_RUNTIME_ROOT = "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport";
        private const string TRANSPORT_EDITOR_ROOT = "Assets/Arawn/NetworkingLayerForGC2/Editor/Transport";

        private static bool s_IsUpdating;
        private static bool s_PendingUpdate;
        private static readonly Dictionary<string, bool> s_NamespaceCache = new Dictionary<string, bool>();

        static GC2NetworkingDefineSymbols()
        {
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            EditorApplication.projectChanged += QueueUpdate;
            QueueUpdate();
        }

        public static void RefreshNow(bool manageReloadLock = true)
        {
            s_NamespaceCache.Clear();
            UpdateDefineSymbols(manageReloadLock);
        }

        private static void OnAfterAssemblyReload()
        {
            s_NamespaceCache.Clear();
            QueueUpdate();
        }

        private static void QueueUpdate()
        {
            if (s_PendingUpdate) return;

            s_PendingUpdate = true;
            EditorApplication.delayCall += () =>
            {
                s_PendingUpdate = false;
                UpdateDefineSymbols();
            };
        }

        private static void UpdateDefineSymbols(bool manageReloadLock = true)
        {
            if (s_IsUpdating) return;

            s_IsUpdating = true;

            try
            {
                BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
                if (group == BuildTargetGroup.Unknown) return;

                NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(group);
                string currentSymbols = PlayerSettings.GetScriptingDefineSymbols(namedBuildTarget);
                List<string> symbolList = currentSymbols
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                ManageSymbol(symbolList, SYMBOL_INVENTORY, IsInventoryInstalled());
                ManageSymbol(
                    symbolList,
                    SYMBOL_INVENTORY_AUTHORITY_PATCH,
                    IsInventoryInstalled() && IsInventoryAuthorityPatchApplied());
                ManageSymbol(symbolList, SYMBOL_STATS, IsStatsInstalled());
                ManageSymbol(symbolList, SYMBOL_SHOOTER, IsShooterInstalled());
                ManageSymbol(symbolList, SYMBOL_MELEE, IsMeleeInstalled());
                ManageSymbol(
                    symbolList,
                    SYMBOL_MELEE_AUTHORITY_PATCH,
                    IsMeleeInstalled() && IsMeleeAuthorityPatchApplied());
                ManageSymbol(symbolList, SYMBOL_QUESTS, IsQuestsInstalled());
                ManageSymbol(symbolList, SYMBOL_DIALOGUE, IsDialogueInstalled());
                ManageSymbol(symbolList, SYMBOL_TRAVERSAL, IsTraversalInstalled());
                ManageSymbol(
                    symbolList,
                    SYMBOL_TRAVERSAL_AUTHORITY_PATCH,
                    IsTraversalInstalled() && IsTraversalAuthorityPatchApplied());
                ManageSymbol(symbolList, SYMBOL_ABILITIES, IsAbilitiesInstalled());
                ManageSymbol(symbolList, SYMBOL_TRANSPORT_INTEGRATION, IsTransportIntegrationInstalled());
                RemoveSymbol(symbolList, OBSOLETE_SYMBOL_PURRNET_TRANSPORT);

                string newSymbols = string.Join(";", symbolList);
                if (newSymbols == currentSymbols) return;

                if (manageReloadLock) EditorApplication.LockReloadAssemblies();
                try
                {
                    PlayerSettings.SetScriptingDefineSymbols(namedBuildTarget, newSymbols);
                    Debug.Log("[GC2 Networking] Scripting Define Symbols synchronized for current build target.");
                }
                finally
                {
                    if (manageReloadLock) EditorApplication.UnlockReloadAssemblies();
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GC2 Networking] Failed to synchronize define symbols: {exception}");
            }
            finally
            {
                s_IsUpdating = false;
            }
        }

        private static bool IsInventoryInstalled()
        {
            return Directory.Exists(GC2_INVENTORY_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Inventory");
        }

        public static bool IsInventoryAuthorityPatchApplied()
        {
            if (!File.Exists(GC2_INVENTORY_PATCH_ABI_FILE)) return false;

            try
            {
                if (!HasInventoryAuthorityPatchAbi(File.ReadAllText(GC2_INVENTORY_PATCH_ABI_FILE)))
                {
                    return false;
                }

                // The define enables code that consumes hook members spread across all twelve
                // patched Inventory files. Reuse the patcher's complete structural verification;
                // a partial or stale patch must never enable that compile-time ABI.
                return new Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches.InventoryPatcher()
                    .IsPatched();
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public static bool HasInventoryAuthorityPatchAbi(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;

            return content.Contains("// [GC2_NETWORK_PATCH_Inventory_", StringComparison.Ordinal) &&
                   content.Contains("public const int NetworkPatchRevision = 300;", StringComparison.Ordinal) &&
                   content.Contains("public enum NetworkInventoryInterceptResult", StringComparison.Ordinal) &&
                   content.Contains("NetworkInstructionAddItemInterceptor", StringComparison.Ordinal);
        }

        private static bool IsStatsInstalled()
        {
            return Directory.Exists(GC2_STATS_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Stats");
        }

        private static bool IsShooterInstalled()
        {
            return Directory.Exists(GC2_SHOOTER_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Shooter");
        }

        private static bool IsMeleeInstalled()
        {
            return Directory.Exists(GC2_MELEE_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Melee");
        }

        public static bool IsMeleeAuthorityPatchApplied()
        {
            try
            {
                var patcher =
                    new Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches.MeleePatcher();
                return patcher.ValidateFilesExist() && patcher.IsPatched();
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsQuestsInstalled()
        {
            return Directory.Exists(GC2_QUESTS_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Quests");
        }

        private static bool IsDialogueInstalled()
        {
            return Directory.Exists(GC2_DIALOGUE_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Dialogue");
        }

        private static bool IsTraversalInstalled()
        {
            return Directory.Exists(GC2_TRAVERSAL_DIR) || IsNamespacePresentCached("GameCreator.Runtime.Traversal");
        }

        public static bool IsTraversalAuthorityPatchApplied()
        {
            try
            {
                var patcher =
                    new Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches.TraversalPatcher();
                return patcher.ValidateFilesExist() && patcher.IsPatched();
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsAbilitiesInstalled()
        {
            return Directory.Exists(ABILITIES_MODULE_DIR) || IsNamespacePresentCached("DaimahouGames.Runtime.Abilities");
        }

        private static bool IsTransportIntegrationInstalled()
        {
            return HasTransportSubdirectory(TRANSPORT_RUNTIME_ROOT) ||
                   HasTransportSubdirectory(TRANSPORT_EDITOR_ROOT) ||
                   IsNamespacePresentCached("Arawn.GameCreator2.Networking.Transport.PurrNet") ||
                   IsNamespacePresentCached("Arawn.GameCreator2.Networking.Transport.Fusion");
        }

        private static bool HasTransportSubdirectory(string rootPath)
        {
            if (!Directory.Exists(rootPath)) return false;

            return Directory.EnumerateDirectories(rootPath).Any(directory =>
            {
                string directoryName = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(directoryName)) return false;
                if (directoryName.StartsWith(".", StringComparison.Ordinal)) return false;

                return Directory.EnumerateFileSystemEntries(directory).Any();
            });
        }

        private static void ManageSymbol(List<string> symbolList, string symbol, bool shouldDefine)
        {
            if (shouldDefine)
            {
                if (!symbolList.Contains(symbol))
                {
                    symbolList.Add(symbol);
                }
            }
            else
            {
                symbolList.RemoveAll(existing => existing == symbol);
            }
        }

        private static void RemoveSymbol(List<string> symbolList, string symbol)
        {
            symbolList.RemoveAll(existing => existing == symbol);
        }

        private static bool IsNamespacePresentCached(string namespaceName)
        {
            if (s_NamespaceCache.TryGetValue(namespaceName, out bool exists))
            {
                return exists;
            }

            exists = IsNamespacePresent(namespaceName);
            s_NamespaceCache[namespaceName] = exists;
            return exists;
        }

        private static bool IsNamespacePresent(string namespaceName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || assembly.ReflectionOnly) continue;

                try
                {
                    if (assembly.GetTypes().Any(type => type.Namespace == namespaceName))
                    {
                        return true;
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException)
                {
                    // Ignore partially loadable assemblies.
                }
            }

            return false;
        }
    }
}
#endif
