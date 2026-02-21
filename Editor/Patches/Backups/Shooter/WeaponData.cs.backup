using System;
using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Common.Audio;
using UnityEngine;

namespace GameCreator.Runtime.Shooter
{
    public class WeaponData
    {
        private const float INFINITY = 9999f;
        
        private static readonly int BOOL_IS_EMPTY = Animator.StringToHash("Is-Empty");
        private static readonly int BOOL_IS_JAM = Animator.StringToHash("Is-Jam");
        private static readonly int BOOL_IS_CHARGING = Animator.StringToHash("Is-Charging");
        private static readonly int BOOL_IS_RELOADING = Animator.StringToHash("Is-Reloading");
        private static readonly int FLOAT_CHARGE = Animator.StringToHash("Charge");
        private static readonly int TRIGGER_SHOOT = Animator.StringToHash("Shoot");
        
        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private float m_EquipTime;
        
        [NonSerialized] private float m_LastTriggerPull;
        [NonSerialized] private float m_LastTriggerRelease;
        
        [NonSerialized] private bool m_AttemptedToShoot;
        [NonSerialized] private bool m_LoadStartPlayed;
        [NonSerialized] private bool m_IsPullingTrigger;
        [NonSerialized] private bool m_IsCanceledTrigger;
        [NonSerialized] private int m_NumShots;

        [NonSerialized] private List<Vector3> m_TrajectoryPoints;
        [NonSerialized] private Trajectory m_Trajectory;
        [NonSerialized] private Laser m_Laser;
        [NonSerialized] private readonly AnimFloat m_LoadRatio;
        
        [NonSerialized] private readonly AudioConfigSoundEffect m_ConfigShoot;
        [NonSerialized] private readonly AudioConfigSoundEffect m_ConfigEmpty;

        [NonSerialized] private readonly Dictionary<IdString, CrosshairData> m_Crosshairs;
        
        // PROPERTIES: ----------------------------------------------------------------------------

        [field: NonSerialized] public Args WeaponArgs { get; }
        [field: NonSerialized] public Args CombatArgs { get; }
        
        [field: NonSerialized] public Character Character { get; }
        [field: NonSerialized] public ShooterWeapon Weapon { get; }
        [field: NonSerialized] public GameObject Prop { get; }
        
        [field: NonSerialized] public IdString SightId { get; private set; }
        
        [field: NonSerialized] public bool IsJammed { get; set; }

        [field: NonSerialized] public float LastShotTime { get; private set; } = -999f;
        [field: NonSerialized] public int LastShotFrame { get; private set; } = -999;
        
        [field: NonSerialized] public float ChargeRatio { get; private set; }
        
        public bool IsPullingTrigger
        {
            get
            {
                if (!this.m_IsPullingTrigger) return false;
                if (this.IsJammed) return false;
                
                ShooterStance stance = this.Character.Combat.RequestStance<ShooterStance>();
                return this.Weapon != null && stance.Reloading.WeaponReloading != this.Weapon;
            }
        }

        // CONSTRUCTOR: ---------------------------------------------------------------------------

        public WeaponData(Character character, ShooterWeapon weapon, GameObject prop)
        {
            this.WeaponArgs = new Args(character.gameObject, prop);
            this.CombatArgs = new Args(character.gameObject, null);
            
            this.Character = character;
            this.Weapon = weapon;
            this.Prop = prop;
            
            this.m_EquipTime = this.Character.Time.Time;
            this.m_Crosshairs = new Dictionary<IdString, CrosshairData>(weapon.Sights.Length);

            this.m_ConfigShoot = AudioConfigSoundEffect.Create(
                1f, new Vector2(0.95f, 1.05f), 0f, character.Time.UpdateTime,
                prop != null ? SpatialBlending.Spatial : SpatialBlending.None,
                prop
            );
            
            this.m_ConfigEmpty = AudioConfigSoundEffect.Create(
                1f, Vector2.one, 0f, character.Time.UpdateTime,
                prop != null ? SpatialBlending.Spatial : SpatialBlending.None,
                prop
            );

            this.m_LoadRatio = new AnimFloat(0f, 0f);
        }
        
        // UPDATE METHOD: -------------------------------------------------------------------------
        
        internal void OnUpdate()
        {
            ShooterStance stance = this.Character.Combat.RequestStance<ShooterStance>();
            ShooterMunition munition = this.Character.Combat.RequestMunition(this.Weapon) as ShooterMunition;

            float loadRatioTarget = 0f;
            Animator animator = this.Prop.Get<Animator>();

            this.ChargeRatio = 0f;
            
            if (animator != null)
            {
                bool isEmpty = (munition?.InMagazine ?? 0) <= 0;
                bool isJammed = this.IsJammed;
                bool isCharging = this.Weapon.Fire.Mode == ShootMode.Charge &&
                                  this.m_IsPullingTrigger;
                bool isReloading = stance.Reloading.WeaponReloading == this.Weapon;
                
                animator.SetBool(BOOL_IS_EMPTY, isEmpty);
                animator.SetBool(BOOL_IS_JAM, isJammed);
                animator.SetBool(BOOL_IS_CHARGING, isCharging);
                animator.SetBool(BOOL_IS_RELOADING, isReloading);

                if (isCharging)
                {
                    float maxChargeTime = this.Weapon.Fire.MaxChargeTime(this.WeaponArgs);
                    float elapsedTime = this.Character.Time.Time - this.m_LastTriggerPull;

                    this.ChargeRatio = Mathf.Clamp01(elapsedTime / maxChargeTime);
                    animator.SetFloat(FLOAT_CHARGE, this.ChargeRatio);
                }
            }
            
            Sight sight = this.Weapon.Sights.Get(this.SightId)?.Sight;
            bool canShoot = sight != null && sight.CanShoot(this.WeaponArgs);
            
            if (stance.Reloading.WeaponReloading == this.Weapon || this.IsJammed)
            {
                this.m_IsPullingTrigger = false;
                canShoot = false;
            }
            
            if (canShoot && this.m_IsPullingTrigger)
            {
                float fireRate = this.Weapon.Fire.FireRate(this.WeaponArgs);
                switch (this.Weapon.Fire.Mode)
                {
                    case ShootMode.Single:
                        if (this.m_NumShots == 0 && this.PassEnoughTime(fireRate)) this.Shoot();
                        break;

                    case ShootMode.Burst:
                        int maxBurst = this.Weapon.Fire.Burst(this.WeaponArgs);
                        if (this.m_NumShots < maxBurst && this.PassEnoughTime(fireRate)) this.Shoot();
                        break;

                    case ShootMode.FullAuto:
                        loadRatioTarget = 1f;
                        fireRate = this.Weapon.Fire.AutoLoading switch
                        {
                            FullAutoLoading.Instant => fireRate,
                            FullAutoLoading.Progressive => fireRate * this.m_LoadRatio.Current,
                            FullAutoLoading.WaitToLoad => this.m_LoadRatio.Current >= 1f ? fireRate : 0f,
                            _ => throw new ArgumentOutOfRangeException()
                        };
                        
                        if (this.PassEnoughTime(fireRate)) this.Shoot();
                        break;

                    case ShootMode.Charge:
                        if (this.m_NumShots == 0) this.ChargeUpdate();
                        break;

                    default: throw new ArgumentOutOfRangeException();
                }
            }
            
            if (sight != null)
            {
                if (sight.Trajectory.CanDraw(this.WeaponArgs))
                {
                    int maxResolution = sight.Trajectory.GetMaxResolution(this.WeaponArgs);
                    float maxDistance = sight.Trajectory.GetMaxDistance(this.WeaponArgs);
                    EnablerLayerMask useRaycast = sight.Trajectory.UseRaycast;
                
                    this.m_TrajectoryPoints ??= new List<Vector3>(maxResolution);
                    if (this.m_TrajectoryPoints.Capacity < maxResolution)
                    {
                        this.m_TrajectoryPoints.Capacity = maxResolution;
                    }
                
                    this.Weapon.Projectile.GetTrajectory(
                        this.m_TrajectoryPoints,
                        Math.Max(maxResolution, 1),
                        maxDistance,
                        useRaycast,
                        this
                    );

                    if (this.m_Trajectory == null)
                    {
                        GameObject dot = sight.Trajectory.GetPrefabDot(this.WeaponArgs);
                        this.m_Trajectory = Trajectory.Create(maxResolution, dot);
                    }
                
                    this.m_Trajectory.Set(
                        this.m_TrajectoryPoints,
                        sight.Trajectory.GetMaterial(this.WeaponArgs),
                        sight.Trajectory.GetColor(this.WeaponArgs),
                        sight.Trajectory.GetWidth(this.WeaponArgs),
                        sight.Trajectory.TextureMode,
                        sight.Trajectory.TextureAlign,
                        sight.Trajectory.GetCornerVertices(this.WeaponArgs),
                        sight.Trajectory.GetCapVertices(this.WeaponArgs)
                    );                    
                }

                if (this.m_Crosshairs.TryGetValue(this.SightId, out CrosshairData crosshairs))
                {
                    Vector3 targetPoint = sight.Aim.GetPoint(this.WeaponArgs);
                    foreach (CrosshairUI crosshairsUI in crosshairs.Elements)
                    {
                        crosshairsUI.Refresh(targetPoint, stance.CurrentAccuracy);
                    }
                }
                
                if (sight.Laser.CanDraw(this.WeaponArgs))
                {
                    float maxDistance = sight.Laser.GetMaxDistance(this.WeaponArgs);
                    EnablerLayerMask useRaycast = sight.Laser.UseRaycast;
                    
                    Vector3 pointNormal = this.Weapon.Projectile.GetLaser(
                        out Vector3 pointSource,
                        out Vector3 pointTarget,
                        maxDistance,
                        useRaycast,
                        this
                    );

                    if (this.m_Laser == null)
                    {
                        GameObject dot = sight.Laser.GetPrefabDot(this.WeaponArgs);
                        this.m_Laser = Laser.Create(dot);
                    }
                    
                    this.m_Laser.Set(
                        pointSource,
                        pointTarget,
                        pointNormal,
                        sight.Laser.GetMaterial(this.WeaponArgs),
                        sight.Laser.GetColor(this.WeaponArgs),
                        sight.Laser.GetWidth(this.WeaponArgs),
                        sight.Laser.TextureMode,
                        sight.Laser.TextureAlign
                    );                    
                }
            }

            bool useLoadAudio = this.Weapon.Fire.Mode switch
            {
                ShootMode.Single => false,
                ShootMode.Burst => false,
                ShootMode.FullAuto => this.Weapon.Fire.AutoLoading != FullAutoLoading.Instant,
                ShootMode.Charge => true,
                _ => throw new ArgumentOutOfRangeException()
            };

            if (useLoadAudio)
            {
                if (canShoot && sight.CanShoot(this.WeaponArgs) && this.m_IsPullingTrigger)
                {
                    if (!this.m_LoadStartPlayed)
                    {
                        AudioClip loadStartAudio =
                            this.Weapon.Fire.LoadStartAudio(this.WeaponArgs);
                        _ = AudioManager.Instance.SoundEffect.Play(
                            loadStartAudio,
                            AudioConfigAmbient.Create(1f, 0f, SpatialBlending.Spatial,
                                this.Prop),
                            this.WeaponArgs
                        );

                        this.m_LoadStartPlayed = true;
                    }
                    
                    AudioClip loadLoop = this.Weapon.Fire.LoadLoopAudio(this.WeaponArgs);
                    if (!AudioManager.Instance.Ambient.IsPlaying(loadLoop, this.Prop))
                    {
                        _ = AudioManager.Instance.Ambient.Play(
                            loadLoop,
                            AudioConfigAmbient.Create(1f, 0.25f, SpatialBlending.Spatial,
                                this.Prop),
                            this.WeaponArgs
                        );
                    }
                }

                AudioClip loadLoopPitch = this.Weapon.Fire.LoadLoopAudio(this.WeaponArgs);
                if (AudioManager.Instance.Ambient.IsPlaying(loadLoopPitch, this.Prop))
                {
                    float pitch = Mathf.Lerp(
                        this.Weapon.Fire.LoadMinPitch(this.WeaponArgs),
                        this.Weapon.Fire.LoadMaxPitch(this.WeaponArgs),
                        this.m_LoadRatio.Current
                    );

                    AudioManager.Instance.Ambient.ChangePitch(loadLoopPitch, this.Prop, pitch);
                }

                if (this.m_LoadRatio.Current <= 0f)
                {
                    AudioClip loadStartAudio = this.Weapon.Fire.LoadStartAudio(this.WeaponArgs);

                    _ = AudioManager.Instance.Ambient.Stop(this.Prop, 0.1f);
                    _ = AudioManager.Instance.SoundEffect.Stop(loadStartAudio, this.Prop, 0.1f);
                }
            }

            this.m_LoadRatio.UpdateWithDelta(
                loadRatioTarget,
                this.Weapon.Fire.AutoLoadDuration(this.WeaponArgs),
                this.Character.Time.DeltaTime
            );
        }

        // PRIVATE METHODS: -----------------------------------------------------------------------

        private void Shoot()
        {
            if (this.Weapon.Jam.Run(this.WeaponArgs, this.IsJammed))
            {
                AudioClip jamAudio = this.Weapon.Jam.GetAudio(this.WeaponArgs);
                if (jamAudio != null)
                {
                    _ = AudioManager.Instance.SoundEffect.Play(
                        jamAudio,
                        this.m_ConfigEmpty,
                        this.WeaponArgs
                    );
                }
                
                this.IsJammed = true;
            }
            
            float maxChargeTime = this.Weapon.Fire.MaxChargeTime(this.WeaponArgs);
            float chargeRation = this.Weapon.Fire.Mode == ShootMode.Charge
                ? Mathf.Clamp01(this.Character.Time.Time - this.m_LastTriggerPull / maxChargeTime)
                : 1f;
            
            bool success = this.Weapon.Projectile.Run(
                this.WeaponArgs, 
                this.Weapon,
                chargeRation,
                this.m_LastTriggerPull
            );

            if (!success)
            {
                if (!this.m_AttemptedToShoot)
                {
                    AudioClip emptyAudio = this.Weapon.Fire.EmptyAudio(this.WeaponArgs);
                    if (emptyAudio != null)
                    {
                        _ = AudioManager.Instance.SoundEffect.Play(
                            emptyAudio,
                            this.m_ConfigEmpty,
                            this.WeaponArgs
                        );
                    }

                    this.m_AttemptedToShoot = true;
                }

                return;
            }

            Animator propAnimator = this.Prop.Get<Animator>();
            if (propAnimator != null) propAnimator.SetTrigger(TRIGGER_SHOOT);
            
            AnimationClip fireAnimationClip = this.Weapon.Fire.FireAnimation(this.WeaponArgs);
            float duration = 0f;
            
            if (fireAnimationClip != null)
            {
                ConfigGesture config = this.Weapon.Fire.FireConfig(fireAnimationClip);
                _ = this.Character.Gestures.CrossFade(
                    fireAnimationClip,
                    this.Weapon.Fire.FireAvatarMask, 
                    BlendMode.Blend,
                    config,
                    true
                );

                duration = fireAnimationClip.length - config.TransitionOut;
            }
            
            AudioClip fireAudio = this.Weapon.Fire.FireAudio(this.WeaponArgs);
            if (fireAudio != null)
            {
                _ = AudioManager.Instance.SoundEffect.Play(
                    fireAudio,
                    this.m_ConfigShoot,
                    this.WeaponArgs
                );
            }
            
            SightItem sight = this.Weapon.Sights.Get(this.SightId);
            GameObject muzzleEffect = this.Weapon.Fire.MuzzleEffect(this.WeaponArgs);
            
            if (muzzleEffect != null && sight?.Sight != null)
            {
                muzzleEffect.transform.SetPositionAndRotation(
                    this.Weapon.Muzzle.GetPosition(this.WeaponArgs),
                    this.Weapon.Muzzle.GetRotation(this.WeaponArgs)
                );
            }
            
            ShooterStance stance = this.Character.Combat.RequestStance<ShooterStance>();
            stance.Shooting.OnShoot(duration);

            this.LastShotFrame = this.Character.Time.Frame;
            this.LastShotTime = this.Character.Time.Time;
            this.m_NumShots += 1;
        }

        private void ChargeUpdate()
        {
            float maxChargeTime = this.Weapon.Fire.MaxChargeTime(this.WeaponArgs);
            float elapsedTime = this.Character.Time.Time - this.m_LastTriggerPull;

            bool autoRelease = this.Weapon.Fire.AutoRelease(this.WeaponArgs);
            float releaseDuration = this.Weapon.Fire.ReleaseDuration(this.WeaponArgs);

            if (autoRelease && elapsedTime >= maxChargeTime + releaseDuration)
            {
                this.ChargeRelease();
            }
        }

        private void ChargeRelease()
        {
            if (this.m_IsCanceledTrigger)
            {
                this.m_IsPullingTrigger = false;
                this.m_IsCanceledTrigger = false;
                return;
            }
            
            this.m_IsCanceledTrigger = false;
            
            if (this.m_NumShots != 0) return;
            
            this.m_LastTriggerRelease = this.Character.Time.Time;
            this.m_IsPullingTrigger = false;
            
            float minChargeTime = this.Weapon.Fire.MinChargeTime(this.WeaponArgs);
            float elapsedTime = this.Character.Time.Time - this.m_LastTriggerPull;
            
            if (elapsedTime < minChargeTime) return;
            this.Shoot();
        }

        private bool PassEnoughTime(float fireRate)
        {
            float currentTime = this.Character.Time.Time;
            float shotFrequency = fireRate > float.Epsilon
                ? 1f / fireRate
                : INFINITY;
            
            return currentTime >= this.LastShotTime + shotFrequency;
        }

        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        public void OnEquip()
        { }

        public void OnUnequip()
        {
            if (this.m_Trajectory != null)
            {
                this.m_Trajectory.Dispose();
            }

            if (this.m_Laser != null)
            {
                this.m_Laser.Dispose();
            }
            
            foreach (KeyValuePair<IdString, CrosshairData> entry in this.m_Crosshairs)
            {
                if (entry.Value.Instance == null) continue;
                UnityEngine.Object.Destroy(entry.Value.Instance);
            }
            
            this.m_Crosshairs.Clear();
            
            AudioClip loadLoop = this.Weapon.Fire.LoadLoopAudio(this.WeaponArgs);
            if (AudioManager.Instance.Ambient.IsPlaying(loadLoop, this.Prop))
            {
                _ = AudioManager.Instance.Ambient.Stop(loadLoop, this.Prop, 0.5f);
            }
        }
        
        public void OnPullTrigger()
        {
            SightItem sight = this.Weapon.Sights.Get(this.SightId);
            if (sight != null && !sight.CanShoot(this.WeaponArgs)) return;
            
            this.m_LastTriggerPull = this.Character.Time.Time;

            this.m_AttemptedToShoot = false;
            this.m_LoadStartPlayed = false;
            this.m_IsPullingTrigger = true;
            this.m_IsCanceledTrigger = false;
            this.m_NumShots = 0;
        }
        
        public void OnReleaseTrigger()
        {
            this.m_LastTriggerRelease = this.Character.Time.Time;
            this.m_IsPullingTrigger = false;
            
            if (this.m_NumShots == 0 && this.Weapon.Fire.Mode == ShootMode.Charge)
            {
                this.ChargeRelease();
            }
        }

        public void OnCancelTrigger()
        {
            if (!this.m_IsPullingTrigger) return;
             
            this.m_IsCanceledTrigger = true;
            this.m_IsPullingTrigger = false;
        }
        
        public void OnChangeSight(IdString sightId)
        {
            if (this.m_Crosshairs.TryGetValue(this.SightId, out CrosshairData crosshairs))
            {
                if (crosshairs.Instance != null)
                {
                    crosshairs.Instance.SetActive(false);
                }
            }
            
            this.SightId = sightId;

            Sight sight = this.Weapon.Sights.Get(this.SightId)?.Sight;
            if (sight == null) return;
            
            if (sight.Crosshair.UseCrosshair(this.WeaponArgs) == false) return;
            if (sight.Crosshair.Skin == null) return;
            
            if (this.m_Crosshairs.TryGetValue(this.SightId, out crosshairs) == false)
            {
                GameObject instance = sight.Crosshair.Skin.Create();
                if (instance == null) return;

                crosshairs = new CrosshairData(instance);
                this.m_Crosshairs.Add(this.SightId, crosshairs);
            }
            
            crosshairs.Instance.SetActive(true);
        }
    }
}