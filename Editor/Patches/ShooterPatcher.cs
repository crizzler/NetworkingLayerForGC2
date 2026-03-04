using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Shooter module.
    /// Adds network validation hooks to ShooterStance and WeaponData for server-authoritative shooting.
    /// </summary>
    public class ShooterPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Shooter";
        public override string PatchVersion => "1.0.0";
        public override string DisplayName => "Shooter (Game Creator 2)";
        
        public override string PatchDescription =>
            "This will modify the Game Creator 2 Shooter source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "ShooterStance.PullTrigger/ReleaseTrigger will have network validation.\n" +
            "ShooterStance.Reload will have network hooks.\n" +
            "WeaponData.Shoot will be intercepted for hit validation.\n" +
            "Also fixes obsolete API warnings in editor files.";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Stances/ShooterStance.cs",
            "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Data/WeaponData.cs",
            "Plugins/GameCreator/Packages/Shooter/Editor/Editors/ReloadEditor.cs",
            "Plugins/GameCreator/Packages/Shooter/Editor/Editors/ShooterWeaponEditor.cs"
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
            
            if (relativePath.EndsWith("ShooterStance.cs"))
            {
                return PatchShooterStance(relativePath, content);
            }
            else if (relativePath.EndsWith("WeaponData.cs"))
            {
                return PatchWeaponData(relativePath, content);
            }
            else if (relativePath.EndsWith("ReloadEditor.cs") || relativePath.EndsWith("ShooterWeaponEditor.cs"))
            {
                return PatchEditorFile(relativePath, content);
            }
            
            return false;
        }
        
        private bool PatchShooterStance(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Shooter
{
    public class ShooterStance : TStance
    {";

            string patchedUsings = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Shooter > Unpatch to restore.

namespace GameCreator.Runtime.Shooter
{
    public class ShooterStance : TStance
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if pulling trigger should proceed locally.</summary>
        public static Func<ShooterStance, ShooterWeapon, bool> NetworkPullTriggerValidator;
        
        /// <summary>Validates if releasing trigger should proceed locally.</summary>
        public static Func<ShooterStance, ShooterWeapon, bool> NetworkReleaseTriggerValidator;
        
        /// <summary>Validates if reload should proceed locally.</summary>
        public static Func<ShooterStance, ShooterWeapon, bool> NetworkReloadValidator;
        
        /// <summary>Called when a shot is fired (for network sync).</summary>
        public static Action<ShooterStance, ShooterWeapon, Vector3, Vector3> NetworkShotFired;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkPullTriggerValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!content.Contains(originalUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in ShooterStance.cs.");
                return false;
            }
            
            content = content.Replace(originalUsings, patchedUsings);
            
            // Patch PullTrigger method
            string originalPullTrigger = @"        public void PullTrigger(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;";
            
            string patchedPullTrigger = @"        public void PullTrigger(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkPullTriggerValidator != null && !NetworkPullTriggerValidator.Invoke(this, shooterWeapon))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;";

            if (!content.Contains(originalPullTrigger))
            {
                Debug.LogError("[GC2 Networking] Could not find expected PullTrigger method in ShooterStance.cs.");
                return false;
            }
            
            content = content.Replace(originalPullTrigger, patchedPullTrigger);
            
            // Patch ReleaseTrigger method
            string originalReleaseTrigger = @"        public void ReleaseTrigger(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            
            weaponData.OnReleaseTrigger();
        }";
            
            string patchedReleaseTrigger = @"        public void ReleaseTrigger(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkReleaseTriggerValidator != null && !NetworkReleaseTriggerValidator.Invoke(this, shooterWeapon))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            
            weaponData.OnReleaseTrigger();
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct release trigger (bypasses validation)
        public void ReleaseTriggerDirect(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            weaponData.OnReleaseTrigger();
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalReleaseTrigger))
            {
                Debug.LogError("[GC2 Networking] Could not find expected ReleaseTrigger method in ShooterStance.cs.");
                return false;
            }
            
            content = content.Replace(originalReleaseTrigger, patchedReleaseTrigger);
            
            // Patch Reload method
            string originalReload = @"        public async Task Reload(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            
            await this.Reloading.Reload(shooterWeapon);
        }";
            
            string patchedReload = @"        public async Task Reload(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkReloadValidator != null && !NetworkReloadValidator.Invoke(this, shooterWeapon))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            await this.Reloading.Reload(shooterWeapon);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct reload (bypasses validation)
        public async Task ReloadDirect(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            await this.Reloading.Reload(shooterWeapon);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalReload))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Reload method in ShooterStance.cs.");
                return false;
            }
            
            content = content.Replace(originalReload, patchedReload);
            
            // Add direct PullTrigger after CancelTrigger
            string originalCancelTrigger = @"        public void CancelTrigger(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;

            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            
            weaponData.OnCancelTrigger();
        }";
            
            string patchedCancelTrigger = @"        public void CancelTrigger(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;

            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            
            weaponData.OnCancelTrigger();
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct pull trigger (bypasses validation)
        public void PullTriggerDirect(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            
            if (this.Reloading.WeaponReloading == shooterWeapon)
            {
                if (!this.Reloading.CanPartialReload) return;
                this.StopReload(shooterWeapon, CancelReason.PartialReload);
                return;
            }
            
            weaponData.OnPullTrigger();
        }
        // [GC2_NETWORK_PATCH_END]";

            if (content.Contains(originalCancelTrigger))
            {
                content = content.Replace(originalCancelTrigger, patchedCancelTrigger);
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchWeaponData(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Common.Audio;
using UnityEngine;

namespace GameCreator.Runtime.Shooter
{
    public class WeaponData
    {";

            string patchedUsings = @"using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Common.Audio;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Shooter > Unpatch to restore.

namespace GameCreator.Runtime.Shooter
{
    public class WeaponData
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if a shot should proceed locally.</summary>
        public static Func<WeaponData, bool> NetworkShootValidator;
        
        /// <summary>Called when a shot is fired (for network sync).</summary>
        public static Action<WeaponData, Vector3, Vector3, float> NetworkShotFired;
        
        /// <summary>Called when a projectile hits something (for damage validation).</summary>
        public static Action<WeaponData, GameObject, Vector3, Vector3> NetworkProjectileHit;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkShootValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!content.Contains(originalUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in WeaponData.cs.");
                return false;
            }
            
            content = content.Replace(originalUsings, patchedUsings);

            // Fix charge ratio math precedence bug:
            // (now - lastPull) / maxChargeTime
            content = content.Replace(
                " ? Mathf.Clamp01(this.Character.Time.Time - this.m_LastTriggerPull / maxChargeTime)",
                " ? Mathf.Clamp01((this.Character.Time.Time - this.m_LastTriggerPull) / maxChargeTime)");
            
            // Patch the private Shoot method - insert validation at the beginning
            string originalShoot = @"        private void Shoot()
        {
            if (this.Weapon.Jam.Run(this.WeaponArgs, this.IsJammed))
            {";
            
            string patchedShoot = @"        private void Shoot()
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkShootValidator != null && !NetworkShootValidator.Invoke(this))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            if (this.Weapon.Jam.Run(this.WeaponArgs, this.IsJammed))
            {";

            if (!content.Contains(originalShoot))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Shoot method in WeaponData.cs.");
                return false;
            }
            
            content = content.Replace(originalShoot, patchedShoot);
            
            // Find where shot is confirmed successful and add network notification
            // Look for the line after success shot happens
            string shotSuccess = @"            bool success = this.Weapon.Projectile.Run(
                this.WeaponArgs, 
                this.Weapon,
                chargeRation,
                this.m_LastTriggerPull
            );

            if (!success)
            {";
            
            string patchedShotSuccess = @"            bool success = this.Weapon.Projectile.Run(
                this.WeaponArgs, 
                this.Weapon,
                chargeRation,
                this.m_LastTriggerPull
            );
            
            // [GC2_NETWORK_PATCH] Notify network of successful shot
            if (success)
            {
                Vector3 origin = this.Prop != null ? this.Prop.transform.position : this.Character.transform.position;
                Vector3 direction = this.Prop != null ? this.Prop.transform.forward : this.Character.transform.forward;
                NetworkShotFired?.Invoke(this, origin, direction, chargeRation);
            }
            // [GC2_NETWORK_PATCH_END]

            if (!success)
            {";

            if (content.Contains(shotSuccess))
            {
                content = content.Replace(shotSuccess, patchedShotSuccess);
            }
            
            // Add a direct shoot method
            string internalShootEnd = "internal void OnShoot(float duration)";
            int internalShootIndex = content.IndexOf(internalShootEnd);
            if (internalShootIndex < 0)
            {
                // Try finding another anchor point
                string publicMethods = "// PUBLIC METHODS: ";
                int publicIndex = content.IndexOf(publicMethods);
                if (publicIndex > 0)
                {
                    string directShootMethod = @"
        // [GC2_NETWORK_PATCH] Server-side direct shoot (bypasses validation)
        public void ShootDirect()
        {
            if (this.Weapon.Jam.Run(this.WeaponArgs, this.IsJammed))
            {
                this.IsJammed = true;
                return;
            }
            
            float maxChargeTime = this.Weapon.Fire.MaxChargeTime(this.WeaponArgs);
            float chargeRation = 1f;
            
            bool success = this.Weapon.Projectile.Run(
                this.WeaponArgs, 
                this.Weapon,
                chargeRation,
                this.m_LastTriggerPull
            );

            if (!success) return;

            this.LastShotTime = this.Character.Time.Time;
            this.LastShotFrame = Time.frameCount;
            this.m_NumShots += 1;
        }
        // [GC2_NETWORK_PATCH_END]
        
        " + publicMethods;
                    
                    content = content.Replace(publicMethods, directShootMethod);
                }
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchEditorFile(string relativePath, string content)
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
