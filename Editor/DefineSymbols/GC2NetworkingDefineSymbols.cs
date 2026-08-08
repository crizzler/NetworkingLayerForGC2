#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        public const string SYMBOL_FUSION_KCC = "ARAWN_GC2_FUSION_KCC";

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
        private const string FUSION_KCC_ROOT_SESSION_KEY =
            "Arawn.GC2Networking.FusionKccSourceRoot";

        private static bool s_IsUpdating;
        private static bool s_PendingUpdate;
        private static bool s_ForceFusionKccAbsentUntilReload;
        private static bool? s_FusionKccApiInstalled;
        private static string s_FusionKccSourceRoot;
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
            s_FusionKccApiInstalled = null;
            s_FusionKccSourceRoot = null;
            UpdateDefineSymbols(manageReloadLock);
        }

        private static void OnAfterAssemblyReload()
        {
            s_ForceFusionKccAbsentUntilReload = false;
            s_FusionKccApiInstalled = null;
            s_FusionKccSourceRoot = null;
            s_NamespaceCache.Clear();
            QueueUpdate();
        }

        internal static void QueueUpdate()
        {
            s_FusionKccApiInstalled = null;
            s_FusionKccSourceRoot = null;
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
                ManageSymbol(symbolList, SYMBOL_FUSION_KCC, IsFusionKccApiInstalled());
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

        /// <summary>
        /// Returns true only when the installed Advanced KCC exposes the API surface consumed by
        /// the optional GC2 adapter. KCC currently compiles into Assembly-CSharp, so an asmdef or
        /// namespace-only check is not sufficient.
        /// </summary>
        public static bool IsFusionKccApiInstalled()
        {
            if (s_ForceFusionKccAbsentUntilReload) return false;
            if (s_FusionKccApiInstalled.HasValue) return s_FusionKccApiInstalled.Value;

            Type kccType = FindLoadedType("Fusion.Addons.KCC.KCC");
            if (kccType != null)
            {
                CacheFusionKccSourceRoot();
                bool compatible = HasRequiredFusionKccApi(kccType);
                // An imported source update can leave the previous Assembly-CSharp Type loaded
                // until the next successful compile. Validate source too when it is present so a
                // removed/renamed API disables the adapter before that stale Type causes errors.
                if (!string.IsNullOrEmpty(FindFusionKccSourcePath("/Core/KCC.cs")))
                {
                    compatible &= HasRequiredFusionKccSourceApi();
                }
                s_FusionKccApiInstalled = compatible;
                return s_FusionKccApiInstalled.Value;
            }

            // During first import the KCC scripts exist before Assembly-CSharp reloads. Validate
            // all source markers needed by the adapter so the define is ready for that compilation.
            s_FusionKccApiInstalled = HasRequiredFusionKccSourceApi();
            return s_FusionKccApiInstalled.Value;
        }

        public static bool IsFusionKccSymbolDefinedForCurrentBuildTarget()
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown) return false;

            NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            string symbols = PlayerSettings.GetScriptingDefineSymbols(namedBuildTarget);
            return symbols
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(symbol => string.Equals(
                    symbol.Trim(),
                    SYMBOL_FUSION_KCC,
                    StringComparison.Ordinal));
        }

        internal static void NotifyFusionKccAssetsDeleted()
        {
            // OnWillDeleteAsset runs before Unity recompiles the remaining Assembly-CSharp files.
            // Force-remove the symbol now; relying on a stale loaded KCC Type would make the
            // define-guarded adapter compile once against an API that has already been deleted.
            s_ForceFusionKccAbsentUntilReload = true;
            s_FusionKccApiInstalled = false;
            SetFusionKccSymbolForCurrentBuildTarget(false);
        }

        private static void SetFusionKccSymbolForCurrentBuildTarget(bool shouldDefine)
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown) return;

            NamedBuildTarget namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(group);
            string currentSymbols = PlayerSettings.GetScriptingDefineSymbols(namedBuildTarget);
            List<string> symbols = currentSymbols
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            ManageSymbol(symbols, SYMBOL_FUSION_KCC, shouldDefine);
            string nextSymbols = string.Join(";", symbols);
            if (string.Equals(currentSymbols, nextSymbols, StringComparison.Ordinal)) return;

            PlayerSettings.SetScriptingDefineSymbols(namedBuildTarget, nextSymbols);
            Debug.Log(
                shouldDefine
                    ? $"[GC2 Networking] Enabled {SYMBOL_FUSION_KCC} for the current build target."
                    : $"[GC2 Networking] Removed {SYMBOL_FUSION_KCC} before Advanced KCC recompilation.");
        }

        private static bool HasRequiredFusionKccApi(Type kccType)
        {
            if (kccType == null ||
                !string.Equals(
                    kccType.FullName,
                    "Fusion.Addons.KCC.KCC",
                    StringComparison.Ordinal))
            {
                return false;
            }

            Type baseType = kccType.BaseType;
            bool derivesNetworkTrsp = false;
            while (baseType != null)
            {
                if (string.Equals(baseType.FullName, "Fusion.NetworkTRSP", StringComparison.Ordinal))
                {
                    derivesNetworkTrsp = true;
                    break;
                }
                baseType = baseType.BaseType;
            }
            if (!derivesNetworkTrsp) return false;

            Type dataType = FindLoadedType("Fusion.Addons.KCC.KCCData");
            Type settingsType = FindLoadedType("Fusion.Addons.KCC.KCCSettings");
            Type shapeType = FindLoadedType("Fusion.Addons.KCC.EKCCShape");
            Type authorityType = FindLoadedType(
                "Fusion.Addons.KCC.EKCCAuthorityBehavior");
            Type interpolationType = FindLoadedType(
                "Fusion.Addons.KCC.EKCCInterpolationMode");
            Type processorType = FindLoadedType("Fusion.Addons.KCC.KCCProcessor");
            Type processorInterfaceType = FindLoadedType(
                "Fusion.Addons.KCC.IKCCProcessor");
            Type utilityType = FindLoadedType("Fusion.Addons.KCC.KCCUtility");
            Type stageType = FindLoadedType("Fusion.Addons.KCC.IKCCStage`1");
            Type prepareDataType = FindLoadedType("Fusion.Addons.KCC.PrepareData");
            Type prepareInterface = FindLoadedType("Fusion.Addons.KCC.IPrepareData");
            Type environmentType = FindLoadedType(
                "Fusion.Addons.KCC.EnvironmentProcessor");
            Type groundSnapType = FindLoadedType(
                "Fusion.Addons.KCC.GroundSnapProcessor");
            Type stepUpType = FindLoadedType("Fusion.Addons.KCC.StepUpProcessor");
            if (dataType == null || settingsType == null || shapeType == null ||
                authorityType == null || interpolationType == null ||
                processorType == null || processorInterfaceType == null ||
                utilityType == null || stageType == null || prepareDataType == null ||
                prepareInterface == null || environmentType == null ||
                groundSnapType == null || stepUpType == null)
            {
                return false;
            }

            Type vector3 = typeof(Vector3);
            Type quaternion = typeof(Quaternion);
            Type resolveCollisionType = typeof(Func<,,>).MakeGenericType(
                kccType,
                typeof(Collider),
                typeof(bool));
            Type postProcessCallbackType = typeof(Action<,>).MakeGenericType(
                kccType,
                dataType);
            Type prepareStage = stageType.MakeGenericType(prepareDataType);
            bool processorsCompatible =
                processorType.IsAssignableFrom(environmentType) &&
                processorType.IsAssignableFrom(groundSnapType) &&
                processorType.IsAssignableFrom(stepUpType) &&
                prepareStage.IsAssignableFrom(prepareInterface) &&
                prepareInterface.IsAssignableFrom(environmentType) &&
                utilityType.GetMethod(
                    "ResolveProcessor",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[]
                    {
                        typeof(UnityEngine.Object),
                        processorInterfaceType.MakeByRefType()
                    },
                    null)?.ReturnType == typeof(bool) &&
                HasPublicMethod(processorType, "GetPriority", kccType);

            return processorsCompatible &&
                   HasPublicProperty(kccType, "FixedData", dataType) &&
                   HasPublicProperty(kccType, "RenderData", dataType) &&
                   HasPublicProperty(kccType, "Data", dataType) &&
                   HasPublicProperty(kccType, "Settings", settingsType) &&
                   HasPublicProperty(kccType, "IsSpawned", typeof(bool)) &&
                   HasPublicProperty(kccType, "HasManualUpdate", typeof(bool)) &&
                   HasPublicProperty(kccType, "IsInFixedUpdate", typeof(bool)) &&
                   HasPublicProperty(
                       kccType,
                       "IsPredictingInRenderUpdate",
                       typeof(bool)) &&
                   HasPublicProperty(kccType, "PredictionError", vector3) &&
                   HasPublicProperty(kccType, "Rigidbody", typeof(Rigidbody)) &&
                   HasPublicField(kccType, "ResolveCollision", resolveCollisionType) &&
                   HasPublicMethod(kccType, "SetManualUpdate", typeof(bool)) &&
                   HasPublicMethod(kccType, "ManualFixedUpdate") &&
                   HasPublicMethod(kccType, "ManualRenderUpdate") &&
                   HasPublicMethod(
                       kccType,
                       "EnqueuePostProcess",
                       postProcessCallbackType) &&
                   HasPublicMethod(
                       kccType,
                       "SetPosition",
                       vector3,
                       typeof(bool),
                       typeof(bool),
                       typeof(bool)) &&
                   HasPublicMethod(
                       kccType,
                       "SetShape",
                       shapeType,
                       typeof(float),
                       typeof(float)) &&
                   HasPublicMethod(
                       kccType,
                       "SynchronizeTransform",
                       typeof(bool),
                       typeof(bool),
                       typeof(bool),
                       typeof(bool)) &&
                   HasPublicField(dataType, "TargetPosition", vector3) &&
                   HasPublicField(dataType, "BasePosition", vector3) &&
                   HasPublicField(dataType, "DeltaTime", typeof(float)) &&
                   HasPublicProperty(dataType, "TransformRotation", quaternion) &&
                   HasPublicField(dataType, "InputDirection", vector3) &&
                   HasPublicProperty(dataType, "LookYaw", typeof(float)) &&
                   HasPublicField(dataType, "JumpImpulse", vector3) &&
                   HasPublicField(dataType, "ExternalDelta", vector3) &&
                   HasPublicField(dataType, "KinematicVelocity", vector3) &&
                   HasPublicField(dataType, "DynamicVelocity", vector3) &&
                   HasPublicField(dataType, "RealVelocity", vector3) &&
                   HasPublicField(dataType, "IsGrounded", typeof(bool)) &&
                   HasPublicField(dataType, "GroundNormal", vector3) &&
                   HasPublicField(settingsType, "Shape", shapeType) &&
                   HasPublicField(settingsType, "IsTrigger", typeof(bool)) &&
                   HasPublicField(settingsType, "Radius", typeof(float)) &&
                   HasPublicField(settingsType, "Height", typeof(float)) &&
                   HasPublicField(settingsType, "Extent", typeof(float)) &&
                   HasPublicField(settingsType, "ColliderLayer", typeof(int)) &&
                   HasPublicField(settingsType, "CollisionLayerMask", typeof(LayerMask)) &&
                   HasPublicField(
                       settingsType,
                       "InputAuthorityBehavior",
                       authorityType) &&
                   HasPublicField(
                       settingsType,
                       "StateAuthorityBehavior",
                       authorityType) &&
                   HasPublicField(
                       settingsType,
                       "ProxyInterpolationMode",
                       interpolationType) &&
                   HasPublicField(
                       settingsType,
                       "ForcePredictedLookRotation",
                       typeof(bool)) &&
                   HasPublicField(
                       settingsType,
                       "AllowClientTeleports",
                       typeof(bool)) &&
                   HasPublicField(settingsType, "Processors", typeof(UnityEngine.Object[])) &&
                   HasPublicField(environmentType, "KinematicSpeed", typeof(float)) &&
                   HasPublicField(environmentType, "Gravity", vector3);
        }

        private static bool HasPublicProperty(
            Type type,
            string name,
            Type propertyType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            if (type == null) return false;

            // KCC inherits from Fusion's NetworkTRSP hierarchy, where a base and derived type
            // can expose public properties with the same name. Type.GetProperty(name) throws
            // AmbiguousMatchException in that case, which would prevent automatic define
            // recovery. Enumerate and match the exact signature instead.
            return type.GetProperties(flags).Any(property =>
                string.Equals(property.Name, name, StringComparison.Ordinal) &&
                property.PropertyType == propertyType &&
                property.GetIndexParameters().Length == 0);
        }

        private static bool HasPublicField(Type type, string name, Type fieldType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            if (type == null) return false;
            return type.GetFields(flags).Any(field =>
                string.Equals(field.Name, name, StringComparison.Ordinal) &&
                field.FieldType == fieldType);
        }

        private static bool HasPublicMethod(
            Type type,
            string name,
            params Type[] parameterTypes)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            if (type == null) return false;
            Type[] expected = parameterTypes ?? Type.EmptyTypes;

            // As with properties, the KCC/NetworkTRSP inheritance chain can expose multiple
            // public overload candidates that make Type.GetMethod(...) ambiguous on Mono.
            return type.GetMethods(flags).Any(method =>
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal) ||
                    method.ContainsGenericParameters)
                {
                    return false;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != expected.Length) return false;
                for (int i = 0; i < parameters.Length; ++i)
                {
                    if (parameters[i].ParameterType != expected[i]) return false;
                }
                return true;
            });
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == null || assembly.IsDynamic || assembly.ReflectionOnly) continue;
                try
                {
                    Type type = assembly.GetType(fullName, false, false);
                    if (type != null) return type;
                }
                catch (FileLoadException)
                {
                    // A partially replaced package assembly is ignored until the next reload.
                }
                catch (BadImageFormatException)
                {
                    // Native and incompatible plugin assemblies cannot contain the managed API.
                }
            }
            return null;
        }

        private static bool HasRequiredFusionKccSourceApi()
        {
            string corePath = FindFusionKccSourcePath("/Core/KCC.cs");
            if (string.IsNullOrEmpty(corePath)) return false;

            int coreSuffix = corePath.LastIndexOf("/Core/KCC.cs", StringComparison.Ordinal);
            if (coreSuffix <= 0) return false;
            string root = corePath.Substring(0, coreSuffix);
            RememberFusionKccSourceRoot(root);

            return SourceContains(
                       root + "/Core/KCC.cs",
                       "namespace Fusion.Addons.KCC",
                       "public sealed partial class KCC : NetworkTRSP",
                       "public void SynchronizeTransform(",
                       "public void SetManualUpdate(bool hasManualUpdate)",
                       "public void ManualFixedUpdate()",
                       "public void ManualRenderUpdate()") &&
                   SourceContains(
                       root + "/Core/KCC.Methods.cs",
                       "public void SetPosition(Vector3 position",
                       "public void SetShape(EKCCShape shape") &&
                   SourceContains(
                       root + "/Core/KCC.Stages.cs",
                       "public void EnqueuePostProcess(Action<KCC, KCCData> callback)") &&
                   SourceContains(
                       root + "/Core/KCC.Properties.cs",
                       "public bool IsSpawned",
                       "public bool HasManualUpdate",
                       "KCCData Data =>",
                       "public KCCData FixedData",
                       "public KCCData RenderData",
                       "public KCCSettings Settings",
                       "public Rigidbody Rigidbody",
                       "public bool IsInFixedUpdate",
                       "public bool IsPredictingInRenderUpdate",
                       "public Vector3 PredictionError") &&
                   SourceContains(
                       root + "/Core/KCC.Events.cs",
                       "public Func<KCC, Collider, bool> ResolveCollision") &&
                   SourceContains(
                       root + "/Data/KCCData.cs",
                       "public Vector3 TargetPosition",
                       "public Vector3 BasePosition",
                       "public float DeltaTime",
                       "public Quaternion TransformRotation",
                       "public Vector3 InputDirection",
                       "public float LookYaw",
                       "public Vector3 JumpImpulse",
                       "public Vector3 ExternalDelta",
                       "public Vector3 KinematicVelocity",
                       "public Vector3 DynamicVelocity",
                       "public bool IsGrounded",
                       "public Vector3 RealVelocity",
                       "public Vector3 GroundNormal") &&
                   SourceContains(
                       root + "/Data/KCCSettings.cs",
                       "public EKCCShape Shape",
                       "public bool IsTrigger",
                       "public float Radius",
                       "public float Height",
                       "public float Extent",
                       "public int ColliderLayer",
                       "public LayerMask CollisionLayerMask",
                       "public EKCCAuthorityBehavior InputAuthorityBehavior",
                       "public EKCCAuthorityBehavior StateAuthorityBehavior",
                       "public EKCCInterpolationMode ProxyInterpolationMode",
                       "public bool ForcePredictedLookRotation",
                       "public bool AllowClientTeleports",
                       "public UnityEngine.Object[] Processors") &&
                   SourceContains(
                       root + "/Processors/Core/KCCProcessor.cs",
                       "public abstract partial class KCCProcessor",
                       "public virtual float GetPriority(KCC kcc)") &&
                   SourceContains(
                       root + "/Processors/EnvironmentProcessor.cs",
                       "class EnvironmentProcessor : KCCProcessor, IPrepareData",
                       "public float KinematicSpeed",
                       "public Vector3 Gravity") &&
                   SourceContains(
                       root + "/Processors/GroundSnapProcessor.cs",
                       "class GroundSnapProcessor : KCCProcessor") &&
                   SourceContains(
                       root + "/Processors/StepUpProcessor.cs",
                       "class StepUpProcessor : KCCProcessor") &&
                   SourceContains(
                       root + "/Utilities/KCCUtility.cs",
                       "public static bool ResolveProcessor(UnityEngine.Object unityObject, " +
                       "out IKCCProcessor processor)") &&
                   SourceContains(
                       root + "/Stages/PrepareData.cs",
                       "interface IPrepareData : IKCCStage<PrepareData>") &&
                   SourceContains(
                       root + "/Stages/Core/IKCCStage.cs",
                       "interface IKCCStage<TStageObject> : IKCCStage",
                       "float GetPriority(KCC kcc)",
                       "void Execute(TStageObject stage, KCC kcc, KCCData data)");
        }

        private static string FindFusionKccSourcePath(string suffix)
        {
            string known = "Assets/Photon/FusionAddons/KCC" + suffix;
            if (AssetDatabase.LoadAssetAtPath<MonoScript>(known) != null) return known;

            foreach (string guid in AssetDatabase.FindAssets("KCC t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith(suffix, StringComparison.Ordinal)) return path;
            }
            return string.Empty;
        }

        private static void CacheFusionKccSourceRoot()
        {
            if (!string.IsNullOrEmpty(s_FusionKccSourceRoot)) return;
            string corePath = FindFusionKccSourcePath("/Core/KCC.cs");
            if (string.IsNullOrEmpty(corePath)) return;

            int suffix = corePath.LastIndexOf("/Core/KCC.cs", StringComparison.Ordinal);
            if (suffix > 0) RememberFusionKccSourceRoot(corePath.Substring(0, suffix));
        }

        private static void RememberFusionKccSourceRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            s_FusionKccSourceRoot = root.Replace('\\', '/').TrimEnd('/');
            SessionState.SetString(FUSION_KCC_ROOT_SESSION_KEY, s_FusionKccSourceRoot);
        }

        internal static bool IsKnownFusionKccAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string root = s_FusionKccSourceRoot;
            if (string.IsNullOrEmpty(root))
            {
                root = SessionState.GetString(FUSION_KCC_ROOT_SESSION_KEY, string.Empty);
            }
            if (string.IsNullOrEmpty(root)) return false;

            string normalized = assetPath.Replace('\\', '/').TrimEnd('/');
            return string.Equals(normalized, root, StringComparison.Ordinal) ||
                   normalized.StartsWith(root + "/", StringComparison.Ordinal);
        }

        private static bool SourceContains(string assetPath, params string[] markers)
        {
            string fullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", assetPath));
            if (!File.Exists(fullPath)) return false;

            try
            {
                string source = File.ReadAllText(fullPath);
                return markers.All(marker =>
                    source.IndexOf(marker, StringComparison.Ordinal) >= 0);
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

    internal sealed class GC2NetworkingBuildTargetChange : IActiveBuildTargetChanged
    {
        public int callbackOrder => 100;

        public void OnActiveBuildTargetChanged(
            BuildTarget previousTarget,
            BuildTarget newTarget)
        {
            GC2NetworkingDefineSymbols.RefreshNow();
        }
    }

    /// <summary>
    /// Unity can invoke InitializeOnLoad static constructors while the asset pipeline is still
    /// compiling Assembly-CSharp. Run one additional refresh after both compilation and import
    /// settle so a pre-existing, asmdef-less KCC install is detected on first package import too.
    /// </summary>
    internal static class GC2NetworkingDefineSymbolBootstrap
    {
        [InitializeOnLoadMethod]
        private static void ScheduleRefreshWhenEditorIsReady()
        {
            EditorApplication.delayCall += RefreshWhenEditorIsReady;
        }

        private static void RefreshWhenEditorIsReady()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += RefreshWhenEditorIsReady;
                return;
            }

            GC2NetworkingDefineSymbols.RefreshNow();
        }
    }

    internal sealed class GC2NetworkingFusionKccAssetPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            bool deletedKccApi = deletedAssets.Any(IsFusionKccApiAssetPath);
            if (deletedKccApi)
            {
                GC2NetworkingDefineSymbols.NotifyFusionKccAssetsDeleted();
            }

            bool relevant = deletedKccApi ||
                            importedAssets.Any(IsFusionKccApiAssetPath) ||
                            movedAssets.Any(IsFusionKccApiAssetPath) ||
                            movedFromAssetPaths.Any(IsFusionKccApiAssetPath);
            if (relevant) GC2NetworkingDefineSymbols.QueueUpdate();
        }

        internal static bool IsFusionKccApiAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            string normalized = assetPath.Replace('\\', '/');
            return normalized.IndexOf("/FusionAddons/KCC/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   normalized.EndsWith("/FusionAddons/KCC", StringComparison.OrdinalIgnoreCase) ||
                   GC2NetworkingDefineSymbols.IsKnownFusionKccAssetPath(normalized) ||
                   normalized.EndsWith("/Core/KCC.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Core/KCC.Methods.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Core/KCC.Interactions.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Core/KCC.Properties.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Core/KCC.Events.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Data/KCCData.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Data/KCCSettings.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Processors/Core/KCCProcessor.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Processors/EnvironmentProcessor.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Processors/GroundSnapProcessor.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Processors/StepUpProcessor.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Stages/PrepareData.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/Stages/Core/IKCCStage.cs", StringComparison.Ordinal) ||
                   normalized.EndsWith("/kcc_build_info.txt", StringComparison.Ordinal);
        }
    }

    internal sealed class GC2NetworkingFusionKccDeletionProcessor : AssetModificationProcessor
    {
        private static AssetDeleteResult OnWillDeleteAsset(
            string assetPath,
            RemoveAssetOptions options)
        {
            if (GC2NetworkingFusionKccAssetPostprocessor.IsFusionKccApiAssetPath(assetPath))
            {
                GC2NetworkingDefineSymbols.NotifyFusionKccAssetsDeleted();
            }
            return AssetDeleteResult.DidNotDelete;
        }
    }
}
#endif
