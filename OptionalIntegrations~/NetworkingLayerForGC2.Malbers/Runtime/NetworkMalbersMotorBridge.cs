#if ARAWN_GC2_MALBERS
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Characters;
using MalbersAnimations;
using MalbersAnimations.Controller;
using MalbersAnimations.Controller.AI;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Malbers
{
    /// <summary>
    /// Keeps the Malbers Animal Controller visually and behaviourally coherent while the
    /// Game Creator 2 Networking Layer owns movement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Server-authoritative NPCs keep Malbers' own <c>UnitDriverMAnimal</c> on the simulation
    /// authority, so MAnimal remains the native movement authority there. Every other network
    /// role receives a Networking Layer driver (<c>UnitDriverNetworkServer</c>,
    /// <c>UnitDriverNetworkClient</c>, <c>UnitDriverNetworkRemote</c>, or a NavMesh equivalent).
    /// In those roles this bridge:
    /// </para>
    /// <list type="bullet">
    /// <item><description>disables MAnimal's root position and rotation writes;</description></item>
    /// <item><description>suppresses local Malbers input and AI sources on replicas;</description></item>
    /// <item><description>feeds the network driver's authoritative or interpolated velocity into
    /// MAnimal's locomotion input so its Animator state machine still tells the truth;</description></item>
    /// <item><description>makes the root Rigidbody kinematic while the network driver owns the
    /// transform.</description></item>
    /// </list>
    /// <para>
    /// The bridge is intentionally compiled into Assembly-CSharp behind
    /// <c>ARAWN_GC2_MALBERS</c>. The main Networking Layer assembly locates it by type name and
    /// attaches it during role initialization, so projects without Malbers never reference it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10000)]
    [AddComponentMenu("Game Creator/Network/Malbers Motor Bridge")]
    public sealed class NetworkMalbersMotorBridge : MonoBehaviour
    {
        private const float MaximumPresentationAxis = 1f;
        private const float MinimumReferenceSpeed = 0.01f;
        private const string MalbersTankPlayerTypeName =
            "MalbersAnimations.Integrations.GameCreator2.UnitPlayerTankMAnimal";

        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [Header("Presentation")]
        [Tooltip(
            "Disables Malbers input and AI sources while a network driver owns the root. " +
            "Disable this only when another local system deliberately drives MAnimal on replicas.")]
        [SerializeField] private bool m_SuspendMalbersInput = true;

        [Tooltip(
            "Copies the network driver's grounded flag into MAnimal. Leave disabled to let " +
            "MAnimal's own ground probing continue while the root is network-driven.")]
        [SerializeField] private bool m_SynchronizeGrounded = false;

        [Tooltip(
            "Suppresses MAnimalAIControl and MAnimalBrain on presentation replicas. Disable " +
            "only when another local system must keep a Malbers AI component running.")]
        [SerializeField] private bool m_SuppressMalbersAi = true;

        [Tooltip(
            "Also suppresses IInputSource components on parent objects. Leave disabled when a " +
            "shared parent input source also drives other characters.")]
        [SerializeField] private bool m_SuppressInputSourcesOnParents = false;

        [Header("Diagnostics")]
        [SerializeField] private bool m_LogDiagnostics = false;
        [SerializeField] private float m_DiagnosticInterval = 1f;

        // MEMBERS: -------------------------------------------------------------------------------

        private Character m_Character;
        private MAnimal m_Animal;
        private NetworkCharacter m_NetworkCharacter;
        private Rigidbody m_Rigidbody;
        private TUnitPlayer m_CachedPlayerUnit;
        private TUnitPlayer m_InstalledPlayerUnit;

        private NetworkMalbersMotionMode m_Mode = NetworkMalbersMotionMode.Inactive;
        private NetworkCharacter.NetworkRole m_LastRole = NetworkCharacter.NetworkRole.None;
        private IUnitDriver m_LastDriver;
        private bool m_HasPresentationSnapshot;
        private bool m_InputsSuppressed;
        private bool m_Subscribed;
        private bool m_DriverEventSubscribed;
        private bool m_RagdollEventSubscribed;
        private bool m_PendingRigidbodyRestore;
        private bool m_ReassertPreInputAtEndOfFrame;
        private bool m_WarnedParentInputSource;
        private bool m_WarnedMalbersPlatformMovement;
        private float m_NextDiagnosticTime;
        private bool m_CachedRootMotion;
        private bool m_CachedRootMotionRotation;
        private bool m_CachedDisablePosition;
        private bool m_CachedDisableRotation;
        private bool m_CachedUseRawInput;
        private bool m_CachedUseCameraInput;

        private bool m_CachedRigidbodyKinematic;
        private bool m_CachedRigidbodyUseGravity;
        private bool m_CachedRigidbodyDetectCollisions;

        private Vector3 m_PresentationAxis;

        private readonly List<MonoBehaviour> m_SuppressedInputs = new();
        private readonly List<MAnimalAIControl> m_SuppressedAiControls = new();
        private readonly List<MAnimalBrain> m_SuppressedBrains = new();

        // PROPERTIES: ----------------------------------------------------------------------------

        public NetworkMalbersMotionMode Mode => m_Mode;

        public bool IsPresentationActive =>
            m_Mode == NetworkMalbersMotionMode.NetworkPresentation;

        /// <summary>
        /// True when this bridge owns MAnimal's grounded flag from the network driver, so the
        /// Malbers state bridge must not also write it.
        /// </summary>
        public bool SynchronizeGrounded => m_SynchronizeGrounded;

        // INITIALIZERS: --------------------------------------------------------------------------

        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_Animal = GetComponent<MAnimal>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
            m_Rigidbody = m_Animal != null && m_Animal.RB != null
                ? m_Animal.RB
                : GetComponent<Rigidbody>();
        }

        private void OnEnable()
        {
            Subscribe();
            RefreshMode(force: true);
        }

        private void Start()
        {
            RefreshMode(force: true);
        }

        private void OnDisable()
        {
            Unsubscribe();
            UnsubscribeKernelDriverEvent();

            if (m_Mode == NetworkMalbersMotionMode.NetworkPresentation)
            {
                RestoreNativeMotion();
            }

            if (!m_PendingRigidbodyRestore)
            {
                UnsubscribeRagdollEvents();
            }

            m_ReassertPreInputAtEndOfFrame = false;
            m_Mode = NetworkMalbersMotionMode.Inactive;
            m_LastRole = NetworkCharacter.NetworkRole.None;
            m_LastDriver = null;
        }

        private void OnDestroy()
        {
            Unsubscribe();
            UnsubscribeKernelDriverEvent();

            if (!IsRagdollActive() &&
                (m_HasPresentationSnapshot || m_PendingRigidbodyRestore))
            {
                m_PendingRigidbodyRestore = false;
                RestoreRigidbodyState();
                NetworkRagdollPhysicsGuard.Unregister(m_Character, m_Rigidbody);
            }
            else if (m_HasPresentationSnapshot &&
                     IsRagdollActive() &&
                     m_Rigidbody != null)
            {
                // The bridge is disappearing while still in presentation inside an active
                // epoch. Publish the authored baseline so recovery does not leave the character
                // permanently kinematic/no-gravity after the component is gone.
                NetworkRagdollPhysicsGuard.SetIntendedRigidbodyState(
                    m_Character,
                    m_Rigidbody,
                    m_CachedRigidbodyKinematic,
                    m_CachedRigidbodyUseGravity,
                    m_CachedRigidbodyDetectCollisions);
            }

            // During an active ragdoll epoch the guard owns the published baseline, so recovery
            // still restores it after this component is gone.
            UnsubscribeRagdollEvents();
        }

        private void Update()
        {
            RefreshMode(force: false);
            CompletePendingRigidbodyRestore();

            if (m_Mode != NetworkMalbersMotionMode.NetworkPresentation) return;

            TickPresentation();
        }

        private void LateUpdate()
        {
            if (!m_ReassertPreInputAtEndOfFrame) return;

            m_ReassertPreInputAtEndOfFrame = false;
            if (m_Mode != NetworkMalbersMotionMode.NetworkPresentation) return;

            // A facing unit that re-subscribed after the bridge inside EventChangeDriver can
            // still move its PreInput handler after ours. Reassert once after all Update-time
            // kernel subscribers have run so the next MAnimal PreInput pass uses the network
            // axis as its final decision.
            ReassertPresentationPreInputPriority();
        }

        // MODE HANDLING: -------------------------------------------------------------------------

        private void Subscribe()
        {
            if (m_Subscribed || m_NetworkCharacter == null) return;

            m_NetworkCharacter.OnRoleAssigned += OnRoleAssigned;
            m_NetworkCharacter.OnRoleReset += OnRoleReset;
            m_NetworkCharacter.OnDriverAssigned += OnDriverAssigned;
            m_Subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!m_Subscribed) return;

            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnRoleAssigned -= OnRoleAssigned;
                m_NetworkCharacter.OnRoleReset -= OnRoleReset;
                m_NetworkCharacter.OnDriverAssigned -= OnDriverAssigned;
            }

            m_Subscribed = false;
        }

        private void SubscribeKernelDriverEvent()
        {
            if (m_DriverEventSubscribed || m_Character == null || m_Character.Kernel == null)
            {
                return;
            }

            m_Character.Kernel.EventChangeDriver += OnKernelRuntimeUnitChanged;
            m_Character.Kernel.EventChangePlayer += OnKernelRuntimeUnitChanged;
            m_Character.Kernel.EventChangeFacing += OnKernelRuntimeUnitChanged;
            m_DriverEventSubscribed = true;
        }

        private void UnsubscribeKernelDriverEvent()
        {
            if (!m_DriverEventSubscribed) return;

            if (m_Character != null && m_Character.Kernel != null)
            {
                m_Character.Kernel.EventChangeDriver -= OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangePlayer -= OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangeFacing -= OnKernelRuntimeUnitChanged;
            }

            m_DriverEventSubscribed = false;
        }

        private void OnKernelRuntimeUnitChanged()
        {
            if (m_Mode != NetworkMalbersMotionMode.NetworkPresentation) return;

            IUnitDriver driver = m_Character != null ? m_Character.Driver : null;
            if (driver == null ||
                !NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverHierarchy(driver.GetType()))
            {
                return;
            }

            // Kernel events may still have later subscribers which re-subscribe their PreInput
            // handler after this callback. Keep an immediate reassert and repeat once after all
            // Update-time subscribers have finished.
            m_ReassertPreInputAtEndOfFrame = true;
            ReassertPresentationPreInputPriority();
        }

        private void SubscribeRagdollEvents()
        {
            if (m_RagdollEventSubscribed || m_Character == null || m_Character.Ragdoll == null)
            {
                return;
            }

            m_Character.Ragdoll.EventAfterStartRecover += OnRagdollRecoveryStarted;
            m_Character.Ragdoll.EventAfterFinishRecover += OnRagdollRecoveryFinished;
            m_RagdollEventSubscribed = true;
        }

        private void UnsubscribeRagdollEvents()
        {
            if (!m_RagdollEventSubscribed) return;

            if (m_Character != null && m_Character.Ragdoll != null)
            {
                m_Character.Ragdoll.EventAfterStartRecover -= OnRagdollRecoveryStarted;
                m_Character.Ragdoll.EventAfterFinishRecover -= OnRagdollRecoveryFinished;
            }

            m_RagdollEventSubscribed = false;
        }

        private void OnRagdollRecoveryStarted()
        {
            if (m_Mode != NetworkMalbersMotionMode.NetworkPresentation) return;
            ApplyPresentationRigidbodyState();
        }

        private void OnRagdollRecoveryFinished()
        {
            if (m_Mode == NetworkMalbersMotionMode.NetworkPresentation)
            {
                EnsurePresentationRigidbodyState();
                return;
            }

            CompletePendingRigidbodyRestore();
        }

        /// <summary>
        /// Malbers facing units re-subscribe their own <c>MAnimal.PreInput</c> handler when the
        /// GC2 driver changes. Move the bridge handler back to the end so the network movement
        /// axis remains the final input decision.
        /// </summary>
        private void ReassertPresentationPreInputPriority()
        {
            if (m_Animal == null) return;

            // Move this bridge's own kernel handlers to the end as well. A facing unit can
            // re-subscribe after the bridge at any time, and its ChangeDriver handler would
            // otherwise run after ours and permanently reclaim the final PreInput slot.
            if (m_Character != null && m_Character.Kernel != null)
            {
                m_Character.Kernel.EventChangeDriver -= OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangeDriver += OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangePlayer -= OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangePlayer += OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangeFacing -= OnKernelRuntimeUnitChanged;
                m_Character.Kernel.EventChangeFacing += OnKernelRuntimeUnitChanged;
            }

            m_Animal.PreInput -= OnPresentationPreInput;
            m_Animal.PreInput += OnPresentationPreInput;
            OnPresentationPreInput(m_Animal);
        }

        private void OnRoleAssigned(NetworkCharacter.NetworkRole role) => RefreshMode(force: true);

        private void OnDriverAssigned(IUnitDriver driver) => RefreshMode(force: true);

        private void OnRoleReset()
        {
            if (m_Mode == NetworkMalbersMotionMode.NetworkPresentation)
            {
                RestoreNativeMotion();
            }

            m_Mode = NetworkMalbersMotionMode.Inactive;
            m_LastRole = NetworkCharacter.NetworkRole.None;
            m_LastDriver = null;
        }

        private void RefreshMode(bool force)
        {
            if (m_Character == null) m_Character = GetComponent<Character>();
            if (m_Animal == null) m_Animal = GetComponent<MAnimal>();
            if (m_NetworkCharacter == null) m_NetworkCharacter = GetComponent<NetworkCharacter>();
            if (m_NetworkCharacter != null) Subscribe();

            if (m_Animal == null || m_Character == null)
            {
                if (m_Mode == NetworkMalbersMotionMode.NetworkPresentation)
                {
                    RestoreNativeMotion();
                }

                m_Mode = NetworkMalbersMotionMode.Inactive;
                m_LastRole = NetworkCharacter.NetworkRole.None;
                m_LastDriver = null;
                return;
            }

            bool initialized = m_NetworkCharacter != null &&
                               m_NetworkCharacter.Role != NetworkCharacter.NetworkRole.None;
            NetworkCharacter.NetworkRole role = m_NetworkCharacter != null
                ? m_NetworkCharacter.Role
                : NetworkCharacter.NetworkRole.None;
            IUnitDriver driver = m_Character.Driver;
            Type driverType = driver != null ? driver.GetType() : null;

            if (!force &&
                m_LastRole == role &&
                ReferenceEquals(m_LastDriver, driver))
            {
                return;
            }

            m_LastRole = role;
            m_LastDriver = driver;

            NetworkMalbersMotionMode resolved =
                NetworkMalbersIntegrationPolicy.ResolveMode(initialized, role, driverType);

            if (resolved == NetworkMalbersMotionMode.NetworkPresentation)
            {
                SubscribeKernelDriverEvent();
                SubscribeRagdollEvents();
            }
            else
            {
                UnsubscribeKernelDriverEvent();
                // RestoreNativeMotion keeps the ragdoll completion subscription alive while a
                // deferred Rigidbody restore is pending, so it owns that unsubscribe.
            }

            if (!force && resolved == m_Mode) return;

            // Publish the mode before EnterPresentation runs. The player swap installs a new
            // UnitPlayerTankNetwork and raises EventChangePlayer; that kernel event must observe
            // presentation mode so the bridge can re-assert its PreInput priority.
            NetworkMalbersMotionMode previous = m_Mode;
            if (resolved != previous)
            {
                m_Mode = resolved;
                LogDiagnostic($"mode {previous} -> {resolved} role={role} " +
                              $"driver={driverType?.Name ?? "<none>"}");
            }

            if (resolved == NetworkMalbersMotionMode.NetworkPresentation)
            {
                if (previous != NetworkMalbersMotionMode.NetworkPresentation)
                {
                    EnterPresentation();
                }
            }
            else if (previous == NetworkMalbersMotionMode.NetworkPresentation)
            {
                RestoreNativeMotion();
            }
        }

        private void EnterPresentation()
        {
            if (m_HasPresentationSnapshot) return;

            if (m_Rigidbody == null)
            {
                m_Rigidbody = m_Animal.RB != null ? m_Animal.RB : GetComponent<Rigidbody>();
            }

            m_CachedRootMotion = m_Animal.RootMotion;
            m_CachedRootMotionRotation = m_Animal.RootMotionRotation;
            m_CachedDisablePosition = m_Animal.DisablePosition;
            m_CachedDisableRotation = m_Animal.DisableRotation;
            m_CachedUseRawInput = m_Animal.UseRawInput;
            m_CachedUseCameraInput = m_Animal.UseCameraInput;

            // A pending restore means authority left presentation during an active ragdoll
            // epoch. The root body was deliberately left suspended, so the previously cached
            // authored physics state is still the correct one to preserve if presentation is
            // re-entered before recovery finishes.
            bool preserveCachedRigidbodyState =
                m_PendingRigidbodyRestore && m_Rigidbody != null;

            m_HasPresentationSnapshot = true;
            m_PendingRigidbodyRestore = false;

            m_Animal.DisablePosition = true;
            m_Animal.DisableRotation = true;
            m_Animal.RootMotion = false;
            m_Animal.RootMotionRotation = false;
            m_Animal.UseRawInput = true;
            m_Animal.UseCameraInput = false;
            m_PresentationAxis = Vector3.zero;
            m_Animal.PreInput -= OnPresentationPreInput;
            m_Animal.PreInput += OnPresentationPreInput;
            m_Animal.Reset_Movement();

            SubscribeKernelDriverEvent();
            ReassertPresentationPreInputPriority();
            m_ReassertPreInputAtEndOfFrame = true;

            if (m_Rigidbody != null)
            {
                bool hasGuardBaseline = false;
                if (!preserveCachedRigidbodyState)
                {
                    // A replacement bridge may attach during an active epoch after this body
                    // was already suspended. Prefer any captured authored baseline over the
                    // live suspended values, including after a silently ended epoch.
                    hasGuardBaseline =
                        NetworkRagdollPhysicsGuard.TryGetRigidbodyBaseline(
                            m_Character,
                            m_Rigidbody,
                            out bool cachedKinematic,
                            out bool cachedUseGravity,
                            out bool cachedDetectCollisions);
                    if (hasGuardBaseline)
                    {
                        m_CachedRigidbodyKinematic = cachedKinematic;
                        m_CachedRigidbodyUseGravity = cachedUseGravity;
                        m_CachedRigidbodyDetectCollisions = cachedDetectCollisions;
                    }
                    else
                    {
                        m_CachedRigidbodyKinematic = m_Rigidbody.isKinematic;
                        m_CachedRigidbodyUseGravity = m_Rigidbody.useGravity;
                        m_CachedRigidbodyDetectCollisions = m_Rigidbody.detectCollisions;
                    }
                }

                if (IsRagdollActive() || hasGuardBaseline)
                {
                    // GC2 owns the dynamic epoch or a previous bridge left a captured
                    // baseline. Adopt the presentation baseline rather than letting the guard
                    // capture the currently suspended or still-authored body; its recovery then
                    // restores a kinematic root even if this bridge dies before
                    // EventAfterFinishRecover.
                    NetworkRagdollPhysicsGuard.Adopt(
                        m_Character,
                        m_Rigidbody,
                        true,
                        false,
                        m_CachedRigidbodyDetectCollisions,
                        replaceActiveBaseline: true);
                }
                else
                {
                    NetworkRagdollPhysicsGuard.Register(m_Character, m_Rigidbody);
                }

                ApplyPresentationRigidbodyState();
            }

            if (m_SuspendMalbersInput)
            {
                SuppressInputSources();
            }

            EnsureNetworkCompatiblePlayerUnit();

            LogDiagnostic("presentation enabled");
        }

        /// <summary>
        /// GC2's networking layer already upgrades the stock tank player unit. Malbers tank
        /// control is a separate <c>TUnitPlayer</c> subtype, so upgrade it to the networked
        /// equivalent while the network driver owns movement. Leaving the local tank unit in
        /// place would feed <c>Character.Motion</c> but never the network input sink.
        /// </summary>
        private void EnsureNetworkCompatiblePlayerUnit()
        {
            if (m_Character == null || m_Character.Kernel == null) return;
            if (!m_Character.IsPlayer || m_Character.Player == null) return;

            Type tankPlayerType =
                NetworkMalbersIntegrationPolicy.FindLoadedType(MalbersTankPlayerTypeName);
            if (tankPlayerType == null ||
                !tankPlayerType.IsInstanceOfType(m_Character.Player))
            {
                return;
            }

            TUnitPlayer authoredPlayerUnit = m_Character.Player as TUnitPlayer;
            if (authoredPlayerUnit == null) return;

            var installedPlayerUnit = new UnitPlayerTankNetwork();
            m_CachedPlayerUnit = authoredPlayerUnit;
            m_InstalledPlayerUnit = installedPlayerUnit;

            m_Character.Kernel.ChangePlayer(m_Character, installedPlayerUnit);
            LogDiagnostic("upgraded Malbers tank player unit to UnitPlayerTankNetwork");
        }

        private void RestoreNetworkCompatiblePlayerUnit()
        {
            TUnitPlayer installedPlayerUnit = m_InstalledPlayerUnit;
            TUnitPlayer authoredPlayerUnit = m_CachedPlayerUnit;
            m_InstalledPlayerUnit = null;
            m_CachedPlayerUnit = null;

            if (installedPlayerUnit == null ||
                authoredPlayerUnit == null ||
                m_Character == null ||
                m_Character.Kernel == null)
            {
                return;
            }

            // NetworkCharacter or another system may have cleared or replaced the player unit
            // during cleanup/role migration. Never revive a unit this bridge no longer owns.
            if (!ReferenceEquals(m_Character.Player, installedPlayerUnit)) return;

            // Remote/observer roles intentionally clear Character.IsPlayer. Do not restore the
            // authored local player unit into a role that must not process local input.
            if (!m_Character.IsPlayer) return;

            m_Character.Kernel.ChangePlayer(m_Character, authoredPlayerUnit);
            LogDiagnostic("restored the authored Malbers tank player unit");
        }

        private void RestoreNativeMotion()
        {
            if (!m_HasPresentationSnapshot) return;

            m_HasPresentationSnapshot = false;
            m_ReassertPreInputAtEndOfFrame = false;

            UnsubscribeKernelDriverEvent();

            if (m_Animal != null)
            {
                m_Animal.PreInput -= OnPresentationPreInput;
                m_Animal.RootMotion = m_CachedRootMotion;
                m_Animal.RootMotionRotation = m_CachedRootMotionRotation;
                m_Animal.DisablePosition = m_CachedDisablePosition;
                m_Animal.DisableRotation = m_CachedDisableRotation;
                m_Animal.UseRawInput = m_CachedUseRawInput;
                m_Animal.UseCameraInput = m_CachedUseCameraInput;
                m_Animal.Reset_Movement();
            }

            if (m_Rigidbody != null)
            {
                if (IsRagdollActive())
                {
                    // GC2 still owns the physics epoch. Publish the authored baseline to the
                    // guard first, then defer the bridge's own restore until recovery ends.
                    // The guard can now restore the correct owner even if this component is
                    // disabled or destroyed before EventAfterFinishRecover.
                    m_PendingRigidbodyRestore = true;
                    NetworkRagdollPhysicsGuard.SetIntendedRigidbodyState(
                        m_Character,
                        m_Rigidbody,
                        m_CachedRigidbodyKinematic,
                        m_CachedRigidbodyUseGravity,
                        m_CachedRigidbodyDetectCollisions);
                }
                else
                {
                    RestoreRigidbodyState();
                    NetworkRagdollPhysicsGuard.Unregister(m_Character, m_Rigidbody);
                }
            }

            if (!m_PendingRigidbodyRestore)
            {
                UnsubscribeRagdollEvents();
            }

            RestoreInputSources();
            RestoreNetworkCompatiblePlayerUnit();
            LogDiagnostic("presentation disabled");
        }

        private bool IsRagdollActive()
        {
            return m_Character != null &&
                   m_Character.Ragdoll != null &&
                   m_Character.Ragdoll.IsRagdoll;
        }

        private void ApplyPresentationRigidbodyState()
        {
            if (m_Rigidbody == null) return;

            m_Rigidbody.isKinematic = true;
            m_Rigidbody.useGravity = false;

#if UNITY_6000_0_OR_NEWER
            m_Rigidbody.linearVelocity = Vector3.zero;
#else
            m_Rigidbody.velocity = Vector3.zero;
#endif
            m_Rigidbody.angularVelocity = Vector3.zero;
        }

        private void EnsurePresentationRigidbodyState()
        {
            if (m_Rigidbody == null) return;

            bool hasLinearVelocity;
#if UNITY_6000_0_OR_NEWER
            hasLinearVelocity = m_Rigidbody.linearVelocity.sqrMagnitude > 0.0001f;
#else
            hasLinearVelocity = m_Rigidbody.velocity.sqrMagnitude > 0.0001f;
#endif
            bool hasAngularVelocity = m_Rigidbody.angularVelocity.sqrMagnitude > 0.0001f;

            if (m_Rigidbody.isKinematic &&
                !m_Rigidbody.useGravity &&
                !hasLinearVelocity &&
                !hasAngularVelocity)
            {
                return;
            }

            ApplyPresentationRigidbodyState();
        }

        private void RestoreRigidbodyState()
        {
            if (m_Rigidbody == null) return;

            m_Rigidbody.isKinematic = m_CachedRigidbodyKinematic;
            m_Rigidbody.useGravity = m_CachedRigidbodyUseGravity;
            m_Rigidbody.detectCollisions = m_CachedRigidbodyDetectCollisions;
        }

        private bool CanCompleteDeferredRigidbodyRestore()
        {
            if (m_Character == null) return true;

            IUnitDriver driver = m_Character.Driver;
            if (driver == null) return true;

            // Only hand the root back to native MAnimal or to no driver at all. A foreign
            // custom driver may still own the transform in Inactive mode, and a network
            // driver must keep the body suspended for its presentation state.
            return NetworkMalbersIntegrationPolicy.IsNativeDriverHierarchy(driver.GetType());
        }

        private void CompletePendingRigidbodyRestore()
        {
            if (!m_PendingRigidbodyRestore) return;
            if (m_Mode == NetworkMalbersMotionMode.NetworkPresentation) return;
            if (IsRagdollActive()) return;

            if (!CanCompleteDeferredRigidbodyRestore())
            {
                // The guard normally restores the intended baseline at recovery. If the epoch
                // ended without its finish callback, apply that baseline now; then drop the
                // bridge-side bookkeeping so a foreign/network owner does not leave this
                // subscription alive forever. Leave the guard registration intact for its
                // next epoch.
                NetworkRagdollPhysicsGuard.ApplyPendingRigidbodyBaseline(
                    m_Character,
                    m_Rigidbody);
                m_PendingRigidbodyRestore = false;
                UnsubscribeRagdollEvents();
                LogDiagnostic("released deferred Rigidbody restore to the active driver");
                return;
            }

            m_PendingRigidbodyRestore = false;
            RestoreRigidbodyState();
            NetworkRagdollPhysicsGuard.Unregister(m_Character, m_Rigidbody);
            UnsubscribeRagdollEvents();
            LogDiagnostic("completed deferred Rigidbody restore after ragdoll");
        }

        private void WarnIfMalbersPlatformMovementIsActive()
        {
            if (m_WarnedMalbersPlatformMovement || m_Animal == null || m_Animal.platform == null)
            {
                return;
            }

            m_WarnedMalbersPlatformMovement = true;
            Debug.LogWarning(
                $"[NetworkMalbers] '{name}' is standing on MAnimal platform " +
                $"'{m_Animal.platform.name}'. MAnimal's platform delta bypasses " +
                "DisablePosition and is not carried by this companion; the network driver " +
                "owns the replicated root. Disable platform carry for this character or add " +
                "a dedicated networked support flow.",
                this);
        }

        // PRESENTATION: --------------------------------------------------------------------------

        /// <summary>
        /// GC2 facing units subscribe to <c>MAnimal.PreInput</c> during character startup, so
        /// they run after the driver update. Re-assert the network movement axis here so an
        /// explicit facing/look-at intent cannot switch MAnimal back to rotate-only input while
        /// the network driver owns the root.
        /// </summary>
        private void OnPresentationPreInput(MAnimal animal)
        {
            if (animal == null) return;

            animal.Rotate_at_Direction = false;
            animal.SetInputAxis(m_PresentationAxis);
        }

        private void TickPresentation()
        {
            if (m_Animal == null || m_Character == null) return;

            SubscribeRagdollEvents();
            EnsurePresentationRigidbodyState();
            WarnIfMalbersPlatformMovementIsActive();

            IUnitDriver driver = m_Character.Driver;
            bool suspended = driver == null ||
                             m_Character.IsDead ||
                             (m_Character.Ragdoll != null && m_Character.Ragdoll.IsRagdoll);

            float referenceSpeed = MinimumReferenceSpeed;
            if (m_Character.Motion != null)
            {
                referenceSpeed = Mathf.Max(MinimumReferenceSpeed, m_Character.Motion.LinearSpeed);
            }

            Vector3 worldVelocity = suspended ? Vector3.zero : driver.WorldMoveDirection;
            Vector3 axis = NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                worldVelocity,
                transform,
                referenceSpeed,
                MaximumPresentationAxis,
                suspended);

            m_PresentationAxis = axis;
            m_Animal.SetInputAxis(axis);

            if (m_SynchronizeGrounded && driver != null)
            {
                m_Animal.Grounded = driver.IsGrounded;
            }

            LogTickDiagnostic(axis, worldVelocity, referenceSpeed, suspended);
        }

        // INPUT SUPPRESSION: ---------------------------------------------------------------------

        private void SuppressInputSources()
        {
            if (m_InputsSuppressed) return;

            // Malbers exposes every movement input through IInputSource. This covers
            // MalbersInput, MInputLink (the Input System package), mobile input, and custom
            // input sources without referencing their optional assemblies.
            SuppressInputSourcesIn(GetComponentsInChildren<MonoBehaviour>(true));

            if (m_SuppressInputSourcesOnParents)
            {
                SuppressInputSourcesIn(GetComponentsInParent<MonoBehaviour>(true));
            }
            else
            {
                WarnIfParentInputSourceIsUnsuppressed();
            }

            if (m_SuppressMalbersAi)
            {
                MAnimalAIControl[] aiControls = GetComponentsInChildren<MAnimalAIControl>(true);
                for (int i = 0; i < aiControls.Length; i++)
                {
                    MAnimalAIControl aiControl = aiControls[i];
                    if (aiControl == null || !aiControl.enabled) continue;

                    m_SuppressedAiControls.Add(aiControl);
                    aiControl.enabled = false;
                }

                MAnimalBrain[] brains = GetComponentsInChildren<MAnimalBrain>(true);
                for (int i = 0; i < brains.Length; i++)
                {
                    MAnimalBrain brain = brains[i];
                    if (brain == null || !brain.enabled) continue;

                    m_SuppressedBrains.Add(brain);
                    brain.enabled = false;
                }
            }

            m_InputsSuppressed = true;
        }

        private void SuppressInputSourcesIn(MonoBehaviour[] behaviours)
        {
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !behaviour.enabled) continue;
                if (behaviour is not IInputSource) continue;
                if (m_SuppressedInputs.Contains(behaviour)) continue;

                m_SuppressedInputs.Add(behaviour);
                behaviour.enabled = false;
            }
        }

        private void WarnIfParentInputSourceIsUnsuppressed()
        {
            if (m_WarnedParentInputSource) return;

            MonoBehaviour[] parents = GetComponentsInParent<MonoBehaviour>(true);
            for (int i = 0; i < parents.Length; i++)
            {
                MonoBehaviour behaviour = parents[i];
                if (behaviour == null || behaviour == this || !behaviour.enabled) continue;
                if (behaviour is not IInputSource) continue;

                m_WarnedParentInputSource = true;
                Debug.LogWarning(
                    $"[NetworkMalbers] '{name}' found an enabled parent IInputSource " +
                    $"('{behaviour.GetType().Name}') that is not suppressed. Enable " +
                    "Suppress Input Sources On Parents or disable the shared input source.",
                    this);
                return;
            }
        }

        /// <summary>
        /// Registers an additional Malbers input source which is not a child of this character,
        /// such as a sibling, shared controller, or custom input bridge. The bridge disables it
        /// for the presentation epoch and restores its exact prior enabled state on exit.
        /// </summary>
        public void RegisterAdditionalInputSource(MonoBehaviour inputSource)
        {
            if (m_Mode != NetworkMalbersMotionMode.NetworkPresentation) return;
            if (inputSource == null || !inputSource.enabled) return;
            if (inputSource is not IInputSource) return;
            if (m_SuppressedInputs.Contains(inputSource)) return;

            m_SuppressedInputs.Add(inputSource);
            inputSource.enabled = false;
            m_InputsSuppressed = true;
        }

        private void RestoreInputSources()
        {
            if (!m_InputsSuppressed) return;

            for (int i = m_SuppressedBrains.Count - 1; i >= 0; i--)
            {
                if (m_SuppressedBrains[i] != null) m_SuppressedBrains[i].enabled = true;
            }

            for (int i = m_SuppressedAiControls.Count - 1; i >= 0; i--)
            {
                if (m_SuppressedAiControls[i] != null) m_SuppressedAiControls[i].enabled = true;
            }

            for (int i = m_SuppressedInputs.Count - 1; i >= 0; i--)
            {
                if (m_SuppressedInputs[i] != null) m_SuppressedInputs[i].enabled = true;
            }

            m_SuppressedBrains.Clear();
            m_SuppressedAiControls.Clear();
            m_SuppressedInputs.Clear();
            m_InputsSuppressed = false;
        }

        // DIAGNOSTICS: ---------------------------------------------------------------------------

        private void LogDiagnostic(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log($"[NetworkMalbers] '{name}' {message}", this);
        }

        private void LogTickDiagnostic(
            Vector3 axis,
            Vector3 worldVelocity,
            float referenceSpeed,
            bool suspended)
        {
            if (!m_LogDiagnostics) return;
            if (Time.unscaledTime < m_NextDiagnosticTime) return;

            m_NextDiagnosticTime = Time.unscaledTime + Mathf.Max(0.1f, m_DiagnosticInterval);
            Debug.Log(
                $"[NetworkMalbers] '{name}' axis={axis} worldVelocity={worldVelocity} " +
                $"referenceSpeed={referenceSpeed:F2} suspended={suspended} " +
                $"driver={m_Character.Driver?.GetType().Name ?? "<none>"}",
                this);
        }
    }
}
#endif
