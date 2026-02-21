using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Melee module.
    /// Adds network validation hooks to MeleeStance for server-authoritative combat.
    /// </summary>
    public class MeleePatcher : GC2PatcherBase
    {
        public override string ModuleName => "Melee";
        public override string PatchVersion => "1.0.0";
        public override string DisplayName => "Melee (Game Creator 2)";
        
        public override string PatchDescription =>
            "This will modify the Game Creator 2 Melee source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "MeleeStance.InputCharge/InputExecute will have network validation.\n" +
            "MeleeStance.PlaySkill/PlayReaction will have network hooks.\n" +
            "Skill.OnHit will be intercepted for damage validation.\n" +
            "Also fixes obsolete API warning in SkillEditor.cs.";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Melee/Runtime/Classes/Stance/MeleeStance.cs",
            "Plugins/GameCreator/Packages/Melee/Runtime/ScriptableObjects/Skill.cs",
            "Plugins/GameCreator/Packages/Melee/Editor/Editors/SkillEditor.cs"
        };
        
        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);
            
            // Check if already patched
            if (content.Contains(PatchMarker))
            {
                Debug.LogWarning($"[GC2 Networking] {relativePath} already contains patch marker.");
                return true;
            }
            
            if (relativePath.EndsWith("MeleeStance.cs"))
            {
                return PatchMeleeStance(relativePath, content);
            }
            else if (relativePath.EndsWith("Skill.cs"))
            {
                return PatchSkill(relativePath, content);
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
// Use Tools > Game Creator 2 Networking > Patches > Melee > Unpatch to restore.

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
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkInputChargeValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!content.Contains(originalUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in MeleeStance.cs.");
                return false;
            }
            
            content = content.Replace(originalUsings, patchedUsings);
            
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

            if (!content.Contains(originalInputCharge))
            {
                Debug.LogError("[GC2 Networking] Could not find expected InputCharge method in MeleeStance.cs.");
                return false;
            }
            
            content = content.Replace(originalInputCharge, patchedInputCharge);
            
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

            if (!content.Contains(originalInputExecute))
            {
                Debug.LogError("[GC2 Networking] Could not find expected InputExecute method in MeleeStance.cs.");
                return false;
            }
            
            content = content.Replace(originalInputExecute, patchedInputExecute);
            
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

            if (!content.Contains(originalPlaySkill))
            {
                Debug.LogError("[GC2 Networking] Could not find expected PlaySkill method in MeleeStance.cs.");
                return false;
            }
            
            content = content.Replace(originalPlaySkill, patchedPlaySkill);
            
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
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct play reaction (bypasses validation)
        public void PlayReactionDirect(GameObject from, ReactionInput input, IReaction withReaction, bool canFallback)
        {
            this.m_Attacks.ToReact(from, withReaction, input, canFallback);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalPlayReaction))
            {
                Debug.LogError("[GC2 Networking] Could not find expected PlayReaction method in MeleeStance.cs.");
                return false;
            }
            
            content = content.Replace(originalPlayReaction, patchedPlayReaction);
            
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

            if (!content.Contains(originalHit))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Hit method in MeleeStance.cs.");
                return false;
            }
            
            content = content.Replace(originalHit, patchedHit);
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchSkill(string relativePath, string content)
        {
            // Find the namespace
            string namespaceStart = "namespace GameCreator.Runtime.Melee";
            int namespaceIndex = content.IndexOf(namespaceStart);
            if (namespaceIndex < 0)
            {
                Debug.LogError("[GC2 Networking] Could not find namespace in Skill.cs");
                return false;
            }
            
            // Insert patch marker before namespace
            string patchHeader = PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Melee > Unpatch to restore.

";
            content = content.Insert(namespaceIndex, patchHeader);
            
            // Find OnHit method and add static hook if possible
            // Look for class declaration
            string classDecl = "public class Skill :";
            int classIndex = content.IndexOf(classDecl);
            if (classIndex > 0)
            {
                int braceIndex = content.IndexOf('{', classIndex);
                if (braceIndex > 0)
                {
                    string staticHooks = @"
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Called before OnHit is executed for network validation.</summary>
        public static Func<Skill, Args, Vector3, Vector3, bool> NetworkOnHitValidator;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkOnHitValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";
                    content = content.Insert(braceIndex + 1, staticHooks);
                }
            }
            
            // Patch OnHit method if found
            string onHitPattern = "public void OnHit(Args args, Vector3 point, Vector3 direction)";
            int onHitIndex = content.IndexOf(onHitPattern);
            if (onHitIndex > 0)
            {
                // Find the opening brace
                int braceIndex = content.IndexOf('{', onHitIndex);
                if (braceIndex > 0)
                {
                    string validationCheck = @"
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkOnHitValidator != null && !NetworkOnHitValidator.Invoke(this, args, point, direction))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
";
                    content = content.Insert(braceIndex + 1, validationCheck);
                }
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchSkillEditor(string relativePath, string content)
        {
            // Fix obsolete API: EditorUtility.InstanceIDToObject -> EditorUtility.EntityIdToObject
            // This is a GC2 bug that we fix as part of our patch
            
            string obsoleteCall = "EditorUtility.InstanceIDToObject(instanceID)";
            string fixedCall = "EditorUtility.EntityIdToObject(instanceID)";
            
            if (content.Contains(fixedCall))
            {
                // Already fixed
                Debug.Log($"[GC2 Networking] {relativePath} already has the API fix.");
                return true;
            }
            
            if (!content.Contains(obsoleteCall))
            {
                Debug.LogWarning($"[GC2 Networking] Could not find obsolete InstanceIDToObject call in {relativePath}.");
                return true; // Not a failure, just nothing to fix
            }
            
            content = content.Replace(obsoleteCall, fixedCall);
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Fixed obsolete API in {relativePath}");
            return true;
        }
    }
}
