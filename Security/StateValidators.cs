using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Security
{
    /// <summary>
    /// Base class for server-side state validation.
    /// Tracks authoritative state and detects discrepancies.
    /// </summary>
    /// <typeparam name="TKey">Key type for identifying entities (usually uint networkId).</typeparam>
    /// <typeparam name="TState">State structure to track.</typeparam>
    public abstract class StateValidator<TKey, TState> where TState : struct, IEquatable<TState>
    {
        protected readonly Dictionary<TKey, TState> m_AuthoritativeState = new(64);
        protected readonly Dictionary<TKey, TState> m_LastReportedClientState = new(64);
        protected readonly Dictionary<TKey, float> m_LastValidation = new(64);
        protected readonly Dictionary<TKey, int> m_DiscrepancyCount = new(64);
        
        protected readonly string m_ModuleName;
        protected readonly float m_ValidationInterval;
        protected readonly int m_MaxDiscrepancies;
        
        public StateValidator(string moduleName, float validationInterval = 2f, int maxDiscrepancies = 3)
        {
            m_ModuleName = moduleName;
            m_ValidationInterval = validationInterval;
            m_MaxDiscrepancies = maxDiscrepancies;
        }
        
        /// <summary>
        /// Set the authoritative state for an entity.
        /// </summary>
        public void SetAuthoritativeState(TKey key, TState state)
        {
            m_AuthoritativeState[key] = state;
        }
        
        /// <summary>
        /// Get the authoritative state for an entity.
        /// </summary>
        public bool TryGetAuthoritativeState(TKey key, out TState state)
        {
            return m_AuthoritativeState.TryGetValue(key, out state);
        }
        
        /// <summary>
        /// Report client state and check for discrepancies.
        /// </summary>
        /// <returns>True if state is valid, false if discrepancy detected.</returns>
        public bool ValidateClientState(TKey key, TState clientState, float currentTime, 
            out StateDiscrepancy discrepancy)
        {
            discrepancy = default;
            
            // Check if we should validate this tick
            if (m_LastValidation.TryGetValue(key, out float lastTime))
            {
                if (currentTime - lastTime < m_ValidationInterval)
                {
                    // Store for next validation
                    m_LastReportedClientState[key] = clientState;
                    return true;
                }
            }
            
            m_LastValidation[key] = currentTime;
            m_LastReportedClientState[key] = clientState;
            
            // Compare with authoritative state
            if (!m_AuthoritativeState.TryGetValue(key, out TState authState))
            {
                // No authoritative state yet - accept client state
                m_AuthoritativeState[key] = clientState;
                return true;
            }
            
            // Check for discrepancy
            if (!CompareStates(authState, clientState, out string details))
            {
                // Discrepancy found
                if (!m_DiscrepancyCount.TryGetValue(key, out int count))
                {
                    count = 0;
                }
                
                count++;
                m_DiscrepancyCount[key] = count;
                
                discrepancy = new StateDiscrepancy
                {
                    Key = key.ToString(),
                    Module = m_ModuleName,
                    Details = details,
                    DiscrepancyCount = count,
                    ExceedsThreshold = count >= m_MaxDiscrepancies
                };
                
                return false;
            }
            
            // Valid - reset discrepancy count
            m_DiscrepancyCount[key] = 0;
            return true;
        }
        
        /// <summary>
        /// Compare two states. Override to implement custom comparison logic.
        /// </summary>
        /// <param name="authoritative">Server-authoritative state.</param>
        /// <param name="reported">Client-reported state.</param>
        /// <param name="details">Description of discrepancy if found.</param>
        /// <returns>True if states match, false if discrepancy.</returns>
        protected abstract bool CompareStates(TState authoritative, TState reported, out string details);
        
        /// <summary>
        /// Force reconciliation by sending authoritative state to client.
        /// </summary>
        public TState GetReconciliationState(TKey key)
        {
            if (m_AuthoritativeState.TryGetValue(key, out TState state))
            {
                return state;
            }
            return default;
        }
        
        /// <summary>
        /// Remove tracking for an entity.
        /// </summary>
        public void RemoveEntity(TKey key)
        {
            m_AuthoritativeState.Remove(key);
            m_LastReportedClientState.Remove(key);
            m_LastValidation.Remove(key);
            m_DiscrepancyCount.Remove(key);
        }
        
        /// <summary>
        /// Clear all tracking data.
        /// </summary>
        public void Clear()
        {
            m_AuthoritativeState.Clear();
            m_LastReportedClientState.Clear();
            m_LastValidation.Clear();
            m_DiscrepancyCount.Clear();
        }
        
        /// <summary>
        /// Get all entities with active discrepancies.
        /// </summary>
        public IEnumerable<TKey> GetEntitiesWithDiscrepancies()
        {
            foreach (var kvp in m_DiscrepancyCount)
            {
                if (kvp.Value > 0)
                {
                    yield return kvp.Key;
                }
            }
        }
    }
    
    /// <summary>
    /// Describes a state discrepancy.
    /// </summary>
    public struct StateDiscrepancy
    {
        public string Key;
        public string Module;
        public string Details;
        public int DiscrepancyCount;
        public bool ExceedsThreshold;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // CONCRETE STATE VALIDATORS
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// State structure for character core features.
    /// </summary>
    public struct CharacterCoreState : IEquatable<CharacterCoreState>
    {
        public bool IsRagdoll;
        public bool IsInvincible;
        public float InvincibleEndTime;
        public bool IsBusy;
        public int ActivePropCount;
        public float CurrentPoise;
        public float MaxPoise;
        
        public bool Equals(CharacterCoreState other)
        {
            return IsRagdoll == other.IsRagdoll &&
                   IsInvincible == other.IsInvincible &&
                   IsBusy == other.IsBusy &&
                   ActivePropCount == other.ActivePropCount &&
                   Mathf.Approximately(CurrentPoise, other.CurrentPoise);
        }
        
        public override bool Equals(object obj) => obj is CharacterCoreState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(IsRagdoll, IsInvincible, IsBusy, ActivePropCount);
    }
    
    /// <summary>
    /// Validator for character core state.
    /// </summary>
    public class CharacterCoreStateValidator : StateValidator<uint, CharacterCoreState>
    {
        private const float POISE_TOLERANCE = 0.5f;
        
        public CharacterCoreStateValidator() : base("Core", 2f, 3) { }
        
        protected override bool CompareStates(CharacterCoreState auth, CharacterCoreState reported, out string details)
        {
            details = null;
            
            if (auth.IsRagdoll != reported.IsRagdoll)
            {
                details = $"Ragdoll mismatch: server={auth.IsRagdoll}, client={reported.IsRagdoll}";
                return false;
            }
            
            if (auth.IsInvincible != reported.IsInvincible)
            {
                details = $"Invincible mismatch: server={auth.IsInvincible}, client={reported.IsInvincible}";
                return false;
            }
            
            if (auth.IsBusy != reported.IsBusy)
            {
                details = $"Busy mismatch: server={auth.IsBusy}, client={reported.IsBusy}";
                return false;
            }
            
            if (Mathf.Abs(auth.CurrentPoise - reported.CurrentPoise) > POISE_TOLERANCE)
            {
                details = $"Poise mismatch: server={auth.CurrentPoise}, client={reported.CurrentPoise}";
                return false;
            }
            
            return true;
        }
    }
    
    /// <summary>
    /// State structure for stats.
    /// </summary>
    public struct StatsState : IEquatable<StatsState>
    {
        public Dictionary<int, float> StatValues;
        public Dictionary<int, float> AttributeValues;
        public HashSet<int> ActiveStatusEffects;
        
        public bool Equals(StatsState other)
        {
            // Simplified comparison - full implementation would check each value
            return StatValues?.Count == other.StatValues?.Count &&
                   AttributeValues?.Count == other.AttributeValues?.Count &&
                   ActiveStatusEffects?.Count == other.ActiveStatusEffects?.Count;
        }
        
        public override bool Equals(object obj) => obj is StatsState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StatValues?.Count, AttributeValues?.Count);
    }
    
    /// <summary>
    /// Validator for stats state.
    /// </summary>
    public class StatsStateValidator : StateValidator<uint, StatsState>
    {
        private const float STAT_TOLERANCE = 0.01f;
        private const float ATTRIBUTE_TOLERANCE = 0.5f;
        
        public StatsStateValidator() : base("Stats", 2f, 3) { }
        
        protected override bool CompareStates(StatsState auth, StatsState reported, out string details)
        {
            details = null;
            
            if (auth.StatValues != null && reported.StatValues != null)
            {
                foreach (var kvp in auth.StatValues)
                {
                    if (reported.StatValues.TryGetValue(kvp.Key, out float clientValue))
                    {
                        if (Mathf.Abs(kvp.Value - clientValue) > STAT_TOLERANCE)
                        {
                            details = $"Stat {kvp.Key} mismatch: server={kvp.Value}, client={clientValue}";
                            return false;
                        }
                    }
                }
            }
            
            if (auth.AttributeValues != null && reported.AttributeValues != null)
            {
                foreach (var kvp in auth.AttributeValues)
                {
                    if (reported.AttributeValues.TryGetValue(kvp.Key, out float clientValue))
                    {
                        if (Mathf.Abs(kvp.Value - clientValue) > ATTRIBUTE_TOLERANCE)
                        {
                            details = $"Attribute {kvp.Key} mismatch: server={kvp.Value}, client={clientValue}";
                            return false;
                        }
                    }
                }
            }
            
            return true;
        }
    }
    
    /// <summary>
    /// State structure for inventory.
    /// </summary>
    public struct InventoryState : IEquatable<InventoryState>
    {
        public int ItemCount;
        public Dictionary<int, int> CurrencyAmounts;
        public HashSet<long> EquippedItems;
        
        public bool Equals(InventoryState other)
        {
            return ItemCount == other.ItemCount &&
                   CurrencyAmounts?.Count == other.CurrencyAmounts?.Count;
        }
        
        public override bool Equals(object obj) => obj is InventoryState other && Equals(other);
        public override int GetHashCode() => ItemCount.GetHashCode();
    }
    
    /// <summary>
    /// Validator for inventory state.
    /// </summary>
    public class InventoryStateValidator : StateValidator<uint, InventoryState>
    {
        public InventoryStateValidator() : base("Inventory", 5f, 2) { }
        
        protected override bool CompareStates(InventoryState auth, InventoryState reported, out string details)
        {
            details = null;
            
            if (auth.ItemCount != reported.ItemCount)
            {
                details = $"Item count mismatch: server={auth.ItemCount}, client={reported.ItemCount}";
                return false;
            }
            
            if (auth.CurrencyAmounts != null && reported.CurrencyAmounts != null)
            {
                foreach (var kvp in auth.CurrencyAmounts)
                {
                    if (reported.CurrencyAmounts.TryGetValue(kvp.Key, out int clientAmount))
                    {
                        if (kvp.Value != clientAmount)
                        {
                            details = $"Currency {kvp.Key} mismatch: server={kvp.Value}, client={clientAmount}";
                            return false;
                        }
                    }
                }
            }
            
            return true;
        }
    }
    
    /// <summary>
    /// State structure for combat (melee/shooter).
    /// </summary>
    public struct CombatState : IEquatable<CombatState>
    {
        public bool IsAttacking;
        public bool IsBlocking;
        public bool IsAiming;
        public int CurrentWeaponHash;
        public int CurrentAmmo;
        public bool IsReloading;
        public float ChargeLevel;
        
        public bool Equals(CombatState other)
        {
            return IsAttacking == other.IsAttacking &&
                   IsBlocking == other.IsBlocking &&
                   IsAiming == other.IsAiming &&
                   CurrentWeaponHash == other.CurrentWeaponHash;
        }
        
        public override bool Equals(object obj) => obj is CombatState other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(IsAttacking, IsBlocking, CurrentWeaponHash);
    }
    
    /// <summary>
    /// Validator for combat state.
    /// </summary>
    public class CombatStateValidator : StateValidator<uint, CombatState>
    {
        public CombatStateValidator(string moduleName) : base(moduleName, 1f, 5) { }
        
        protected override bool CompareStates(CombatState auth, CombatState reported, out string details)
        {
            details = null;
            
            // Combat state can change rapidly, so we're lenient
            if (auth.CurrentWeaponHash != reported.CurrentWeaponHash)
            {
                details = $"Weapon mismatch: server={auth.CurrentWeaponHash}, client={reported.CurrentWeaponHash}";
                return false;
            }
            
            // Ammo is critical for shooter
            if (auth.CurrentAmmo != reported.CurrentAmmo)
            {
                int diff = Mathf.Abs(auth.CurrentAmmo - reported.CurrentAmmo);
                if (diff > 2) // Allow small tolerance for network delay
                {
                    details = $"Ammo mismatch: server={auth.CurrentAmmo}, client={reported.CurrentAmmo}";
                    return false;
                }
            }
            
            return true;
        }
    }
}
