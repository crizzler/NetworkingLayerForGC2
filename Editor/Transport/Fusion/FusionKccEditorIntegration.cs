using System;
using System.Collections.Generic;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>
    /// Immutable setup options passed from the asmdef-based Fusion wizard to the optional
    /// Assembly-CSharp-Editor KCC setup extension.
    /// </summary>
    public readonly struct FusionKccEditorSetupOptions
    {
        public FusionKccEditorSetupOptions(
            FusionKccSharedAuthorityMode sharedAuthorityMode,
            bool requireAppliedSetup = false)
        {
            SharedAuthorityMode = sharedAuthorityMode;
            RequireAppliedSetup = requireAppliedSetup;
        }

        public FusionKccSharedAuthorityMode SharedAuthorityMode { get; }

        /// <summary>
        /// False during wizard preflight, where absent wizard-owned components are convertible
        /// warnings. True during postflight, where the extension must report an incomplete or
        /// invalid applied KCC setup as an error.
        /// </summary>
        public bool RequireAppliedSetup { get; }
    }

    public enum FusionKccEditorIssueSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Transport-neutral validation result returned by the optional KCC editor extension.
    /// Keeping this type in the core Fusion editor assembly avoids a compile-time KCC reference.
    /// </summary>
    public readonly struct FusionKccEditorValidationIssue
    {
        public FusionKccEditorValidationIssue(
            FusionKccEditorIssueSeverity severity,
            string message,
            UnityEngine.Object context = null)
        {
            Severity = severity;
            Message = message ?? string.Empty;
            Context = context;
        }

        public FusionKccEditorIssueSeverity Severity { get; }
        public string Message { get; }
        public UnityEngine.Object Context { get; }
    }

    /// <summary>
    /// Implemented by the optional, define-guarded KCC editor code in Assembly-CSharp-Editor.
    /// The core wizard discovers exactly one implementation through Unity's TypeCache.
    /// </summary>
    public interface IFusionKccEditorSetupExtension
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }

        /// <returns>
        /// True when setup completed successfully, including an idempotent no-change pass;
        /// false only when the prefab transaction must be rolled back.
        /// </returns>
        bool ConfigurePlayerPrefab(
            GameObject prefabRoot,
            FusionKccEditorSetupOptions options,
            IList<string> changes,
            out string error);

        /// <returns>
        /// True when cleanup completed successfully, including when no owned setup existed;
        /// false only when the prefab transaction must be rolled back.
        /// </returns>
        bool RemoveFromPlayerPrefab(
            GameObject prefabRoot,
            IList<string> changes,
            out string error);

        void ValidatePlayerPrefab(
            GameObject prefabRoot,
            FusionKccEditorSetupOptions options,
            IList<FusionKccEditorValidationIssue> issues);
    }

    /// <summary>
    /// Optional KCC capability boundary used by the Fusion wizard and validation. This class must
    /// remain free of direct Fusion.Addons.KCC references because Advanced KCC has no runtime asmdef.
    /// </summary>
    internal static class FusionKccEditorIntegration
    {
        private const string ExpectedExtensionAssembly = "Assembly-CSharp-Editor";

        public static bool IsApiInstalled =>
            GC2NetworkingDefineSymbols.IsFusionKccApiInstalled();

        public static bool TryGetAvailableExtension(
            out IFusionKccEditorSetupExtension extension,
            out string reason)
        {
            extension = null;
            reason = string.Empty;

            if (!IsApiInstalled)
            {
                reason =
                    "Photon Fusion Advanced KCC is not installed. Import the Advanced KCC addon " +
                    "and let Unity finish compiling to enable this backend.";
                return false;
            }

            if (!GC2NetworkingDefineSymbols.IsFusionKccSymbolDefinedForCurrentBuildTarget())
            {
                reason =
                    $"Advanced KCC was detected, but the " +
                    $"{GC2NetworkingDefineSymbols.SYMBOL_FUSION_KCC} define is still being " +
                    "synchronized for the current build target. Let Unity finish compiling.";
                return false;
            }

            Type[] candidates = TypeCache
                .GetTypesDerivedFrom<IFusionKccEditorSetupExtension>()
                .Where(type =>
                    type != null &&
                    !type.IsAbstract &&
                    !type.IsInterface &&
                    string.Equals(
                        type.Assembly.GetName().Name,
                        ExpectedExtensionAssembly,
                        StringComparison.Ordinal))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();

            if (candidates.Length == 0)
            {
                reason = EditorApplication.isCompiling
                    ? "Advanced KCC support is compiling. Wait for the script reload and reopen " +
                      "the Fusion setup wizard."
                    : "Advanced KCC is present, but its GC2 setup extension was not found in " +
                      "Assembly-CSharp-Editor. Reimport the Networking Layer Fusion KCC optional " +
                      "integration and check the first compiler error.";
                return false;
            }

            if (candidates.Length > 1)
            {
                reason =
                    "Multiple GC2 Fusion KCC setup extensions were found in " +
                    $"{ExpectedExtensionAssembly}: " +
                    string.Join(", ", candidates.Select(type => type.FullName)) +
                    ". Keep exactly one optional integration implementation.";
                return false;
            }

            try
            {
                extension = Activator.CreateInstance(candidates[0]) as
                    IFusionKccEditorSetupExtension;
            }
            catch (Exception exception)
            {
                reason =
                    $"Could not create the Advanced KCC setup extension " +
                    $"'{candidates[0].FullName}': {exception.Message}";
                return false;
            }

            if (extension == null)
            {
                reason =
                    $"The Advanced KCC setup extension '{candidates[0].FullName}' could not be " +
                    "created. It must provide a public parameterless constructor.";
                return false;
            }

            bool isAvailable;
            string unavailableReason;
            try
            {
                isAvailable = extension.IsAvailable;
                unavailableReason = extension.UnavailableReason;
            }
            catch (Exception exception)
            {
                reason =
                    $"The Advanced KCC setup extension '{candidates[0].FullName}' failed its " +
                    $"availability check: {exception.Message}";
                extension = null;
                return false;
            }

            if (!isAvailable)
            {
                reason = string.IsNullOrWhiteSpace(unavailableReason)
                    ? "The Advanced KCC setup extension reported that its required API is unavailable."
                    : unavailableReason;
                extension = null;
                return false;
            }

            return true;
        }
    }
}
