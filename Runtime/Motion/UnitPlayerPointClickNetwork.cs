using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-aware point-and-click player input unit.
    /// Captures click input, raycasts to find position, and sends commands to the network driver.
    /// Works with UnitDriverNavmeshNetworkClient for click-to-move gameplay (Diablo, PoE, RTS style).
    /// </summary>
    [Title("Network Point & Click (Client)")]
    [Image(typeof(IconLocationDrop), ColorTheme.Type.Red, typeof(OverlayArrowRight))]
    [Category("Network Point & Click (Client)")]
    [Description("Captures point-and-click input for networked NavMesh movement. " +
                 "Sends destination to server with optional client-side prediction. " +
                 "Use for Diablo-style ARPG, RTS, or MOBA games.")]
    [Serializable]
    public class UnitPlayerPointClickNetwork : TUnitPlayer
    {
        private const int BUFFER_SIZE = 32;

        // RAYCAST COMPARER: ----------------------------------------------------------------------
        
        private static readonly RaycastComparer RAYCAST_COMPARER = new RaycastComparer();
        
        private class RaycastComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b)
            {
                return a.distance.CompareTo(b.distance);
            }
        }
        
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] 
        private InputPropertyButton m_InputMove;
        
        [SerializeField]
        private InputPropertyButton m_InputStop;
        
        [SerializeField]
        private LayerMask m_LayerMask = Physics.DefaultRaycastLayers;

        [SerializeField]
        private PropertyGetInstantiate m_Indicator;
        
        [Header("Network Settings")]
        [Tooltip("Maximum clicks per second (anti-spam)")]
        [SerializeField] private float m_MaxClickRate = 10f;
        
        [Tooltip("Minimum distance from current position to send move command")]
        [SerializeField] private float m_MinMoveDistance = 0.5f;
        
        [Tooltip("Enable hold-to-move (continuously move while button held)")]
        [SerializeField] private bool m_HoldToMove = true;
        
        [Tooltip("How often to update destination while holding (per second)")]
        [SerializeField] private float m_HoldUpdateRate = 5f;

        [Tooltip("Send a stop command when hold-to-move input is released")]
        [SerializeField] private bool m_StopOnRelease = true;
        
        [Header("Anti-Cheat")]
        [Tooltip("Maximum distance from character to click (0 = unlimited)")]
        [SerializeField] private float m_MaxClickDistance = 0f;
        
        [Tooltip("Require line of sight to click position")]
        [SerializeField] private bool m_RequireLineOfSight = false;

        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private RaycastHit[] m_HitBuffer;
        [NonSerialized] private UnitDriverNavmeshNetworkClient m_NetworkDriver;
        
        [NonSerialized] private bool m_IsInputEnabled = true;
        [NonSerialized] private float m_LastClickTime;
        [NonSerialized] private float m_LastHoldUpdateTime;
        [NonSerialized] private Vector3 m_LastClickPosition;
        [NonSerialized] private bool m_IsHolding;
        [NonSerialized] private bool m_PressThisFrame;
        [NonSerialized] private bool m_MovePerformedThisFrame;
        
        // Click validation
        [NonSerialized] private ushort m_ClickSequence;
        [NonSerialized] private Queue<ClickRecord> m_ClickHistory;
        
        /// <summary>
        /// Record of a click for anti-cheat validation.
        /// </summary>
        public struct ClickRecord
        {
            public float Time;
            public Vector3 Position;
            public ushort Sequence;
        }

        // PROPERTIES: ----------------------------------------------------------------------------
        
        /// <summary>
        /// Enable or disable input processing.
        /// </summary>
        public bool IsInputEnabled
        {
            get => m_IsInputEnabled;
            set => m_IsInputEnabled = value;
        }
        
        /// <summary>
        /// Last clicked world position.
        /// </summary>
        public Vector3 LastClickPosition => m_LastClickPosition;
        
        /// <summary>
        /// Whether currently holding move button.
        /// </summary>
        public bool IsHolding => m_IsHolding;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when a valid click is captured and will be sent to server.
        /// </summary>
        public event Action<Vector3> OnClickCaptured;
        
        /// <summary>
        /// Fired when click is rejected (rate limit, distance, etc.)
        /// </summary>
        public event Action<string> OnClickRejected;
        
        /// <summary>
        /// Fired when stop command is issued.
        /// </summary>
        public event Action OnStopCaptured;

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitPlayerPointClickNetwork()
        {
            this.m_InputMove = InputButtonMouseWhilePressing.Create();
            this.m_Indicator = new PropertyGetInstantiate
            {
                usePooling = true,
                size = 5,
                hasDuration = true,
                duration = 1f
            };
        }
        
        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            this.m_InputMove.OnStartup();
            this.m_InputStop?.OnStartup();
            
            m_ClickHistory = new Queue<ClickRecord>(32);
            
            // Try to find network driver
            m_NetworkDriver = character.Driver as UnitDriverNavmeshNetworkClient;
        }
        
        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            
            this.m_InputMove.OnDispose();
            this.m_InputStop?.OnDispose();
        }

        public override void OnEnable()
        {
            base.OnEnable();

            this.m_HitBuffer = new RaycastHit[BUFFER_SIZE];
            
            this.m_InputMove.RegisterStart(this.OnStartClick);
            this.m_InputMove.RegisterPerform(this.OnPerformClick);
            this.m_InputMove.RegisterCancel(this.OnCancelClick);
            
            this.m_InputStop?.RegisterPerform(this.OnPerformStop);
        }

        public override void OnDisable()
        {
            base.OnDisable();
            
            this.m_HitBuffer = Array.Empty<RaycastHit>();
            
            this.m_InputMove.ForgetStart(this.OnStartClick);
            this.m_InputMove.ForgetPerform(this.OnPerformClick);
            this.m_InputMove.ForgetCancel(this.OnCancelClick);
            
            this.m_InputStop?.ForgetPerform(this.OnPerformStop);
            
            if (m_IsHolding && m_StopOnRelease)
            {
                RequestStopMovement();
            }

            m_IsHolding = false;
            m_MovePerformedThisFrame = false;
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            
            this.m_InputMove.OnUpdate();
            this.m_InputStop?.OnUpdate();
            
            if (!m_IsInputEnabled)
            {
                if (m_IsHolding && m_StopOnRelease)
                {
                    RequestStopMovement();
                }

                m_MovePerformedThisFrame = false;
                return;
            }

            if (m_HoldToMove && m_IsHolding && !m_MovePerformedThisFrame)
            {
                if (m_StopOnRelease)
                {
                    RequestStopMovement();
                }
                else
                {
                    m_IsHolding = false;
                }
            }
            
            // Handle hold-to-move
            if (m_HoldToMove && m_IsHolding)
            {
                float timeSinceLastUpdate = Time.time - m_LastHoldUpdateTime;
                if (timeSinceLastUpdate >= 1f / m_HoldUpdateRate)
                {
                    ProcessClick(false); // Don't show indicator on hold updates
                    m_LastHoldUpdateTime = Time.time;
                }
            }
            
            // Show indicator on press
            if (m_PressThisFrame && m_LastClickPosition != Vector3.zero)
            {
                GameObject user = this.Character.gameObject;
                this.m_Indicator.Get(user, m_LastClickPosition, Quaternion.identity);
                m_PressThisFrame = false;
            }
            
            // Clean old click history
            CleanClickHistory();

            m_MovePerformedThisFrame = false;
        }
        
        // INPUT HANDLERS: ------------------------------------------------------------------------

        private void OnStartClick()
        {
            if (!this.Character.IsPlayer) return;
            if (!this.Character.Player.IsControllable) return;
            if (!m_IsInputEnabled) return;
            
            m_PressThisFrame = true;
            ProcessClick(true);
        }
        
        private void OnPerformClick()
        {
            if (!this.Character.IsPlayer) return;
            if (!this.m_IsControllable) return;
            if (!m_IsInputEnabled) return;
            if (!m_HoldToMove) return;

            m_MovePerformedThisFrame = true;
            if (!m_IsHolding)
            {
                m_LastHoldUpdateTime = Time.time;
            }

            m_IsHolding = true;
        }
        
        private void OnCancelClick()
        {
            bool wasHolding = m_IsHolding;
            m_IsHolding = false;

            if (m_StopOnRelease && wasHolding)
            {
                RequestStopMovement();
            }
        }
        
        private void OnPerformStop()
        {
            if (!this.Character.IsPlayer) return;
            if (!m_IsInputEnabled) return;
            
            RequestStopMovement();
        }
        
        // CLICK PROCESSING: ----------------------------------------------------------------------

        private void ProcessClick(bool isNewClick)
        {
            // Rate limiting
            if (isNewClick)
            {
                float timeSinceLastClick = Time.time - m_LastClickTime;
                if (timeSinceLastClick < 1f / m_MaxClickRate)
                {
                    OnClickRejected?.Invoke("Rate limited");
                    return;
                }
            }
            
            // Get click position from raycast
            Vector3? clickPos = GetClickPosition();
            if (!clickPos.HasValue)
            {
                return;
            }
            
            Vector3 destination = clickPos.Value;
            
            // Minimum distance check
            float distFromCurrent = Vector3.Distance(this.Transform.position, destination);
            if (distFromCurrent < m_MinMoveDistance)
            {
                OnClickRejected?.Invoke("Too close to current position");
                return;
            }
            
            // Maximum distance check (anti-cheat)
            if (m_MaxClickDistance > 0 && distFromCurrent > m_MaxClickDistance)
            {
                OnClickRejected?.Invoke("Click too far from character");
                return;
            }
            
            // Line of sight check (anti-cheat)
            if (m_RequireLineOfSight && !HasLineOfSight(destination))
            {
                OnClickRejected?.Invoke("No line of sight to destination");
                return;
            }
            
            // Record click
            m_LastClickTime = Time.time;
            m_LastClickPosition = destination;
            m_ClickSequence++;
            
            RecordClick(destination);
            
            // Send to network driver
            RefreshNetworkDriver();
            if (m_NetworkDriver != null)
            {
                m_NetworkDriver.RequestMoveToPosition(destination);
            }
            else
            {
                // Fallback to local movement
                this.Character.Motion?.MoveToLocation(new Location(destination), 0.1f, null, 0);
            }
            
            // Update input direction for GC2 compatibility
            this.InputDirection = Vector3.Scale(
                destination - this.Character.transform.position, 
                Vector3Plane.NormalUp
            );
            
            OnClickCaptured?.Invoke(destination);
        }
        
        private Vector3? GetClickPosition()
        {
            Camera camera = ShortcutMainCamera.Get<Camera>();
            if (camera == null) return null;

            Ray ray = camera.ScreenPointToRay(Application.isMobilePlatform
                ? Touchscreen.current.primaryTouch.position.ReadValue()
                : Mouse.current.position.ReadValue()
            );

            int hitCount = Physics.RaycastNonAlloc(
                ray, this.m_HitBuffer,
                Mathf.Infinity, this.m_LayerMask,
                QueryTriggerInteraction.Ignore
            );
            
            if (hitCount == 0) return null;
            
            Array.Sort(this.m_HitBuffer, 0, hitCount, RAYCAST_COMPARER);

            for (int i = 0; i < hitCount; ++i)
            {
                int colliderLayer = this.m_HitBuffer[i].transform.gameObject.layer; 
                if ((colliderLayer & LAYER_UI) > 0) return null;
                
                if (this.m_HitBuffer[i].transform.IsChildOf(this.Transform)) continue;

                return this.m_HitBuffer[i].point;
            }
            
            return null;
        }
        
        private bool HasLineOfSight(Vector3 destination)
        {
            Vector3 origin = this.Transform.position + Vector3.up * 1f; // Eye height
            Vector3 direction = destination - origin;
            
            return !Physics.Raycast(origin, direction.normalized, direction.magnitude, m_LayerMask);
        }
        
        // ANTI-CHEAT TRACKING: -------------------------------------------------------------------
        
        private void RecordClick(Vector3 position)
        {
            m_ClickHistory.Enqueue(new ClickRecord
            {
                Time = Time.time,
                Position = position,
                Sequence = m_ClickSequence
            });
            
            // Limit history size
            while (m_ClickHistory.Count > 100)
            {
                m_ClickHistory.Dequeue();
            }
        }
        
        private void CleanClickHistory()
        {
            // Remove clicks older than 5 seconds
            float cutoff = Time.time - 5f;
            while (m_ClickHistory.Count > 0 && m_ClickHistory.Peek().Time < cutoff)
            {
                m_ClickHistory.Dequeue();
            }
        }
        
        /// <summary>
        /// Get recent click history for server validation.
        /// </summary>
        public ClickRecord[] GetRecentClicks(int count)
        {
            var clicks = m_ClickHistory.ToArray();
            if (clicks.Length <= count) return clicks;
            
            var recent = new ClickRecord[count];
            Array.Copy(clicks, clicks.Length - count, recent, 0, count);
            return recent;
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        /// <summary>
        /// Programmatically trigger a move to position.
        /// Useful for UI buttons, AI takeover, or scripted sequences.
        /// </summary>
        public void MoveTo(Vector3 destination)
        {
            if (!m_IsInputEnabled) return;
            
            m_LastClickPosition = destination;
            m_LastClickTime = Time.time;
            m_ClickSequence++;
            
            RecordClick(destination);
            
            RefreshNetworkDriver();
            if (m_NetworkDriver != null)
            {
                m_NetworkDriver.RequestMoveToPosition(destination);
            }
            else
            {
                this.Character.Motion?.MoveToLocation(new Location(destination), 0.1f, null, 0);
            }
            
            OnClickCaptured?.Invoke(destination);
        }
        
        /// <summary>
        /// Programmatically stop movement.
        /// </summary>
        public void Stop()
        {
            RequestStopMovement();
        }

        private void RequestStopMovement()
        {
            RefreshNetworkDriver();

            if (m_NetworkDriver != null)
            {
                m_NetworkDriver.RequestStop(true);
            }
            else
            {
                this.Character.Motion?.StopToDirection(0);
            }
            
            m_IsHolding = false;
            m_MovePerformedThisFrame = false;
            m_LastClickPosition = Vector3.zero;
            this.InputDirection = Vector3.zero;
            OnStopCaptured?.Invoke();
        }
        
        /// <summary>
        /// Connect to a network driver after initialization.
        /// </summary>
        public void SetNetworkDriver(UnitDriverNavmeshNetworkClient driver)
        {
            m_NetworkDriver = driver;
        }

        private void RefreshNetworkDriver()
        {
            if (this.Character == null) return;
            if (ReferenceEquals(m_NetworkDriver, this.Character.Driver)) return;
            m_NetworkDriver = this.Character.Driver as UnitDriverNavmeshNetworkClient;
        }

        // STRING: --------------------------------------------------------------------------------

        public override string ToString() => "Network Point & Click";
    }
}
