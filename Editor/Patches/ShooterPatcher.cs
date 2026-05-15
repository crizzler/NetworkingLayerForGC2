using System;
using System.Text.RegularExpressions;
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
        public override string PatchVersion => "2.2.2-shooter";
        public override string DisplayName => "Shooter (Game Creator 2)";
        
        public override string PatchDescription =>
            "This will modify the Game Creator 2 Shooter source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "ShooterStance.PullTrigger/ReleaseTrigger will have network validation.\n" +
            "ShooterStance.Reload will have network hooks.\n" +
            "WeaponData.Shoot will be intercepted for hit validation.\n" +
            "ShooterWeapon.OnHit will report hit claims for server validation.\n" +
            "Also fixes obsolete API warnings in editor files.";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Stances/ShooterStance.cs",
            "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Data/WeaponData.cs",
            "Plugins/GameCreator/Packages/Shooter/Runtime/ScriptableObjects/ShooterWeapon.cs",
            "Plugins/GameCreator/Packages/Shooter/Runtime/Classes/Aim/Aim.cs",
            "Plugins/GameCreator/Packages/Shooter/Editor/Editors/ReloadEditor.cs",
            "Plugins/GameCreator/Packages/Shooter/Editor/Editors/ShooterWeaponEditor.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Shooter/Editor/Version.txt", "2.2.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("ShooterStance.cs"))
            {
                return new[]
                {
                    "NetworkPullTriggerValidator",
                    "NetworkReleaseTriggerValidator",
                    "NetworkReloadValidator",
                    "PullTriggerDirect(",
                    "ReleaseTriggerDirect(",
                    "ReloadDirect("
                };
            }

            if (relativePath.EndsWith("WeaponData.cs"))
            {
                return new[]
                {
                    "NetworkShootValidator",
                    "NetworkShotFired",
                    "ShootDirect("
                };
            }

            if (relativePath.EndsWith("ShooterWeapon.cs"))
            {
                return new[]
                {
                    "NetworkHitDetected",
                    "NetworkHitDetected?.Invoke"
                };
            }

            if (relativePath.EndsWith("Aim.cs"))
            {
                return new[]
                {
                    "NetworkAimPointResolver",
                    "NetworkAimPointResolver?.Invoke"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("ShooterStance.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkPullTriggerValidator.Invoke", 1 },
                    { "NetworkReleaseTriggerValidator.Invoke", 1 },
                    { "NetworkReloadValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("WeaponData.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkShootValidator.Invoke", 1 },
                    { "NetworkShotFired?.Invoke(", 1 }
                };
            }

            if (relativePath.EndsWith("ShooterWeapon.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkHitDetected?.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("Aim.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkAimPointResolver?.Invoke", 1 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchRegexTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("ShooterStance.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { @"NetworkPullTriggerValidator\.Invoke", 1 },
                    { @"NetworkReleaseTriggerValidator\.Invoke", 1 },
                    { @"NetworkReloadValidator\.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("WeaponData.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { @"NetworkShootValidator\.Invoke\(this\)", 1 },
                    { @"NetworkShotFired\?\.Invoke\(", 1 }
                };
            }

            if (relativePath.EndsWith("ShooterWeapon.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { @"NetworkHitDetected\?\.Invoke\(", 1 }
                };
            }

            if (relativePath.EndsWith("Aim.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { @"NetworkAimPointResolver\?\.Invoke\(", 1 }
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
            
            if (relativePath.EndsWith("ShooterStance.cs"))
            {
                return PatchShooterStance(relativePath, content);
            }
            else if (relativePath.EndsWith("WeaponData.cs"))
            {
                return PatchWeaponData(relativePath, content);
            }
            else if (relativePath.EndsWith("ShooterWeapon.cs"))
            {
                return PatchShooterWeapon(relativePath, content);
            }
            else if (relativePath.EndsWith("Aim.cs"))
            {
                return PatchAim(relativePath, content);
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
// Use Game Creator > Networking Layer > Patches > Shooter > Unpatch to restore.

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

            if (!content.Contains("NetworkPullTriggerValidator") &&
                !TryReplaceWithFlexibleWhitespace(ref content, originalUsings, patchedUsings) &&
                !TryInsertShooterStanceNetworkingHeader(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not add network hook header in ShooterStance.cs.");
                return false;
            }
            
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

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalPullTrigger, patchedPullTrigger) &&
                !TryInjectStanceValidation(ref content, "PullTrigger", "NetworkPullTriggerValidator"))
            {
                Debug.LogError("[GC2 Networking] Could not find expected PullTrigger method in ShooterStance.cs.");
                return false;
            }
            
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

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalReleaseTrigger, patchedReleaseTrigger) &&
                !TryInjectStanceValidation(ref content, "ReleaseTrigger", "NetworkReleaseTriggerValidator"))
            {
                Debug.LogError("[GC2 Networking] Could not find expected ReleaseTrigger method in ShooterStance.cs.");
                return false;
            }
            
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

            if (!TryReplaceWithFlexibleWhitespace(ref content, originalReload, patchedReload) &&
                !TryInjectStanceValidation(ref content, "Reload", "NetworkReloadValidator"))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Reload method in ShooterStance.cs.");
                return false;
            }
            
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

            if (TryReplaceWithFlexibleWhitespace(ref content, originalCancelTrigger, patchedCancelTrigger))
            {
                Debug.Log("[GC2 Networking] Applied flexible replacement for CancelTrigger block.");
            }

            if (!EnsureStanceDirectMethods(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not ensure direct ShooterStance methods were inserted.");
                return false;
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
// Use Game Creator > Networking Layer > Patches > Shooter > Unpatch to restore.

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

            if (!content.Contains("NetworkShootValidator"))
            {
                if (!TryReplaceWithFlexibleWhitespace(ref content, originalUsings, patchedUsings) &&
                    !TryInsertWeaponDataNetworkingHeader(ref content))
                {
                    Debug.LogError("[GC2 Networking] Could not add network hook header in WeaponData.cs.");
                    return false;
                }
            }

            // Fix charge ratio math precedence bug:
            // (now - lastPull) / maxChargeTime
            if (!TryFixLegacyChargeRatioExpression(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not normalize legacy charge ratio expression in WeaponData.cs.");
                return false;
            }
            
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

            if (!content.Contains("NetworkShootValidator.Invoke(this)") &&
                !TryReplaceWithFlexibleWhitespace(ref content, originalShoot, patchedShoot) &&
                !TryInjectShootValidationFallback(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Shoot method in WeaponData.cs.");
                return false;
            }

            if (!HasShotFiredInvocation(content) &&
                !TryInjectShotSuccessNotification(ref content))
            {
                Debug.LogError("[GC2 Networking] Could not find shot success anchor in WeaponData.cs.");
                return false;
            }
            
            // Add a direct shoot method
            if (!content.Contains("ShootDirect("))
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
	            float chargeRatio = 1f;
	            
	            bool success = this.Weapon.Projectile.Run(
	                this.WeaponArgs, 
	                this.Weapon,
	                chargeRatio,
	                this.m_LastTriggerPull
	            );

            if (!success) return;

            this.LastShotTime = this.Character.Time.Time;
            this.LastShotFrame = Time.frameCount;
            this.m_NumShots += 1;
        }
        // [GC2_NETWORK_PATCH_END]
";

                if (!TryInsertDirectShootMethod(ref content, directShootMethod))
                {
                    Debug.LogError("[GC2 Networking] Could not insert ShootDirect method in WeaponData.cs.");
                    return false;
                }
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchShooterWeapon(string relativePath, string content)
        {
            if (!content.Contains("NetworkHitDetected"))
            {
                const string lastShotData = "        public static ShotData LastShotData { get; private set; }";
                const string patchedLastShotData =
                    "        public static ShotData LastShotData { get; private set; }\n" +
                    "        public static Action<ShotData, Args> NetworkHitDetected;";

                if (!TryReplaceWithFlexibleWhitespace(ref content, lastShotData, patchedLastShotData))
                {
                    Debug.LogError("[GC2 Networking] Could not add NetworkHitDetected hook in ShooterWeapon.cs.");
                    return false;
                }
            }

            if (!content.Contains("NetworkHitDetected = null"))
            {
                const string resetAnchor =
                    "        private static void RuntimeInitializeOnLoad()\n" +
                    "        {\n" +
                    "            LastShotData = default;";
                const string patchedResetAnchor =
                    "        private static void RuntimeInitializeOnLoad()\n" +
                    "        {\n" +
                    "            LastShotData = default;\n" +
                    "            NetworkHitDetected = null;";

                if (!TryReplaceWithFlexibleWhitespace(ref content, resetAnchor, patchedResetAnchor))
                {
                    Debug.LogError("[GC2 Networking] Could not add NetworkHitDetected reset in ShooterWeapon.cs.");
                    return false;
                }
            }

            if (!content.Contains("NetworkHitDetected?.Invoke"))
            {
                const string onHitAnchor =
                    "        internal void OnHit(ShotData data, Args args)\n" +
                    "        {\n" +
                    "            LastShotData = data;";
                const string patchedOnHitAnchor =
                    "        internal void OnHit(ShotData data, Args args)\n" +
                    "        {\n" +
                    "            LastShotData = data;\n" +
                    "            NetworkHitDetected?.Invoke(data, args);";

                if (!TryReplaceWithFlexibleWhitespace(ref content, onHitAnchor, patchedOnHitAnchor))
                {
                    Debug.LogError("[GC2 Networking] Could not inject NetworkHitDetected invocation in ShooterWeapon.cs.");
                    return false;
                }
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool PatchAim(string relativePath, string content)
        {
            if (!content.Contains("NetworkAimPointResolver"))
            {
                const string classAnchor =
                    "    public class Aim\n" +
                    "    {";
                const string patchedClassAnchor =
                    "    public class Aim\n" +
                    "    {\n" +
                    "        public static Func<Args, Aim, Vector3?> NetworkAimPointResolver;";

                if (!TryReplaceWithFlexibleWhitespace(ref content, classAnchor, patchedClassAnchor))
                {
                    Debug.LogError("[GC2 Networking] Could not add NetworkAimPointResolver hook in Aim.cs.");
                    return false;
                }
            }

            if (!content.Contains("NetworkAimPointResolver?.Invoke"))
            {
                const string getPointAnchor =
                    "        public Vector3 GetPoint(Args args)\n" +
                    "        {\n" +
                    "            return this.m_Aim.GetPoint(args);\n" +
                    "        }";
                const string patchedGetPointAnchor =
                    "        public Vector3 GetPoint(Args args)\n" +
                    "        {\n" +
                    "            Vector3? networkAimPoint = NetworkAimPointResolver?.Invoke(args, this);\n" +
                    "            if (networkAimPoint.HasValue) return networkAimPoint.Value;\n\n" +
                    "            return this.m_Aim.GetPoint(args);\n" +
                    "        }";

                if (!TryReplaceWithFlexibleWhitespace(ref content, getPointAnchor, patchedGetPointAnchor))
                {
                    Debug.LogError("[GC2 Networking] Could not inject NetworkAimPointResolver invocation in Aim.cs.");
                    return false;
                }
            }

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private bool TryInsertWeaponDataNetworkingHeader(ref string content)
        {
            Match namespaceMatch = Regex.Match(
                content,
                @"(?m)^\s*namespace\s+GameCreator\.Runtime\.Shooter\s*$");
            if (!namespaceMatch.Success)
            {
                return false;
            }

            if (!content.Contains(PatchMarker, StringComparison.Ordinal))
            {
                string header =
                    PatchMarker + "\n" +
                    "// This file has been patched for GC2 Networking server authority.\n" +
                    "// Do not modify the patched sections manually.\n" +
                    "// Use Game Creator > Networking Layer > Patches > Shooter > Unpatch to restore.\n\n";
                content = content.Insert(namespaceMatch.Index, header);
            }

            Match classMatch = Regex.Match(
                content,
                @"(?m)^\s*public\s+class\s+WeaponData\b[^{]*\{\s*$");
            if (!classMatch.Success)
            {
                return false;
            }

            const string staticHooks = @"
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

            int insertionIndex = classMatch.Index + classMatch.Length;
            content = content.Insert(insertionIndex, staticHooks);
            return true;
        }

        private bool TryInsertShooterStanceNetworkingHeader(ref string content)
        {
            Match namespaceMatch = Regex.Match(
                content,
                @"(?m)^\s*namespace\s+GameCreator\.Runtime\.Shooter\s*$");
            if (!namespaceMatch.Success)
            {
                return false;
            }

            if (!content.Contains(PatchMarker, StringComparison.Ordinal))
            {
                string header =
                    PatchMarker + "\n" +
                    "// This file has been patched for GC2 Networking server authority.\n" +
                    "// Do not modify the patched sections manually.\n" +
                    "// Use Game Creator > Networking Layer > Patches > Shooter > Unpatch to restore.\n\n";
                content = content.Insert(namespaceMatch.Index, header);
            }

            Match classMatch = Regex.Match(
                content,
                @"(?m)^\s*public\s+class\s+ShooterStance\b[^{]*\{\s*$");
            if (!classMatch.Success)
            {
                return false;
            }

            const string staticHooks = @"
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

            int insertionIndex = classMatch.Index + classMatch.Length;
            content = content.Insert(insertionIndex, staticHooks);
            return true;
        }

        private static bool TryInjectStanceValidation(ref string content, string methodName, string validatorFieldName)
        {
            if (!TryFindMethodBodySpan(content, methodName, out int bodyStart, out int bodyLength))
            {
                return false;
            }

            string body = content.Substring(bodyStart, bodyLength);
            if (body.Contains($"{validatorFieldName}.Invoke", StringComparison.Ordinal))
            {
                return true;
            }

            Match weaponVariableMatch = Regex.Match(
                body,
                @"ShooterWeapon\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*this\.GetWeapon\s*\(",
                RegexOptions.CultureInvariant);
            string weaponVariable = weaponVariableMatch.Success
                ? weaponVariableMatch.Groups["name"].Value
                : "shooterWeapon";

            int insertionOffset = 0;
            Match nullCheckMatch = Regex.Match(
                body,
                $@"if\s*\(\s*{Regex.Escape(weaponVariable)}\s*==\s*null\s*\)\s*return\s*;",
                RegexOptions.CultureInvariant);
            if (nullCheckMatch.Success)
            {
                insertionOffset = nullCheckMatch.Index + nullCheckMatch.Length;
            }
            else if (weaponVariableMatch.Success)
            {
                insertionOffset = weaponVariableMatch.Index + weaponVariableMatch.Length;
            }

            string validationBlock = $@"
            
            // [GC2_NETWORK_PATCH] Server authority check
            if ({validatorFieldName} != null && !{validatorFieldName}.Invoke(this, {weaponVariable}))
            {{
                return; // Network will handle this
            }}
            // [GC2_NETWORK_PATCH_END]
";

            content = content.Insert(bodyStart + insertionOffset, validationBlock);
            return true;
        }

        private static bool EnsureStanceDirectMethods(ref string content)
        {
            const string releaseDirectMethod = @"
        // [GC2_NETWORK_PATCH] Server-side direct release trigger (bypasses validation)
        public void ReleaseTriggerDirect(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            int shooterWeaponId = shooterWeapon.GetInstanceID();
            if (!this.m_Equipment.TryGetValue(shooterWeaponId, out WeaponData weaponData)) return;
            weaponData.OnReleaseTrigger();
        }
        // [GC2_NETWORK_PATCH_END]
";

            const string pullDirectMethod = @"
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
        // [GC2_NETWORK_PATCH_END]
";

            const string reloadDirectMethod = @"
        // [GC2_NETWORK_PATCH] Server-side direct reload (bypasses validation)
        public async Task ReloadDirect(ShooterWeapon optionalWeapon)
        {
            ShooterWeapon shooterWeapon = this.GetWeapon(optionalWeapon);
            if (shooterWeapon == null) return;
            await this.Reloading.Reload(shooterWeapon);
        }
        // [GC2_NETWORK_PATCH_END]
";

            if (!EnsureStanceMethod(ref content, "ReleaseTriggerDirect(", releaseDirectMethod))
            {
                return false;
            }

            if (!EnsureStanceMethod(ref content, "PullTriggerDirect(", pullDirectMethod))
            {
                return false;
            }

            if (!EnsureStanceMethod(ref content, "ReloadDirect(", reloadDirectMethod))
            {
                return false;
            }

            return true;
        }

        private static bool EnsureStanceMethod(ref string content, string signatureToken, string methodSnippet)
        {
            if (content.Contains(signatureToken, StringComparison.Ordinal))
            {
                return true;
            }

            Match stopReloadMatch = Regex.Match(
                content,
                @"(?m)^\s*(?:public|private|internal|protected)\s+void\s+StopReload\s*\(",
                RegexOptions.CultureInvariant);
            if (stopReloadMatch.Success)
            {
                content = content.Insert(stopReloadMatch.Index, methodSnippet + "\n");
                return true;
            }

            Match privateMethodsMatch = Regex.Match(
                content,
                @"(?m)^\s*//\s*PRIVATE METHODS:\s*",
                RegexOptions.CultureInvariant);
            if (privateMethodsMatch.Success)
            {
                content = content.Insert(privateMethodsMatch.Index, methodSnippet + "\n");
                return true;
            }

            int classClosingIndex = content.LastIndexOf("\n    }", StringComparison.Ordinal);
            if (classClosingIndex < 0)
            {
                return false;
            }

            content = content.Insert(classClosingIndex, "\n" + methodSnippet + "\n");
            return true;
        }

        private static bool TryFixLegacyChargeRatioExpression(ref string content)
        {
            const string normalizedExpression =
                "Mathf.Clamp01((this.Character.Time.Time - this.m_LastTriggerPull) / maxChargeTime)";

            if (content.Contains(normalizedExpression, StringComparison.Ordinal))
            {
                return true;
            }

            string updated = Regex.Replace(
                content,
                @"Mathf\.Clamp01\(\s*this\.Character\.Time\.Time\s*-\s*this\.m_LastTriggerPull\s*/\s*maxChargeTime\s*\)",
                normalizedExpression,
                RegexOptions.CultureInvariant);

            if (updated != content)
            {
                content = updated;
                return true;
            }

            // Normalize equivalent parenthesized variants to a deterministic form.
            updated = Regex.Replace(
                content,
                @"Mathf\.Clamp01\(\s*\(\s*this\.Character\.Time\.Time\s*-\s*this\.m_LastTriggerPull\s*\)\s*/\s*maxChargeTime\s*\)",
                normalizedExpression,
                RegexOptions.CultureInvariant);

            if (updated != content)
            {
                content = updated;
            }

            updated = Regex.Replace(
                content,
                @"\bchargeRation\b",
                "chargeRatio",
                RegexOptions.CultureInvariant);

            if (updated != content)
            {
                content = updated;
            }

            return true;
        }

        private static bool HasShotFiredInvocation(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            return Regex.IsMatch(
                content,
                @"NetworkShotFired\?\.Invoke\(\s*this\s*,\s*origin\s*,\s*direction\s*,\s*(?:chargeRati(?:o|on)|[^)\r\n]+)\)",
                RegexOptions.CultureInvariant);
        }

        private static string ResolveChargeRatioIdentifier(string methodBody)
        {
            if (string.IsNullOrEmpty(methodBody))
            {
                return "chargeRatio";
            }

            if (Regex.IsMatch(methodBody, @"\bchargeRatio\b", RegexOptions.CultureInvariant))
            {
                return "chargeRatio";
            }

            if (Regex.IsMatch(methodBody, @"\bchargeRation\b", RegexOptions.CultureInvariant))
            {
                return "chargeRation";
            }

            return "chargeRatio";
        }

        private static bool TryInjectShootValidationFallback(ref string content)
        {
            if (!TryFindMethodBodySpan(content, "Shoot", out int bodyStart, out int bodyLength))
            {
                return false;
            }

            string body = content.Substring(bodyStart, bodyLength);
            if (body.Contains("NetworkShootValidator.Invoke(this)", StringComparison.Ordinal))
            {
                return true;
            }

            Match anchor = Regex.Match(body, @"if\s*\(\s*this\.Weapon\.Jam\.Run\(", RegexOptions.CultureInvariant);
            if (!anchor.Success)
            {
                return false;
            }

            const string validationBlock = @"
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkShootValidator != null && !NetworkShootValidator.Invoke(this))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
";

            content = content.Insert(bodyStart + anchor.Index, validationBlock);
            return true;
        }

        private static bool TryInjectShotSuccessNotification(ref string content)
        {
            if (!TryFindMethodBodySpan(content, "Shoot", out int bodyStart, out int bodyLength))
            {
                return false;
            }

            string body = content.Substring(bodyStart, bodyLength);
            if (HasShotFiredInvocation(body))
            {
                return true;
            }

            string chargeRatioIdentifier = ResolveChargeRatioIdentifier(body);
            string notifyBlock = $@"
            // [GC2_NETWORK_PATCH] Notify network before the projectile pipeline reports hits
            Vector3 origin = this.Weapon.Muzzle.GetPosition(this.WeaponArgs);
            Vector3 direction = this.Weapon.Muzzle.GetRotation(this.WeaponArgs) * Vector3.forward;
            NetworkShotFired?.Invoke(this, origin, direction, {chargeRatioIdentifier});
            // [GC2_NETWORK_PATCH_END]

";

            Match successAssignment = Regex.Match(
                body,
                @"bool\s+success\s*=\s*this\.Weapon\.Projectile\.Run\s*\(",
                RegexOptions.CultureInvariant);
            if (successAssignment.Success)
            {
                content = content.Insert(bodyStart + successAssignment.Index, notifyBlock);
                return true;
            }

            Match failureBranchAnchor = Regex.Match(body, @"if\s*\(\s*!\s*success\s*\)", RegexOptions.CultureInvariant);
            if (!failureBranchAnchor.Success)
            {
                return false;
            }

            content = content.Insert(bodyStart + failureBranchAnchor.Index, notifyBlock);
            return true;
        }

        private static bool TryInsertDirectShootMethod(ref string content, string directShootMethod)
        {
            Match onShootMatch = Regex.Match(
                content,
                @"(?m)^\s*(?:public|internal|private|protected)\s+void\s+OnShoot\s*\(",
                RegexOptions.CultureInvariant);
            if (onShootMatch.Success)
            {
                content = content.Insert(onShootMatch.Index, directShootMethod + "\n");
                return true;
            }

            Match publicMethodsMatch = Regex.Match(
                content,
                @"(?m)^\s*//\s*PUBLIC METHODS:\s*",
                RegexOptions.CultureInvariant);
            if (publicMethodsMatch.Success)
            {
                content = content.Insert(publicMethodsMatch.Index, directShootMethod + "\n");
                return true;
            }

            int classClosingIndex = content.LastIndexOf("\n    }", StringComparison.Ordinal);
            if (classClosingIndex < 0)
            {
                return false;
            }

            content = content.Insert(classClosingIndex, "\n" + directShootMethod + "\n");
            return true;
        }

        private static bool TryFindMethodBodySpan(
            string content,
            string methodName,
            out int bodyStart,
            out int bodyLength)
        {
            bodyStart = 0;
            bodyLength = 0;

            Match methodMatch = Regex.Match(
                content,
                $@"(?m)^\s*(?:public|private|internal|protected)\s+(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+{Regex.Escape(methodName)}\s*\([^)]*\)\s*\{{",
                RegexOptions.CultureInvariant);
            if (!methodMatch.Success)
            {
                return false;
            }

            int braceIndex = content.IndexOf('{', methodMatch.Index + methodMatch.Length - 1);
            if (braceIndex < 0)
            {
                return false;
            }

            int depth = 0;
            for (int i = braceIndex; i < content.Length; i++)
            {
                if (content[i] == '{')
                {
                    depth++;
                }
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        bodyStart = braceIndex + 1;
                        bodyLength = i - braceIndex - 1;
                        return true;
                    }
                }
            }

            return false;
        }
        
        private bool PatchEditorFile(string relativePath, string content)
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
