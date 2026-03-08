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
        public override string PatchVersion => "2.1.0-traversal";
        public override string DisplayName => "Traversal (Game Creator 2)";

        public override string PatchDescription =>
            "This will modify the Game Creator 2 Traversal source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "TraverseLink.Run, TraverseInteractive.Enter, and TraversalStance\n" +
            "action APIs will be validated through network hooks before local execution.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseLink.cs",
            "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseInteractive.cs",
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

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return new[]
                {
                    "NetworkTryCancelValidator",
                    "NetworkForceCancelValidator",
                    "NetworkTryJumpValidator",
                    "NetworkTryActionValidator",
                    "NetworkTryStateEnterValidator",
                    "NetworkTryStateExitValidator"
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

            if (relativePath.EndsWith("TraversalStance.cs"))
            {
                return PatchTraversalStance(relativePath, content);
            }

            return false;
        }

        private bool PatchTraverseLink(string relativePath, string content)
        {
            if (!TryReplaceRequired(
                    ref content,
                    @"    [Serializable]
    public class TraverseLink : Traverse
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------",
                    @"    [Serializable]
    public class TraverseLink : Traverse
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraverseLink, Character, bool> NetworkRunValidator;

        public static bool IsNetworkingActive => NetworkRunValidator != null;

        // [GC2_NETWORK_PATCH_END]

        // EXPOSED MEMBERS: -----------------------------------------------------------------------",
                    "[GC2 Networking] Could not patch TraverseLink class header in TraverseLink.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async Task Run(Character character)
        {
            if (character == null) return;",
                    @"        public async Task Run(Character character)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkRunValidator != null && !NetworkRunValidator.Invoke(this, character))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (character == null) return;",
                    "[GC2 Networking] Could not patch TraverseLink.Run in TraverseLink.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"namespace GameCreator.Runtime.Traversal
{",
                    PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Traversal > Unpatch to restore.

namespace GameCreator.Runtime.Traversal
{",
                    "[GC2 Networking] Could not add patch marker header in TraverseLink.cs."))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchTraverseInteractive(string relativePath, string content)
        {
            if (!TryReplaceRequired(
                    ref content,
                    @"    [Serializable]
    public class TraverseInteractive : Traverse
    {
        public enum CharacterRotationMode",
                    @"    [Serializable]
    public class TraverseInteractive : Traverse
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<TraverseInteractive, Character, InteractiveTransitionData, bool> NetworkEnterValidator;

        public static bool IsNetworkingActive => NetworkEnterValidator != null;

        // [GC2_NETWORK_PATCH_END]

        public enum CharacterRotationMode",
                    "[GC2 Networking] Could not patch TraverseInteractive class header in TraverseInteractive.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async Task Enter(Character character, InteractiveTransitionData transition)
        {
            if (character == null) return;",
                    @"        public async Task Enter(Character character, InteractiveTransitionData transition)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkEnterValidator != null && !NetworkEnterValidator.Invoke(this, character, transition))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (character == null) return;",
                    "[GC2 Networking] Could not patch TraverseInteractive.Enter in TraverseInteractive.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"namespace GameCreator.Runtime.Traversal
{",
                    PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Traversal > Unpatch to restore.

namespace GameCreator.Runtime.Traversal
{",
                    "[GC2 Networking] Could not add patch marker header in TraverseInteractive.cs."))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchTraversalStance(string relativePath, string content)
        {
            if (!TryReplaceRequired(
                    ref content,
                    @"    public class TraversalStance : TStance
    {
        public static readonly int ID = ""Traversal"".GetHashCode();",
                    @"    public class TraversalStance : TStance
    {
        public static readonly int ID = ""Traversal"".GetHashCode();

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

        // [GC2_NETWORK_PATCH_END]",
                    "[GC2 Networking] Could not patch TraversalStance class header in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void TryCancel(Args args)
        {
            if (this.Traverse == null) return;",
                    @"        public void TryCancel(Args args)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryCancelValidator != null && !NetworkTryCancelValidator.Invoke(this, args))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.Traverse == null) return;",
                    "[GC2 Networking] Could not patch TraversalStance.TryCancel in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public bool ForceCancel()
        {
            if (this.Traverse == null) return false;",
                    @"        public bool ForceCancel()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkForceCancelValidator != null && !NetworkForceCancelValidator.Invoke(this))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.Traverse == null) return false;",
                    "[GC2 Networking] Could not patch TraversalStance.ForceCancel in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void TryJump()
        {
            if (this.Traverse == null) return;",
                    @"        public void TryJump()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryJumpValidator != null && !NetworkTryJumpValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.Traverse == null) return;",
                    "[GC2 Networking] Could not patch TraversalStance.TryJump in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void TryAction(IdString actionId)
        {
            if (this.Traverse == null) return;",
                    @"        public void TryAction(IdString actionId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryActionValidator != null && !NetworkTryActionValidator.Invoke(this, actionId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.Traverse == null) return;",
                    "[GC2 Networking] Could not patch TraversalStance.TryAction in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void TryStateEnter(IdString stateId)
        {
            if (this.Traverse == null) return;",
                    @"        public void TryStateEnter(IdString stateId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryStateEnterValidator != null && !NetworkTryStateEnterValidator.Invoke(this, stateId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.Traverse == null) return;",
                    "[GC2 Networking] Could not patch TraversalStance.TryStateEnter in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void TryStateExit()
        {
            if (this.Traverse == null) return;",
                    @"        public void TryStateExit()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTryStateExitValidator != null && !NetworkTryStateExitValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.Traverse == null) return;",
                    "[GC2 Networking] Could not patch TraversalStance.TryStateExit in TraversalStance.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"namespace GameCreator.Runtime.Traversal
{",
                    PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Traversal > Unpatch to restore.

namespace GameCreator.Runtime.Traversal
{",
                    "[GC2 Networking] Could not add patch marker header in TraversalStance.cs."))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
    }
}
