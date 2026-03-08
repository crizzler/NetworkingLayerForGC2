using System;
using UnityEngine;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Hooks into the patched GC2 Core classes to provide true server-authoritative validation.
    /// This component works in conjunction with NetworkCoreController.
    /// 
    /// When the Core patch is applied:
    /// - All Invincibility/Poise/Jump/Dash/Death calls are intercepted at the source
    /// - Clients cannot bypass validation by calling methods directly
    /// - Server has full authority over character state
    /// 
    /// When the patch is NOT applied:
    /// - Falls back to interception-based validation
    /// - Less secure but still functional
    /// </summary>
    public class NetworkCorePatchHooks : NetworkSingleton<NetworkCorePatchHooks>
    {
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        private bool m_PatchHooksInstalled;
        
        /// <summary>
        /// Returns true if the Core patch is applied and hooks are active.
        /// </summary>
        public bool IsPatchActive => m_PatchHooksInstalled && IsCorePatched();
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS - Invincibility
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when invincibility is requested. Return true to allow local execution.
        /// </summary>
        public Func<Invincibility, float, bool> OnInvincibilityValidation;
        
        /// <summary>
        /// Called when invincibility is set (for sync).
        /// </summary>
        public Action<Invincibility, float> OnInvincibilitySet;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS - Poise
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when poise damage is requested. Return true to allow local execution.
        /// </summary>
        public Func<Poise, float, bool> OnPoiseDamageValidation;
        
        /// <summary>
        /// Called when poise set is requested. Return true to allow local execution.
        /// </summary>
        public Func<Poise, float, bool> OnPoiseSetValidation;
        
        /// <summary>
        /// Called when poise reset is requested. Return true to allow local execution.
        /// </summary>
        public Func<Poise, float, bool> OnPoiseResetValidation;
        
        /// <summary>
        /// Called when poise is damaged (for sync).
        /// </summary>
        public Action<Poise, float, bool> OnPoiseDamaged;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS - Jump
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when jump is requested. Return true to allow local execution.
        /// </summary>
        public Func<Jump, bool> OnJumpValidation;
        
        /// <summary>
        /// Called when jump with force is requested. Return true to allow local execution.
        /// </summary>
        public Func<Jump, float, bool> OnJumpForceValidation;
        
        /// <summary>
        /// Called when jump is executed (for sync).
        /// </summary>
        public Action<Jump, float> OnJumpExecuted;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS - Dash
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when dash is requested. Return true to allow local execution.
        /// </summary>
        public Func<Dash, Vector3, float, float, float, float, bool> OnDashValidation;
        
        /// <summary>
        /// Called when dash starts (for sync).
        /// </summary>
        public Action<Dash, Vector3, float, float, float, float> OnDashStarted;
        
        /// <summary>
        /// Called when dash finishes (for sync).
        /// </summary>
        public Action<Dash> OnDashFinished;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS - Death
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when death state change is requested. Return true to allow local execution.
        /// </summary>
        public Func<Character, bool, bool> OnDeathValidation;
        
        /// <summary>
        /// Called when death state changes (for sync).
        /// </summary>
        public Action<Character, bool> OnDeathStateChanged;
        
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
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;

            if (m_IsServer) InstallHooks();
            else UninstallHooks();
        }
        
        /// <summary>
        /// Check if the Core classes have been patched.
        /// </summary>
        public static bool IsCorePatched()
        {
            try
            {
                bool hasInvincibilityHooks =
                    HasPublicStaticField(typeof(Invincibility), "NetworkSetValidator", typeof(Func<Invincibility, float, bool>)) &&
                    HasPublicStaticField(typeof(Invincibility), "NetworkInvincibilitySet", typeof(Action<Invincibility, float>)) &&
                    HasPublicStaticProperty(typeof(Invincibility), "IsNetworkingActive", typeof(bool)) &&
                    HasInstanceMethod(typeof(Invincibility), "SetDirect", typeof(float));

                bool hasPoiseHooks =
                    HasPublicStaticField(typeof(Poise), "NetworkDamageValidator", typeof(Func<Poise, float, bool>)) &&
                    HasPublicStaticField(typeof(Poise), "NetworkSetValidator", typeof(Func<Poise, float, bool>)) &&
                    HasPublicStaticField(typeof(Poise), "NetworkResetValidator", typeof(Func<Poise, float, bool>)) &&
                    HasPublicStaticField(typeof(Poise), "NetworkPoiseDamaged", typeof(Action<Poise, float, bool>)) &&
                    HasPublicStaticProperty(typeof(Poise), "IsNetworkingActive", typeof(bool)) &&
                    HasInstanceMethod(typeof(Poise), "DamageDirect", typeof(float)) &&
                    HasInstanceMethod(typeof(Poise), "SetDirect", typeof(float)) &&
                    HasInstanceMethod(typeof(Poise), "ResetDirect", typeof(float));

                bool hasJumpHooks =
                    HasPublicStaticField(typeof(Jump), "NetworkJumpValidator", typeof(Func<Jump, bool>)) &&
                    HasPublicStaticField(typeof(Jump), "NetworkJumpForceValidator", typeof(Func<Jump, float, bool>)) &&
                    HasPublicStaticField(typeof(Jump), "NetworkJumpExecuted", typeof(Action<Jump, float>)) &&
                    HasPublicStaticProperty(typeof(Jump), "IsNetworkingActive", typeof(bool)) &&
                    (HasInstanceMethod(typeof(Jump), "DoDirect") || HasInstanceMethod(typeof(Jump), "DoDirect", typeof(float)));

                bool hasDashHooks =
                    HasPublicStaticField(
                        typeof(Dash),
                        "NetworkDashValidator",
                        typeof(Func<Dash, Vector3, float, float, float, float, bool>)) &&
                    HasPublicStaticField(
                        typeof(Dash),
                        "NetworkDashStarted",
                        typeof(Action<Dash, Vector3, float, float, float, float>)) &&
                    HasPublicStaticField(typeof(Dash), "NetworkDashFinished", typeof(Action<Dash>)) &&
                    HasPublicStaticProperty(typeof(Dash), "IsNetworkingActive", typeof(bool)) &&
                    HasInstanceMethod(typeof(Dash), "ExecuteDirect", typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float));

                bool hasCharacterHooks =
                    HasPublicStaticField(typeof(Character), "NetworkIsDeadValidator", typeof(Func<Character, bool, bool>)) &&
                    HasPublicStaticField(typeof(Character), "NetworkDeathStateChanged", typeof(Action<Character, bool>)) &&
                    HasPublicStaticProperty(typeof(Character), "IsNetworkingActive", typeof(bool)) &&
                    HasInstanceMethod(typeof(Character), "SetIsDeadDirect", typeof(bool));

                return hasInvincibilityHooks && hasPoiseHooks && hasJumpHooks && hasDashHooks && hasCharacterHooks;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Check if a specific Core class has been patched.
        /// </summary>
        public static bool IsClassPatched<T>() where T : class
        {
            try
            {
                var type = typeof(T);
                return
                    type.GetProperty(
                        "IsNetworkingActive",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null ||
                    type.GetField(
                        "IsNetworkingActive",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) != null;
            }
            catch
            {
                return false;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DIRECT EXECUTION (Server use only)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Force invincibility to be set locally (server use only).
        /// </summary>
        public void SetInvincibilityDirect(Invincibility invincibility, float duration)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCorePatchHooks] SetInvincibilityDirect should only be called on server.");
                return;
            }
            
            TryCallMethod(invincibility, "SetDirect", duration);
        }
        
        /// <summary>
        /// Force poise damage to be applied locally (server use only).
        /// </summary>
        public bool DamagePoiseDirect(Poise poise, float value)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCorePatchHooks] DamagePoiseDirect should only be called on server.");
                return false;
            }
            
            return TryCallMethod<bool>(poise, "DamageDirect", value);
        }
        
        /// <summary>
        /// Force death state to be set locally (server use only).
        /// </summary>
        public void SetIsDeadDirect(Character character, bool isDead)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkCorePatchHooks] SetIsDeadDirect should only be called on server.");
                return;
            }
            
            TryCallMethod(character, "SetIsDeadDirect", isDead);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HOOK INSTALLATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void InstallHooks()
        {
            if (m_PatchHooksInstalled) return;
            
            if (!IsCorePatched())
            {
                Debug.Log("[NetworkCorePatchHooks] Core is not patched. Using interception-based validation.");
                return;
            }
            
            // Install Invincibility hooks
            SetStaticField<Invincibility>("NetworkSetValidator", 
                new Func<Invincibility, float, bool>(ValidateInvincibility));
            SetStaticField<Invincibility>("NetworkInvincibilitySet",
                new Action<Invincibility, float>(HandleInvincibilitySet));
            
            // Install Poise hooks
            SetStaticField<Poise>("NetworkDamageValidator",
                new Func<Poise, float, bool>(ValidatePoiseDamage));
            SetStaticField<Poise>("NetworkSetValidator",
                new Func<Poise, float, bool>(ValidatePoiseSet));
            SetStaticField<Poise>("NetworkResetValidator",
                new Func<Poise, float, bool>(ValidatePoiseReset));
            SetStaticField<Poise>("NetworkPoiseDamaged",
                new Action<Poise, float, bool>(HandlePoiseDamaged));
            
            // Install Jump hooks
            SetStaticField<Jump>("NetworkJumpValidator",
                new Func<Jump, bool>(ValidateJump));
            SetStaticField<Jump>("NetworkJumpForceValidator",
                new Func<Jump, float, bool>(ValidateJumpForce));
            SetStaticField<Jump>("NetworkJumpExecuted",
                new Action<Jump, float>(HandleJumpExecuted));
            
            // Install Dash hooks
            SetStaticField<Dash>("NetworkDashValidator",
                new Func<Dash, Vector3, float, float, float, float, bool>(ValidateDash));
            SetStaticField<Dash>("NetworkDashStarted",
                new Action<Dash, Vector3, float, float, float, float>(HandleDashStarted));
            SetStaticField<Dash>("NetworkDashFinished",
                new Action<Dash>(HandleDashFinished));
            
            // Install Character (death) hooks
            SetStaticField<Character>("NetworkIsDeadValidator",
                new Func<Character, bool, bool>(ValidateDeath));
            SetStaticField<Character>("NetworkDeathStateChanged",
                new Action<Character, bool>(HandleDeathStateChanged));
            
            m_PatchHooksInstalled = true;
            Debug.Log("[NetworkCorePatchHooks] Server authority hooks installed successfully.");
        }
        
        private void UninstallHooks()
        {
            if (!m_PatchHooksInstalled) return;
            
            // Clear all hooks
            SetStaticField<Invincibility>("NetworkSetValidator", null);
            SetStaticField<Invincibility>("NetworkInvincibilitySet", null);
            
            SetStaticField<Poise>("NetworkDamageValidator", null);
            SetStaticField<Poise>("NetworkSetValidator", null);
            SetStaticField<Poise>("NetworkResetValidator", null);
            SetStaticField<Poise>("NetworkPoiseDamaged", null);
            
            SetStaticField<Jump>("NetworkJumpValidator", null);
            SetStaticField<Jump>("NetworkJumpForceValidator", null);
            SetStaticField<Jump>("NetworkJumpExecuted", null);
            
            SetStaticField<Dash>("NetworkDashValidator", null);
            SetStaticField<Dash>("NetworkDashStarted", null);
            SetStaticField<Dash>("NetworkDashFinished", null);
            
            SetStaticField<Character>("NetworkIsDeadValidator", null);
            SetStaticField<Character>("NetworkDeathStateChanged", null);
            
            m_PatchHooksInstalled = false;
            Debug.Log("[NetworkCorePatchHooks] Server authority hooks uninstalled.");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION HANDLERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool ValidateInvincibility(Invincibility invincibility, float duration)
        {
            if (OnInvincibilityValidation != null)
                return OnInvincibilityValidation.Invoke(invincibility, duration);
            return m_IsServer;
        }
        
        private void HandleInvincibilitySet(Invincibility invincibility, float duration)
        {
            OnInvincibilitySet?.Invoke(invincibility, duration);
        }
        
        private bool ValidatePoiseDamage(Poise poise, float value)
        {
            if (OnPoiseDamageValidation != null)
                return OnPoiseDamageValidation.Invoke(poise, value);
            return m_IsServer;
        }
        
        private bool ValidatePoiseSet(Poise poise, float value)
        {
            if (OnPoiseSetValidation != null)
                return OnPoiseSetValidation.Invoke(poise, value);
            return m_IsServer;
        }
        
        private bool ValidatePoiseReset(Poise poise, float value)
        {
            if (OnPoiseResetValidation != null)
                return OnPoiseResetValidation.Invoke(poise, value);
            return m_IsServer;
        }
        
        private void HandlePoiseDamaged(Poise poise, float value, bool poiseBroken)
        {
            OnPoiseDamaged?.Invoke(poise, value, poiseBroken);
        }
        
        private bool ValidateJump(Jump jump)
        {
            if (OnJumpValidation != null)
                return OnJumpValidation.Invoke(jump);
            return m_IsServer;
        }
        
        private bool ValidateJumpForce(Jump jump, float force)
        {
            if (OnJumpForceValidation != null)
                return OnJumpForceValidation.Invoke(jump, force);
            return m_IsServer;
        }
        
        private void HandleJumpExecuted(Jump jump, float force)
        {
            OnJumpExecuted?.Invoke(jump, force);
        }
        
        private bool ValidateDash(Dash dash, Vector3 direction, float speed, float gravity, float duration, float fade)
        {
            if (OnDashValidation != null)
                return OnDashValidation.Invoke(dash, direction, speed, gravity, duration, fade);
            return m_IsServer;
        }
        
        private void HandleDashStarted(Dash dash, Vector3 direction, float speed, float gravity, float duration, float fade)
        {
            OnDashStarted?.Invoke(dash, direction, speed, gravity, duration, fade);
        }
        
        private void HandleDashFinished(Dash dash)
        {
            OnDashFinished?.Invoke(dash);
        }
        
        private bool ValidateDeath(Character character, bool isDead)
        {
            if (OnDeathValidation != null)
                return OnDeathValidation.Invoke(character, isDead);
            return m_IsServer;
        }
        
        private void HandleDeathStateChanged(Character character, bool isDead)
        {
            OnDeathStateChanged?.Invoke(character, isDead);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // REFLECTION HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static void SetStaticField<T>(string fieldName, object value)
        {
            try
            {
                var field = typeof(T).GetField(fieldName,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (field == null)
                {
                    Debug.LogWarning(
                        $"[NetworkCorePatchHooks] Missing patched field {typeof(T).Name}.{fieldName}. GC2 update likely changed signatures.");
                    return;
                }

                field.SetValue(null, value);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkCorePatchHooks] Could not set {typeof(T).Name}.{fieldName}: {e.Message}");
            }
        }
        
        private static void TryCallMethod<T>(T instance, string methodName, params object[] args) where T : class
        {
            try
            {
                var method = typeof(T).GetMethod(methodName,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (method == null)
                {
                    Debug.LogWarning(
                        $"[NetworkCorePatchHooks] Missing method {typeof(T).Name}.{methodName}. GC2 update likely changed signatures.");
                    return;
                }

                method.Invoke(instance, args);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkCorePatchHooks] Could not call {typeof(T).Name}.{methodName}: {e.Message}");
            }
        }
        
        private static TResult TryCallMethod<TResult>(object instance, string methodName, params object[] args)
        {
            try
            {
                var method = instance.GetType().GetMethod(methodName,
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
                if (method != null)
                {
                    return (TResult)method.Invoke(instance, args);
                }

                Debug.LogWarning(
                    $"[NetworkCorePatchHooks] Missing method {instance.GetType().Name}.{methodName}. GC2 update likely changed signatures.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkCorePatchHooks] Could not call {instance.GetType().Name}.{methodName}: {e.Message}");
            }
            return default;
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            var field = type.GetField(
                fieldName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }

        private static bool HasPublicStaticProperty(Type type, string propertyName, Type expectedPropertyType)
        {
            var property = type.GetProperty(
                propertyName,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            return property != null && expectedPropertyType.IsAssignableFrom(property.PropertyType);
        }

        private static bool HasInstanceMethod(Type type, string methodName, params Type[] parameterTypes)
        {
            var method = type.GetMethod(
                methodName,
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance,
                null,
                parameterTypes,
                null);

            return method != null;
        }
    }
}
