#if UNITY_EDITOR
using System.IO;
using NUnit.Framework;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using Arawn.GameCreator2.Networking.Editor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    public class PatcherVerificationTests
    {
        [Test]
        public void InventoryPatchDefineDetector_RequiresCompleteV3Abi()
        {
            const string completeAbi = @"
// [GC2_NETWORK_PATCH_Inventory_3_0_0_inventory]
public enum NetworkInventoryInterceptResult : byte { }
public static object NetworkInstructionAddItemInterceptor;
public const int NetworkPatchRevision = 300;";

            Assert.IsTrue(GC2NetworkingDefineSymbols.HasInventoryAuthorityPatchAbi(completeAbi));
            Assert.IsFalse(GC2NetworkingDefineSymbols.HasInventoryAuthorityPatchAbi(
                completeAbi.Replace("public const int NetworkPatchRevision = 300;", string.Empty)));
            Assert.IsFalse(GC2NetworkingDefineSymbols.HasInventoryAuthorityPatchAbi(
                completeAbi.Replace("NetworkInstructionAddItemInterceptor", "MissingInterceptor")));
            Assert.IsFalse(GC2NetworkingDefineSymbols.HasInventoryAuthorityPatchAbi(string.Empty));
        }

        [Test]
        public void InventoryDependentAssemblies_RequireAuthorityPatchDefine()
        {
            string[] paths =
            {
                "Arawn/NetworkingLayerForGC2/Inventory/Arawn.GameCreator2.Networking.Inventory.asmdef",
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/Inventory/Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet.asmdef",
                "Arawn/NetworkingLayerForGC2/Tests/Inventory/Arawn.GameCreator2.Networking.Inventory.Tests.asmdef"
            };

            foreach (string relativePath in paths)
            {
                string content = File.ReadAllText(Path.Combine(Application.dataPath, relativePath));
                StringAssert.Contains(
                    $"\"{GC2NetworkingDefineSymbols.SYMBOL_INVENTORY_AUTHORITY_PATCH}\"",
                    content,
                    relativePath);
            }
        }

        [Test]
        public void MeleeAndTraversalRuntime_AvoidCompileTimePatchMethodDependencies()
        {
            string meleeSource = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeController.GuardsSkills.cs"));
            string traversalSource = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalController.cs"));

            StringAssert.DoesNotContain(".NetworkResolveDefenseDirect(", meleeSource);
            StringAssert.Contains("TryResolveDefenseDirect(", meleeSource);
            StringAssert.DoesNotContain(".NetworkInvalidatePendingEnter(", traversalSource);
            StringAssert.Contains("TryInvalidatePendingTraversalEnter(", traversalSource);
        }

        [Test]
        public void MeleeAndTraversalTests_RequireCompletePatchAbiDefines()
        {
            string meleeTests = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Tests/Melee/" +
                "Arawn.GameCreator2.Networking.Melee.Tests.asmdef"));
            string traversalTests = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Tests/Traversal/" +
                "Arawn.GameCreator2.Networking.Traversal.Tests.asmdef"));

            StringAssert.Contains(
                $"\"{GC2NetworkingDefineSymbols.SYMBOL_MELEE_AUTHORITY_PATCH}\"",
                meleeTests);
            StringAssert.Contains(
                $"\"{GC2NetworkingDefineSymbols.SYMBOL_TRAVERSAL_AUTHORITY_PATCH}\"",
                traversalTests);
        }

        [Test]
        public void PurrNetInstallCompatibility_DetectsOnlyMixedLiteNetLibGenerations()
        {
            const string combined =
                "namespace LiteNetLib { public class NetManager : LiteNetManager { } }";
            const string split =
                "namespace LiteNetLib { public partial class NetManager : LiteNetManager { } }";
            string[] legacyFiles = { "Assets/PurrNet/Externals/LiteNetLib/NetManager.Socket.cs" };

            Assert.IsTrue(PurrNetInstallCompatibility.HasMixedLiteNetLibLayout(
                combined,
                legacyFiles));
            Assert.IsFalse(PurrNetInstallCompatibility.HasMixedLiteNetLibLayout(
                split,
                legacyFiles));
            Assert.IsFalse(PurrNetInstallCompatibility.HasMixedLiteNetLibLayout(
                combined,
                System.Array.Empty<string>()));
        }

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

            public bool ReplaceFlexible(ref string content, string original, string replacement)
            {
                return TryReplaceWithFlexibleWhitespace(ref content, original, replacement);
            }
        }

        [Test]
        public void FlexiblePatchAnchor_DoesNotConsumePrecedingNewline()
        {
            var patcher = new InventoryPatcherProxy();
            string content =
                "        // RUN METHOD: ----------------------------------------\n\n" +
                "        protected override Task Run(Args args)\n" +
                "        {\n" +
                "            return DefaultResult;\n" +
                "        }";
            const string original =
                "        protected override Task Run(Args args)\n" +
                "        {\n" +
                "\n" +
                "            return DefaultResult;\n" +
                "        }";
            const string replacement =
                "        protected override Task Run(Args args)\n" +
                "        {\n" +
                "            return RunNetworkOrLocal();\n" +
                "        }";

            Assert.IsTrue(patcher.ReplaceFlexible(ref content, original, replacement));
            int replacementIndex = content.IndexOf(
                "        protected override Task Run",
                System.StringComparison.Ordinal);
            Assert.Greater(replacementIndex, 0);
            Assert.AreEqual('\n', content[replacementIndex - 1]);
            StringAssert.DoesNotContain(
                "// RUN METHOD: ----------------------------------------        protected override",
                content);
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

            public bool PrepareMigration(string relativePath, ref string content)
            {
                return PrepareContentForPatch(relativePath, ref content) == ExistingPatchState.Continue;
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
        public void ShooterPatcher_VerifyShooterWeapon_FailsForLegacyNotificationOnlyHook()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "internal void OnHit(ShotData data, Args args)\n" +
                "{\n" +
                "    NetworkHitDetected?.Invoke(data, args);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkOnHitValidator", reason);
        }

        [Test]
        public void ShooterPatcher_VerifyShooterWeapon_FailsWithoutNativeOutcomeCallback()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Action<ShotData> NetworkShotFiredExact;\n" +
                "public static System.Func<ShotData, Args, bool> NetworkOnHitValidator;\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "internal void OnHit(ShotData data, Args args)\n" +
                "{\n" +
                "    if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(data, args)) return;\n" +
                "    NetworkHitDetected?.Invoke(data, args);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkOnHitResolved", reason);
        }

        [Test]
        public void ShooterPatcher_VerifyShooterWeapon_PassesWithCancellableValidatorAndCompatibilityEvent()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Func<ShotData, Args, bool> NetworkOnHitValidator;\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "public static System.Action<ShotData, Args, BlockType, ReactionOutput, float> NetworkOnHitResolved;\n" +
                "internal void OnShoot(ShotData data, Args args) { NetworkShotFiredExact?.Invoke(data); }\n" +
                "internal void OnHit(ShotData data, Args args)\n" +
                "{\n" +
                "    if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(data, args)) return;\n" +
                "    NetworkHitDetected?.Invoke(data, args);\n" +
                "    BlockType networkBlockType = BlockType.None;\n" +
                "    ReactionOutput networkReactionOutput = ReactionOutput.None;\n" +
                "    float networkReactionPower = float.NaN;\n" +
                "    networkReactionPower = reactionInput.Power;\n" +
                "    networkBlockType = shieldOutput.Type;\n" +
                "    networkReactionOutput = target.Combat.GetHitReaction(reactionInput, args, data.Weapon.HitReaction);\n" +
                "    NetworkOnHitResolved?.Invoke(data, args, networkBlockType, networkReactionOutput, networkReactionPower);\n" +
                "    _ = this.m_OnHit.Run(args);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void ShooterPatcher_VerifyShooterWeapon_RejectsResolvedCallbackAfterDamageInstructions()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Func<ShotData, Args, bool> NetworkOnHitValidator;\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "public static System.Action<ShotData, Args, BlockType, ReactionOutput, float> NetworkOnHitResolved;\n" +
                "void OnHit(ShotData data, Args args) {\n" +
                "if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(data, args)) return;\n" +
                "NetworkHitDetected?.Invoke(data, args);\n" +
                "BlockType networkBlockType = BlockType.None;\n" +
                "ReactionOutput networkReactionOutput = ReactionOutput.None;\n" +
                "float networkReactionPower = float.NaN;\n" +
                "networkReactionPower = reactionInput.Power;\n" +
                "networkBlockType = shieldOutput.Type;\n" +
                "networkReactionOutput = target.Combat.GetHitReaction(input, args, reaction);\n" +
                "_ = this.m_OnHit.Run(args);\n" +
                "NetworkOnHitResolved?.Invoke(data, args, networkBlockType, networkReactionOutput, networkReactionPower);\n" +
                "}\n}\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out _);

            Assert.IsFalse(valid);
        }

        [Test]
        public void ShooterPatcher_VerifyShooterWeapon_RejectsLegacyThreeArgumentResultCallback()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Func<ShotData, Args, bool> NetworkOnHitValidator;\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "public static System.Action<ShotData, Args, BlockType> NetworkOnHitResolved;\n" +
                "internal void OnHit(ShotData data, Args args)\n" +
                "{\n" +
                "    if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(data, args)) return;\n" +
                "    NetworkHitDetected?.Invoke(data, args);\n" +
                "    BlockType networkBlockType = BlockType.None;\n" +
                "    networkBlockType = shieldOutput.Type;\n" +
                "    NetworkOnHitResolved?.Invoke(data, args, networkBlockType);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out _);

            Assert.IsFalse(valid);
        }

        [Test]
        public void ShooterPatcher_VerifyShooterWeapon_RejectsLegacyFourArgumentResultCallback()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Func<ShotData, Args, bool> NetworkOnHitValidator;\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "public static System.Action<ShotData, Args, BlockType, ReactionOutput> NetworkOnHitResolved;\n" +
                "internal void OnHit(ShotData data, Args args)\n" +
                "{\n" +
                "    if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(data, args)) return;\n" +
                "    NetworkHitDetected?.Invoke(data, args);\n" +
                "    BlockType networkBlockType = BlockType.None;\n" +
                "    ReactionOutput networkReactionOutput = ReactionOutput.None;\n" +
                "    networkBlockType = shieldOutput.Type;\n" +
                "    networkReactionOutput = target.Combat.GetHitReaction(reactionInput, args, reaction);\n" +
                "    NetworkOnHitResolved?.Invoke(data, args, networkBlockType, networkReactionOutput);\n" +
                "    _ = this.m_OnHit.Run(args);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out _);

            Assert.IsFalse(valid);
        }

        [Test]
        public void ShooterPatcher_VerifyShooterWeapon_RejectsReevaluatedOrMissingReactionPower()
        {
            var patcher = new ShooterPatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class ShooterWeapon {\n" +
                "public static System.Func<ShotData, Args, bool> NetworkOnHitValidator;\n" +
                "public static System.Action<ShotData, Args> NetworkHitDetected;\n" +
                "public static System.Action<ShotData, Args, BlockType, ReactionOutput, float> NetworkOnHitResolved;\n" +
                "void OnHit(ShotData data, Args args) {\n" +
                "if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(data, args)) return;\n" +
                "NetworkHitDetected?.Invoke(data, args);\n" +
                "BlockType networkBlockType = BlockType.None;\n" +
                "ReactionOutput networkReactionOutput = ReactionOutput.None;\n" +
                "float networkReactionPower = (float) data.Weapon.Fire.Power(args);\n" +
                "networkBlockType = shieldOutput.Type;\n" +
                "networkReactionOutput = target.Combat.GetHitReaction(reactionInput, args, reaction);\n" +
                "NetworkOnHitResolved?.Invoke(data, args, networkBlockType, networkReactionOutput, networkReactionPower);\n" +
                "_ = this.m_OnHit.Run(args);\n" +
                "}\n}\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
                content,
                out _);

            Assert.IsFalse(valid);
        }

        [Test]
        public void MeleePatcher_VerifyStance_FailsWithoutBothReactionTransitionCallbacks()
        {
            var patcher = new MeleePatcherProxy();
            string content = BuildMeleeStanceVerificationContent(patcher.Marker, 1);

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/MeleeStance.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkReactionStarted?.Invoke", reason);
        }

        [Test]
        public void MeleePatcher_VerifyStance_PassesWithReactionTransitionCallbacks()
        {
            var patcher = new MeleePatcherProxy();
            string content = BuildMeleeStanceVerificationContent(patcher.Marker, 2);

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/MeleeStance.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        private static string BuildMeleeStanceVerificationContent(
            string marker,
            int reactionCallbackCount)
        {
            string reactionCallbacks = reactionCallbackCount >= 1
                ? "NetworkReactionStarted?.Invoke(this, from, input, reaction);\n"
                : string.Empty;
            if (reactionCallbackCount >= 2)
            {
                reactionCallbacks += "NetworkReactionStarted?.Invoke(this, from, input, reaction);\n";
            }

            return
                $"{marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class MeleeStance {\n" +
                "object NetworkInputChargeValidator, NetworkInputExecuteValidator;\n" +
                "object NetworkPlaySkillValidator, NetworkPlayReactionValidator;\n" +
                "object NetworkReactionStarted, NetworkSkillExecuted, NetworkHitRegistered;\n" +
                "void Verify() {\n" +
                "NetworkInputChargeValidator.Invoke();\n" +
                "NetworkInputExecuteValidator.Invoke();\n" +
                "NetworkPlaySkillValidator.Invoke();\n" +
                "NetworkPlayReactionValidator.Invoke();\n" +
                reactionCallbacks +
                "NetworkSkillExecuted?.Invoke();\n" +
                "NetworkHitRegistered?.Invoke();\n" +
                "}\n" +
                "void InputChargeDirect() {}\n" +
                "void InputExecuteDirect() {}\n" +
                "void PlaySkillDirect() {}\n" +
                "void PlayReactionDirect() {}\n" +
                "void HitDirect() {}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";
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
                "public BlockType NetworkResolveDefenseDirect(\n" +
                "    Character attacker, Character target, Vector3 point,\n" +
                "    Vector3 targetLocalDirection, float power) => BlockType.None;\n" +
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
        public void MeleePatcher_VerifySkill_FailsWithoutAuthoredDefenseResolver()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Skill {\n" +
                "public static System.Func<Skill, Args, Vector3, Vector3, bool> NetworkOnHitValidator;\n" +
                "internal void OnHit(Args args, Vector3 point, Vector3 direction)\n" +
                "{\n" +
                "    if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(this, args, point, direction)) return;\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/ScriptableObjects/Skill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkResolveDefenseDirect", reason);
        }

        [Test]
        public void MeleePatcher_VerifySkill_PassesWithAuthoredDefenseResolver()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class Skill {\n" +
                "public static System.Func<Skill, Args, Vector3, Vector3, bool> NetworkOnHitValidator;\n" +
                "internal void OnHit(Args args, Vector3 point, Vector3 direction)\n" +
                "{\n" +
                "    if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(this, args, point, direction)) return;\n" +
                "}\n" +
                "public BlockType NetworkResolveDefenseDirect(\n" +
                "    Character attacker, Character target, Vector3 point,\n" +
                "    Vector3 targetLocalDirection, float power) => BlockType.None;\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/ScriptableObjects/Skill.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void MeleePatcher_VerifyShieldResponse_RequiresExactReactionOutputCallback()
        {
            var patcher = new MeleePatcherProxy();
            string missingCallback =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public abstract class TShieldResponse {\n" +
                "public static Action<TShieldResponse, Args, ShieldOutput, ReactionInput, Reaction, ReactionOutput> NetworkReactionResolved;\n" +
                "void Run() { ReactionOutput reactionOutput = this.m_Reaction.Run(character, args, reactionInput); }\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Shield/TShieldResponse.cs",
                missingCallback,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkReactionResolved?.Invoke", reason);
        }

        [Test]
        public void MeleePatcher_VerifyShieldResponse_PassesWithExactReactionOutputCallback()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public abstract class TShieldResponse {\n" +
                "public static Action<TShieldResponse, Args, ShieldOutput, ReactionInput, Reaction, ReactionOutput> NetworkReactionResolved;\n" +
                "void Run() {\n" +
                "ReactionOutput reactionOutput = this.m_Reaction.Run(character, args, reactionInput);\n" +
                "NetworkReactionResolved?.Invoke(this, args, shieldOutput, reactionInput, this.m_Reaction, reactionOutput);\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Shield/TShieldResponse.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void MeleePatcher_VerifyAttackSkill_FailsWithoutStrikeInvocation()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class AttackSkill {\n" +
                "public static System.Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;\n" +
                "private readonly System.Collections.Generic.HashSet<int> m_HitsBuffer = new();\n" +
                "private void OnUpdatePhaseStrike()\n" +
                "{\n" +
                "    if (!this.ComboSkill.CanHit(hitArgs))\n" +
                "    {\n" +
                "        this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "        continue;\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkStrikeValidator.Invoke", reason);
        }

        [Test]
        public void MeleePatcher_VerifyAttackSkill_PassesWithPostCanHitInterceptor()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class AttackSkill {\n" +
                "public static System.Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;\n" +
                "public static Action<MeleeStance, Skill, int, int> NetworkStrikeProbe;\n" +
                "public static Action<MeleeStance, MeleeWeapon, Skill, int> NetworkSkillStarted;\n" +
                "private readonly System.Collections.Generic.HashSet<int> m_HitsBuffer = new();\n" +
                "private void WhenEnter()\n" +
                "{\n" +
                "    this.m_Duration = this.ComboSkill.GetDuration(this.m_Speed, this.m_Args);\n" +
                "    NetworkSkillStarted?.Invoke(\n" +
                "        this.Attacks.MeleeStance, this.Attacks.Weapon, this.ComboSkill, this.Attacks.ComboId);\n" +
                "    this.ComboSkill.Run();\n" +
                "}\n" +
                "private void OnUpdatePhaseStrike()\n" +
                "{\n" +
                "    var hits = new System.Collections.Generic.List<StrikeOutput>();\n" +
                "    hits.Add(candidate);\n" +
                "    NetworkStrikeProbe?.Invoke(\n" +
                "        this.Attacks.MeleeStance, this.ComboSkill, this.m_Strikers.Count, hits.Count);\n" +
                "    foreach (StrikeOutput hit in hits)\n" +
                "    {\n" +
                "    if (!this.ComboSkill.CanHit(hitArgs))\n" +
                "    {\n" +
                "        this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "        continue;\n" +
                "    }\n" +
                "    if (NetworkStrikeValidator != null &&\n" +
                "        !NetworkStrikeValidator.Invoke(this.Attacks.MeleeStance, hit, this.ComboSkill))\n" +
                "    {\n" +
                "        this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "        continue;\n" +
                "    }\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
                content,
                out string reason);

            Assert.IsTrue(valid, reason);
        }

        [Test]
        public void MeleePatcher_VerifyAttackSkill_FailsWithoutPersistentStrikeProbe()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                "// [GC2_NETWORK_PATCH_Melee_v3_3_0_melee]\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class AttackSkill {\n" +
                "public static System.Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;\n" +
                "private readonly System.Collections.Generic.HashSet<int> m_HitsBuffer = new();\n" +
                "private void OnUpdatePhaseStrike()\n" +
                "{\n" +
                "    var hits = new System.Collections.Generic.List<StrikeOutput>();\n" +
                "    hits.Add(candidate);\n" +
                "    foreach (StrikeOutput hit in hits)\n" +
                "    {\n" +
                "        if (!this.ComboSkill.CanHit(hitArgs))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "        if (NetworkStrikeValidator != null &&\n" +
                "            !NetworkStrikeValidator.Invoke(this.Attacks.MeleeStance, hit, this.ComboSkill))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkStrikeProbe", reason);
        }

        [Test]
        public void MeleePatcher_VerifyAttackSkill_FailsWhenStrikeProbeRunsInsideHitLoop()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class AttackSkill {\n" +
                "public static System.Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;\n" +
                "public static Action<MeleeStance, Skill, int, int> NetworkStrikeProbe;\n" +
                "public static Action<MeleeStance, MeleeWeapon, Skill, int> NetworkSkillStarted;\n" +
                "private readonly System.Collections.Generic.HashSet<int> m_HitsBuffer = new();\n" +
                "private void WhenEnter()\n" +
                "{\n" +
                "    this.m_Duration = this.ComboSkill.GetDuration(this.m_Speed, this.m_Args);\n" +
                "    NetworkSkillStarted?.Invoke(\n" +
                "        this.Attacks.MeleeStance, this.Attacks.Weapon, this.ComboSkill, this.Attacks.ComboId);\n" +
                "    this.ComboSkill.Run();\n" +
                "}\n" +
                "private void OnUpdatePhaseStrike()\n" +
                "{\n" +
                "    var hits = new System.Collections.Generic.List<StrikeOutput>();\n" +
                "    hits.Add(candidate);\n" +
                "    foreach (StrikeOutput hit in hits)\n" +
                "    {\n" +
                "        NetworkStrikeProbe?.Invoke(\n" +
                "            this.Attacks.MeleeStance, this.ComboSkill, this.m_Strikers.Count, hits.Count);\n" +
                "        if (!this.ComboSkill.CanHit(hitArgs))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "        if (NetworkStrikeValidator != null &&\n" +
                "            !NetworkStrikeValidator.Invoke(this.Attacks.MeleeStance, hit, this.ComboSkill))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkStrikeProbe", reason);
        }

        [Test]
        public void MeleePatcher_VerifyAttackSkill_FailsWithoutSkillStartInvocation()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class AttackSkill {\n" +
                "public static System.Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;\n" +
                "public static Action<MeleeStance, Skill, int, int> NetworkStrikeProbe;\n" +
                "public static Action<MeleeStance, MeleeWeapon, Skill, int> NetworkSkillStarted;\n" +
                "private readonly System.Collections.Generic.HashSet<int> m_HitsBuffer = new();\n" +
                "private void OnUpdatePhaseStrike()\n" +
                "{\n" +
                "    var hits = new System.Collections.Generic.List<StrikeOutput>();\n" +
                "    hits.Add(candidate);\n" +
                "    NetworkStrikeProbe?.Invoke(\n" +
                "        this.Attacks.MeleeStance, this.ComboSkill, this.m_Strikers.Count, hits.Count);\n" +
                "    foreach (StrikeOutput hit in hits)\n" +
                "    {\n" +
                "        if (!this.ComboSkill.CanHit(hitArgs))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "        if (NetworkStrikeValidator != null &&\n" +
                "            !NetworkStrikeValidator.Invoke(this.Attacks.MeleeStance, hit, this.ComboSkill))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkSkillStarted?.Invoke", reason);
        }

        [Test]
        public void MeleePatcher_VerifyAttackSkill_FailsWhenSkillStartRunsAfterSkillInstructions()
        {
            var patcher = new MeleePatcherProxy();
            string content =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class AttackSkill {\n" +
                "public static System.Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;\n" +
                "public static Action<MeleeStance, Skill, int, int> NetworkStrikeProbe;\n" +
                "public static Action<MeleeStance, MeleeWeapon, Skill, int> NetworkSkillStarted;\n" +
                "private readonly System.Collections.Generic.HashSet<int> m_HitsBuffer = new();\n" +
                "private void WhenEnter()\n" +
                "{\n" +
                "    this.m_Duration = this.ComboSkill.GetDuration(this.m_Speed, this.m_Args);\n" +
                "    this.ComboSkill.Run();\n" +
                "    NetworkSkillStarted?.Invoke(\n" +
                "        this.Attacks.MeleeStance, this.Attacks.Weapon, this.ComboSkill, this.Attacks.ComboId);\n" +
                "}\n" +
                "private void OnUpdatePhaseStrike()\n" +
                "{\n" +
                "    var hits = new System.Collections.Generic.List<StrikeOutput>();\n" +
                "    hits.Add(candidate);\n" +
                "    NetworkStrikeProbe?.Invoke(\n" +
                "        this.Attacks.MeleeStance, this.ComboSkill, this.m_Strikers.Count, hits.Count);\n" +
                "    foreach (StrikeOutput hit in hits)\n" +
                "    {\n" +
                "        if (!this.ComboSkill.CanHit(hitArgs))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "        if (NetworkStrikeValidator != null &&\n" +
                "            !NetworkStrikeValidator.Invoke(this.Attacks.MeleeStance, hit, this.ComboSkill))\n" +
                "        {\n" +
                "            this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());\n" +
                "            continue;\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
                content,
                out string reason);

            Assert.IsFalse(valid);
            StringAssert.Contains("NetworkSkillStarted", reason);
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
                "private uint m_NetworkEnterSequence;\n" +
                "public TraversalToken NetworkSnapshotToken => this.m_CurrentToken;\n" +
                "public void NetworkInvalidatePendingEnter() { ++this.m_NetworkEnterSequence; }\n" +
                "public bool NetworkRestoreInteractiveSnapshot(TraverseInteractive traverse, Vector3 relativePosition) => true;\n" +
                "public bool NetworkClearSnapshot() => true;\n" +
                "void Hooks() {\n" +
                "NetworkTryCancelValidator.Invoke(this, null);\n" +
                "NetworkForceCancelValidator.Invoke(this);\n" +
                "NetworkTryJumpValidator.Invoke(this);\n" +
                "NetworkTryActionValidator.Invoke(this, default);\n" +
                "NetworkTryStateEnterValidator.Invoke(this, default);\n" +
                "}\n" +
                "void OnTraverseEnter() { uint networkEnterSequence = ++this.m_NetworkEnterSequence; " +
                "if (networkEnterSequence != this.m_NetworkEnterSequence) return; }\n" +
                "void OnTraverseExit(object traverse, object token) { if (token != this.m_CurrentToken) return; }\n" +
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
        public void TraversalPatcher_VerifyMotionInteractive_RequiresResolverBeforeAuthoredContinue()
        {
            var patcher = new TraversalPatcherProxy();
            string commonHeader =
                $"{patcher.Marker}\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "public class MotionInteractive {\n" +
                "public object NetworkEdgeConnectionResolver;\n" +
                "public object NetworkConnectionSkipTransitionResolver;\n" +
                "public void NetworkResumeInteractiveSnapshot() { }\n" +
                "void Enter() { NetworkConnectionSkipTransitionResolver.Invoke(a, b, c); }\n";
            string edgeB =
                "if (traverseInteractive.PushingOutB(currentLocalPosition, swizzleLocalInput)) {\n" +
                "if (NetworkEdgeConnectionResolver != null) {\n" +
                "Traverse networkConnection = NetworkEdgeConnectionResolver.Invoke(this, traverseInteractive, character, currentLocalPosition, swizzleLocalInput, true);\n" +
                "if (networkConnection != null) { return networkConnection; }\n}\n" +
                "else if (traverseInteractive.ContinueB != null) return traverseInteractive.ContinueB;\n" +
                "}\n";

            string validContent = commonHeader +
                "void OnUpdate() {\n" +
                "if (traverseInteractive.PushingOutA(currentLocalPosition, swizzleLocalInput)) {\n" +
                "if (NetworkEdgeConnectionResolver != null) {\n" +
                "Traverse networkConnection = NetworkEdgeConnectionResolver.Invoke(this, traverseInteractive, character, currentLocalPosition, swizzleLocalInput, false);\n" +
                "if (networkConnection != null) { return networkConnection; }\n}\n" +
                "else if (traverseInteractive.ContinueA != null) return traverseInteractive.ContinueA;\n" +
                "}\n" + edgeB + "}\n}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            bool valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Traversal/Runtime/ScriptableObjects/MotionInteractive.cs",
                validContent,
                out string validReason);
            Assert.IsTrue(valid, validReason);

            string legacyContent = commonHeader +
                "void OnUpdate() {\n" +
                "if (traverseInteractive.PushingOutA(currentLocalPosition, swizzleLocalInput)) {\n" +
                "if (traverseInteractive.ContinueA != null) return traverseInteractive.ContinueA;\n" +
                "if (NetworkEdgeConnectionResolver != null) {\n" +
                "Traverse networkConnection = NetworkEdgeConnectionResolver.Invoke(this, traverseInteractive, character, currentLocalPosition, swizzleLocalInput, false);\n" +
                "if (networkConnection != null) { return networkConnection; }\n}\n" +
                "}\n" + edgeB + "}\n}\n" +
                "// [GC2_NETWORK_PATCH_END]\n";

            valid = patcher.Verify(
                "Plugins/GameCreator/Packages/Traversal/Runtime/ScriptableObjects/MotionInteractive.cs",
                legacyContent,
                out string legacyReason);
            Assert.IsFalse(valid);
            StringAssert.Contains("Required patch regex", legacyReason);
        }

        [Test]
        public void TraversalPatcher_StaleReplacementLayout_RestoresPristineBackupBeforeMigration()
        {
            const string relativePath =
                "Plugins/GameCreator/Packages/Traversal/Runtime/ScriptableObjects/MotionInteractive.cs";
            string backupPath = Path.GetFullPath(Path.Combine(
                Application.dataPath,
                "../Library/GameCreator2NetworkingLayer/Patches/Backups/Traversal",
                relativePath + ".backup"));
            if (!File.Exists(backupPath))
            {
                Assert.Ignore("Traversal migration test requires the pristine source backup created by the patch transaction.");
            }

            var patcher = new TraversalPatcherProxy();
            string staleContent =
                "// [GC2_NETWORK_PATCH_Traversal_v2_5_0_traversal]\n" +
                "namespace GameCreator.Runtime.Traversal { class MotionInteractive {\n" +
                "void OnUpdate() {\n" +
                "// [GC2_NETWORK_PATCH]\n" +
                "if (NetworkEdgeConnectionResolver != null) { return; }\n" +
                "// [GC2_NETWORK_PATCH_END]\n" +
                "}\n}\n}\n";

            bool prepared = patcher.PrepareMigration(relativePath, ref staleContent);

            Assert.IsTrue(prepared);
            StringAssert.DoesNotContain("GC2_NETWORK_PATCH", staleContent);
            StringAssert.Contains("traverseInteractive.ContinueA", staleContent);
            StringAssert.Contains("traverseInteractive.ContinueB", staleContent);
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
                "    object token = null; if (token == null || token.IsCancelled) return;\n" +
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
                "    object token = null; if (token == null || token.IsCancelled) return;\n" +
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

        [Test]
        public void MeleeRuntime_DefaultLogging_HasNoTemporaryHostOrClientPrefixes()
        {
            string[] relativePaths =
            {
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeController.cs",
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeController.GuardsSkills.cs",
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeController.Hits.cs",
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeManager.cs",
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleePatchHooks.cs"
            };

            foreach (string relativePath in relativePaths)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                Assert.That(File.Exists(fullPath), Is.True, $"Missing Melee runtime source: {relativePath}");

                string source = File.ReadAllText(fullPath);
                StringAssert.DoesNotContain("[NetworkMeleeHostDebug]", source, relativePath);
                StringAssert.DoesNotContain("[NetworkMeleeClientDebug]", source, relativePath);
                StringAssert.DoesNotContain("NetworkMeleeHostTrace", source, relativePath);
            }
        }

        [Test]
        public void MeleeRuntime_StrikeProbe_RemainsFunctionalWithoutTemporaryDiagnostics()
        {
            string fullPath = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleePatchHooks.cs");
            string source = File.ReadAllText(fullPath);

            StringAssert.Contains("NetworkStrikeProbe", source);
            StringAssert.Contains("if (candidateCount == 0)", source);
            StringAssert.Contains("EnsureIntendedTargetPhysicsProxy(stance);", source);
            StringAssert.DoesNotContain("HostStrikeProbeState", source);
            StringAssert.DoesNotContain("BuildStrikeSpatialSnapshot", source);
            StringAssert.DoesNotContain("HostTrace", source);

            string managerSource = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Melee/NetworkMeleeManager.cs"));
            StringAssert.DoesNotContain("m_EnableHostTrace", managerSource);
            StringAssert.DoesNotContain("NetworkMeleeHostTrace", managerSource);
        }
    }
}
#endif
