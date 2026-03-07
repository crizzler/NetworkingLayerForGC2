using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Core module.
    /// Adds network validation hooks to critical character features for server-authoritative control.
    /// 
    /// Key patches:
    /// - Invincibility.Set() - Prevents clients from making themselves invincible
    /// - Poise.Damage/Set/Reset - Validates poise changes server-side
    /// - Combat.CurrentDefense - Validates defense changes
    /// - Jump.Do() - Validates jump attempts
    /// - Dash.Execute() - Validates dash attempts
    /// - Character.IsDead - Validates death/revive state changes
    /// </summary>
    public class CorePatcher : GC2PatcherBase
    {
        public override string ModuleName => "Core";
        public override string PatchVersion => "1.0.0";
        public override string DisplayName => "Core (Game Creator 2)";
        
        public override string PatchDescription =>
            "This will modify the Game Creator 2 Core source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "Changes:\n" +
            "• Invincibility.Set() will have network validation\n" +
            "• Poise manipulation methods will have network hooks\n" +
            "• Combat.CurrentDefense will have network validation\n" +
            "• Jump.Do() will have network validation\n" +
            "• Dash.Execute() will have network validation\n" +
            "• Character.IsDead will have network validation";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Core/Runtime/Characters/Features/Combat/Invincibility/Invincibility.cs",
            "Plugins/GameCreator/Packages/Core/Runtime/Characters/Features/Combat/Poise/Poise.cs",
            "Plugins/GameCreator/Packages/Core/Runtime/Characters/Features/Jump/Jump.cs",
            "Plugins/GameCreator/Packages/Core/Runtime/Characters/Features/Dash/Dash.cs",
            "Plugins/GameCreator/Packages/Core/Runtime/Characters/Components/Character.cs"
        };

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("Invincibility.cs"))
            {
                return new[] { "NetworkSetValidator", "SetDirect(" };
            }

            if (relativePath.EndsWith("Poise.cs"))
            {
                return new[] { "NetworkDamageValidator", "DamageDirect(", "ResetDirect(" };
            }

            if (relativePath.EndsWith("Jump.cs"))
            {
                return new[] { "NetworkJumpValidator", "DoDirect(" };
            }

            if (relativePath.EndsWith("Dash.cs"))
            {
                return new[] { "NetworkDashValidator", "ExecuteDirect(" };
            }

            if (relativePath.EndsWith("Character.cs"))
            {
                return new[] { "NetworkIsDeadValidator", "SetIsDeadDirect(" };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("Invincibility.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkSetValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("Poise.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkDamageValidator.Invoke", 1 },
                    { "NetworkSetValidator.Invoke", 1 },
                    { "NetworkResetValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("Jump.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkJumpValidator.Invoke", 1 },
                    { "NetworkJumpForceValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("Dash.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkDashValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("Character.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkIsDeadValidator.Invoke", 1 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }
        
        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);
            
            // Check if already patched
            if (ContainsPatchMarker(content))
            {
                Debug.LogWarning($"[GC2 Networking] {relativePath} already contains patch marker.");
                return true;
            }
            
            if (relativePath.EndsWith("Invincibility.cs"))
            {
                return PatchInvincibility(relativePath, content);
            }
            else if (relativePath.EndsWith("Poise.cs"))
            {
                return PatchPoise(relativePath, content);
            }
            else if (relativePath.EndsWith("Jump.cs"))
            {
                return PatchJump(relativePath, content);
            }
            else if (relativePath.EndsWith("Dash.cs"))
            {
                return PatchDash(relativePath, content);
            }
            else if (relativePath.EndsWith("Character.cs"))
            {
                return PatchCharacter(relativePath, content);
            }
            
            return false;
        }
        
        private bool PatchInvincibility(string relativePath, string content)
        {
            // Add using and patch marker
            string originalUsings = @"using System;

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Invincibility
    {";

            string patchedUsings = @"using System;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Core > Unpatch to restore.

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Invincibility
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if invincibility can be set locally.</summary>
        public static Func<Invincibility, float, bool> NetworkSetValidator;
        
        /// <summary>Called when invincibility is set (for network sync).</summary>
        public static Action<Invincibility, float> NetworkInvincibilitySet;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkSetValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected structure in Invincibility.cs."))
            {
                return false;
            }
            
            // Patch Set method
            string originalSet = @"        public void Set(float duration)
        {
            if (this.m_Character == null) return;

            this.m_InvincibleStartTime = this.m_Character.Time.Time;
            this.m_InvincibleUntil = Math.Max(
                this.m_InvincibleUntil,
                this.m_Character.Time.Time + duration
            );
        }";
            
            string patchedSet = @"        public void Set(float duration)
        {
            if (this.m_Character == null) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, duration))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            this.m_InvincibleStartTime = this.m_Character.Time.Time;
            this.m_InvincibleUntil = Math.Max(
                this.m_InvincibleUntil,
                this.m_Character.Time.Time + duration
            );
            
            // [GC2_NETWORK_PATCH] Notify network
            NetworkInvincibilitySet?.Invoke(this, duration);
            // [GC2_NETWORK_PATCH_END]
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct set (bypasses validation)
        public void SetDirect(float duration)
        {
            if (this.m_Character == null) return;

            this.m_InvincibleStartTime = this.m_Character.Time.Time;
            this.m_InvincibleUntil = Math.Max(
                this.m_InvincibleUntil,
                this.m_Character.Time.Time + duration
            );
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalSet,
                    patchedSet,
                    "[GC2 Networking] Could not find expected Set method in Invincibility.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchPoise(string relativePath, string content)
        {
            // Add using and patch marker
            string originalUsings = @"using System;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    public class Poise
    {";

            string patchedUsings = @"using System;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Core > Unpatch to restore.

namespace GameCreator.Runtime.Characters
{
    public class Poise
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if poise damage can be applied locally.</summary>
        public static Func<Poise, float, bool> NetworkDamageValidator;
        
        /// <summary>Validates if poise can be set locally.</summary>
        public static Func<Poise, float, bool> NetworkSetValidator;
        
        /// <summary>Validates if poise can be reset locally.</summary>
        public static Func<Poise, float, bool> NetworkResetValidator;
        
        /// <summary>Called when poise damage is applied (for network sync).</summary>
        public static Action<Poise, float, bool> NetworkPoiseDamaged;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkDamageValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected structure in Poise.cs."))
            {
                return false;
            }
            
            // Patch Damage method
            string originalDamage = @"        public bool Damage(float value)
        {
            this.Current -= Math.Min(this.Current, value);
            this.EventChange?.Invoke();

            if (this.Current > 0f) return false;
            
            this.EventPoiseBreak?.Invoke();
            return true;
        }";
            
            string patchedDamage = @"        public bool Damage(float value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkDamageValidator != null && !NetworkDamageValidator.Invoke(this, value))
            {
                return false; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.Current -= Math.Min(this.Current, value);
            this.EventChange?.Invoke();

            if (this.Current > 0f)
            {
                // [GC2_NETWORK_PATCH] Notify network
                NetworkPoiseDamaged?.Invoke(this, value, false);
                // [GC2_NETWORK_PATCH_END]
                return false;
            }
            
            this.EventPoiseBreak?.Invoke();
            
            // [GC2_NETWORK_PATCH] Notify network
            NetworkPoiseDamaged?.Invoke(this, value, true);
            // [GC2_NETWORK_PATCH_END]
            
            return true;
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct damage (bypasses validation)
        public bool DamageDirect(float value)
        {
            this.Current -= Math.Min(this.Current, value);
            this.EventChange?.Invoke();

            if (this.Current > 0f) return false;
            
            this.EventPoiseBreak?.Invoke();
            return true;
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalDamage,
                    patchedDamage,
                    "[GC2 Networking] Could not find expected Damage method in Poise.cs."))
            {
                return false;
            }
            
            // Patch Set method
            string originalSet = @"        public void Set(float value)
        {
            this.Current = Math.Clamp(value, 0f, this.Maximum);
        }";
            
            string patchedSetMethod = @"        public void Set(float value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, value))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.Current = Math.Clamp(value, 0f, this.Maximum);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct set (bypasses validation)
        public void SetDirect(float value)
        {
            this.Current = Math.Clamp(value, 0f, this.Maximum);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalSet,
                    patchedSetMethod,
                    "[GC2 Networking] Could not find expected Set method in Poise.cs."))
            {
                return false;
            }
            
            // Patch Reset method
            string originalReset = @"        public void Reset(float value)
        {
            this.Maximum = value;
            this.Current = value;
            
            this.EventChange?.Invoke();
        }";
            
            string patchedReset = @"        public void Reset(float value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkResetValidator != null && !NetworkResetValidator.Invoke(this, value))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.Maximum = value;
            this.Current = value;
            
            this.EventChange?.Invoke();
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct reset (bypasses validation)
        public void ResetDirect(float value)
        {
            this.Maximum = value;
            this.Current = value;
            
            this.EventChange?.Invoke();
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalReset,
                    patchedReset,
                    "[GC2 Networking] Could not find expected Reset method in Poise.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchJump(string relativePath, string content)
        {
            // Add using and patch marker
            string originalUsings = @"using System;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Jump
    {";

            string patchedUsings = @"using System;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Core > Unpatch to restore.

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Jump
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if jump can be executed locally.</summary>
        public static Func<Jump, bool> NetworkJumpValidator;
        
        /// <summary>Validates if jump with force can be executed locally.</summary>
        public static Func<Jump, float, bool> NetworkJumpForceValidator;
        
        /// <summary>Called when jump is executed (for network sync).</summary>
        public static Action<Jump, float> NetworkJumpExecuted;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkJumpValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected structure in Jump.cs."))
            {
                return false;
            }
            
            // Patch Do() method (no force)
            string originalDo = @"        public void Do()
        {
            this.EventAttemptJump?.Invoke();
            
            if (!this.CanJump()) return;
            this.m_Character.Motion.Jump();
        }";
            
            string patchedDo = @"        public void Do()
        {
            this.EventAttemptJump?.Invoke();
            
            if (!this.CanJump()) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkJumpValidator != null && !NetworkJumpValidator.Invoke(this))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.m_Character.Motion.Jump();
            
            // [GC2_NETWORK_PATCH] Notify network
            NetworkJumpExecuted?.Invoke(this, this.m_Character.Motion.JumpForce);
            // [GC2_NETWORK_PATCH_END]
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct jump (bypasses validation)
        public void DoDirect()
        {
            if (!this.CanJump()) return;
            this.m_Character.Motion.Jump();
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalDo,
                    patchedDo,
                    "[GC2 Networking] Could not find expected Do() method in Jump.cs."))
            {
                return false;
            }
            
            // Patch Do(float force) method
            string originalDoForce = @"        public void Do(float force)
        {
            this.EventAttemptJump?.Invoke();
            
            if (!this.CanJump()) return;
            this.m_Character.Motion.Jump(force);
        }";
            
            string patchedDoForce = @"        public void Do(float force)
        {
            this.EventAttemptJump?.Invoke();
            
            if (!this.CanJump()) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkJumpForceValidator != null && !NetworkJumpForceValidator.Invoke(this, force))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.m_Character.Motion.Jump(force);
            
            // [GC2_NETWORK_PATCH] Notify network
            NetworkJumpExecuted?.Invoke(this, force);
            // [GC2_NETWORK_PATCH_END]
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct jump with force (bypasses validation)
        public void DoDirect(float force)
        {
            if (!this.CanJump()) return;
            this.m_Character.Motion.Jump(force);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalDoForce,
                    patchedDoForce,
                    "[GC2 Networking] Could not find expected Do(force) method in Jump.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchDash(string relativePath, string content)
        {
            // Add using and patch marker
            string originalUsings = @"using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Dash
    {";

            string patchedUsings = @"using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Core > Unpatch to restore.

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Dash
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if dash can be executed locally.</summary>
        public static Func<Dash, Vector3, float, float, float, float, bool> NetworkDashValidator;
        
        /// <summary>Called when dash starts (for network sync).</summary>
        public static Action<Dash, Vector3, float, float, float, float> NetworkDashStarted;
        
        /// <summary>Called when dash finishes (for network sync).</summary>
        public static Action<Dash> NetworkDashFinished;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkDashValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected structure in Dash.cs."))
            {
                return false;
            }
            
            // Patch Execute method - need to find it by its signature
            string originalExecute = @"        public async Task Execute(Vector3 direction, float speed, float gravity, float duration, float fade)
        {
            this.m_HasDodged = false;

            float resetTime = this.m_LastDashFinishTime + this.m_Character.Motion.DashCooldown;
            this.m_NumDashes = this.m_Character.Time.Time < resetTime 
                ? this.m_NumDashes + 1
                : 1;
            
            this.IsDashing = true;";
            
            string patchedExecute = @"        public async Task Execute(Vector3 direction, float speed, float gravity, float duration, float fade)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkDashValidator != null && !NetworkDashValidator.Invoke(this, direction, speed, gravity, duration, fade))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.m_HasDodged = false;

            float resetTime = this.m_LastDashFinishTime + this.m_Character.Motion.DashCooldown;
            this.m_NumDashes = this.m_Character.Time.Time < resetTime 
                ? this.m_NumDashes + 1
                : 1;
            
            this.IsDashing = true;
            
            // [GC2_NETWORK_PATCH] Notify network
            NetworkDashStarted?.Invoke(this, direction, speed, gravity, duration, fade);
            // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalExecute,
                    patchedExecute,
                    "[GC2 Networking] Could not find expected Execute method in Dash.cs."))
            {
                return false;
            }
            
            // Patch OnDashFinish callback
            string originalFinish = @"        private void OnDashFinish(bool isComplete)
        {
            this.m_Character.Busy.RemoveLegsBusy();

            this.IsDashing = false;
            this.m_Character.Driver.RemoveGravityInfluence(GRAVITY_INFLUENCE_KEY);
            
            this.EventDashFinish?.Invoke();
        }";
            
            string patchedFinish = @"        private void OnDashFinish(bool isComplete)
        {
            this.m_Character.Busy.RemoveLegsBusy();

            this.IsDashing = false;
            this.m_Character.Driver.RemoveGravityInfluence(GRAVITY_INFLUENCE_KEY);
            
            this.EventDashFinish?.Invoke();
            
            // [GC2_NETWORK_PATCH] Notify network
            NetworkDashFinished?.Invoke(this);
            // [GC2_NETWORK_PATCH_END]
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct execute (bypasses validation)
        public async Task ExecuteDirect(Vector3 direction, float speed, float gravity, float duration, float fade)
        {
            this.m_HasDodged = false;

            float resetTime = this.m_LastDashFinishTime + this.m_Character.Motion.DashCooldown;
            this.m_NumDashes = this.m_Character.Time.Time < resetTime 
                ? this.m_NumDashes + 1
                : 1;
            
            this.IsDashing = true;
            if (!Mathf.Approximately(gravity, 1f))
            {
                this.m_Character.Driver.SetGravityInfluence(GRAVITY_INFLUENCE_KEY, gravity);
            }
            
            this.EventDashStart?.Invoke();

            direction = Vector3.Scale(direction, Vector3Plane.NormalUp);
            direction = direction.sqrMagnitude > float.Epsilon ? direction.normalized : Vector3.forward;
            
            this.m_Character.Motion.SetMotionTransient(direction, speed, duration, fade);
            
            TweenInput<float> input = new TweenInput<float>(
                0f, 1f, duration,
                HASH, Easing.Type.Linear
            );
            
            input.EventFinish += this.OnDashFinish;
            
            Tween.To(this.m_Character.gameObject, input);
            while (this.IsDashing && !ApplicationManager.IsExiting) await Task.Yield();
            
            this.m_LastDashFinishTime = this.m_Character.Time.Time;
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalFinish,
                    patchedFinish,
                    "[GC2 Networking] Could not find expected OnDashFinish method in Dash.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchCharacter(string relativePath, string content)
        {
            // Add patch marker after the using statements
            string originalUsings = @"using System;
using GameCreator.Runtime.Characters.Animim;
using GameCreator.Runtime.Common;
using UnityEngine;
using UnityEngine.Playables;

namespace GameCreator.Runtime.Characters
{";
            
            string patchedUsings = @"using System;
using GameCreator.Runtime.Characters.Animim;
using GameCreator.Runtime.Common;
using UnityEngine;
using UnityEngine.Playables;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Core > Unpatch to restore.

namespace GameCreator.Runtime.Characters
{";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected structure in Character.cs."))
            {
                return false;
            }
            
            // Find the class opening and add static hooks
            string classStart = @"    public class Character : MonoBehaviour, ISpatialHash
    {
        public enum MovementType
        {
            None,
            MoveToDirection,
            MoveToPosition,
        }";
            
            string patchedClassStart = @"    public class Character : MonoBehaviour, ISpatialHash
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if death state can be changed locally.</summary>
        public static Func<Character, bool, bool> NetworkIsDeadValidator;
        
        /// <summary>Called when death state changes (for network sync).</summary>
        public static Action<Character, bool> NetworkDeathStateChanged;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkIsDeadValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
        
        public enum MovementType
        {
            None,
            MoveToDirection,
            MoveToPosition,
        }";

            if (!TryReplaceRequired(
                    ref content,
                    classStart,
                    patchedClassStart,
                    "[GC2 Networking] Could not find expected class start in Character.cs."))
            {
                return false;
            }
            
            // Patch IsDead property setter
            string originalIsDead = @"        public bool IsDead
        {
            get => this.m_IsDead;
            set
            {
                if (this.m_IsDead == value) return;
                this.m_IsDead = value;

                switch (this.m_IsDead)
                {
                    case true:  this.EventDie?.Invoke(); break;
                    case false: this.EventRevive?.Invoke(); break;
                }
            }
        }";
            
            string patchedIsDead = @"        public bool IsDead
        {
            get => this.m_IsDead;
            set
            {
                if (this.m_IsDead == value) return;
                
                // [GC2_NETWORK_PATCH] Server authority check
                if (NetworkIsDeadValidator != null && !NetworkIsDeadValidator.Invoke(this, value))
                {
                    return; // Network will handle this
                }
                // [GC2_NETWORK_PATCH_END]
                
                this.m_IsDead = value;

                switch (this.m_IsDead)
                {
                    case true:  this.EventDie?.Invoke(); break;
                    case false: this.EventRevive?.Invoke(); break;
                }
                
                // [GC2_NETWORK_PATCH] Notify network
                NetworkDeathStateChanged?.Invoke(this, value);
                // [GC2_NETWORK_PATCH_END]
            }
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct death state set (bypasses validation)
        public void SetIsDeadDirect(bool value)
        {
            if (this.m_IsDead == value) return;
            this.m_IsDead = value;

            switch (this.m_IsDead)
            {
                case true:  this.EventDie?.Invoke(); break;
                case false: this.EventRevive?.Invoke(); break;
            }
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalIsDead,
                    patchedIsDead,
                    "[GC2 Networking] Could not find expected IsDead property in Character.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
    }
}
