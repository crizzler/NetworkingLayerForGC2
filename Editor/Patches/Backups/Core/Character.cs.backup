using System;
using GameCreator.Runtime.Characters.Animim;
using GameCreator.Runtime.Common;
using UnityEngine;
using UnityEngine.Playables;

namespace GameCreator.Runtime.Characters
{
    [SelectionBase]
    [DisallowMultipleComponent]
    [HelpURL("https://docs.gamecreator.io/gamecreator/characters")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT_EARLIER)]
    
    [AddComponentMenu("Game Creator/Characters/Character")]
    [Icon(RuntimePaths.GIZMOS + "GizmoCharacter.png")]
    
    public class Character : MonoBehaviour, ISpatialHash
    {
        public enum MovementType
        {
            None,
            MoveToDirection,
            MoveToPosition,
        }

        // CONSTANTS: -----------------------------------------------------------------------------

        public const float BIG_EPSILON = 0.01f;
        
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [SerializeField] protected bool m_IsPlayer;
        [SerializeField] protected TimeMode m_Time;
        
        [SerializeField] protected Busy m_Busy = new Busy();
        [SerializeReference] protected CharacterKernel m_Kernel = new CharacterKernel();
        
        [SerializeField] protected AnimimGraph m_AnimimGraph = new AnimimGraph();
        [SerializeField] protected InverseKinematics m_InverseKinematics = new InverseKinematics();
        
        [SerializeField] protected Interaction m_Interaction = new Interaction();
        [SerializeField] protected Footsteps m_Footsteps = new Footsteps();
        [SerializeField] protected Ragdoll m_Ragdoll = new Ragdoll();
        [SerializeField] protected Props m_Props = new Props();
        [SerializeField] protected Combat m_Combat = new Combat();
        [SerializeField] protected Jump m_Jump = new Jump();
        [SerializeField] protected Dash m_Dash = new Dash();
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private bool m_IsDead;
        
        // PROPERTIES: ----------------------------------------------------------------------------

        public Busy Busy => this.m_Busy;
        
        public TimeMode Time
        {
            get => this.m_Time;
            set => this.m_Time = value;
        }
        
        [field: NonSerialized] public Args Args { get; private set; }

        public bool IsPlayer
        {
            get => this.m_IsPlayer;
            set
            {
                ShortcutPlayer.Change(value ? this.gameObject : null);
                this.m_IsPlayer = value;

                switch (this.m_IsPlayer)
                {
                    case true: this.EventChangeToPlayer?.Invoke(); break;
                    case false: this.EventChangeToNPC?.Invoke(); break;
                }
            }
        }

        public bool IsDead
        {
            get => this.m_IsDead;
            set
            {
                if (this.m_IsDead == value) return;
                this.m_IsDead = value;

                switch (this.m_IsDead)
                {
                    case true:  this.EventDie?.Invoke(); break;
                    case false: this.EventRevive?.Invoke(); break;
                }
            }
        }

        public CharacterKernel Kernel => this.m_Kernel;
        public InverseKinematics IK => this.m_InverseKinematics;

        public Interaction Interaction => this.m_Interaction;
        public Footsteps Footsteps => this.m_Footsteps;
        public Ragdoll Ragdoll => this.m_Ragdoll;
        public Props Props => this.m_Props;
        public Combat Combat => this.m_Combat;
        public Jump Jump => this.m_Jump;
        public Dash Dash => this.m_Dash;

        public PlayableGraph AnimationGraph => this.m_AnimimGraph.Graph;
        
        public StatesOutput States => this.m_AnimimGraph.States;
        public GesturesOutput Gestures => this.m_AnimimGraph.Gestures;
        
        public float RootMotionPosition => this.m_AnimimGraph.RootMotionPosition;
        public float RootMotionRotation => this.m_AnimimGraph.RootMotionRotation;

        public bool CanUseRootMotionPosition { set => this.m_AnimimGraph.UseRootMotionPosition = value; }
        public bool CanUseRootMotionRotation { set => this.m_AnimimGraph.UseRootMotionRotation = value; }
        
        public Phases Phases => this.m_AnimimGraph.Phases;

        public IUnitPlayer Player => this.m_Kernel?.Player;
        public IUnitMotion Motion => this.m_Kernel?.Motion;
        public IUnitDriver Driver => this.m_Kernel?.Driver;
        public IUnitFacing Facing => this.m_Kernel?.Facing;
        public IUnitAnimim Animim => this.m_Kernel?.Animim;
        
        public Vector3 Eyes
        {
            get
            {
                if (this.Animim.Animator != null && this.Animim.Animator.isHuman)
                {
                    Transform head = this.Animim.Animator.GetBoneTransform(HumanBodyBones.Head);
                    if (head != null) return head.position;
                }

                return this.transform.position + Vector3.up * (this.Motion.Height * 0.5f);
            }
        }

        public Vector3 Crown => this.transform.position + Vector3.up * this.Motion.Height * 0.5f;
        public Vector3 Feet => this.transform.position - Vector3.up * this.Motion.Height * 0.5f;
        
        // EVENTS: --------------------------------------------------------------------------------

        public event Action EventEnable;
        public event Action EventDisable;
        public event Action EventDestroy;

        public event Action EventBeforeUpdate;
        public event Action EventAfterUpdate;
        
        public event Action EventBeforeLateUpdate;
        public event Action EventAfterLateUpdate;
        
        public event Action EventBeforeFixedUpdate;
        public event Action EventAfterFixedUpdate;

        public event Action EventDie;
        public event Action EventRevive;

        public event Action<float> EventLand;
        public event Action<float> EventJump;

        public event Action EventBeforeChangeModel;
        public event Action EventAfterChangeModel;

        public event Action EventChangeToPlayer;
        public event Action EventChangeToNPC;
        
        // INITIALIZERS: --------------------------------------------------------------------------

        protected virtual void Awake()
        {
            this.Args = new Args(this);
            
            if (this.IsPlayer) ShortcutPlayer.Change(this.gameObject);
            
            this.m_Busy?.OnStartup(this);
            this.m_Kernel?.OnStartup(this);
            this.m_AnimimGraph?.OnStartup(this);
            this.m_Footsteps?.OnStartup(this);
            this.m_InverseKinematics?.OnStartup(this);
            this.m_Interaction?.OnStartup(this);
            this.m_Ragdoll?.OnStartup(this);
            this.m_Props?.OnStartup(this);
            this.m_Combat?.OnStartup(this);
            this.m_Jump?.OnStartup(this);
            this.m_Dash?.OnStartup(this);
            
            SpatialHashCharacters.Insert(this);
        }

        protected void Start()
        {
            this.m_Busy?.AfterStartup(this);
            this.m_Kernel?.AfterStartup(this);
            this.m_AnimimGraph?.AfterStartup(this);
            this.m_Footsteps?.AfterStartup(this);
            this.m_InverseKinematics?.AfterStartup(this);
            this.m_Interaction?.AfterStartup(this);
            this.m_Props?.AfterStartup(this);
            this.m_Combat?.AfterStartup(this);
            this.m_Jump?.AfterStartup(this);
            this.m_Dash?.AfterStartup(this);
        }

        protected virtual void OnDestroy()
        {
            this.m_Kernel?.OnDispose(this);
            this.m_AnimimGraph?.OnDispose(this);
            this.m_Footsteps?.OnDispose(this);
            this.m_Interaction?.OnDispose(this);
            this.m_Ragdoll?.OnDispose(this);
            this.m_Props?.OnDispose(this);
            this.m_Combat?.OnDispose(this);
            this.m_Jump?.OnDispose(this);
            this.m_Dash?.OnDispose(this);
            
            SpatialHashCharacters.Remove(this);
            
            this.EventDestroy?.Invoke();
        }

        protected virtual void OnEnable()
        {
            this.m_Kernel?.OnEnable();
            this.m_InverseKinematics?.OnEnable();
            this.m_Footsteps?.OnEnable();
            this.m_Interaction?.OnEnable();
            this.m_Ragdoll?.OnEnable();
            this.m_Props?.OnEnable();
            this.m_Combat?.OnEnable();
            this.m_Jump?.OnEnable();
            this.m_Dash?.OnEnable();
            
            this.EventEnable?.Invoke();
        }

        protected virtual void OnDisable()
        {
            this.m_Kernel?.OnDisable();
            this.m_Footsteps?.OnDisable();
            this.m_InverseKinematics?.OnDisable();
            this.m_Interaction?.OnDisable();
            this.m_Ragdoll?.OnDisable();
            this.m_Props?.OnDisable();
            this.m_Combat?.OnDisable();
            this.m_Jump?.OnDisable();
            this.m_Dash?.OnDisable();
            
            this.EventDisable?.Invoke();
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        protected virtual void Update()
        {
            this.EventBeforeUpdate?.Invoke();

            this.m_Kernel?.OnUpdate();
            this.m_AnimimGraph?.OnUpdate();
            this.m_Ragdoll?.OnUpdate();
            this.m_Footsteps?.OnUpdate();
            this.m_InverseKinematics?.OnUpdate();
            this.m_Interaction?.OnUpdate();

            this.EventAfterUpdate?.Invoke();
        }

        protected virtual void LateUpdate()
        {
            this.EventBeforeLateUpdate?.Invoke();
            
            this.Combat?.OnLateUpdate();
            this.m_Ragdoll.OnLateUpdate();
            
            this.EventAfterLateUpdate?.Invoke();
        }

        protected virtual void FixedUpdate()
        {
            this.EventBeforeFixedUpdate?.Invoke();

            this.m_Kernel?.OnFixedUpdate();

            this.EventAfterFixedUpdate?.Invoke();
        }

        // PROTECTED METHODS: ---------------------------------------------------------------------

        protected virtual void OnDrawGizmosSelected()
        {
            #if UNITY_EDITOR
            if (UnityEditor.PrefabUtility.IsPartOfPrefabAsset(this.gameObject)) return;
            #endif
            
            this.m_Kernel?.OnDrawGizmos(this);
            this.m_Ragdoll?.OnDrawGizmos(this);
            this.m_Footsteps?.OnDrawGizmos(this);
            this.m_InverseKinematics?.OnDrawGizmos(this);
            this.m_Interaction?.OnDrawGizmos(this);
            this.m_Combat?.OnDrawGizmos(this);
        }

        // CALLBACKS: -----------------------------------------------------------------------------

        public void OnLand(float velocity)
        {
            this.EventLand?.Invoke(velocity);
        }

        public void OnJump(float force)
        {
            this.EventJump?.Invoke(force);
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------

        public struct ChangeOptions
        {
            [NonSerialized] public MaterialSoundsAsset materials;
            [NonSerialized] public RuntimeAnimatorController controller;
            [NonSerialized] public Vector3 offset;
        }
        
        /// <summary>
        /// Change the 3D character model.
        /// </summary>
        /// <param name="prefab">A prefab representing the character model</param>
        /// <param name="options">[Optional] Default features added to the new model</param>
        /// <returns>The instance of the new model used</returns>
        public GameObject ChangeModel(GameObject prefab, ChangeOptions options)
        {
            if (prefab == null) return null;
            
            this.EventBeforeChangeModel?.Invoke();

            Transform hull = this.Animim.Mannequin;

            if (this.Animim.Animator != null)
            {
                Destroy(this.Animim.Animator.gameObject);
            }

            if (hull == null)
            {
                this.Animim.Mannequin = new GameObject("Mannequin").transform;
                this.Animim.Mannequin.transform.SetParent(this.transform);
            }
            
            Vector3 position = Vector3.down * (this.Motion.Height * 0.5f);

            this.Animim.Mannequin.transform.localPosition = position + options.offset;
            this.Animim.Mannequin.transform.localRotation = Quaternion.identity;
            this.Animim.Mannequin.transform.localScale = Vector3.one;
            
            GameObject model = Instantiate(prefab, this.Animim.Mannequin);
            
            model.name = prefab.name;

            Animator modelAnimator = model.GetComponentInChildren<Animator>(true);
            if (modelAnimator == null) modelAnimator = model.AddComponent<Animator>();
            
            this.Animim.Animator = modelAnimator;

            if (Application.isPlaying)
            {
                this.Animim.ApplyMannequinPosition();
                this.Animim.ApplyMannequinRotation();
                this.Animim.ApplyMannequinScale();
            }
            
            if (modelAnimator != null && options.controller != null)
            {
                modelAnimator.runtimeAnimatorController = options.controller;
            }

            if (options.materials != null)
            {
                this.m_Footsteps.ChangeFootstepSounds(options.materials);
            }
            
            this.EventAfterChangeModel?.Invoke();
            return model;
        }
        
        // VALIDATION: ----------------------------------------------------------------------------
        
        private void OnValidate()
        {
            this.m_Busy ??= new Busy();
            this.m_Kernel ??= new CharacterKernel();
            this.m_AnimimGraph ??= new AnimimGraph();
            this.m_InverseKinematics ??= new InverseKinematics();
            
            this.m_Footsteps ??= new Footsteps();
            this.m_Ragdoll ??= new Ragdoll();
            this.m_Props ??= new Props();
            this.m_Combat ??= new Combat();
            this.m_Jump ??= new Jump();

            this.m_Interaction ??= new Interaction();
        }
    }   
}
