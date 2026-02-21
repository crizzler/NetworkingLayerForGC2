using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Stats
{
    public class RuntimeAttributeData
    {
        private readonly Traits m_Traits;
        private readonly Attribute m_Attribute;

        private readonly double m_MinValue;
        private readonly Stat m_MaxValue;
        private double m_Value;
        
        // PROPERTIES: ----------------------------------------------------------------------------

        public double MinValue => this.m_MinValue;
        public double MaxValue => this.m_MaxValue != null
            ? this.m_Traits.RuntimeStats.Get(this.m_MaxValue.ID).Value
            : 0f;

        public double Value
        {
            get => this.m_Value;
            set
            {
                double oldValue = this.Value;
                double newValue = Math.Clamp(value, this.MinValue, this.MaxValue);
                if (Math.Abs(this.m_Value - newValue) < float.Epsilon) return;

                this.m_Value = newValue;
                this.EventChange?.Invoke(this.m_Attribute.ID, newValue - oldValue);
            }
        }

        public double Ratio => (this.Value - this.MinValue) / (this.MaxValue - this.MinValue); 

        // EVENTS: --------------------------------------------------------------------------------

        public event Action<IdString, double> EventChange;
        
        // CONSTRUCTORS: --------------------------------------------------------------------------

        public RuntimeAttributeData(Traits traits, AttributeItem attribute)
        {
            this.m_Traits = traits;
            this.m_Attribute = attribute.Attribute;

            this.m_MinValue = attribute.MinValue;
            this.m_MaxValue = attribute.MaxValue;
            
            this.m_Value = MathUtils.Lerp(this.MinValue, this.MaxValue, attribute.StartPercent);

            if (this.m_MaxValue != null)
            {
                traits.RuntimeStats.EventChange += this.RecalculateValue;
            }
        }
        
        // PRIVATE METHODS: -----------------------------------------------------------------------

        private void RecalculateValue(IdString statID)
        {
            double value = Math.Clamp(this.m_Value, this.MinValue, this.MaxValue); 
            if (Math.Abs(this.Value - value) < float.Epsilon) return;
            
            this.Value = this.m_Value;
        }
        
        // INTERNAL METHODS: ----------------------------------------------------------------------

        internal void SetValueWithoutNotify(double value)
        {
            this.m_Value = value;
        }
    }
}