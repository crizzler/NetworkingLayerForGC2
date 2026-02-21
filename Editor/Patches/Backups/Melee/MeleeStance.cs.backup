using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Melee
{
    public class MeleeStance : TStance
    {
        public static readonly int ID = "Melee".GetHashCode();
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private readonly Input m_Input;

        [NonSerialized] private readonly Attacks m_Attacks;

        // PROPERTIES: ----------------------------------------------------------------------------

        public override int Id => ID;
        
        public MeleePhase CurrentPhase => this.m_Attacks.Phase;
        
        [field: NonSerialized] public bool LastCancelSuccessful { get; private set; }
        
        [field: NonSerialized] public override Character Character { get; set; }
        [field: NonSerialized] public Args Args { get; private set; }
        
        public float BufferWindow
        {
            get => this.m_Input.BufferWindow;
            set => this.m_Input.BufferWindow = value;
        }
        
        // EVENTS: --------------------------------------------------------------------------------

        public event Action<MeleeKey> EventInputCharge;
        public event Action<MeleeKey> EventInputExecute;

        // CONSTRUCTOR: ---------------------------------------------------------------------------

        public MeleeStance()
        {
            this.m_Input = new Input(this);
            this.m_Attacks = new Attacks(this, this.m_Input);
        }

        // STANCE METHODS: ------------------------------------------------------------------------
        
        public override void OnEnable(Character character)
        {
            if (this.IsEnabled) return;
            base.OnEnable(character);
            
            this.Character = character;
            this.Args ??= new Args(
                this.Character.gameObject,
                this.Character.Combat.Targets.Primary
            );
            
            this.m_Attacks.ForceCancel();
            this.m_Attacks.ToNone();
            
            this.m_Input.Cancel();

            this.Character.Combat.Targets.EventChangeTarget -= this.OnChangeTarget;
            this.Character.Combat.Targets.EventChangeTarget += this.OnChangeTarget;
            
            this.m_Input.EventUseCharge += this.OnInputCharge;
            this.m_Input.EventUseExecute += this.OnInputExecute;
        }

        public override void OnDisable(Character character)
        {
            base.OnDisable(character);
            this.Character.Combat.Targets.EventChangeTarget -= this.OnChangeTarget;
            
            this.m_Input.EventUseCharge -= this.OnInputCharge;
            this.m_Input.EventUseExecute -= this.OnInputExecute;
        }

        public override void OnUpdate()
        {
            this.m_Attacks.Update();
        }

        // INPUT METHODS: -------------------------------------------------------------------------
        
        public void InputCharge(MeleeKey key)
        {
            this.m_Input.InputCharge(key);
        }
        
        public void InputExecute(MeleeKey key)
        {
            this.m_Input.InputExecute(key);
        }

        // PUBLIC METHODS: ------------------------------------------------------------------------

        public void PlayReaction(GameObject from, ReactionInput input, IReaction withReaction, bool canFallback)
        {
            this.m_Attacks.ToReact(from, withReaction, input, canFallback);
        }

        public void PlaySkill(MeleeWeapon weapon, Skill skill, GameObject target)
        {
            this.m_Attacks.ToSkill(weapon, skill, target);
            this.m_Input.Cancel();
        }

        public void ForceCancel()
        {
            this.m_Attacks.ForceCancel();
            this.m_Input.Cancel();
        }
        
        public void TryToCancel()
        {
            this.LastCancelSuccessful = this.m_Attacks.TryToCancel();
            
            if (!this.LastCancelSuccessful) return;
            this.m_Input.Cancel();
        }
        
        public void ResetHitsBuffer()
        {
            this.m_Attacks.ResetHitsBuffer();
        }

        public void Hit(Character attacker, ReactionInput input, Skill skill)
        {
            if (skill.SyncReaction != null) return;
            Args args = new Args(this.Character, attacker);
            
            bool isAttacking = 
                this.m_Attacks.Phase == MeleePhase.Anticipation ||
                this.m_Attacks.Phase == MeleePhase.Strike       ||
                this.m_Attacks.Phase == MeleePhase.Recovery; 
            
            if (isAttacking)
            {
                float damage = skill.GetPoiseDamage(args);
                bool poiseBroken = this.Character.Combat.Poise.Damage(damage);
                
                if (!poiseBroken) return;
            }
            
            this.PlayReaction(attacker.gameObject, input, null, true);
        }
        
        // PRIVATE METHODS: -----------------------------------------------------------------------
        
        private void OnChangeTarget(GameObject newTarget)
        {
            this.Args.ChangeTarget(newTarget);
        }

        private void OnInputCharge(MeleeKey key) => this.EventInputCharge?.Invoke(key);
        private void OnInputExecute(MeleeKey key) => this.EventInputExecute?.Invoke(key);
    }
}