using System;
using System.Threading.Tasks;
using UnityEngine;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Core.Common;
using DaimahouGames.Runtime.Pawns;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Hooks into the patched DaimahouGames Caster to provide true server-authoritative validation.
    /// This component works in conjunction with NetworkAbilitiesController.
    /// 
    /// When the Abilities patch is applied:
    /// - All Cast/Learn/UnLearn calls are intercepted at the source
    /// - Clients cannot bypass validation by calling methods directly
    /// - Server has full authority over ability execution
    /// 
    /// When the patch is NOT applied:
    /// - Falls back to interception-based validation
    /// - Less secure but still functional
    /// </summary>
    public class NetworkAbilitiesPatchHooks : NetworkSingleton<NetworkAbilitiesPatchHooks>
    {
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        private bool m_PatchHooksInstalled;
        
        /// <summary>
        /// Returns true if the Caster patch is applied and hooks are active.
        /// </summary>
        public bool IsPatchActive => m_PatchHooksInstalled && IsCasterPatched();
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when a cast is requested. Return true to allow local execution.
        /// For clients: return false to send to server for validation.
        /// For server: validate and return true to execute, false to reject.
        /// </summary>
        public Func<Caster, Ability, ExtendedArgs, bool> OnCastValidation;
        
        /// <summary>
        /// Called when learn is requested. Return true to allow local execution.
        /// </summary>
        public Func<Caster, Ability, int, bool> OnLearnValidation;
        
        /// <summary>
        /// Called when unlearn is requested. Return true to allow local execution.
        /// </summary>
        public Func<Caster, Ability, bool> OnUnLearnValidation;
        
        /// <summary>
        /// Called when a cast completes.
        /// </summary>
        public Action<Caster, Ability, bool> OnCastCompleted;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the patch hooks with network role.
        /// </summary>
        /// <param name="isServer">True if this is the server</param>
        /// <param name="isClient">True if this is a client</param>
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            
            InstallHooks();
        }
        
        /// <summary>
        /// Check if the Caster class has been patched.
        /// Uses reflection to check for the static hooks.
        /// </summary>
        public static bool IsCasterPatched()
        {
            try
            {
                var casterType = typeof(Caster);
                var field = casterType.GetField("NetworkCastValidator", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return field != null;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Force a cast to execute locally (server use only).
        /// This bypasses the network validation hook.
        /// </summary>
        public async Task ExecuteCastLocally(Caster caster, Ability ability, ExtendedArgs args)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesPatchHooks] ExecuteCastLocally should only be called on server.");
                return;
            }
            
            // Temporarily remove the hook to allow direct execution
            var savedValidator = GetCastValidator();
            SetCastValidator(null);
            
            try
            {
                await caster.Cast(ability, args);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAbilitiesPatchHooks] ExecuteCastLocally failed: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                SetCastValidator(savedValidator);
            }
        }
        
        /// <summary>
        /// Force a learn to execute locally (server use only).
        /// </summary>
        public void ExecuteLearnLocally(Caster caster, Ability ability, int slot)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesPatchHooks] ExecuteLearnLocally should only be called on server.");
                return;
            }
            
            // Try to use LearnDirect if available (patched method)
            if (TryCallLearnDirect(caster, ability, slot))
            {
                return;
            }
            
            // Fallback: temporarily remove hook
            var savedValidator = GetLearnValidator();
            SetLearnValidator(null);
            
            try
            {
                caster.Learn(ability, slot);
            }
            finally
            {
                SetLearnValidator(savedValidator);
            }
        }
        
        /// <summary>
        /// Force an unlearn to execute locally (server use only).
        /// </summary>
        public void ExecuteUnLearnLocally(Caster caster, Ability ability)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesPatchHooks] ExecuteUnLearnLocally should only be called on server.");
                return;
            }
            
            // Try to use UnLearnDirect if available (patched method)
            if (TryCallUnLearnDirect(caster, ability))
            {
                return;
            }
            
            // Fallback: temporarily remove hook
            var savedValidator = GetUnLearnValidator();
            SetUnLearnValidator(null);
            
            try
            {
                caster.UnLearn(ability);
            }
            finally
            {
                SetUnLearnValidator(savedValidator);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HOOK INSTALLATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void InstallHooks()
        {
            if (m_PatchHooksInstalled) return;
            
            if (!IsCasterPatched())
            {
                Debug.Log("[NetworkAbilitiesPatchHooks] Caster is not patched. Using interception-based validation.");
                return;
            }
            
            // Install our validation hooks
            SetCastValidator(ValidateCast);
            SetLearnValidator(ValidateLearn);
            SetUnLearnValidator(ValidateUnLearn);
            SetCastCompletedCallback(HandleCastCompleted);
            
            m_PatchHooksInstalled = true;
            Debug.Log("[NetworkAbilitiesPatchHooks] Server authority hooks installed successfully.");
        }
        
        private void UninstallHooks()
        {
            if (!m_PatchHooksInstalled) return;
            
            SetCastValidator(null);
            SetLearnValidator(null);
            SetUnLearnValidator(null);
            SetCastCompletedCallback(null);
            
            m_PatchHooksInstalled = false;
            Debug.Log("[NetworkAbilitiesPatchHooks] Server authority hooks uninstalled.");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION HANDLERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool ValidateCast(Caster caster, Ability ability, ExtendedArgs args)
        {
            // If we have an external validator, use it
            if (OnCastValidation != null)
            {
                return OnCastValidation.Invoke(caster, ability, args);
            }
            
            // Default behavior:
            // - Server always allows (it validates itself)
            // - Client never allows (must go through network)
            if (m_IsServer)
            {
                return true;
            }
            
            // Client: block local execution, NetworkAbilitiesController will handle
            return false;
        }
        
        private bool ValidateLearn(Caster caster, Ability ability, int slot)
        {
            if (OnLearnValidation != null)
            {
                return OnLearnValidation.Invoke(caster, ability, slot);
            }
            
            // Server allows, client blocks
            return m_IsServer;
        }
        
        private bool ValidateUnLearn(Caster caster, Ability ability)
        {
            if (OnUnLearnValidation != null)
            {
                return OnUnLearnValidation.Invoke(caster, ability);
            }
            
            // Server allows, client blocks
            return m_IsServer;
        }
        
        private void HandleCastCompleted(Caster caster, Ability ability, bool success)
        {
            OnCastCompleted?.Invoke(caster, ability, success);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // REFLECTION HELPERS (to access patched static fields)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static void SetCastValidator(Func<Caster, Ability, ExtendedArgs, bool> validator)
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkCastValidator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, validator);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkAbilitiesPatchHooks] Could not set NetworkCastValidator: {e.Message}");
            }
        }
        
        private static Func<Caster, Ability, ExtendedArgs, bool> GetCastValidator()
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkCastValidator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return field?.GetValue(null) as Func<Caster, Ability, ExtendedArgs, bool>;
            }
            catch
            {
                return null;
            }
        }
        
        private static void SetLearnValidator(Func<Caster, Ability, int, bool> validator)
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkLearnValidator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, validator);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkAbilitiesPatchHooks] Could not set NetworkLearnValidator: {e.Message}");
            }
        }
        
        private static Func<Caster, Ability, int, bool> GetLearnValidator()
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkLearnValidator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return field?.GetValue(null) as Func<Caster, Ability, int, bool>;
            }
            catch
            {
                return null;
            }
        }
        
        private static void SetUnLearnValidator(Func<Caster, Ability, bool> validator)
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkUnLearnValidator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, validator);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkAbilitiesPatchHooks] Could not set NetworkUnLearnValidator: {e.Message}");
            }
        }
        
        private static Func<Caster, Ability, bool> GetUnLearnValidator()
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkUnLearnValidator",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                return field?.GetValue(null) as Func<Caster, Ability, bool>;
            }
            catch
            {
                return null;
            }
        }
        
        private static void SetCastCompletedCallback(Action<Caster, Ability, bool> callback)
        {
            try
            {
                var field = typeof(Caster).GetField("NetworkCastCompleted",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                field?.SetValue(null, callback);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkAbilitiesPatchHooks] Could not set NetworkCastCompleted: {e.Message}");
            }
        }
        
        private static bool TryCallLearnDirect(Caster caster, Ability ability, int slot)
        {
            try
            {
                var method = typeof(Caster).GetMethod("LearnDirect",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    method.Invoke(caster, new object[] { ability, slot });
                    return true;
                }
            }
            catch
            {
                // Method not available
            }
            return false;
        }
        
        private static bool TryCallUnLearnDirect(Caster caster, Ability ability)
        {
            try
            {
                var method = typeof(Caster).GetMethod("UnLearnDirect",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    method.Invoke(caster, new object[] { ability });
                    return true;
                }
            }
            catch
            {
                // Method not available
            }
            return false;
        }
    }
}
