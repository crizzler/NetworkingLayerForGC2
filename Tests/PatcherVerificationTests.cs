#if UNITY_EDITOR
using NUnit.Framework;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;

namespace Arawn.GameCreator2.Networking.Tests
{
    public class PatcherVerificationTests
    {
        private sealed class ShooterPatcherProxy : ShooterPatcher
        {
            public string Marker => PatchMarker;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        private sealed class MeleePatcherProxy : MeleePatcher
        {
            public string Marker => PatchMarker;

            public bool Verify(string relativePath, string content, out string reason)
            {
                return VerifyPatchedFile(relativePath, content, out reason);
            }
        }

        [Test]
        public void ShooterPatcher_VerifyWeaponData_FailsWithoutChargeRatioFixToken()
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

            Assert.IsFalse(valid);
            StringAssert.Contains("maxChargeTime", reason);
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
                "    float chargeRation = maxChargeTime > 0f ? Mathf.Clamp01((this.Character.Time.Time - this.m_LastTriggerPull) / maxChargeTime) : 1f;\n" +
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
    }
}
#endif
