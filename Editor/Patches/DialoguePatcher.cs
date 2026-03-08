using System.Collections.Generic;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Dialogue module.
    /// Adds validation hooks to Play/Stop/Continue/Choose for strict server authority mode.
    /// </summary>
    public class DialoguePatcher : GC2PatcherBase
    {
        public override string ModuleName => "Dialogue";
        public override string PatchVersion => "2.1.0-dialogue";
        public override string DisplayName => "Dialogue (Game Creator 2)";

        public override string PatchDescription =>
            "This will modify the Game Creator 2 Dialogue source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "Dialogue.Play/Stop, Story.Continue, and NodeTypeChoice.Choose\n" +
            "will be validated through network hooks before local execution.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Dialogue/Runtime/Components/Dialogue.cs",
            "Plugins/GameCreator/Packages/Dialogue/Runtime/Dialogue/Story.cs",
            "Plugins/GameCreator/Packages/Dialogue/Runtime/Dialogue/Nodes/NodeTypeChoice.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Dialogue/Editor/Version.txt", "2.5.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("Dialogue.cs"))
            {
                return new[]
                {
                    "NetworkPlayValidator",
                    "NetworkStopValidator"
                };
            }

            if (relativePath.EndsWith("Story.cs"))
            {
                return new[]
                {
                    "NetworkContinueValidator"
                };
            }

            if (relativePath.EndsWith("NodeTypeChoice.cs"))
            {
                return new[]
                {
                    "NetworkChooseValidator"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("Dialogue.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkPlayValidator.Invoke", 1 },
                    { "NetworkStopValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("Story.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkContinueValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("NodeTypeChoice.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkChooseValidator.Invoke", 1 }
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

            if (relativePath.EndsWith("Dialogue.cs"))
            {
                return PatchDialogueComponent(relativePath, content);
            }

            if (relativePath.EndsWith("Story.cs"))
            {
                return PatchStory(relativePath, content);
            }

            if (relativePath.EndsWith("NodeTypeChoice.cs"))
            {
                return PatchNodeTypeChoice(relativePath, content);
            }

            return false;
        }

        private bool PatchDialogueComponent(string relativePath, string content)
        {
            if (!TryReplaceRequired(
                    ref content,
                    @"    [DisallowMultipleComponent]
    public class Dialogue : MonoBehaviour
    {
        #if UNITY_EDITOR",
                    @"    [DisallowMultipleComponent]
    public class Dialogue : MonoBehaviour
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<Dialogue, Args, bool> NetworkPlayValidator;
        public static System.Func<Dialogue, bool> NetworkStopValidator;

        public static bool IsNetworkingActive =>
            NetworkPlayValidator != null ||
            NetworkStopValidator != null;

        // [GC2_NETWORK_PATCH_END]

        #if UNITY_EDITOR",
                    "[GC2 Networking] Could not patch Dialogue class header in Dialogue.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async Task Play(Args args)
        {
            if (this.m_Story.Content.DialogueSkin == null)",
                    @"        public async Task Play(Args args)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkPlayValidator != null && !NetworkPlayValidator.Invoke(this, args))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.m_Story.Content.DialogueSkin == null)",
                    "[GC2 Networking] Could not patch Dialogue.Play in Dialogue.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void Stop()
        {
            this.m_Story.EventStartNext -= this.OnStartNext;",
                    @"        public void Stop()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkStopValidator != null && !NetworkStopValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_Story.EventStartNext -= this.OnStartNext;",
                    "[GC2 Networking] Could not patch Dialogue.Stop in Dialogue.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"namespace GameCreator.Runtime.Dialogue
{",
                    PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Dialogue > Unpatch to restore.

namespace GameCreator.Runtime.Dialogue
{",
                    "[GC2 Networking] Could not add patch marker header in Dialogue.cs."))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchStory(string relativePath, string content)
        {
            if (!TryReplaceRequired(
                    ref content,
                    @"    [Serializable]
    public class Story
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------",
                    @"    [Serializable]
    public class Story
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<Story, bool> NetworkContinueValidator;

        public static bool IsNetworkingActive => NetworkContinueValidator != null;

        // [GC2_NETWORK_PATCH_END]

        // EXPOSED MEMBERS: -----------------------------------------------------------------------",
                    "[GC2 Networking] Could not patch Story class header in Story.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void Continue()
        {
            this.m_Content.Get(this.m_CurrentId)?.Continue();
        }",
                    @"        public void Continue()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkContinueValidator != null && !NetworkContinueValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_Content.Get(this.m_CurrentId)?.Continue();
        }",
                    "[GC2 Networking] Could not patch Story.Continue in Story.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"namespace GameCreator.Runtime.Dialogue
{",
                    PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Dialogue > Unpatch to restore.

namespace GameCreator.Runtime.Dialogue
{",
                    "[GC2 Networking] Could not add patch marker header in Story.cs."))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchNodeTypeChoice(string relativePath, string content)
        {
            if (!TryReplaceRequired(
                    ref content,
                    @"    [Serializable]
    public class NodeTypeChoice : TNodeType
    {
        public static readonly string NAME_SKIP_CHOICE = nameof(m_SkipChoice);",
                    @"    [Serializable]
    public class NodeTypeChoice : TNodeType
    {
        public static readonly string NAME_SKIP_CHOICE = nameof(m_SkipChoice);

        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<NodeTypeChoice, int, bool> NetworkChooseValidator;

        public static bool IsNetworkingActive => NetworkChooseValidator != null;

        // [GC2_NETWORK_PATCH_END]",
                    "[GC2 Networking] Could not patch NodeTypeChoice class header in NodeTypeChoice.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void Choose(int nodeId)
        {
            if (this.m_ChosenId != Content.NODE_INVALID) return;
            this.m_ChosenId = nodeId;
        }",
                    @"        public void Choose(int nodeId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkChooseValidator != null && !NetworkChooseValidator.Invoke(this, nodeId))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            if (this.m_ChosenId != Content.NODE_INVALID) return;
            this.m_ChosenId = nodeId;
        }",
                    "[GC2 Networking] Could not patch NodeTypeChoice.Choose in NodeTypeChoice.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"namespace GameCreator.Runtime.Dialogue
{",
                    PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Dialogue > Unpatch to restore.

namespace GameCreator.Runtime.Dialogue
{",
                    "[GC2 Networking] Could not add patch marker header in NodeTypeChoice.cs."))
            {
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
    }
}
