using System;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    public class Poise
    {
        // PROPERTIES: ----------------------------------------------------------------------------

        [field: NonSerialized] public float Maximum { get; private set; }
        [field: NonSerialized] public float Current { get; private set; }

        public bool IsBroken => this.Maximum > 0f && this.Current <= 0f;
        
        public float Ratio => this.Maximum > 0f ? Mathf.Clamp01(this.Current / this.Maximum) : 1f;

        // EVENTS: --------------------------------------------------------------------------------

        public event Action EventChange;
        public event Action EventPoiseBreak;

        // CONSTRUCTOR: ---------------------------------------------------------------------------

        public Poise()
        {
            this.Maximum = 1f;
            this.Current = 1f;
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        public void Reset(float value)
        {
            this.Maximum = value;
            this.Current = value;
            
            this.EventChange?.Invoke();
        }

        public void Set(float value)
        {
            this.Current = Math.Clamp(value, 0f, this.Maximum);
        }

        public bool Damage(float value)
        {
            this.Current -= Math.Min(this.Current, value);
            this.EventChange?.Invoke();

            if (this.Current > 0f) return false;
            
            this.EventPoiseBreak?.Invoke();
            return true;
        }
    }
}