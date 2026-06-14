using System.Collections.Generic;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Traversal module.
    /// Adds server-authoritative validation hooks to traversal entry points and stance actions.
    /// </summary>
    public class TraversalPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Traversal";
        public override string PatchVersion => "2.2.0-traversal";
        public override string DisplayName => "Traversal (Game Creator 2)";

        public override string PatchDescription =>
            "This will modify the Game Creator 2 Traversal source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "TraverseLink.Run, TraverseInteractive.Enter, MotionInteractive edge\n" +
            "connections, and TraversalStance action APIs will be validated\n" +
            "through network hooks before local execution.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseLink.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseInteractive.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/ScriptableObjects/MotionInteractive.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/Stance/TraversalStance.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Traversal/Editor/Version.txt", "2.0.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("TraverseLink.cs"))
            {
                return new[] { "NetworkRunValidator" };
            }

            if (relativePath.EndsWith("TraverseInteractive.cs"))
            {
                return new[] { "NetworkEnterValidator" };
            }

            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return new[]
                {
                    "NetworkEdgeConnectionResolver",
                    "NetworkConnectionSkipTransitionResolver"
                };
            }

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return new[]
                {
                    "NetworkTryCancelValidator",
                    "NetworkForceCancelValidator",
                    "NetworkTryJumpValidator",
                    "NetworkTryActionValidator",
                    "NetworkTryStateEnterValidator",
                    "NetworkTryStateExitValidator",
                    "token != this.m_CurrentToken"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("TraverseLink.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkRunValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("TraverseInteractive.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkEnterValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkEdgeConnectionResolver?.Invoke", 2 },
                    { "NetworkConnectionSkipTransitionResolver.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkTryCancelValidator.Invoke", 1 },
                    { "NetworkForceCancelValidator.Invoke", 1 },
                    { "NetworkTryJumpValidator.Invoke", 1 },
                    { "NetworkTryActionValidator.Invoke", 1 },
                    { "NetworkTryStateEnterValidator.Invoke", 1 },
                    { "NetworkTryStateExitValidator.Invoke", 1 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }

        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);

            ExistingPatchState existingPatchState = PrepareContentForPatch(relativePath, ref content);
            if (existingPatchState == ExistingPatchState.SkipAlreadyPatched) return true;
            if (existingPatchState == ExistingPatchState.Failed) return false;

            if (relativePath.EndsWith("TraverseLink.cs"))
            {
                return PatchTraverseLink(relativePath, content);
            }

            if (relativePath.EndsWith("TraverseInteractive.cs"))
            {
                return PatchTraverseInteractive(relativePath, content);
            }

            if (relativePath.EndsWith("MotionInteractive.cs"))
            {
                return PatchMotionInteractive(relativePath, content);
            }

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return PatchTraversalStance(relativePath, content);
            }

            return false;
        }

        private bool PatchTraverseLink(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "TraverseLink",
                    "NetworkRunValidator",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraverseLink, Character, bool> NetworkRunValidator;

        public static bool IsNetworkingActive => NetworkRunValidator != null;

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("TraverseLink class header", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "Run",
                    "NetworkRunValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkRunValidator != null && !NetworkRunValidator.Invoke(this, character))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*character\s*==\s*null\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraverseLink.Run", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchTraverseInteractive(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "TraverseInteractive",
                    "NetworkEnterValidator",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraverseInteractive, Character, InteractiveTransitionData, bool> NetworkEnterValidator;

        public static bool IsNetworkingActive => NetworkEnterValidator != null;

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("TraverseInteractive class header", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "Enter",
                    "NetworkEnterValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkEnterValidator != null && !NetworkEnterValidator.Invoke(this, character, transition))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*character\s*==\s*null\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraverseInteractive.Enter", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchMotionInteractive(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "MotionInteractive",
                    "NetworkEdgeConnectionResolver",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<MotionInteractive, TraverseInteractive, Character, Vector3, Vector3, bool, Traverse> NetworkEdgeConnectionResolver;
        public static System.Func<Traverse, Traverse, Character, bool> NetworkConnectionSkipTransitionResolver;

        public static bool IsNetworkingActive =>
            NetworkEdgeConnectionResolver != null ||
            NetworkConnectionSkipTransitionResolver != null;

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("MotionInteractive class header", failureReason);
            }

            if (!TryReplaceRegexInMethod(
                    ref content,
                    "Enter",
                    "NetworkConnectionSkipTransitionResolver.Invoke",
                    @"[ \t]*_\s*=\s*Traverse\.ChangeTo\s*\(\s*traverse\s*,\s*nextTraverse\s*,\s*character\s*,\s*true\s*\)\s*;",
                    @"                // [GC2_NETWORK_PATCH] Let networking decide whether interactive-to-interactive connections should run transition clips
                bool skipTransition = NetworkConnectionSkipTransitionResolver == null ||
                    NetworkConnectionSkipTransitionResolver.Invoke(traverse, nextTraverse, character);
                _ = Traverse.ChangeTo(traverse, nextTraverse, character, skipTransition);
                // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("MotionInteractive.Enter ChangeTo transition routing", failureReason);
            }

            if (!TryInsertAfterRegexInMethod(
                    ref content,
                    "OnUpdate",
                    null,
                    @"(?m)^[ \t]*if\s*\(\s*traverseInteractive\.ContinueA\s*!=\s*null\s*\)\s*\{.*?^[ \t]*return\s+traverseInteractive\.ContinueA\s*;\s*^[ \t]*\}",
                    @"

                    // [GC2_NETWORK_PATCH] Resolve configured edge connections through the network layer instead of auto-traversing locally
                    Traverse networkConnection = NetworkEdgeConnectionResolver?.Invoke(
                        this,
                        traverseInteractive,
                        character,
                        currentLocalPosition,
                        swizzleLocalInput,
                        false);
                    if (networkConnection != null)
                    {
                        return networkConnection;
                    }
                    // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("MotionInteractive edge A connection resolver", failureReason);
            }

            if (!TryInsertAfterRegexInMethod(
                    ref content,
                    "OnUpdate",
                    null,
                    @"(?m)^[ \t]*if\s*\(\s*traverseInteractive\.ContinueB\s*!=\s*null\s*\)\s*\{.*?^[ \t]*return\s+traverseInteractive\.ContinueB\s*;\s*^[ \t]*\}",
                    @"

                    // [GC2_NETWORK_PATCH] Resolve configured edge connections through the network layer instead of auto-traversing locally
                    Traverse networkConnection = NetworkEdgeConnectionResolver?.Invoke(
                        this,
                        traverseInteractive,
                        character,
                        currentLocalPosition,
                        swizzleLocalInput,
                        true);
                    if (networkConnection != null)
                    {
                        return networkConnection;
                    }
                    // [GC2_NETWORK_PATCH_END]",
                    out failureReason))
            {
                return LogPatchFailure("MotionInteractive edge B connection resolver", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchTraversalStance(string relativePath, string content)
        {
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "TraversalStance",
                    "NetworkTryCancelValidator",
                    @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraversalStance, Args, bool> NetworkTryCancelValidator;
        public static System.Func<TraversalStance, bool> NetworkForceCancelValidator;
        public static System.Func<TraversalStance, bool> NetworkTryJumpValidator;
        public static System.Func<TraversalStance, IdString, bool> NetworkTryActionValidator;
        public static System.Func<TraversalStance, IdString, bool> NetworkTryStateEnterValidator;
        public static System.Func<TraversalStance, bool> NetworkTryStateExitValidator;

        public static bool IsNetworkingActive =>
            NetworkTryCancelValidator != null ||
            NetworkForceCancelValidator != null ||
            NetworkTryJumpValidator != null ||
            NetworkTryActionValidator != null ||
            NetworkTryStateEnterValidator != null ||
            NetworkTryStateExitValidator != null;

        // [GC2_NETWORK_PATCH_END]
",
                    out string failureReason))
            {
                return LogPatchFailure("TraversalStance class header", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryCancel",
                    "NetworkTryCancelValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryCancelValidator != null && !NetworkTryCancelValidator.Invoke(this, args))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryCancel", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "ForceCancel",
                    "NetworkForceCancelValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkForceCancelValidator != null && !NetworkForceCancelValidator.Invoke(this))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s+false\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s+false\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\.IsCancelled\s*\)\s*return\s+false\s*;"))
            {
                return LogPatchFailure("TraversalStance.ForceCancel", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryJump",
                    "NetworkTryJumpValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryJumpValidator != null && !NetworkTryJumpValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.InInteractiveTransition\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryJump", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryAction",
                    "NetworkTryActionValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryActionValidator != null && !NetworkTryActionValidator.Invoke(this, actionId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.InInteractiveTransition\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryAction", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryStateEnter",
                    "NetworkTryStateEnterValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryStateEnterValidator != null && !NetworkTryStateEnterValidator.Invoke(this, stateId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.InInteractiveTransition\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryStateEnter", failureReason);
            }

            if (!TryInsertAfterMethodAnchors(
                    ref content,
                    "TryStateExit",
                    "NetworkTryStateExitValidator.Invoke",
                    @"

            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryStateExitValidator != null && !NetworkTryStateExitValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]",
                    out failureReason,
                    @"if\s*\(\s*this\.Traverse\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentToken\s*==\s*null\s*\)\s*return\s*;",
                    @"if\s*\(\s*this\.m_CurrentStateId\s*==\s*IdString\.EMPTY\s*\)\s*return\s*;"))
            {
                return LogPatchFailure("TraversalStance.TryStateExit", failureReason);
            }

            if (!TryInsertAtMethodStart(
                    ref content,
                    "OnTraverseExit",
                    "token != this.m_CurrentToken",
                    @"
            // [GC2_NETWORK_PATCH] Ignore stale exits from the previous interactive traverse during ChangeTo transitions
            if (traverse != this.Traverse || token != this.m_CurrentToken)
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]
",
                    out failureReason))
            {
                return LogPatchFailure("TraversalStance.OnTraverseExit stale-exit guard", failureReason);
            }

            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Traversal"))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private static bool LogPatchFailure(string context, string failureReason)
        {
            Debug.LogError($"[GC2 Networking] Could not patch {context}: {failureReason}");
            return false;
        }

    }
}
