using System.Collections.Generic;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Quests module.
    /// Adds server-authoritative validation hooks to Journal quest/task mutation APIs.
    /// </summary>
    public class QuestsPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Quests";
        public override string PatchVersion => "2.1.0-quests";
        public override string DisplayName => "Quests (Game Creator 2)";

        public override string PatchDescription =>
            "This will modify the Game Creator 2 Quests source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "Journal quest/task mutation methods will be validated through\n" +
            "network hooks before local execution.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Quests/Runtime/Components/Journal.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Quests/Editor/Version.txt", "2.3.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("Journal.cs"))
            {
                return new[]
                {
                    "NetworkActivateQuestValidator",
                    "NetworkDeactivateQuestValidator",
                    "NetworkActivateTaskValidator",
                    "NetworkDeactivateTaskValidator",
                    "NetworkCompleteTaskValidator",
                    "NetworkAbandonTaskValidator",
                    "NetworkFailTaskValidator",
                    "NetworkSetTaskValueValidator",
                    "NetworkTrackQuestValidator",
                    "NetworkUntrackQuestValidator",
                    "NetworkUntrackAllValidator"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("Journal.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkActivateQuestValidator.Invoke", 1 },
                    { "NetworkDeactivateQuestValidator.Invoke", 1 },
                    { "NetworkActivateTaskValidator.Invoke", 1 },
                    { "NetworkDeactivateTaskValidator.Invoke", 1 },
                    { "NetworkCompleteTaskValidator.Invoke", 1 },
                    { "NetworkAbandonTaskValidator.Invoke", 1 },
                    { "NetworkFailTaskValidator.Invoke", 1 },
                    { "NetworkSetTaskValueValidator.Invoke", 1 },
                    { "NetworkTrackQuestValidator.Invoke", 1 },
                    { "NetworkUntrackQuestValidator.Invoke", 1 },
                    { "NetworkUntrackAllValidator.Invoke", 1 }
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

            if (!relativePath.EndsWith("Journal.cs")) return false;
            return PatchJournal(relativePath, content);
        }

        private bool PatchJournal(string relativePath, string content)
        {
            string classStartOriginal = @"    [Serializable]
    public class Journal : MonoBehaviour
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------";

            string classStartPatched = @"    [Serializable]
    public class Journal : MonoBehaviour
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        public static System.Func<Journal, Quest, bool> NetworkActivateQuestValidator;
        public static System.Func<Journal, Quest, bool> NetworkDeactivateQuestValidator;

        public static System.Func<Journal, Quest, int, bool> NetworkActivateTaskValidator;
        public static System.Func<Journal, Quest, int, bool> NetworkDeactivateTaskValidator;
        public static System.Func<Journal, Quest, int, bool> NetworkCompleteTaskValidator;
        public static System.Func<Journal, Quest, int, bool> NetworkAbandonTaskValidator;
        public static System.Func<Journal, Quest, int, bool> NetworkFailTaskValidator;
        public static System.Func<Journal, Quest, int, double, bool> NetworkSetTaskValueValidator;

        public static System.Func<Journal, Quest, bool> NetworkTrackQuestValidator;
        public static System.Func<Journal, Quest, bool> NetworkUntrackQuestValidator;
        public static System.Func<Journal, bool> NetworkUntrackAllValidator;

        public static bool IsNetworkingActive => NetworkActivateQuestValidator != null;

        // [GC2_NETWORK_PATCH_END]

        // EXPOSED MEMBERS: -----------------------------------------------------------------------";

            if (!TryReplaceRequired(
                    ref content,
                    classStartOriginal,
                    classStartPatched,
                    "[GC2 Networking] Could not find expected Journal class header in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> ActivateQuest(Quest quest)
        {
            if (!await this.m_Quests.Activate(quest)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> ActivateQuest(Quest quest)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkActivateQuestValidator != null && !NetworkActivateQuestValidator.Invoke(this, quest))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Quests.Activate(quest)) return false;",
                    "[GC2 Networking] Could not patch ActivateQuest in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> DeactivateQuest(Quest quest)
        {
            if (!await this.m_Quests.Deactivate(quest)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> DeactivateQuest(Quest quest)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkDeactivateQuestValidator != null && !NetworkDeactivateQuestValidator.Invoke(this, quest))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Quests.Deactivate(quest)) return false;",
                    "[GC2 Networking] Could not patch DeactivateQuest in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> ActivateTask(Quest quest, int taskId)
        {
            if (!await this.m_Tasks.Activate(quest, taskId)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> ActivateTask(Quest quest, int taskId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkActivateTaskValidator != null && !NetworkActivateTaskValidator.Invoke(this, quest, taskId))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Tasks.Activate(quest, taskId)) return false;",
                    "[GC2 Networking] Could not patch ActivateTask in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> DeactivateTask(Quest quest, int taskId)
        {
            if (!await this.m_Tasks.Deactivate(quest, taskId)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> DeactivateTask(Quest quest, int taskId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkDeactivateTaskValidator != null && !NetworkDeactivateTaskValidator.Invoke(this, quest, taskId))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Tasks.Deactivate(quest, taskId)) return false;",
                    "[GC2 Networking] Could not patch DeactivateTask in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> CompleteTask(Quest quest, int taskId)
        {
            if (!await this.m_Tasks.Complete(quest, taskId)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> CompleteTask(Quest quest, int taskId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkCompleteTaskValidator != null && !NetworkCompleteTaskValidator.Invoke(this, quest, taskId))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Tasks.Complete(quest, taskId)) return false;",
                    "[GC2 Networking] Could not patch CompleteTask in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> AbandonTask(Quest quest, int taskId)
        {
            if (!await this.m_Tasks.Abandon(quest, taskId)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> AbandonTask(Quest quest, int taskId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkAbandonTaskValidator != null && !NetworkAbandonTaskValidator.Invoke(this, quest, taskId))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Tasks.Abandon(quest, taskId)) return false;",
                    "[GC2 Networking] Could not patch AbandonTask in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> FailTask(Quest quest, int taskId)
        {
            if (!await this.m_Tasks.Fail(quest, taskId)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> FailTask(Quest quest, int taskId)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkFailTaskValidator != null && !NetworkFailTaskValidator.Invoke(this, quest, taskId))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!await this.m_Tasks.Fail(quest, taskId)) return false;",
                    "[GC2 Networking] Could not patch FailTask in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public async System.Threading.Tasks.Task<bool> SetTaskValue(Quest quest, int taskId, double value)
        {
            if (!this.m_Tasks.SetValue(quest, taskId, value)) return false;",
                    @"        public async System.Threading.Tasks.Task<bool> SetTaskValue(Quest quest, int taskId, double value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkSetTaskValueValidator != null && !NetworkSetTaskValueValidator.Invoke(this, quest, taskId, value))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!this.m_Tasks.SetValue(quest, taskId, value)) return false;",
                    "[GC2 Networking] Could not patch SetTaskValue in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public bool TrackQuest(Quest quest)
        {
            if (!this.m_Quests.IsActive(quest)) return false;",
                    @"        public bool TrackQuest(Quest quest)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkTrackQuestValidator != null && !NetworkTrackQuestValidator.Invoke(this, quest))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!this.m_Quests.IsActive(quest)) return false;",
                    "[GC2 Networking] Could not patch TrackQuest in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public bool UntrackQuest(Quest quest)
        {
            if (!this.m_Quests.Untrack(quest)) return false;",
                    @"        public bool UntrackQuest(Quest quest)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkUntrackQuestValidator != null && !NetworkUntrackQuestValidator.Invoke(this, quest))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            if (!this.m_Quests.Untrack(quest)) return false;",
                    "[GC2 Networking] Could not patch UntrackQuest in Journal.cs."))
            {
                return false;
            }

            if (!TryReplaceRequired(
                    ref content,
                    @"        public void UntrackQuests()
        {
            QuestsList quests = Settings.From<QuestsRepository>().Quests;",
                    @"        public void UntrackQuests()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkUntrackAllValidator != null && !NetworkUntrackAllValidator.Invoke(this))
            {
                return;
            }
            // [GC2_NETWORK_PATCH_END]

            QuestsList quests = Settings.From<QuestsRepository>().Quests;",
                    "[GC2 Networking] Could not patch UntrackQuests in Journal.cs."))
            {
                return false;
            }

            // Add module patch marker comment close to namespace opening.
            string namespaceOriginal = @"namespace GameCreator.Runtime.Quests
{";
            string namespacePatched = PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Quests > Unpatch to restore.

namespace GameCreator.Runtime.Quests
{";

            if (!TryReplaceWithFlexibleWhitespace(ref content, namespaceOriginal, namespacePatched))
            {
                Debug.LogError("[GC2 Networking] Could not add patch marker header in Journal.cs.");
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
    }
}
