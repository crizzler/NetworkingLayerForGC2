using UnityEngine;
using System.Text.RegularExpressions;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Melee module.
    /// Adds network validation hooks to MeleeStance for server-authoritative combat.
    /// </summary>
    public class MeleePatcher : GC2PatcherBase
    {
        public override string ModuleName => "Melee";
        public override string PatchVersion => "3.5.0-melee";
        public override string DisplayName => "Melee (Game Creator 2)";

        public override string PatchDescription =>
            "This will modify the Game Creator 2 Melee source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "MeleeStance.InputCharge/InputExecute will have network validation.\n" +
            "MeleeStance.PlaySkill/PlayReaction will have network hooks.\n" +
            "Reaction phase starts will be emitted synchronously for exact-once replication.\n" +
            "Authored shield response reactions will report their exact asset and output.\n" +
            "AttackSkill strike outputs will be intercepted before gameplay side effects.\n" +
            "AttackSkill strike scans expose a persistent diagnostic probe.\n" +
            "AttackSkill state entry reports its exact resolved weapon, Skill, and combo node before instructions or motion run.\n" +
            "Skill exposes a server-only direct defense resolver that preserves authored shield, block, parry, and break behavior.\n" +
            "Skill.OnHit validation remains for upgraded-project compatibility.\n" +
            "Also fixes obsolete API warning in SkillEditor.cs.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/MeleeStance.cs",
            "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/Attacks/States/AttackSkill.cs",
            "Plugins/GameCreator/Packages/Melee/Runtime/ScriptableObjects/Skill.cs",
            "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Shield/TShieldResponse.cs",
            "Plugins/GameCreator/Packages/Melee/Editor/Editors/SkillEditor.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Melee/Editor/Version.txt", "2.2.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("MeleeStance.cs"))
            {
                return new[]
                {
                    "NetworkInputChargeValidator",
                    "NetworkInputExecuteValidator",
                    "NetworkPlaySkillValidator",
                    "NetworkPlayReactionValidator",
                    "NetworkReactionStarted",
                    "NetworkSkillExecuted",
                    "NetworkHitRegistered",
                    "InputChargeDirect(",
                    "InputExecuteDirect(",
                    "PlaySkillDirect(",
                    "PlayReactionDirect(",
                    "HitDirect("
                };
            }

            if (relativePath.EndsWith("/Skill.cs"))
            {
                return new[]
                {
                    "NetworkOnHitValidator",
                    "NetworkResolveDefenseDirect("
                };
            }

            if (relativePath.EndsWith("AttackSkill.cs"))
            {
                return new[]
                {
                    "NetworkStrikeValidator",
                    "NetworkStrikeValidator.Invoke",
                    "NetworkStrikeProbe",
                    "NetworkStrikeProbe?.Invoke",
                    "NetworkSkillStarted",
                    "NetworkSkillStarted?.Invoke",
                    "m_HitsBuffer.Add"
                };
            }

            if (relativePath.EndsWith("TShieldResponse.cs"))
            {
                return new[]
                {
                    "NetworkReactionResolved",
                    "NetworkReactionResolved?.Invoke",
                    "ReactionOutput reactionOutput = this.m_Reaction.Run"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("MeleeStance.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkInputChargeValidator.Invoke", 1 },
                    { "NetworkInputExecuteValidator.Invoke", 1 },
                    { "NetworkPlaySkillValidator.Invoke", 1 },
                    { "NetworkPlayReactionValidator.Invoke", 1 },
                    { "NetworkReactionStarted?.Invoke", 2 },
                    { "NetworkSkillExecuted?.Invoke", 1 },
                    { "NetworkHitRegistered?.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("/Skill.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkOnHitValidator.Invoke", 1 },
                    { "NetworkResolveDefenseDirect(", 1 }
                };
            }


            if (relativePath.EndsWith("AttackSkill.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkStrikeValidator.Invoke", 1 },
                    { "NetworkStrikeProbe?.Invoke", 1 },
                    { "NetworkSkillStarted?.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("TShieldResponse.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkReactionResolved?.Invoke", 1 },
                    { "ReactionOutput reactionOutput = this.m_Reaction.Run", 1 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchRegexTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("/Skill.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { @"(?s)\b(public|internal)\s+void\s+OnHit\s*\(\s*Args\s+args\s*,\s*Vector3\s+point\s*,\s*Vector3\s+direction\s*\)\s*\{.*?NetworkOnHitValidator\.Invoke", 1 },
                    { @"\bpublic\s+BlockType\s+NetworkResolveDefenseDirect\s*\(\s*Character\s+attacker\s*,\s*Character\s+target\s*,\s*Vector3\s+point\s*,\s*Vector3\s+targetLocalDirection\s*,\s*float\s+power\s*\)", 1 }
                };
            }


            if (relativePath.EndsWith("AttackSkill.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    {
                        @"(?s)if\s*\(\s*!this\.ComboSkill\.CanHit\s*\(\s*hitArgs\s*\)\s*\).*?NetworkStrikeValidator\.Invoke\s*\(\s*this\.Attacks\.MeleeStance\s*,\s*hit\s*,\s*this\.ComboSkill\s*\)",
                        1
                    },
                    {
                        @"public\s+static\s+Action\s*<\s*MeleeStance\s*,\s*Skill\s*,\s*int\s*,\s*int\s*>\s+NetworkStrikeProbe",
                        1
                    },
                    {
                        @"public\s+static\s+Action\s*<\s*MeleeStance\s*,\s*MeleeWeapon\s*,\s*Skill\s*,\s*int\s*>\s+NetworkSkillStarted",
                        1
                    },
                    {
                        @"(?s)this\.m_Duration\s*=\s*this\.ComboSkill\.GetDuration\s*\(\s*this\.m_Speed\s*,\s*this\.m_Args\s*\)\s*;.*?NetworkSkillStarted\?\.Invoke\s*\(\s*this\.Attacks\.MeleeStance\s*,\s*this\.Attacks\.Weapon\s*,\s*this\.ComboSkill\s*,\s*this\.Attacks\.ComboId\s*\)\s*;.*?this\.ComboSkill\.Run\s*\(",
                        1
                    },
                    {
                        @"(?s)hits\.Add\s*\(\s*candidate\s*\)\s*;.*?NetworkStrikeProbe\?\.Invoke\s*\(\s*this\.Attacks\.MeleeStance\s*,\s*this\.ComboSkill\s*,\s*this\.m_Strikers\.Count\s*,\s*hits\.Count\s*\)\s*;.*?foreach\s*\(\s*StrikeOutput\s+hit\s+in\s+hits\s*\)",
                        1
                    }
                };
            }

            if (relativePath.EndsWith("TShieldResponse.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    {
                        @"public\s+static\s+Action\s*<\s*TShieldResponse\s*,\s*Args\s*,\s*ShieldOutput\s*,\s*ReactionInput\s*,\s*Reaction\s*,\s*ReactionOutput\s*>\s+NetworkReactionResolved",
                        1
                    },
                    { @"NetworkReactionResolved\?\.Invoke\s*\(", 1 }
                };
            }

            return base.GetRequiredPatchRegexTokenCounts(relativePath);
        }

        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);

            ExistingPatchState existingPatchState = PrepareContentForPatch(relativePath, ref content);
            if (existingPatchState == ExistingPatchState.SkipAlreadyPatched) return true;
            if (existingPatchState == ExistingPatchState.Failed) return false;

            if (relativePath.EndsWith("MeleeStance.cs"))
            {
                return PatchMeleeStance(relativePath, content);
            }
            else if (relativePath.EndsWith("AttackSkill.cs"))
            {
                return PatchAttackSkill(relativePath, content);
            }
            else if (relativePath.EndsWith("/Skill.cs"))
            {
                return PatchSkill(relativePath, content);
            }
            else if (relativePath.EndsWith("TShieldResponse.cs"))
            {
                return PatchShieldResponse(relativePath, content);
            }
            else if (relativePath.EndsWith("SkillEditor.cs"))
            {
                return PatchSkillEditor(relativePath, content);
            }

            return false;
        }

        private bool PatchMeleeStance(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Melee
{
    public class MeleeStance : TStance
    {";

            string patchedUsings = @"using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Melee > Unpatch to restore.

namespace GameCreator.Runtime.Melee
{
    public class MeleeStance : TStance
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        /// <summary>Validates if a charge input should proceed locally.</summary>
        public static Func<MeleeStance, MeleeKey, bool> NetworkInputChargeValidator;

        /// <summary>Validates if an execute input should proceed locally.</summary>
        public static Func<MeleeStance, MeleeKey, bool> NetworkInputExecuteValidator;

        /// <summary>Validates if a skill should proceed locally.</summary>
        public static Func<MeleeStance, MeleeWeapon, Skill, GameObject, bool> NetworkPlaySkillValidator;

        /// <summary>Validates if a reaction should proceed locally.</summary>
        public static Func<MeleeStance, GameObject, ReactionInput, IReaction, bool> NetworkPlayReactionValidator;

        /// <summary>Called when a skill is executed (for network sync).</summary>
        public static Action<MeleeStance, MeleeWeapon, Skill, GameObject> NetworkSkillExecuted;

        /// <summary>Called when a hit is registered (for damage validation).</summary>
        public static Action<MeleeStance, Character, ReactionInput, Skill> NetworkHitRegistered;

        /// <summary>Called immediately after GC2 enters an actual Reaction phase.</summary>
        public static Action<MeleeStance, GameObject, ReactionInput, IReaction> NetworkReactionStarted;

        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkInputChargeValidator != null;

        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalUsings, patchedUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in MeleeStance.cs.");
                return false;
            }

            // Patch InputCharge method
            string originalInputCharge = @"        public void InputCharge(MeleeKey key)
        {
            this.m_Input.InputCharge(key);
        }";

            string patchedInputCharge = @"        public void InputCharge(MeleeKey key)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkInputChargeValidator != null && !NetworkInputChargeValidator.Invoke(this, key))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_Input.InputCharge(key);
        }

        // [GC2_NETWORK_PATCH] Server-side direct input charge (bypasses validation)
        public void InputChargeDirect(MeleeKey key)
        {
            this.m_Input.InputCharge(key);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalInputCharge, patchedInputCharge))
            {
                Debug.LogError("[GC2 Networking] Could not find expected InputCharge method in MeleeStance.cs.");
                return false;
            }

            // Patch InputExecute method
            string originalInputExecute = @"        public void InputExecute(MeleeKey key)
        {
            this.m_Input.InputExecute(key);
        }";

            string patchedInputExecute = @"        public void InputExecute(MeleeKey key)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkInputExecuteValidator != null && !NetworkInputExecuteValidator.Invoke(this, key))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_Input.InputExecute(key);
        }

        // [GC2_NETWORK_PATCH] Server-side direct input execute (bypasses validation)
        public void InputExecuteDirect(MeleeKey key)
        {
            this.m_Input.InputExecute(key);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalInputExecute, patchedInputExecute))
            {
                Debug.LogError("[GC2 Networking] Could not find expected InputExecute method in MeleeStance.cs.");
                return false;
            }

            // Patch PlaySkill method
            string originalPlaySkill = @"        public void PlaySkill(MeleeWeapon weapon, Skill skill, GameObject target)
        {
            this.m_Attacks.ToSkill(weapon, skill, target);
            this.m_Input.Cancel();
        }";

            string patchedPlaySkill = @"        public void PlaySkill(MeleeWeapon weapon, Skill skill, GameObject target)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkPlaySkillValidator != null && !NetworkPlaySkillValidator.Invoke(this, weapon, skill, target))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_Attacks.ToSkill(weapon, skill, target);
            this.m_Input.Cancel();

            // [GC2_NETWORK_PATCH] Notify network layer
            NetworkSkillExecuted?.Invoke(this, weapon, skill, target);
            // [GC2_NETWORK_PATCH_END]
        }

        // [GC2_NETWORK_PATCH] Server-side direct play skill (bypasses validation)
        public void PlaySkillDirect(MeleeWeapon weapon, Skill skill, GameObject target)
        {
            this.m_Attacks.ToSkill(weapon, skill, target);
            this.m_Input.Cancel();
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalPlaySkill, patchedPlaySkill))
            {
                Debug.LogError("[GC2 Networking] Could not find expected PlaySkill method in MeleeStance.cs.");
                return false;
            }

            // Patch PlayReaction method
            string originalPlayReaction = @"        public void PlayReaction(GameObject from, ReactionInput input, IReaction withReaction, bool canFallback)
        {
            this.m_Attacks.ToReact(from, withReaction, input, canFallback);
        }";

            string patchedPlayReaction = @"        public void PlayReaction(GameObject from, ReactionInput input, IReaction withReaction, bool canFallback)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkPlayReactionValidator != null && !NetworkPlayReactionValidator.Invoke(this, from, input, withReaction))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_Attacks.ToReact(from, withReaction, input, canFallback);

            // [GC2_NETWORK_PATCH] Emit from the actual GC2 phase transition, not a frame poll.
            if (this.m_Attacks.Phase == MeleePhase.Reaction)
            {
                NetworkReactionStarted?.Invoke(this, from, input, withReaction);
            }
            // [GC2_NETWORK_PATCH_END]
        }

        // [GC2_NETWORK_PATCH] Server-side direct play reaction (bypasses validation)
        public void PlayReactionDirect(GameObject from, ReactionInput input, IReaction withReaction, bool canFallback)
        {
            this.m_Attacks.ToReact(from, withReaction, input, canFallback);

            if (this.m_Attacks.Phase == MeleePhase.Reaction)
            {
                NetworkReactionStarted?.Invoke(this, from, input, withReaction);
            }
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalPlayReaction, patchedPlayReaction))
            {
                Debug.LogError("[GC2 Networking] Could not find expected PlayReaction method in MeleeStance.cs.");
                return false;
            }

            // Patch Hit method to notify network
            string originalHit = @"        public void Hit(Character attacker, ReactionInput input, Skill skill)
        {
            if (skill.SyncReaction != null) return;
            Args args = new Args(this.Character, attacker);

            bool isAttacking =
                this.m_Attacks.Phase == MeleePhase.Anticipation ||
                this.m_Attacks.Phase == MeleePhase.Strike       ||
                this.m_Attacks.Phase == MeleePhase.Recovery;

            if (isAttacking)
            {
                float damage = skill.GetPoiseDamage(args);
                bool poiseBroken = this.Character.Combat.Poise.Damage(damage);

                if (!poiseBroken) return;
            }

            this.PlayReaction(attacker.gameObject, input, null, true);
        }";

            string patchedHit = @"        public void Hit(Character attacker, ReactionInput input, Skill skill)
        {
            if (skill.SyncReaction != null) return;

            // [GC2_NETWORK_PATCH] Notify network for damage validation
            NetworkHitRegistered?.Invoke(this, attacker, input, skill);
            // [GC2_NETWORK_PATCH_END]

            Args args = new Args(this.Character, attacker);

            bool isAttacking =
                this.m_Attacks.Phase == MeleePhase.Anticipation ||
                this.m_Attacks.Phase == MeleePhase.Strike       ||
                this.m_Attacks.Phase == MeleePhase.Recovery;

            if (isAttacking)
            {
                float damage = skill.GetPoiseDamage(args);
                bool poiseBroken = this.Character.Combat.Poise.Damage(damage);

                if (!poiseBroken) return;
            }

            this.PlayReaction(attacker.gameObject, input, null, true);
        }

        // [GC2_NETWORK_PATCH] Server-side direct hit (bypasses validation)
        public void HitDirect(Character attacker, ReactionInput input, Skill skill)
        {
            if (skill.SyncReaction != null) return;
            Args args = new Args(this.Character, attacker);

            bool isAttacking =
                this.m_Attacks.Phase == MeleePhase.Anticipation ||
                this.m_Attacks.Phase == MeleePhase.Strike       ||
                this.m_Attacks.Phase == MeleePhase.Recovery;

            if (isAttacking)
            {
                float damage = skill.GetPoiseDamage(args);
                bool poiseBroken = this.Character.Combat.Poise.Damage(damage);
                if (!poiseBroken) return;
            }

            this.PlayReactionDirect(attacker.gameObject, input, null, true);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalHit, patchedHit))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Hit method in MeleeStance.cs.");
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchAttackSkill(string relativePath, string content)
        {
            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Melee"))
            {
                return false;
            }

            const string staticHook = @"
        // [GC2_NETWORK_PATCH] Intercept a validated strike output before native gameplay.
        public static Func<MeleeStance, StrikeOutput, Skill, bool> NetworkStrikeValidator;

        // Reports an empty filtered candidate pass so the networking layer can restore a
        // CharacterController query proxy before the following strike frame.
        public static Action<MeleeStance, Skill, int, int> NetworkStrikeProbe;

        // Reports the exact GC2 skill state that entered before its instructions or motion run.
        public static Action<MeleeStance, MeleeWeapon, Skill, int> NetworkSkillStarted;
        // [GC2_NETWORK_PATCH_END]
";
            if (!TryInsertAfterTypeOpeningBrace(
                    ref content,
                    "AttackSkill",
                    "NetworkStrikeValidator",
                    staticHook,
                    out string hookFailure))
            {
                Debug.LogError($"[GC2 Networking] Could not patch AttackSkill hook: {hookFailure}");
                return false;
            }

            string originalSkillRun = @"            this.m_Speed = this.ComboSkill.GetSpeed(this.m_Args);
            this.m_Duration = this.ComboSkill.GetDuration(this.m_Speed, this.m_Args);

            this.ComboSkill.Run(";

            string patchedSkillRun = @"            this.m_Speed = this.ComboSkill.GetSpeed(this.m_Args);
            this.m_Duration = this.ComboSkill.GetDuration(this.m_Speed, this.m_Args);

            // [GC2_NETWORK_PATCH] Route the resolved skill identity at the actual state entry.
            // This is the last safe point before skill instructions and root motion can run.
            NetworkSkillStarted?.Invoke(
                this.Attacks.MeleeStance,
                this.Attacks.Weapon,
                this.ComboSkill,
                this.Attacks.ComboId
            );
            // [GC2_NETWORK_PATCH_END]

            this.ComboSkill.Run(";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalSkillRun, patchedSkillRun))
            {
                Debug.LogError(
                    "[GC2 Networking] Could not find the skill-entry run block in AttackSkill.cs.");
                return false;
            }

            string originalHitIteration = @"            }

            foreach (StrikeOutput hit in hits)";

            string patchedHitIteration = @"            }

            // [GC2_NETWORK_PATCH] Allow recovery of a missing physics query proxy after all
            // candidates have been filtered and before any hit gameplay is processed.
            NetworkStrikeProbe?.Invoke(
                this.Attacks.MeleeStance,
                this.ComboSkill,
                this.m_Strikers.Count,
                hits.Count
            );
            // [GC2_NETWORK_PATCH_END]

            foreach (StrikeOutput hit in hits)";

            if (!TryReplaceWithFlexibleWhitespace(
                    ref content,
                    originalHitIteration,
                    patchedHitIteration))
            {
                Debug.LogError(
                    "[GC2 Networking] Could not find the filtered hit iteration in AttackSkill.cs.");
                return false;
            }

            string originalCanHit = @"                if (!this.ComboSkill.CanHit(hitArgs))
                {
                    this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());
                    continue;
                }";

            string patchedCanHit = originalCanHit + @"

                // [GC2_NETWORK_PATCH] Route the complete strike before triggers, shields,
                // Skill.OnHit instructions, damage, poise, or target reactions can run.
                if (NetworkStrikeValidator != null &&
                    !NetworkStrikeValidator.Invoke(this.Attacks.MeleeStance, hit, this.ComboSkill))
                {
                    this.m_HitsBuffer.Add(hit.GameObject.GetInstanceID());
                    continue;
                }
                // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalCanHit, patchedCanHit))
            {
                Debug.LogError(
                    "[GC2 Networking] Could not find the post-CanHit strike block in AttackSkill.cs.");
                return false;
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchSkill(string relativePath, string content)
        {
            // Find namespace anchor with regex to support spacing/access variations.
            Match namespaceMatch = Regex.Match(content, @"(?m)^namespace\s+GameCreator\.Runtime\.Melee\b");
            if (!namespaceMatch.Success)
            {
                Debug.LogError("[GC2 Networking] Could not find namespace in Skill.cs");
                return false;
            }

            // Insert patch marker before namespace
            string patchHeader = PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Melee > Unpatch to restore.

";
            content = content.Insert(namespaceMatch.Index, patchHeader);

            // Add static hooks after Skill class opening brace.
            if (!content.Contains("NetworkOnHitValidator"))
            {
                Match classMatch = Regex.Match(
                    content,
                    @"(?m)^(\s*)(public\s+)?(?:(?:sealed|partial|abstract)\s+)*class\s+Skill\b[^{]*\{");
                if (!classMatch.Success)
                {
                    Debug.LogError("[GC2 Networking] Could not find Skill class declaration in Skill.cs.");
                    return false;
                }

                string staticHooks = @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking

        /// <summary>Called before OnHit is executed for network validation.</summary>
        public static Func<Skill, Args, Vector3, Vector3, bool> NetworkOnHitValidator;

        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkOnHitValidator != null;

        // [GC2_NETWORK_PATCH_END]
";
                int classInsertIndex = classMatch.Index + classMatch.Length;
                content = content.Insert(classInsertIndex, staticHooks);
            }

            // Patch OnHit method if found (supports internal/public + whitespace variants)
            if (!content.Contains("NetworkOnHitValidator.Invoke"))
            {
                const string onHitPattern = @"(?m)^(\s*)(public|internal)\s+void\s+OnHit\s*\(\s*Args\s+args\s*,\s*Vector3\s+point\s*,\s*Vector3\s+direction\s*\)\s*\{";
                Match onHitMatch = Regex.Match(content, onHitPattern);
                if (onHitMatch.Success)
                {
                    string indent = onHitMatch.Groups[1].Value + "    ";
                    string validationCheck =
                        "\n" +
                        $"{indent}// [GC2_NETWORK_PATCH] Server authority check\n" +
                        $"{indent}if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(this, args, point, direction))\n" +
                        $"{indent}{{\n" +
                        $"{indent}    return; // Network will handle this\n" +
                        $"{indent}}}\n" +
                        $"{indent}// [GC2_NETWORK_PATCH_END]\n";

                    int insertIndex = onHitMatch.Index + onHitMatch.Length;
                    content = content.Insert(insertIndex, validationCheck);
                }
                else
                {
                    Debug.LogError("[GC2 Networking] Could not find OnHit method in Skill.cs.");
                    return false;
                }
            }

            if (!content.Contains("NetworkResolveDefenseDirect("))
            {
                const string internalCallbacksAnchor =
                    "        // INTERNAL CALLBACKS: --------------------------------------------------------------------";
                int helperInsertIndex = content.IndexOf(
                    internalCallbacksAnchor,
                    System.StringComparison.Ordinal);
                if (helperInsertIndex < 0)
                {
                    Debug.LogError(
                        "[GC2 Networking] Could not find the internal callbacks anchor in Skill.cs.");
                    return false;
                }

                const string directDefenseHelper = @"
        // [GC2_NETWORK_PATCH] Server-side authored defense resolution (bypasses network hooks).
        // Damage and the default target reaction remain owned by the networking manager.
        public BlockType NetworkResolveDefenseDirect(
            Character attacker,
            Character target,
            Vector3 point,
            Vector3 targetLocalDirection,
            float power)
        {
            if (attacker == null || target == null) return BlockType.None;

            Args hitArgs = new Args(attacker.gameObject, target.gameObject);
            // Preserve MeleeDirection.None as Vector3.zero, matching AttackSkill exactly.
            Vector3 direction = targetLocalDirection;
            ShieldInput shieldInput = new ShieldInput(direction, point, power);
            ReactionInput reactionInput = new ReactionInput(direction, power);

            IShield shield = target.Combat.GetBlock(
                shieldInput,
                hitArgs,
                this.CanBlock(hitArgs),
                this.CanParry(hitArgs),
                out ShieldOutput shieldOutput
            );

            switch (shieldOutput.Type)
            {
                case BlockType.Block: this.OnBlocked(hitArgs, reactionInput); break;
                case BlockType.Parry: this.OnParried(hitArgs, reactionInput); break;
            }

            if (shield != null)
            {
                Args blockArgs = new Args(hitArgs.Target, hitArgs.Self);
                shield.OnDefend(blockArgs, shieldOutput, reactionInput);
            }

            return shieldOutput.Type;
        }
        // [GC2_NETWORK_PATCH_END]

";
                content = content.Insert(helperInsertIndex, directDefenseHelper);
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchShieldResponse(string relativePath, string content)
        {
            if (!EnsurePatchMarkerBeforeNamespace(ref content, "GameCreator.Runtime.Melee"))
            {
                return false;
            }

            if (!content.Contains("NetworkReactionResolved"))
            {
                const string classAnchor =
                    "    public abstract class TShieldResponse\n" +
                    "    {";
                const string patchedClassAnchor =
                    "    public abstract class TShieldResponse\n" +
                    "    {\n" +
                    "        // [GC2_NETWORK_PATCH] Reports the exact authored shield reaction after it runs.\n" +
                    "        public static Action<TShieldResponse, Args, ShieldOutput, ReactionInput, Reaction, ReactionOutput> NetworkReactionResolved;\n" +
                    "        // [GC2_NETWORK_PATCH_END]";

                if (!TryReplaceWithFlexibleWhitespace(ref content, classAnchor, patchedClassAnchor))
                {
                    Debug.LogError(
                        "[GC2 Networking] Could not add the shield reaction callback to TShieldResponse.cs.");
                    return false;
                }
            }

            if (!content.Contains("ReactionOutput reactionOutput = this.m_Reaction.Run"))
            {
                const string reactionRun =
                    "                Character character = args.Self.Get<Character>();\n" +
                    "                this.m_Reaction.Run(character, args, reactionInput);";
                const string patchedReactionRun =
                    "                Character character = args.Self.Get<Character>();\n" +
                    "                ReactionOutput reactionOutput = this.m_Reaction.Run(character, args, reactionInput);\n\n" +
                    "                // [GC2_NETWORK_PATCH] Shield responses bypass MeleeStance, so report the exact authored result.\n" +
                    "                NetworkReactionResolved?.Invoke(\n" +
                    "                    this, args, shieldOutput, reactionInput, this.m_Reaction, reactionOutput);\n" +
                    "                // [GC2_NETWORK_PATCH_END]";

                if (!TryReplaceWithFlexibleWhitespace(ref content, reactionRun, patchedReactionRun))
                {
                    Debug.LogError(
                        "[GC2 Networking] Could not capture authored shield reactions in TShieldResponse.cs.");
                    return false;
                }
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchSkillEditor(string relativePath, string content)
        {
            // Fix obsolete API: EditorUtility.InstanceIDToObject(...) -> EditorUtility.EntityIdToObject(...)
            // Handle both legacy (instanceID) and modern (entityId) parameter names.
            const string obsoleteCallPrefix = "EditorUtility.InstanceIDToObject(";
            const string fixedCallPrefix = "EditorUtility.EntityIdToObject(";

            if (content.Contains(fixedCallPrefix))
            {
                Debug.Log($"[GC2 Networking] {relativePath} already uses EntityIdToObject.");
                return true;
            }

            if (!content.Contains(obsoleteCallPrefix))
            {
                Debug.Log($"[GC2 Networking] {relativePath} has no OnOpenAsset API migration needed.");
                return true;
            }

            content = content.Replace(obsoleteCallPrefix, fixedCallPrefix);

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Migrated OnOpenAsset API usage in {relativePath}");
            return true;
        }
    }
}
