using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Generic singleton base class for network managers and controllers.
    /// Provides standardized instance management with configurable duplicate handling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Usage:</b>
    /// Inherit from this class and override hooks as needed:
    /// <code>
    /// public class MyManager : NetworkSingleton&lt;MyManager&gt;
    /// {
    ///     protected override void OnSingletonAwake() { /* init logic */ }
    ///     protected override void OnSingletonCleanup() { /* cleanup logic */ }
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// <b>Duplicate Handling:</b>
    /// Override <see cref="OnDuplicatePolicy"/> to change behavior when a second instance is detected.
    /// Default is <see cref="DuplicatePolicy.DestroyGameObject"/>.
    /// </para>
    /// <para>
    /// <b>Lazy Find:</b>
    /// For managers that should auto-discover via <c>FindFirstObjectByType</c>,
    /// shadow the <c>Instance</c> property with <c>new static</c> in the subclass:
    /// <code>
    /// public new static MyManager Instance
    /// {
    ///     get
    ///     {
    ///         if (s_Instance == null)
    ///             s_Instance = FindFirstObjectByType&lt;MyManager&gt;();
    ///         return s_Instance;
    ///     }
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    /// <typeparam name="T">The concrete singleton type.</typeparam>
    public abstract class NetworkSingleton<T> : MonoBehaviour where T : NetworkSingleton<T>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DUPLICATE POLICY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>How duplicate singleton instances are handled.</summary>
        protected enum DuplicatePolicy
        {
            /// <summary>Destroy the entire GameObject of the duplicate.</summary>
            DestroyGameObject,
            
            /// <summary>Destroy only the duplicate component, leaving the GameObject intact.</summary>
            DestroyComponent,
            
            /// <summary>Log a warning but keep both instances alive. Only the first is the singleton.</summary>
            WarnOnly
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Backing field for the singleton instance. Accessible to subclasses for lazy-find overrides.</summary>
        protected static T s_Instance;
        
        /// <summary>The singleton instance, or <c>null</c> if none exists.</summary>
        public static T Instance => s_Instance;
        
        /// <summary>Whether a singleton instance currently exists.</summary>
        public static bool HasInstance => s_Instance != null;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Override to change how duplicate instances are handled.
        /// Default: <see cref="DuplicatePolicy.DestroyGameObject"/>.
        /// </summary>
        protected virtual DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.DestroyGameObject;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected virtual void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                HandleDuplicate();
                return;
            }
            
            s_Instance = (T)this;
            OnSingletonAwake();
        }
        
        protected virtual void OnDestroy()
        {
            if (s_Instance == (T)this)
            {
                OnSingletonCleanup();
                s_Instance = null;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HOOKS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called once when this instance claims the singleton slot.
        /// Override instead of <c>Awake()</c> for initialization logic.
        /// </summary>
        protected virtual void OnSingletonAwake() { }
        
        /// <summary>
        /// Called when the singleton instance is being destroyed, before the slot is cleared.
        /// Override instead of <c>OnDestroy()</c> for cleanup logic.
        /// </summary>
        protected virtual void OnSingletonCleanup() { }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTERNAL
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void HandleDuplicate()
        {
            string typeName = typeof(T).Name;
            
            switch (OnDuplicatePolicy)
            {
                case DuplicatePolicy.DestroyGameObject:
                    Debug.LogWarning($"[{typeName}] Duplicate instance destroyed.");
                    Destroy(gameObject);
                    break;
                    
                case DuplicatePolicy.DestroyComponent:
                    Debug.LogWarning($"[{typeName}] Duplicate component destroyed.");
                    Destroy(this);
                    break;
                    
                case DuplicatePolicy.WarnOnly:
                    Debug.LogWarning($"[{typeName}] Multiple instances detected. Using first.");
                    break;
            }
        }
    }
}
