using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    [Serializable]
    public class Dash
    {
        private static readonly int HASH = Tween.GetHash(typeof(Transform), "position");
        private const int GRAVITY_INFLUENCE_KEY = 1;
        
        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private Character m_Character;
        
        [NonSerialized] private bool m_HasDodged;
        
        [NonSerialized] private float m_LastDashFinishTime = -100f;
        [NonSerialized] private int m_NumDashes;

        // PROPERTIES: ----------------------------------------------------------------------------

        [field: NonSerialized] public bool IsDashing { get; private set; }

        public bool IsDodge => this.IsDashing && this.m_Character.Combat.Invincibility.IsInvincible;

        public int NumDashes
        {
            get => this.m_NumDashes;
            set => this.m_NumDashes = value;
        }

        // EVENTS: --------------------------------------------------------------------------------

        public event Action EventDashStart;
        public event Action EventDashFinish;
        
        public event Action EventDodge;

        // INITIALIZE METHODS: --------------------------------------------------------------------
        
        internal void OnStartup(Character character)
        {
            this.m_Character = character;
        }
        
        internal void AfterStartup(Character character)
        { }

        internal void OnDispose(Character character)
        {
            this.m_Character = character;
        }

        internal void OnEnable()
        { }

        internal void OnDisable()
        { }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------

        public bool CanDash()
        {
            if (this.m_Character.Busy.AreLegsBusy) return false;

            bool canDashInAir = this.m_Character.Motion.DashInAir;
            if (!canDashInAir && !this.m_Character.Driver.IsGrounded) return false;

            if (this.IsDashing) return false;

            float resetTime = this.m_LastDashFinishTime + this.m_Character.Motion.DashCooldown;
            if (this.m_Character.Time.Time >= resetTime) return true;
            
            return this.m_NumDashes <= this.m_Character.Motion.DashInSuccession;
        }
        
        public async Task Execute(Vector3 direction, float speed, float gravity, float duration, float fade)
        {
            this.m_HasDodged = false;

            float resetTime = this.m_LastDashFinishTime + this.m_Character.Motion.DashCooldown;
            this.m_NumDashes = this.m_Character.Time.Time < resetTime 
                ? this.m_NumDashes + 1
                : 1;
            
            this.IsDashing = true;
            if (!Mathf.Approximately(gravity, 1f))
            {
                this.m_Character.Driver.SetGravityInfluence(GRAVITY_INFLUENCE_KEY, gravity);
            }
            
            this.EventDashStart?.Invoke();

            direction = Vector3.Scale(direction, Vector3Plane.NormalUp);
            direction = direction.sqrMagnitude > float.Epsilon ? direction.normalized : Vector3.forward;
            
            this.m_Character.Motion.SetMotionTransient(direction, speed, duration, fade);
            
            TweenInput<float> input = new TweenInput<float>(
                0f, 1f, duration,
                HASH, Easing.Type.Linear
            );
            
            input.EventFinish += this.OnDashFinish;
            
            Tween.To(this.m_Character.gameObject, input);
            while (this.IsDashing && !ApplicationManager.IsExiting) await Task.Yield();
            
            this.m_LastDashFinishTime = this.m_Character.Time.Time;
        }

        public void Cancel()
        {
            if (!this.IsDashing) return;
            Tween.Cancel(this.m_Character.gameObject, HASH);
        }

        // CALLBACKS: -----------------------------------------------------------------------------

        public void OnDodge(Args args)
        {
            if (this.m_HasDodged) return;

            foreach (Weapon weapon in this.m_Character.Combat.Weapons)
            {
                weapon.Asset.RunOnDodge(this.m_Character, args);
            }
            
            this.EventDodge?.Invoke();
            this.m_HasDodged = true;
        }

        private void OnDashFinish(bool isComplete)
        {
            this.m_Character.Busy.RemoveLegsBusy();

            this.IsDashing = false;
            this.m_Character.Driver.RemoveGravityInfluence(GRAVITY_INFLUENCE_KEY);
            
            this.EventDashFinish?.Invoke();
        }
    }
}