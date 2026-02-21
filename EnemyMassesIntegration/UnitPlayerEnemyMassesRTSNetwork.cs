using System;
using System.Collections.Generic;
using Arawn.EnemyMasses.Runtime;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Common;
using UnityEngine;
using UnityEngine.AI;

namespace GameCreator.Runtime.Characters
{
    /// <summary>
    /// Network-aware RTS player unit for Enemy Masses integration.
    /// Server-authoritative with efficient batch command support for multi-unit selection.
    /// </summary>
    [Title("Enemy Masses RTS Network (Client)")]
    [Image(typeof(IconLocationDrop), ColorTheme.Type.Red, typeof(OverlayArrowRight))]
    [Category("Enemy Masses RTS Network (Client)")]
    [Description("Network-aware RTS player unit. Commands are server-validated. " +
                 "Supports batch commands for efficient multi-unit control.")]
    [Serializable]
    public class UnitPlayerEnemyMassesRTSNetwork : TUnitPlayer, IRTSExternalSelectable, IRTSFogRevealer, IRTSMinimapIcon
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [SerializeField] private EnemyMassesRTSController m_RtsController;
        [SerializeField] private GameObject m_SelectionIndicatorOverride;
        [SerializeField] private Vector3 m_SelectionIndicatorOffset = new Vector3(0f, -1.0f, 0f);

        [Header("Fog of War")]
        [SerializeField] private bool m_RevealFogOfWar = true;
        [SerializeField] private float m_FogRevealRadius = 10f;

        [Header("Minimap")]
        [SerializeField] private bool m_ShowOnMinimap = true;
        [SerializeField] private Color m_MinimapColor = new Color(0.2f, 1f, 0.2f, 1f);
        
        [Header("Network Settings")]
        [Tooltip("Maximum commands per second (anti-spam)")]
        [SerializeField] private float m_MaxCommandRate = 10f;
        
        [Tooltip("Enable client-side prediction for immediate visual feedback")]
        [SerializeField] private bool m_EnablePrediction = true;

        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private NavMeshAgent m_NavAgent;
        [NonSerialized] private UnitDriverNavmeshNetworkClient m_NetworkDriver;
        
        // Command tracking
        [NonSerialized] private float m_LastCommandTime;
        [NonSerialized] private ushort m_CommandSequence;
        [NonSerialized] private Vector3 m_LastCommandDestination;
        [NonSerialized] private bool m_HasPendingCommand;
        
        // Network identity (set by NetworkCharacterManager)
        [NonSerialized] private ulong m_NetworkId;
        [NonSerialized] private bool m_IsLocalPlayer;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Raised when a movement command should be sent to the server.
        /// For single unit: destination, networkId, sequence
        /// </summary>
        public event Action<Vector3, ulong, ushort> OnSendMoveCommand;
        
        /// <summary>
        /// Raised when a stop command should be sent to the server.
        /// </summary>
        public event Action<ulong, ushort> OnSendStopCommand;
        
        /// <summary>
        /// Raised when command is rejected (rate limited, etc.)
        /// </summary>
        public event Action<string> OnCommandRejected;

        // PROPERTIES: ----------------------------------------------------------------------------
        
        public ulong NetworkId
        {
            get => m_NetworkId;
            set => m_NetworkId = value;
        }
        
        public bool IsLocalPlayer
        {
            get => m_IsLocalPlayer;
            set => m_IsLocalPlayer = value;
        }
        
        public ushort LastCommandSequence => m_CommandSequence;
        
        /// <summary>
        /// Whether there's a command waiting for server acknowledgment.
        /// </summary>
        public bool HasPendingCommand => m_HasPendingCommand;
        
        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            CacheNavAgent();
            
            // Try to find network driver
            m_NetworkDriver = character.Driver as UnitDriverNavmeshNetworkClient;
            
            // Only register with RTS controller if local player
            if (m_IsLocalPlayer)
            {
                RegisterWithController();
            }
        }

        public override void AfterStartup(Character character)
        {
            base.AfterStartup(character);
            CacheNavAgent();
            
            if (m_IsLocalPlayer)
            {
                RegisterWithController();
            }
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            UnregisterFromController();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (m_IsLocalPlayer)
            {
                RegisterWithController();
            }
        }

        public override void OnDisable()
        {
            base.OnDisable();
            UnregisterFromController();
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            // Movement is driven by server via UnitDriverNavmeshNetworkClient
        }

        private void CacheNavAgent()
        {
            if (this.Character == null) return;
            m_NavAgent = this.Character.GetComponent<NavMeshAgent>();
        }

        private void RegisterWithController()
        {
            if (m_RtsController == null)
            {
#if UNITY_2023_1_OR_NEWER
                m_RtsController = UnityEngine.Object.FindFirstObjectByType<EnemyMassesRTSController>();
#else
                m_RtsController = UnityEngine.Object.FindObjectOfType<EnemyMassesRTSController>();
#endif
            }

            m_RtsController?.RegisterExternalSelectable(this);
        }

        private void UnregisterFromController()
        {
            m_RtsController?.UnregisterExternalSelectable(this);
        }

        // NETWORK COMMAND METHODS: ---------------------------------------------------------------
        
        /// <summary>
        /// Called by RTS controller when player issues a move command.
        /// Validates and sends to server.
        /// </summary>
        public void SetDestination(Vector3 destination)
        {
            // Rate limiting
            float timeSinceLastCommand = Time.time - m_LastCommandTime;
            if (timeSinceLastCommand < 1f / m_MaxCommandRate)
            {
                OnCommandRejected?.Invoke("Rate limited");
                return;
            }
            
            m_LastCommandTime = Time.time;
            m_CommandSequence++;
            m_LastCommandDestination = destination;
            m_HasPendingCommand = true;
            
            // Client-side prediction (immediate visual feedback)
            if (m_EnablePrediction && m_NetworkDriver != null)
            {
                m_NetworkDriver.RequestMoveToPosition(destination);
            }
            else if (m_EnablePrediction && m_NavAgent != null && m_NavAgent.isOnNavMesh)
            {
                // Fallback: use NavAgent directly for prediction
                m_NavAgent.isStopped = false;
                m_NavAgent.SetDestination(destination);
            }
            
            // Also use GC2 motion for animation
            if (m_EnablePrediction && this.Character != null)
            {
                var location = new Location(destination);
                this.Character.Motion?.MoveToLocation(location, 0.1f, null, 0);
            }
            
            // Send command to server
            OnSendMoveCommand?.Invoke(destination, m_NetworkId, m_CommandSequence);
        }
        
        /// <summary>
        /// Stop movement.
        /// </summary>
        public void Stop()
        {
            m_CommandSequence++;
            m_HasPendingCommand = false;
            
            // Prediction
            if (m_EnablePrediction)
            {
                if (m_NetworkDriver != null)
                {
                    m_NetworkDriver.RequestStop(true);
                }
                else if (m_NavAgent != null)
                {
                    m_NavAgent.isStopped = true;
                }
                
                this.Character?.Motion?.MoveToDirection(Vector3.zero, Space.World, 0);
            }
            
            OnSendStopCommand?.Invoke(m_NetworkId, m_CommandSequence);
        }
        
        /// <summary>
        /// Apply server-authoritative path state (called by network manager).
        /// </summary>
        public void ApplyServerPath(NetworkNavMeshPathState pathState)
        {
            m_NetworkDriver?.ApplyPathState(pathState);
            m_HasPendingCommand = false;
        }
        
        /// <summary>
        /// Apply server position update (called by network manager).
        /// </summary>
        public void ApplyServerPosition(NetworkNavMeshPositionUpdate update)
        {
            m_NetworkDriver?.ApplyPositionUpdate(update);
        }
        
        /// <summary>
        /// Connect to network driver after initialization.
        /// </summary>
        public void SetNetworkDriver(UnitDriverNavmeshNetworkClient driver)
        {
            m_NetworkDriver = driver;
            
            if (m_EnablePrediction)
            {
                driver.EnableLocalPrediction = true;
            }
        }

        // IRTSExternalSelectable ----------------------------------------------------------------

        public NavMeshAgent NavAgent => m_NavAgent;

        public new Transform Transform => this.Character != null ? this.Character.transform : null;

        public Vector3 SelectionIndicatorOffset => m_SelectionIndicatorOffset;

        public bool IsSelectable => this.m_IsControllable && this.Character != null && 
                                    this.Character.IsPlayer && m_IsLocalPlayer;

        public bool IsAlive => this.Character != null && this.Character.gameObject.activeInHierarchy;

        public bool SkipFactionFilter => true;

        public GameObject SelectionIndicatorOverride => m_SelectionIndicatorOverride;

        // IRTSFogRevealer ----------------------------------------------------------------------

        public bool RevealFogOfWar => m_RevealFogOfWar;

        public Vector3 FogRevealPosition
        {
            get
            {
                if (m_NavAgent != null) return m_NavAgent.transform.position;
                Transform tr = Transform;
                return tr != null ? tr.position : Vector3.zero;
            }
        }

        public float FogRevealRadius => m_RevealFogOfWar ? m_FogRevealRadius : 0f;

        // IRTSMinimapIcon ----------------------------------------------------------------------

        public bool ShowOnMinimap => m_ShowOnMinimap;

        public Vector3 MinimapPosition
        {
            get
            {
                if (m_NavAgent != null) return m_NavAgent.transform.position;
                Transform tr = Transform;
                return tr != null ? tr.position : Vector3.zero;
            }
        }

        public Color MinimapColor => m_MinimapColor;

        // STRING -------------------------------------------------------------------------------

        public override string ToString() => "Enemy Masses RTS Network";
    }
    
    // ========================================================================================
    // BATCH COMMAND SUPPORT
    // ========================================================================================
    
    /// <summary>
    /// Efficient batch move command for multiple units (RTS multi-select).
    /// Sends all destinations in a single network message.
    /// </summary>
    [Serializable]
    public struct RTSBatchMoveCommand : IEquatable<RTSBatchMoveCommand>
    {
        public const int MAX_UNITS = 64;
        
        /// <summary>Command sequence for ordering/deduplication.</summary>
        public ushort Sequence;
        
        /// <summary>Number of units in this batch.</summary>
        public byte UnitCount;
        
        /// <summary>Network IDs of units (packed as ulong).</summary>
        public ulong[] UnitIds;
        
        /// <summary>Destinations for each unit (compressed).</summary>
        public Vector3[] Destinations;
        
        /// <summary>
        /// Create a batch move command for multiple units.
        /// </summary>
        public static RTSBatchMoveCommand Create(
            IReadOnlyList<UnitPlayerEnemyMassesRTSNetwork> units, 
            Vector3[] destinations,
            ushort sequence)
        {
            int count = Mathf.Min(units.Count, MAX_UNITS);
            
            var cmd = new RTSBatchMoveCommand
            {
                Sequence = sequence,
                UnitCount = (byte)count,
                UnitIds = new ulong[count],
                Destinations = new Vector3[count]
            };
            
            for (int i = 0; i < count; i++)
            {
                cmd.UnitIds[i] = units[i].NetworkId;
                cmd.Destinations[i] = destinations[i];
            }
            
            return cmd;
        }
        
        /// <summary>
        /// Create a batch command where all units go to the same destination.
        /// </summary>
        public static RTSBatchMoveCommand CreateSameDestination(
            IReadOnlyList<UnitPlayerEnemyMassesRTSNetwork> units,
            Vector3 destination,
            ushort sequence)
        {
            int count = Mathf.Min(units.Count, MAX_UNITS);
            
            var cmd = new RTSBatchMoveCommand
            {
                Sequence = sequence,
                UnitCount = (byte)count,
                UnitIds = new ulong[count],
                Destinations = new Vector3[count]
            };
            
            for (int i = 0; i < count; i++)
            {
                cmd.UnitIds[i] = units[i].NetworkId;
                cmd.Destinations[i] = destination; // Server will calculate formation
            }
            
            return cmd;
        }
        
        /// <summary>
        /// Approximate size in bytes for bandwidth estimation.
        /// </summary>
        public int ApproximateSize => 3 + (UnitCount * 8) + (UnitCount * 12);
        
        public bool Equals(RTSBatchMoveCommand other)
        {
            return Sequence == other.Sequence && UnitCount == other.UnitCount;
        }
        
        public override bool Equals(object obj) => obj is RTSBatchMoveCommand other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Sequence, UnitCount);
    }
    
    /// <summary>
    /// Efficient batch stop command for multiple units.
    /// </summary>
    [Serializable]
    public struct RTSBatchStopCommand : IEquatable<RTSBatchStopCommand>
    {
        public const int MAX_UNITS = 64;
        
        public ushort Sequence;
        public byte UnitCount;
        public ulong[] UnitIds;
        
        public static RTSBatchStopCommand Create(IReadOnlyList<UnitPlayerEnemyMassesRTSNetwork> units, ushort sequence)
        {
            int count = Mathf.Min(units.Count, MAX_UNITS);
            
            var cmd = new RTSBatchStopCommand
            {
                Sequence = sequence,
                UnitCount = (byte)count,
                UnitIds = new ulong[count]
            };
            
            for (int i = 0; i < count; i++)
            {
                cmd.UnitIds[i] = units[i].NetworkId;
            }
            
            return cmd;
        }
        
        public int ApproximateSize => 3 + (UnitCount * 8);
        
        public bool Equals(RTSBatchStopCommand other)
        {
            return Sequence == other.Sequence && UnitCount == other.UnitCount;
        }
        
        public override bool Equals(object obj) => obj is RTSBatchStopCommand other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Sequence, UnitCount);
    }
}
