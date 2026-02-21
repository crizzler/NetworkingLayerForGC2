using System;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Jump
    {
        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private Character m_Character;
        [NonSerialized] private int m_RemainingAirJumps;
        
        // PROPERTIES: ----------------------------------------------------------------------------

        public int RemainingAirJumps => this.m_RemainingAirJumps;
        public int AirJumps => this.m_Character.Motion.AirJumps - this.m_RemainingAirJumps;
        
        // EVENTS: --------------------------------------------------------------------------------

        public event Action EventAttemptJump; 

        // INITIALIZE METHODS: --------------------------------------------------------------------
        
        internal void OnStartup(Character character)
        {
            this.m_Character = character;
            this.m_Character.EventLand += this.OnLand;
            this.m_Character.EventJump += this.OnJump;
        }
        
        internal void AfterStartup(Character character)
        { }

        internal void OnDispose(Character character)
        {
            this.m_Character = character;
            this.m_Character.EventLand += this.OnLand;
            this.m_Character.EventJump -= this.OnJump;
        }

        internal void OnEnable()
        { }

        internal void OnDisable()
        { }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------

        public void Do()
        {
            this.EventAttemptJump?.Invoke();
            
            if (!this.CanJump()) return;
            this.m_Character.Motion.Jump();
        }
        
        public void Do(float force)
        {
            this.EventAttemptJump?.Invoke();
            
            if (!this.CanJump()) return;
            this.m_Character.Motion.Jump(force);
        }
        
        public bool CanJump()
        {
            if (this.m_Character.Busy.AreLegsBusy) return false;
            
            if (this.m_Character.Driver.IsGrounded) return true;
            return this.m_RemainingAirJumps > 0;
        }
        
        // CALLBACKS: -----------------------------------------------------------------------------
        
        private void OnLand(float velocity)
        {
            this.m_RemainingAirJumps = this.m_Character.Motion.AirJumps;
        }
        
        private void OnJump(float force)
        {
            if (this.m_Character.Driver.IsGrounded) return;
            this.m_RemainingAirJumps -= 1;
        }
    }
}