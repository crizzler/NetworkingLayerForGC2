using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Stats
{
    public class RuntimeStatData
    {
        [NonSerialized] private readonly GameObject m_Self;
        [NonSerialized] private readonly Stat m_Stat;
        [NonSerialized] private readonly Modifiers m_Modifiers;

        [NonSerialized] private double m_Base;
        [NonSerialized] private readonly Formula m_Formula;

        // PROPERTIES: ----------------------------------------------------------------------------

        /// <summary>
        /// The unprocessed value of the stat. No formula nor modifiers are applied.
        /// </summary>
        public double Base
        {
            get => this.m_Base;
            set
            {
                if (Math.Abs(this.m_Base - value) < float.Epsilon) return;

                double prevValue = this.Value;
                this.m_Base = value;

                this.EventChange?.Invoke(this.m_Stat.ID, this.Value - prevValue);
            }
        }

        /// <summary>
        /// The stat value with the formula and stat modifiers applied.
        /// </summary>
        public double Value
        {
            get
            {
                double value = this.m_Formula != null && this.m_Formula.Exists
                    ? this.m_Formula.Calculate(this.m_Self, this.m_Self)
                    : this.m_Base;

                return this.m_Modifiers.Calculate(value);
            }
        }

        /// <summary>
        /// The amount modifiers contribute to the resulting stat value. 
        /// </summary>
        public double ModifiersValue
        {
            get
            {
                double value = this.m_Formula != null && this.m_Formula.Exists
                    ? this.m_Formula.Calculate(this.m_Self, this.m_Self)
                    : this.m_Base;

                return this.m_Modifiers.Calculate(value) - value;
            }
        }

        public bool HasModifiers => this.m_Modifiers.Count > 0;

        // EVENTS: --------------------------------------------------------------------------------

        public event Action<IdString, double> EventChange;

        // CONSTRUCTORS: --------------------------------------------------------------------------

        public RuntimeStatData(GameObject self, StatItem stat)
        {
            this.m_Self = self;
            this.m_Stat = stat.Stat;
            this.m_Modifiers = new Modifiers(stat.Stat.ID.Hash);
            
            this.m_Base = stat.Base;
            this.m_Formula = stat.Formula;
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------

        /// <summary>
        /// Adds a constant stat modifier that increments or decrements the resulting stat value
        /// </summary>
        /// <example>Constant stat modifiers simply add up to the stat value</example>
        /// <example>Percentage stat modifiers increase the value once all other constant stat
        /// modifiers have been added. A value of 0.1 increases the stat by 10%</example>
        /// <param name="type"></param>
        /// <param name="value"></param>
        public void AddModifier(ModifierType type, double value)
        {
            switch (type)
            {
                case ModifierType.Constant: this.m_Modifiers.AddConstant(value); break;
                case ModifierType.Percent: this.m_Modifiers.AddPercentage(value); break;
                default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
            
            this.EventChange?.Invoke(this.m_Stat.ID, 0f);
        }

        /// <summary>
        /// Removes a stat modifier (or equivalent) from the current list of modifiers. Returns
        /// true if a similar stat modifier is found and successfully removed. False otherwise.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        public bool RemoveModifier(ModifierType type, double value)
        {
            bool success = type switch
            {
                ModifierType.Constant => this.m_Modifiers.RemoveConstant(value),
                ModifierType.Percent => this.m_Modifiers.RemovePercentage(value),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
            
            if (success) this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            return success;
        }

        /// <summary>
        /// Removes all Stat Modifiers applied to this Stat
        /// </summary>
        public void ClearModifiers()
        {
            if (this.m_Modifiers.Count > 0)
            {
                this.m_Modifiers.Clear();
                this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            }
        }
        
        // INTERNAL METHODS: ----------------------------------------------------------------------

        internal void SetBaseWithoutNotify(double value)
        {
            this.m_Base = value;
        }
    }
}