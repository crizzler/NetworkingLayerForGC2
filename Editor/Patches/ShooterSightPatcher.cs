using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Required GC2 Shooter Sight hook.
    /// Keeps GC2 in control of Sight.Enter/Exit while allowing networking layers to suppress
    /// unsafe local-only instruction side effects on remote character replicas.
    /// </summary>
    public sealed class ShooterSightPatcher : GC2PatcherBase
    {
        private const string SightPath =
            "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/Sight.cs";

        public override string ModuleName => "ShooterSight";
        public override string PatchVersion => "2.2.3-shooter-sight";
        public override string DisplayName => "Shooter Sight Hook (Game Creator 2)";

        public override string PatchDescription =>
            "Adds a small explicit hook to Game Creator 2 Shooter Sight.Enter/Exit.\n\n" +
            "The hook lets GC2 keep owning the sight lifecycle, animation state, blend timing, IK, " +
            "crosshair, aim and biomechanics. The networking layer only gates the unsafe side-effect " +
            "bucket: Sight.m_OnEnter and Sight.m_OnExit instruction execution on remote replicas.";

        protected override string[] FilesToPatch => new[] { SightPath };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Shooter/Editor/Version.txt", "2.2.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            return relativePath.EndsWith("Sight.cs", StringComparison.Ordinal)
                ? new[]
                {
                    "NetworkSightInstructionsValidator",
                    "NetworkSightInstructionsValidator.Invoke(character)",
                    "m_OnEnter.Run",
                    "m_OnExit.Run"
                }
                : base.GetRequiredPatchTokens(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            return relativePath.EndsWith("Sight.cs", StringComparison.Ordinal)
                ? new Dictionary<string, int>
                {
                    { "NetworkSightInstructionsValidator.Invoke(character)", 2 }
                }
                : base.GetRequiredPatchTokenCounts(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchRegexTokenCounts(string relativePath)
        {
            return relativePath.EndsWith("Sight.cs", StringComparison.Ordinal)
                ? new Dictionary<string, int>
                {
                    {
                        @"public\s+static\s+Func\s*<\s*Character\s*,\s*bool\s*>\s+NetworkSightInstructionsValidator\s*;",
                        1
                    },
                    {
                        @"NetworkSightInstructionsValidator\s*==\s*null\s*\|\|\s*NetworkSightInstructionsValidator\.Invoke\s*\(\s*character\s*\)",
                        2
                    }
                }
                : base.GetRequiredPatchRegexTokenCounts(relativePath);
        }

        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);

            ExistingPatchState existingPatchState = PrepareContentForPatch(relativePath, ref content);
            if (existingPatchState == ExistingPatchState.SkipAlreadyPatched) return true;
            if (existingPatchState == ExistingPatchState.Failed) return false;

            if (!relativePath.EndsWith("Sight.cs", StringComparison.Ordinal)) return false;

            if (!EnsurePatchMarker(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not add Shooter Sight patch marker.");
                return false;
            }

            if (!EnsureHookField(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not add Shooter Sight network validator field.");
                return false;
            }

            if (!EnsureInstructionGuard(ref content, "Enter", "m_OnEnter"))
            {
                Debug.LogError("[GC2 Networking] Could not guard Sight.Enter m_OnEnter instructions.");
                return false;
            }

            if (!EnsureInstructionGuard(ref content, "Exit", "m_OnExit"))
            {
                Debug.LogError("[GC2 Networking] Could not guard Sight.Exit m_OnExit instructions.");
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool EnsurePatchMarker(ref string content)
        {
            if (ContainsPatchMarker(content)) return true;

            const string namespaceToken = "namespace GameCreator.Runtime.Shooter";
            int namespaceIndex = content.IndexOf(namespaceToken, StringComparison.Ordinal);
            if (namespaceIndex < 0) return false;

            string marker =
                $"{PatchMarker}\n" +
                "// This file has been patched for GC2 Networking Shooter sight safety.\n" +
                "// Do not modify the patched sections manually.\n" +
                "// Use Game Creator > Networking Layer > Patches > Shooter Sight > Unpatch to restore.\n\n";

            content = content.Insert(namespaceIndex, marker);
            return true;
        }

        private static bool EnsureHookField(ref string content)
        {
            const string fieldPattern =
                @"(?m)^(?<indent>[ \t]*)public\s+static\s+Func\s*<\s*Character\s*,\s*bool\s*>\s+NetworkSightInstructionsValidator\s*;\s*$";

            Match existingField = Regex.Match(
                content,
                fieldPattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            if (existingField.Success)
            {
                int lineStart = existingField.Index;
                int searchStart = Math.Max(0, lineStart - 180);
                int searchLength = Math.Min(content.Length - searchStart, existingField.Length + 360);
                string nearby = content.Substring(searchStart, searchLength);
                if (nearby.Contains("// [GC2_NETWORK_PATCH]", StringComparison.Ordinal))
                {
                    return true;
                }

                string indent = existingField.Groups["indent"].Value;
                string replacement =
                    $"{indent}// [GC2_NETWORK_PATCH] Allows networking layers to suppress sight instruction side effects on remote replicas.\n" +
                    $"{indent}public static Func<Character, bool> NetworkSightInstructionsValidator;\n" +
                    $"{indent}// [GC2_NETWORK_PATCH_END]";

                content = content.Remove(existingField.Index, existingField.Length)
                    .Insert(existingField.Index, replacement);
                return true;
            }

            const string classPattern =
                @"(?m)^(?<indent>[ \t]*)public\s+class\s+Sight\s*:\s*ScriptableObject\s*(?:\n(?<open>[ \t]*)\{|[ \t]*\{)[ \t]*$";

            Match classMatch = Regex.Match(
                content,
                classPattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            if (!classMatch.Success) return false;

            int insertIndex = classMatch.Index + classMatch.Length;
            string classIndent = classMatch.Groups["open"].Success
                ? classMatch.Groups["open"].Value
                : classMatch.Groups["indent"].Value;
            string memberIndent = classIndent + "    ";
            string block =
                "\n" +
                $"{memberIndent}// [GC2_NETWORK_PATCH] Allows networking layers to suppress sight instruction side effects on remote replicas.\n" +
                $"{memberIndent}public static Func<Character, bool> NetworkSightInstructionsValidator;\n" +
                $"{memberIndent}// [GC2_NETWORK_PATCH_END]\n";

            content = content.Insert(insertIndex, block);
            return true;
        }

        private static bool EnsureInstructionGuard(ref string content, string methodName, string instructionField)
        {
            if (!TryFindMethodBodySpan(content, methodName, out int bodyStart, out int bodyEnd)) return false;

            string body = content.Substring(bodyStart, bodyEnd - bodyStart);
            if (body.Contains("NetworkSightInstructionsValidator.Invoke(character)", StringComparison.Ordinal) &&
                body.Contains($"{instructionField}.Run", StringComparison.Ordinal))
            {
                return true;
            }

            string statementPattern =
                $@"(?m)^(?<indent>[ \t]*)(?:_\s*=\s*)?this\.{Regex.Escape(instructionField)}\.Run\s*\(\s*weaponData\.WeaponArgs\s*\)\s*;\s*$";

            Match statement = Regex.Match(
                body,
                statementPattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            if (!statement.Success) return false;

            string indent = statement.Groups["indent"].Value;
            string guardedStatement =
                $"{indent}// [GC2_NETWORK_PATCH] Remote network replicas must not execute sight instruction side effects.\n" +
                $"{indent}if (NetworkSightInstructionsValidator == null ||\n" +
                $"{indent}    NetworkSightInstructionsValidator.Invoke(character))\n" +
                $"{indent}{{\n" +
                $"{indent}    _ = this.{instructionField}.Run(weaponData.WeaponArgs);\n" +
                $"{indent}}}\n" +
                $"{indent}// [GC2_NETWORK_PATCH_END]";

            int absoluteIndex = bodyStart + statement.Index;
            content = content.Remove(absoluteIndex, statement.Length)
                .Insert(absoluteIndex, guardedStatement);

            return true;
        }

        private static bool TryFindMethodBodySpan(
            string content,
            string methodName,
            out int bodyStart,
            out int bodyEnd)
        {
            bodyStart = -1;
            bodyEnd = -1;

            string methodPattern =
                $@"\bpublic\s+void\s+{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{";

            Match methodMatch = Regex.Match(
                content,
                methodPattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            if (!methodMatch.Success) return false;

            int openBrace = content.IndexOf('{', methodMatch.Index);
            if (openBrace < 0) return false;

            int depth = 0;
            for (int i = openBrace; i < content.Length; i++)
            {
                char ch = content[i];
                if (ch == '{')
                {
                    depth++;
                    continue;
                }

                if (ch != '}') continue;

                depth--;
                if (depth != 0) continue;

                bodyStart = openBrace + 1;
                bodyEnd = i;
                return true;
            }

            return false;
        }
    }
}
