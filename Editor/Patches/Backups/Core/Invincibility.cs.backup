using System;

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Invincibility
    {
        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private float m_InvincibleStartTime;
        [NonSerialized] private float m_InvincibleUntil;
        
        [NonSerialized] private bool m_WasInvincible;

        [NonSerialized] private Character m_Character;

        // PROPERTIES: ----------------------------------------------------------------------------

        public bool IsInvincible => this.m_Character != null && 
                             this.m_Character.Time.Time <= this.m_InvincibleUntil;
        
        public float StartTime
        {
            get
            {
                if (!this.IsInvincible) return -1f;
                return this.m_InvincibleStartTime;
            }
        }
        
        public float Duration
        {
            get
            {
                if (!this.IsInvincible) return -1f;
                return this.m_InvincibleUntil - this.m_InvincibleStartTime;
            }
        }
        
        // EVENTS: --------------------------------------------------------------------------------

        public event Action<bool> EventChange;
        public event Action EventBecomeInvincible;
        public event Action EventBecomeVincible;

        // INTERNAL METHODS: ----------------------------------------------------------------------

        internal void OnEnable(Character character)
        {
            this.m_Character = character;
            this.m_InvincibleUntil = -1f;
            this.m_WasInvincible = false;
        }

        internal void OnDisable(Character character)
        {
            this.m_InvincibleUntil = -1f;
            this.m_WasInvincible = false;
        }

        internal void OnUpdate()
        {
            bool isInvincible = this.IsInvincible;
            if (isInvincible == this.m_WasInvincible) return;

            switch (this.IsInvincible)
            {
                case true: this.EventBecomeInvincible?.Invoke(); break;
                case false: this.EventBecomeVincible?.Invoke(); break;
            }

            this.EventChange?.Invoke(isInvincible);
            this.m_WasInvincible = isInvincible;
        }

        // PUBLIC METHODS: ------------------------------------------------------------------------

        public void Set(float duration)
        {
            if (this.m_Character == null) return;

            this.m_InvincibleStartTime = this.m_Character.Time.Time;
            this.m_InvincibleUntil = Math.Max(
                this.m_InvincibleUntil,
                this.m_Character.Time.Time + duration
            );
        }
    }
}