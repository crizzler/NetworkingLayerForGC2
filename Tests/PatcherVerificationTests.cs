#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    public class PatcherVerificationTests
    {
        private sealed class ShooterPatcherProxy : ShooterPatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class MeleePatcherProxy : MeleePatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class CorePatcherProxy : CorePatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class InventoryPatcherProxy : InventoryPatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class StatsPatcherProxy : StatsPatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class AbilitiesPatcherProxy : AbilitiesPatcherImpl
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class QuestsPatcherProxy : QuestsPatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class DialoguePatcherProxy : DialoguePatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class TraversalPatcherProxy : TraversalPatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private delegate bool VerifyDelegate(string relativePath, string content, out string reason);

        private static int VerifyPatchedProjectFiles(
            string marker,
            string[] relativePaths,
            VerifyDelegate verify)
        {
            int validatedCount = 0;

            foreach (string relativePath in relativePaths)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath)) continue;

                string content = File.ReadAllText(fullPath);
                if (!content.Contains(marker) && !content.Contains("// [GC2_NETWORK_PATCH_")) continue;

                bool valid = verify(relativePath, content, out string reason);
                Assert.IsTrue(valid, $"{relativePath}: {reason}");
                validatedCount++;
            }

            return validatedCount;
        }

        [Test]
        public void ShooterPatcher_VerifyWeaponData_FailsWithoutShotNotifyInvocation()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class WeaponData {\n" +
                "private void ShootDirect() { }\n" +
                "private void Shoot()\n" +
                "{\n" +
                "    if (NetworkShootValidator != null && !NetworkShootValidator.Invoke(this)) return;\n" +
                "    // missing NetworkShotFired invocation on purpose\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Data/WeaponData.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkShotFired?.Invoke", reason);
        }

        [Test]
        public void ShooterPatcher_VerifyWeaponData_PassesWithRequiredStructure()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class WeaponData {\n" +
                "private void ShootDirect() { }\n" +
                "private void Shoot()\n" +
                "{\n" +
                "    if (NetworkShootValidator != null && !NetworkShootValidator.Invoke(this)) return;\n" +
                "    if (success)\n" +
                "    {\n" +
                "        NetworkShotFired?.Invoke(this, origin, direction, chargeRation);\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Data/WeaponData.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void MeleePatcher_VerifySkill_FailsWithoutOnHitInvocation()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Skill {\n" +
                "public static System.Func<Skill, Args, UnityEngine.Vector3, UnityEngine.Vector3, bool> NetworkOnHitValidator;\n" +
                "internal void OnHit(Args args, UnityEngine.Vector3 point, UnityEngine.Vector3 direction)\n" +
                "{\n" +
                "    // missing invocation on purpose\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/ScriptableObjects/Skill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkOnHitValidator.Invoke", reason);
        }

        [Test]
        public void CorePatcher_VerifyInvincibility_FailsWithoutInvoke()
        {
            var patcher = new CorePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Invincibility {\n" +
                "public static System.Func<Invincibility, float, bool> NetworkSetValidator;\n" +
                "public void Set(float duration) { }\n" +
                "public void SetDirect(float duration) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Core/Runtime/Characters/Features/Combat/Invincibility/Invincibility.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkSetValidator.Invoke", reason);
        }

        [Test]
        public void CorePatcher_VerifyInvincibility_PassesWithRequiredStructure()
        {
            var patcher = new CorePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Invincibility {\n" +
                "public static System.Func<Invincibility, float, bool> NetworkSetValidator;\n" +
                "public void Set(float duration)\n" +
                "{\n" +
                "    if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, duration)) return;\n" +
                "}\n" +
                "public void SetDirect(float duration) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Core/Runtime/Characters/Features/Combat/Invincibility/Invincibility.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void InventoryPatcher_VerifyBagWealth_FailsWithoutAddInvoke()
        {
            var patcher = new InventoryPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class BagWealth {\n" +
                "public static System.Func<BagWealth, IdString, int, bool> NetworkAddValidator;\n" +
                "public static System.Func<BagWealth, IdString, int, bool> NetworkSetValidator;\n" +
                "public void Add(IdString id, int amount)\n" +
                "{\n" +
                "    if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, id, amount)) return;\n" +
                "}\n" +
                "public void SetDirect(IdString id, int amount) { }\n" +
                "public void AddDirect(IdString id, int amount) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Wealth/BagWealth.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkAddValidator.Invoke", reason);
        }

        [Test]
        public void InventoryPatcher_VerifyBagWealth_PassesWithRequiredStructure()
        {
            var patcher = new InventoryPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class BagWealth {\n" +
                "public static System.Func<BagWealth, IdString, int, bool> NetworkAddValidator;\n" +
                "public static System.Func<BagWealth, IdString, int, bool> NetworkSetValidator;\n" +
                "public void Add(IdString id, int amount)\n" +
                "{\n" +
                "    if (NetworkAddValidator != null && !NetworkAddValidator.Invoke(this, id, amount)) return;\n" +
                "}\n" +
                "public void Set(IdString id, int amount)\n" +
                "{\n" +
                "    if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, id, amount)) return;\n" +
                "}\n" +
                "public void SetDirect(IdString id, int amount) { }\n" +
                "public void AddDirect(IdString id, int amount) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Wealth/BagWealth.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void StatsPatcher_VerifyRuntimeAttributeData_FailsWithoutInvoke()
        {
            var patcher = new StatsPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class RuntimeAttributeData {\n" +
                "public static System.Func<RuntimeAttributeData, double, bool> NetworkValueValidator;\n" +
                "public void SetValue(double value) { }\n" +
                "public void SetValueDirect(double value) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Stats/Runtime/Classes/Traits/Attributes/RuntimeAttributeData.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkValueValidator.Invoke", reason);
        }

        [Test]
        public void StatsPatcher_VerifyRuntimeAttributeData_PassesWithRequiredStructure()
        {
            var patcher = new StatsPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class RuntimeAttributeData {\n" +
                "public static System.Func<RuntimeAttributeData, double, bool> NetworkValueValidator;\n" +
                "public void SetValue(double value)\n" +
                "{\n" +
                "    if (NetworkValueValidator != null && !NetworkValueValidator.Invoke(this, value)) return;\n" +
                "}\n" +
                "public void SetValueDirect(double value) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Stats/Runtime/Classes/Traits/Attributes/RuntimeAttributeData.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void AbilitiesPatcher_VerifyCaster_FailsWithoutUnlearnInvoke()
        {
            var patcher = new AbilitiesPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Caster {\n" +
                "public static System.Func<Caster, Ability, ExtendedArgs, bool> NetworkCastValidator;\n" +
                "public static System.Func<Caster, Ability, int, bool> NetworkLearnValidator;\n" +
                "public static System.Func<Caster, Ability, bool> NetworkUnLearnValidator;\n" +
                "public static System.Action<Caster, Ability, bool> NetworkCastCompleted;\n" +
                "public void Cast(Ability ability, ExtendedArgs args)\n" +
                "{\n" +
                "    if (NetworkCastValidator != null && !NetworkCastValidator.Invoke(this, ability, args)) return;\n" +
                "    NetworkCastCompleted?.Invoke(this, ability, true);\n" +
                "}\n" +
                "public void Learn(Ability ability, int slot)\n" +
                "{\n" +
                "    if (NetworkLearnValidator != null && !NetworkLearnValidator.Invoke(this, ability, slot)) return;\n" +
                "}\n" +
                "public void UnLearn(Ability ability) { }\n" +
                "public void LearnDirect(Ability ability, int slot) { }\n" +
                "public void UnLearnDirect(Ability ability) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/DaimahouGames/Packages/Abilities/Runtime/Pawns/Features/Caster.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkUnLearnValidator.Invoke", reason);
        }

        [Test]
        public void AbilitiesPatcher_VerifyCaster_PassesWithRequiredStructure()
        {
            var patcher = new AbilitiesPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Caster {\n" +
                "public static System.Func<Caster, Ability, ExtendedArgs, bool> NetworkCastValidator;\n" +
                "public static System.Func<Caster, Ability, int, bool> NetworkLearnValidator;\n" +
                "public static System.Func<Caster, Ability, bool> NetworkUnLearnValidator;\n" +
                "public static System.Action<Caster, Ability, bool> NetworkCastCompleted;\n" +
                "public void Cast(Ability ability, ExtendedArgs args)\n" +
                "{\n" +
                "    if (NetworkCastValidator != null && !NetworkCastValidator.Invoke(this, ability, args)) return;\n" +
                "    NetworkCastCompleted?.Invoke(this, ability, true);\n" +
                "}\n" +
                "public void Learn(Ability ability, int slot)\n" +
                "{\n" +
                "    if (NetworkLearnValidator != null && !NetworkLearnValidator.Invoke(this, ability, slot)) return;\n" +
                "}\n" +
                "public void UnLearn(Ability ability)\n" +
                "{\n" +
                "    if (NetworkUnLearnValidator != null && !NetworkUnLearnValidator.Invoke(this, ability)) return;\n" +
                "}\n" +
                "public void LearnDirect(Ability ability, int slot) { }\n" +
                "public void UnLearnDirect(Ability ability) { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/DaimahouGames/Packages/Abilities/Runtime/Pawns/Features/Caster.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void QuestsPatcher_VerifyJournal_FailsWithoutTrackInvocation()
        {
            var patcher = new QuestsPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Journal {\n" +
                "public static System.Func<Journal, Quest, bool> NetworkActivateQuestValidator;\n" +
                "public static System.Func<Journal, Quest, bool> NetworkDeactivateQuestValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkActivateTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkDeactivateTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkCompleteTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkAbandonTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkFailTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, double, bool> NetworkSetTaskValueValidator;\n" +
                "public static System.Func<Journal, Quest, bool> NetworkTrackQuestValidator;\n" +
                "public static System.Func<Journal, Quest, bool> NetworkUntrackQuestValidator;\n" +
                "public static System.Func<Journal, bool> NetworkUntrackAllValidator;\n" +
                "void Hooks() {\n" +
                "NetworkActivateQuestValidator.Invoke(this, null);\n" +
                "NetworkDeactivateQuestValidator.Invoke(this, null);\n" +
                "NetworkActivateTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkDeactivateTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkCompleteTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkAbandonTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkFailTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkSetTaskValueValidator.Invoke(this, null, 0, 0d);\n" +
                "NetworkUntrackQuestValidator.Invoke(this, null);\n" +
                "NetworkUntrackAllValidator.Invoke(this);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Quests/Runtime/Components/Journal.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkTrackQuestValidator.Invoke", reason);
        }

        [Test]
        public void QuestsPatcher_VerifyJournal_PassesWithRequiredStructure()
        {
            var patcher = new QuestsPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Journal {\n" +
                "public static System.Func<Journal, Quest, bool> NetworkActivateQuestValidator;\n" +
                "public static System.Func<Journal, Quest, bool> NetworkDeactivateQuestValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkActivateTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkDeactivateTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkCompleteTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkAbandonTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, bool> NetworkFailTaskValidator;\n" +
                "public static System.Func<Journal, Quest, int, double, bool> NetworkSetTaskValueValidator;\n" +
                "public static System.Func<Journal, Quest, bool> NetworkTrackQuestValidator;\n" +
                "public static System.Func<Journal, Quest, bool> NetworkUntrackQuestValidator;\n" +
                "public static System.Func<Journal, bool> NetworkUntrackAllValidator;\n" +
                "void Hooks() {\n" +
                "NetworkActivateQuestValidator.Invoke(this, null);\n" +
                "NetworkDeactivateQuestValidator.Invoke(this, null);\n" +
                "NetworkActivateTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkDeactivateTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkCompleteTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkAbandonTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkFailTaskValidator.Invoke(this, null, 0);\n" +
                "NetworkSetTaskValueValidator.Invoke(this, null, 0, 0d);\n" +
                "NetworkTrackQuestValidator.Invoke(this, null);\n" +
                "NetworkUntrackQuestValidator.Invoke(this, null);\n" +
                "NetworkUntrackAllValidator.Invoke(this);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Quests/Runtime/Components/Journal.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void DialoguePatcher_VerifyDialogueComponent_FailsWithoutStopInvoke()
        {
            var patcher = new DialoguePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Dialogue {\n" +
                "public static System.Func<Dialogue, Args, bool> NetworkPlayValidator;\n" +
                "public static System.Func<Dialogue, bool> NetworkStopValidator;\n" +
                "public async System.Threading.Tasks.Task Play(Args args)\n" +
                "{\n" +
                "    if (NetworkPlayValidator != null && !NetworkPlayValidator.Invoke(this, args)) return;\n" +
                "}\n" +
                "public void Stop() { }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Dialogue/Runtime/Components/Dialogue.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkStopValidator.Invoke", reason);
        }

        [Test]
        public void DialoguePatcher_VerifyStoryAndChoice_PassesWithRequiredStructure()
        {
            var patcher = new DialoguePatcherProxy();
            string storyContent =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Story {\n" +
                "public static System.Func<Story, bool> NetworkContinueValidator;\n" +
                "public void Continue()\n" +
                "{\n" +
                "    if (NetworkContinueValidator != null && !NetworkContinueValidator.Invoke(this)) return;\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool storyValid = patcher.Verify(
                "Plugins/GameCreator/Packages/Dialogue/Runtime/Dialogue/Story.cs",
                storyContent,
                out string storyReason);

            Assert.IsTrue(storyValid, storyReason);

            string choiceContent =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class NodeTypeChoice {\n" +
                "public static System.Func<NodeTypeChoice, int, bool> NetworkChooseValidator;\n" +
                "public void Choose(int nodeId)\n" +
                "{\n" +
                "    if (NetworkChooseValidator != null && !NetworkChooseValidator.Invoke(this, nodeId)) return;\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool choiceValid = patcher.Verify(
                "Plugins/GameCreator/Packages/Dialogue/Runtime/Dialogue/Nodes/NodeTypeChoice.cs",
                choiceContent,
                out string choiceReason);

            Assert.IsTrue(choiceValid, choiceReason);
        }

        [Test]
        public void TraversalPatcher_VerifyTraversalStance_FailsWithoutTryStateExitInvoke()
        {
            var patcher = new TraversalPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class TraversalStance {\n" +
                "public static System.Func<TraversalStance, Args, bool> NetworkTryCancelValidator;\n" +
                "public static System.Func<TraversalStance, bool> NetworkForceCancelValidator;\n" +
                "public static System.Func<TraversalStance, bool> NetworkTryJumpValidator;\n" +
                "public static System.Func<TraversalStance, IdString, bool> NetworkTryActionValidator;\n" +
                "public static System.Func<TraversalStance, IdString, bool> NetworkTryStateEnterValidator;\n" +
                "public static System.Func<TraversalStance, bool> NetworkTryStateExitValidator;\n" +
                "void Hooks() {\n" +
                "NetworkTryCancelValidator.Invoke(this, null);\n" +
                "NetworkForceCancelValidator.Invoke(this);\n" +
                "NetworkTryJumpValidator.Invoke(this);\n" +
                "NetworkTryActionValidator.Invoke(this, default);\n" +
                "NetworkTryStateEnterValidator.Invoke(this, default);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Traversal/Runtime/Stance/TraversalStance.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkTryStateExitValidator.Invoke", reason);
        }

        [Test]
        public void TraversalPatcher_VerifyTraverseRuntime_PassesWithRequiredStructure()
        {
            var patcher = new TraversalPatcherProxy();

            string linkContent =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class TraverseLink {\n" +
                "public static System.Func<TraverseLink, Character, bool> NetworkRunValidator;\n" +
                "public async System.Threading.Tasks.Task Run(Character character)\n" +
                "{\n" +
                "    if (NetworkRunValidator != null && !NetworkRunValidator.Invoke(this, character)) return;\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool linkValid = patcher.Verify(
                "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseLink.cs",
                linkContent,
                out string linkReason);

            Assert.IsTrue(linkValid, linkReason);

            string interactiveContent =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class TraverseInteractive {\n" +
                "public static System.Func<TraverseInteractive, Character, InteractiveTransitionData, bool> NetworkEnterValidator;\n" +
                "public async System.Threading.Tasks.Task Enter(Character character, InteractiveTransitionData transition)\n" +
                "{\n" +
                "    if (NetworkEnterValidator != null && !NetworkEnterValidator.Invoke(this, character, transition)) return;\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool interactiveValid = patcher.Verify(
                "Plugins/GameCreator/Packages/Traversal/Runtime/Components/TraverseInteractive.cs",
                interactiveContent,
                out string interactiveReason);

            Assert.IsTrue(interactiveValid, interactiveReason);
        }

        [Test]
        public void Patchers_VerifyRealProjectFiles_WhenPatched()
        {
            int validatedTotal = 0;

            var core = new CorePatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(core.Marker, core.Files, core.Verify);

            var inventory = new InventoryPatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(inventory.Marker, inventory.Files, inventory.Verify);

            var stats = new StatsPatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(stats.Marker, stats.Files, stats.Verify);

            var melee = new MeleePatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(melee.Marker, melee.Files, melee.Verify);

            var shooter = new ShooterPatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(shooter.Marker, shooter.Files, shooter.Verify);

            var abilities = new AbilitiesPatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(abilities.Marker, abilities.Files, abilities.Verify);

            var quests = new QuestsPatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(quests.Marker, quests.Files, quests.Verify);

            var dialogue = new DialoguePatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(dialogue.Marker, dialogue.Files, dialogue.Verify);

            var traversal = new TraversalPatcherProxy();
            validatedTotal += VerifyPatchedProjectFiles(traversal.Marker, traversal.Files, traversal.Verify);

            if (validatedTotal == 0)
            {
                Assert.Ignore("No patched runtime files detected in this workspace.");
            }
        }
    }
}
#endif
