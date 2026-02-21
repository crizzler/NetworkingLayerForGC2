using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Common.Audio;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace GameCreator.Runtime.Melee
{
    [CreateAssetMenu(
        fileName = "Skill", 
        menuName = "Game Creator/Melee/Skill",
        order    = 50
    )]
    
    [Icon(EditorPaths.PACKAGES + "Melee/Editor/Gizmos/GizmoSkill.png")]
    
    [Serializable]
    public class Skill : ScriptableObject, IStageGizmos
    {
        private const int LAYER_TIME_SCALE = 999;
        
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] private PropertyGetString m_Title = GetStringString.Create;
        [SerializeField] private PropertyGetString m_Description = GetStringTextArea.Create();

        [SerializeField] private PropertyGetSprite m_Icon = GetSpriteNone.Create;
        [SerializeField] private PropertyGetColor m_Color = GetColorColorsWhite.Create;

        [SerializeField] private SkillCharge m_Charge = new SkillCharge();
        [SerializeField] private SkillStrike m_Strike = new SkillStrike();
        [SerializeField] private SkillTrail m_Trail = new SkillTrail();
        [SerializeField] private SkillEffects m_Effects = new SkillEffects();

        [SerializeField] private AnimationClip m_Animation;
        [SerializeField] private AvatarMask m_Mask;

        [SerializeField] private MeleeMotion m_Motion = MeleeMotion.None;
        [SerializeField] private Reaction m_SyncReaction;

        [SerializeField] [Range(0f, 1f)] private float m_Gravity = 1f;
        [SerializeField] private float m_TransitionIn = 0.1f;
        [SerializeField] private float m_TransitionOut = 0.25f;

        [SerializeField] private RunMeleeSequence m_MeleeSequence = new RunMeleeSequence();
        
        [SerializeField] private PropertyGetDecimal m_SpeedAnticipation = GetDecimalConstantOne.Create;
        [SerializeField] private PropertyGetDecimal m_SpeedStrike = GetDecimalConstantOne.Create;
        [SerializeField] private PropertyGetDecimal m_SpeedRecovery = GetDecimalConstantOne.Create;

        [SerializeField] private RunConditionsList m_CanBlock = new RunConditionsList();
        [SerializeField] private RunConditionsList m_CanParry = new RunConditionsList();
        
        [SerializeField] private PropertyGetDecimal m_PoiseArmor = GetDecimalConstantOne.Create;
        [SerializeField] private PropertyGetDecimal m_PoiseDamage = GetDecimalConstantOne.Create;
        
        [SerializeField] private PropertyGetDecimal m_Power = GetDecimalConstantOne.Create;

        [SerializeField] private RunInstructionsList m_OnStart = new RunInstructionsList();
        [SerializeField] private RunInstructionsList m_OnFinish = new RunInstructionsList();

        [SerializeField] private RunConditionsList m_CanHit = new RunConditionsList();
        [SerializeField] private RunInstructionsList m_OnHit = new RunInstructionsList();

        // PROPERTIES: ----------------------------------------------------------------------------

        public AnimationClip Animation => this.m_Animation;
        public AvatarMask Mask => this.m_Mask;

        public SkillStrike Strike => this.m_Strike;
        public SkillTrail Trail => this.m_Trail;
        
        public MeleeMotion Motion => this.m_Motion;
        public Reaction SyncReaction => this.m_SyncReaction;

        public StateData ChargeState => this.m_Charge.State;
        public int ChargeLayer => this.m_Charge.Layer;

        public float Gravity => this.m_Gravity;
        
        public float TransitionIn => this.m_TransitionIn;
        public float TransitionOut => this.m_TransitionOut;
        
        [field: SerializeField] public string EditorModelPath { get; set; }

        // GETTER METHODS: ------------------------------------------------------------------------

        public string GetName(Args args) => this.m_Title.Get(args);
        public string GetDescription(Args args) => this.m_Description.Get(args);
        
        public Sprite GetSprite(Args args) => this.m_Icon.Get(args);
        public Color GetColor(Args args) => this.m_Color.Get(args);

        public float GetPoiseArmor(Args args) => (float) this.m_PoiseArmor.Get(args);
        public float GetPoiseDamage(Args args) => (float) this.m_PoiseDamage.Get(args);
        
        public float GetPower(Args args) => (float) this.m_Power.Get(args);
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        public AttackSpeed GetSpeed(Args args)
        {
            return new AttackSpeed(
                (float) this.m_SpeedAnticipation.Get(args),
                (float) this.m_SpeedStrike.Get(args),
                (float) this.m_SpeedRecovery.Get(args)                
            );
        }
        
        public AttackDuration GetDuration(AttackSpeed speeds, Args args)
        {
            float duration = this.m_Animation != null ? this.m_Animation.length : 0f;
            TrackMeleePhases track = this.m_MeleeSequence.GetTrack<TrackMeleePhases>();

            if (track == null || track.Clips.Length == 0)
            {
                return new AttackDuration(duration, 0f, 0f);
            }

            if (track.Clips[0] is not ClipMeleePhases phases)
            {
                return new AttackDuration(duration, 0f, 0f);
            }

            return new AttackDuration(
                duration * phases.DurationToStart / speeds.Anticipation,
                duration * phases.Duration / speeds.Strike,
                duration * phases.DurationToEnd / speeds.Recovery
            );
        }

        public AttackRatio GetPhasesDilatedRatios(AttackSpeed speeds)
        {
            return this.m_MeleeSequence.GetPhasesDilatedRatios(speeds);
        }

        public bool CanCancel(float t, AttackSpeed speeds)
        {
            TrackMeleeCancel track = this.m_MeleeSequence.GetTrack<TrackMeleeCancel>();
            
            if (track == null || track.Clips.Length == 0) return true;
            if (track.Clips[0] is not ClipMeleeCancel cancel) return true;
            
            float timeStart = this.m_MeleeSequence.GetDilated(cancel.TimeStart, speeds);
            float timeEnd = this.m_MeleeSequence.GetDilated(cancel.TimeEnd, speeds);
            
            return t < timeStart || t > timeEnd;
        }

        public bool CanUseRootMotionPosition(float t, AttackSpeed speeds)
        {
            TrackMeleeRootMotionPosition track = this.m_MeleeSequence.GetTrack<TrackMeleeRootMotionPosition>();
            
            if (track == null || track.Clips.Length == 0) return true;
            if (track.Clips[0] is not ClipMeleeRootMotionPosition disableRootMotion) return true;

            float timeStart = this.m_MeleeSequence.GetDilated(disableRootMotion.TimeStart, speeds);
            return t > timeStart;
        }
        
        public bool CanUseRootMotionRotation(float t, AttackSpeed speeds)
        {
            TrackMeleeRootMotionRotation track = this.m_MeleeSequence.GetTrack<TrackMeleeRootMotionRotation>();
            
            if (track == null || track.Clips.Length == 0) return true;
            if (track.Clips[0] is not ClipMeleeRootMotionRotation disableRootMotion) return true;

            float timeStart = this.m_MeleeSequence.GetDilated(disableRootMotion.TimeStart, speeds);
            return t > timeStart;
        }

        public float MotionWarpingRatio(float t, AttackSpeed speeds, Args args)
        {
            TrackMeleeMotionWarping track = this.m_MeleeSequence.GetTrack<TrackMeleeMotionWarping>();
            
            if (track == null || track.Clips.Length == 0) return -1f;
            if (track.Clips[0] is not ClipMeleeMotionWarping motionWarping) return -1f;

            if (!motionWarping.CheckConditions(args)) return -1f;

            float timeStart = this.m_MeleeSequence.GetDilated(motionWarping.TimeStart, speeds);
            float timeEnd = this.m_MeleeSequence.GetDilated(motionWarping.TimeEnd, speeds);

            if (t < timeStart || t > timeEnd) return -1f;
            return (t - timeStart) / (timeEnd - timeStart);
        }

        public ClipMeleeMotionWarping GetMotionWarp()
        {
            TrackMeleeMotionWarping track = this.m_MeleeSequence.GetTrack<TrackMeleeMotionWarping>();
            
            if (track == null || track.Clips.Length == 0) return null;
            return track.Clips[0] as ClipMeleeMotionWarping;
        }

        // INTERNAL METHODS: ----------------------------------------------------------------------

        internal void Run(Character character, float duration, AttackSpeed speed, ICancellable cancel, Args args)
        {
            _ = this.m_OnStart.Run(args);
            
            ConfigGesture gestureConfig = new ConfigGesture(
                0f, duration, speed.Anticipation, this.m_Motion != MeleeMotion.None,
                this.m_TransitionIn, this.m_TransitionOut
            );

            _ = character.Gestures.CrossFade(
                this.m_Animation, this.m_Mask, BlendMode.Blend, 
                gestureConfig, true
            );

            AttackDuration attackDuration = this.GetDuration(speed, args);
            
            _ = this.m_MeleeSequence.Run(
                TextUtils.Humanize(this.name),
                character.Time, 
                speed,
                attackDuration, 
                this.m_Animation, 
                cancel, args
            );
            
            AudioClip sound = this.m_Effects.GetSoundUse(args);
            if (sound != null)
            {
                Character self = args.Self != null ? args.Self.Get<Character>() : null;
                TimeMode.UpdateMode time = this.m_Effects.UseUnscaledTime
                    ? TimeMode.UpdateMode.UnscaledTime
                    : self != null
                        ? self.Time.UpdateTime
                        : TimeMode.UpdateMode.GameTime;
                
                AudioConfigSoundEffect soundConfig = AudioConfigSoundEffect.Create(
                    1f, SkillEffects.PITCH, 0f,
                    time, SpatialBlending.Spatial, args.Self
                );
                
                _ = AudioManager.Instance.SoundEffect.Play(sound, soundConfig, args);
            }
        }

        internal void Stop(Character character, CancelMeleeSequence cancel, Args args)
        {
            _ = this.m_OnFinish.Run(args);

            character.Gestures.Stop(this.m_Animation, 0f, this.m_TransitionOut);
            cancel.IsCancelled = true;
        }

        internal bool CanHit(Args args)
        {
            return this.m_CanHit.Check(args);
        }
        
        // INTERNAL CALLBACKS: --------------------------------------------------------------------

        internal bool CanBlock(Args args)
        {
            return this.m_CanBlock.Check(args);
        }

        internal bool CanParry(Args args)
        {
            return this.m_CanParry.Check(args);
        }
        
        internal void OnHit(Args args, Vector3 point, Vector3 direction)
        {
            if (this.m_Effects.HitPause)
            {
                TimeManager.Instance.SetTimeScale(
                    this.m_Effects.HitPauseTimeScale, 
                    this.m_Effects.HitPauseDuration,
                    this.m_Effects.HitPauseDelay,
                    LAYER_TIME_SCALE
                );
            }

            AudioClip sound = this.m_Effects.GetSoundHit(args);
            if (sound != null)
            {
                Character self = args.Self != null ? args.Self.Get<Character>() : null;
                TimeMode.UpdateMode time = this.m_Effects.UseUnscaledTime
                    ? TimeMode.UpdateMode.UnscaledTime
                    : self != null
                        ? self.Time.UpdateTime
                        : TimeMode.UpdateMode.GameTime;
                
                AudioConfigSoundEffect config = AudioConfigSoundEffect.Create(
                    1f, SkillEffects.PITCH, 0f,
                    time, SpatialBlending.Spatial, args.Self
                );
                
                _ = AudioManager.Instance.SoundEffect.Play(sound, config, args);
            }

            GameObject effect = this.m_Effects.GetHitEffect(args);
            if (effect != null)
            {
                PoolManager.Instance.Pick(
                    effect,
                    point,
                    SkillEffects.GetRotation(direction),
                    SkillEffects.POOL_COUNT,
                    SkillEffects.POOL_DURATION
                );
            }
            
            _ = this.m_OnHit.Run(args);
        }
        
        internal void OnStrike(Args args)
        {
            AudioClip sound = this.m_Effects.GetSoundStrike(args);
            if (sound != null)
            {
                Character self = args.Self != null ? args.Self.Get<Character>() : null;
                TimeMode.UpdateMode time = this.m_Effects.UseUnscaledTime
                    ? TimeMode.UpdateMode.UnscaledTime
                    : self != null
                        ? self.Time.UpdateTime
                        : TimeMode.UpdateMode.GameTime;
                
                AudioConfigSoundEffect config = AudioConfigSoundEffect.Create(
                    1f, SkillEffects.PITCH, 0f,
                    time, SpatialBlending.Spatial, args.Self
                );
                
                _ = AudioManager.Instance.SoundEffect.Play(sound, config, args);
            }
        }

        internal void OnBlocked(Args args, ReactionInput input)
        {
            AudioClip sound = this.m_Effects.GetSoundBlocked(args);
            if (sound != null)
            {
                Character self = args.Self != null ? args.Self.Get<Character>() : null;
                TimeMode.UpdateMode time = this.m_Effects.UseUnscaledTime
                    ? TimeMode.UpdateMode.UnscaledTime
                    : self != null
                        ? self.Time.UpdateTime
                        : TimeMode.UpdateMode.GameTime;
                
                AudioConfigSoundEffect config = AudioConfigSoundEffect.Create(
                    1f, SkillEffects.PITCH, 0f,
                    time, SpatialBlending.Spatial, args.Self
                );
                
                _ = AudioManager.Instance.SoundEffect.Play(sound, config, args);
            }
        }
        
        internal void OnParried(Args args, ReactionInput input)
        {
            AudioClip sound = this.m_Effects.GetSoundParried(args);
            if (sound != null)
            {
                Character self = args.Self != null ? args.Self.Get<Character>() : null;
                TimeMode.UpdateMode time = this.m_Effects.UseUnscaledTime
                    ? TimeMode.UpdateMode.UnscaledTime
                    : self != null
                        ? self.Time.UpdateTime
                        : TimeMode.UpdateMode.GameTime;
                
                AudioConfigSoundEffect config = AudioConfigSoundEffect.Create(
                    1f, SkillEffects.PITCH, 0f,
                    time, SpatialBlending.Spatial, args.Self
                );
                
                _ = AudioManager.Instance.SoundEffect.Play(sound, config, args);
            }

            Character attacker = args.ComponentFromSelf<Character>();
            if (attacker == null) return;
            
            foreach (Weapon weapon in attacker.Combat.Weapons)
            {
                if (weapon.Asset.ParriedReaction == null) continue;
                
                attacker.Combat.RequestStance<MeleeStance>().PlayReaction(
                    args.Target, 
                    input,
                    weapon.Asset.ParriedReaction,
                    false
                );
                
                break;
            }
        }

        // STAGE GIZMOS: --------------------------------------------------------------------------
        
        public void StageGizmos(StagingGizmos stagingGizmos)
        { }
    }
}