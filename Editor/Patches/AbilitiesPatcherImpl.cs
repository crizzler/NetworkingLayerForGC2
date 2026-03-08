using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for DaimahouGames Abilities system.
    /// </summary>
    public class AbilitiesPatcherImpl : GC2PatcherBase
    {
        public override string ModuleName => "Abilities";
        public override string PatchVersion => "2.1.0-abilities";
        public override string DisplayName => "Abilities (DaimahouGames)";
        
        public override string PatchDescription =>
            "This will modify the DaimahouGames Abilities source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "Caster.Cast() will check for network authority before executing.\n" +
            "Learn/UnLearn methods will have network hooks.";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/DaimahouGames/Packages/Abilities/Runtime/Pawns/Features/Caster.cs"
        };

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("Caster.cs"))
            {
                return new[]
                {
                    "NetworkCastValidator",
                    "NetworkLearnValidator",
                    "NetworkUnLearnValidator",
                    "NetworkCastCompleted",
                    "LearnDirect(",
                    "UnLearnDirect("
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("Caster.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkCastValidator.Invoke", 1 },
                    { "NetworkLearnValidator.Invoke", 1 },
                    { "NetworkUnLearnValidator.Invoke", 1 },
                    { "NetworkCastCompleted?.Invoke", 1 }
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
            
            // Add patch marker and network hooks after usings
            string originalUsings = @"using System;
	using System.Collections.Generic;
using System.Threading.Tasks;
using DaimahouGames.Runtime.Core;
using DaimahouGames.Runtime.Core.Common;
using DaimahouGames.Runtime.Pawns;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace DaimahouGames.Runtime.Abilities
{";

            string patchedUsings = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaimahouGames.Runtime.Core;
using DaimahouGames.Runtime.Core.Common;
using DaimahouGames.Runtime.Pawns;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Abilities > Unpatch to restore.

namespace DaimahouGames.Runtime.Abilities
{";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected using statements in Caster.cs."))
            {
                return false;
            }
            
            // Add static network authority hooks after class declaration (regex anchor for source variants).
            if (!content.Contains("NetworkCastValidator"))
            {
                Match casterClassMatch = Regex.Match(
                    content,
                    @"(?m)^(\s*)(public\s+)?(?:(?:sealed|partial|abstract)\s+)*class\s+Caster\b[^{]*\{");
                if (!casterClassMatch.Success)
                {
                    Debug.LogError("[GC2 Networking] Could not find Caster class declaration in Caster.cs.");
                    return false;
                }

                string staticHooks = @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>When set, validates if a cast should proceed locally.</summary>
        public static Func<Caster, Ability, ExtendedArgs, bool> NetworkCastValidator;
        
        /// <summary>When set, validates if learn should proceed locally.</summary>
        public static Func<Caster, Ability, int, bool> NetworkLearnValidator;
        
        /// <summary>When set, validates if unlearn should proceed locally.</summary>
        public static Func<Caster, Ability, bool> NetworkUnLearnValidator;
        
        /// <summary>Called after a cast completes.</summary>
        public static Action<Caster, Ability, bool> NetworkCastCompleted;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkCastValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";
                int insertIndex = casterClassMatch.Index + casterClassMatch.Length;
                content = content.Insert(insertIndex, staticHooks);
            }
            
            // Patch the Cast method
            string originalCast = @"        public async Task<bool> Cast(Ability ability, ExtendedArgs args)
        {
            if (!CanCancel()) return false;

            CastAbilityMessage.Send(ability);
            
            args.ChangeSelf(GameObject);
            args.Set(new AbiltySource(GameObject));
            args.Set(GetRuntimeAbility(ability));

            var success = m_CastState.TryEnter(args);

            await m_CastState.WaitUntilComplete();
            return success;
        }";
            
            string patchedCast = @"        public async Task<bool> Cast(Ability ability, ExtendedArgs args)
        {
            if (!CanCancel()) return false;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkCastValidator != null && !NetworkCastValidator.Invoke(this, ability, args))
            {
                return false; // Network will handle this cast
            }
            // [GC2_NETWORK_PATCH_END]

            CastAbilityMessage.Send(ability);
            
            args.ChangeSelf(GameObject);
            args.Set(new AbiltySource(GameObject));
            args.Set(GetRuntimeAbility(ability));

            var success = m_CastState.TryEnter(args);

            await m_CastState.WaitUntilComplete();
            
            // [GC2_NETWORK_PATCH] Notify completion
            NetworkCastCompleted?.Invoke(this, ability, success);
            // [GC2_NETWORK_PATCH_END]
            
            return success;
        }";

            if (!TryReplaceRequired(
                    ref content,
                    originalCast,
                    patchedCast,
                    "[GC2 Networking] Could not find expected Cast method in Caster.cs."))
            {
                return false;
            }
            
            // Patch Learn method
            string originalLearn = @"        public void Learn(Ability ability, int slot)
        {
            if (ability == null) return;
            if (slot < 0 || slot >= m_AbilitySlots.Count) return;
            if (this.m_AbilitySlots[slot].Ability == ability) return;

            m_AbilitySlots[slot] = new KnownAbility(ability);
            LearnAbilityMessage.Send(ability);
        }";
            
            string patchedLearn = @"        public void Learn(Ability ability, int slot)
        {
            if (ability == null) return;
            if (slot < 0 || slot >= m_AbilitySlots.Count) return;
            if (this.m_AbilitySlots[slot].Ability == ability) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkLearnValidator != null && !NetworkLearnValidator.Invoke(this, ability, slot))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            m_AbilitySlots[slot] = new KnownAbility(ability);
            LearnAbilityMessage.Send(ability);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct learn (bypasses validation)
        public void LearnDirect(Ability ability, int slot)
        {
            if (ability == null) return;
            if (slot < 0 || slot >= m_AbilitySlots.Count) return;
            m_AbilitySlots[slot] = new KnownAbility(ability);
            LearnAbilityMessage.Send(ability);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalLearn,
                    patchedLearn,
                    "[GC2 Networking] Could not find expected Learn method in Caster.cs."))
            {
                return false;
            }
            
            // Patch UnLearn method
            string originalUnLearn = @"        public void UnLearn(Ability ability)
        {
            if (ability == null) return;

            var slot = this.m_AbilitySlots.FindIndex(x => x.Ability == ability);
            if (slot < 0) return;
            
            if (this.m_AbilitySlots[slot].Ability != ability) return;
            
            this.m_AbilitySlots[slot] = KnownAbility.None;
            UnLearnAbilityMessage.Send(ability);
        }";
            
            string patchedUnLearn = @"        public void UnLearn(Ability ability)
        {
            if (ability == null) return;

            var slot = this.m_AbilitySlots.FindIndex(x => x.Ability == ability);
            if (slot < 0) return;
            
            if (this.m_AbilitySlots[slot].Ability != ability) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkUnLearnValidator != null && !NetworkUnLearnValidator.Invoke(this, ability))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.m_AbilitySlots[slot] = KnownAbility.None;
            UnLearnAbilityMessage.Send(ability);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct unlearn (bypasses validation)
        public void UnLearnDirect(Ability ability)
        {
            if (ability == null) return;
            var slot = this.m_AbilitySlots.FindIndex(x => x.Ability == ability);
            if (slot < 0) return;
            this.m_AbilitySlots[slot] = KnownAbility.None;
            UnLearnAbilityMessage.Send(ability);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalUnLearn,
                    patchedUnLearn,
                    "[GC2 Networking] Could not find expected UnLearn method in Caster.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Successfully patched {relativePath}");
            
            return true;
        }
    }
}
