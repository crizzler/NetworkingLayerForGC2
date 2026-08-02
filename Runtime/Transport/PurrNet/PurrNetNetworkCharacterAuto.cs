using System.Collections;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Bridges PurrNet's <see cref="NetworkManager"/> lifecycle to a GC2
    /// <see cref="NetworkCharacter"/> by calling
    /// <see cref="NetworkCharacter.InitializeNetworkRole"/> with the correct
    /// (isServer, isOwner, isHost) flags as soon as the manager comes up.
    ///
    /// This is the glue for PurrNet-spawned or scene-placed GC2 characters. Place
    /// this component on the same GameObject as a <c>NetworkCharacter</c>. When a
    /// parent <see cref="NetworkIdentity"/> exists, its replicated owner is used
    /// before falling back to the legacy owner mode below.
    ///
    /// Ownership policy:
    ///  - When <see cref="m_OwnerMode"/> is <see cref="OwnerMode.HostOnly"/> the
    ///    scene-placed character is owned by whichever peer is acting as server /
    ///    host. All joining clients treat it as a remote character.
    ///  - <see cref="OwnerMode.Everyone"/> marks every peer as owner of its own
    ///    local copy (each peer simulates its own character locally; useful for
    ///    a quick "two cameras moving independently" smoke test, but state
    ///    won't truly reconcile until proper per-player spawning is wired).
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Network Character Auto-Init")]
    [DefaultExecutionOrder(-200)]
    [RequireComponent(typeof(NetworkCharacter))]
    public sealed class PurrNetNetworkCharacterAuto : MonoBehaviour
    {
        public enum OwnerMode
        {
            HostOnly = 0,
            Everyone = 1
        }

        [Header("References")]
        [InspectorName("PurrNet Network Manager (Optional Scene Override)")]
        [Tooltip("Optional PurrNet.NetworkManager scene-instance override. Leave empty on prefab assets: " +
                 "Unity cannot store a scene reference on a prefab, and runtime auto-resolution uses NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Header("Ownership")]
        [Tooltip("HostOnly: only the hosting peer owns this character (others see it as remote).\n" +
                 "Everyone: every peer owns its local copy (independent simulations).")]
        [SerializeField] private OwnerMode m_OwnerMode = OwnerMode.HostOnly;

        [Tooltip("When a PurrNet NetworkIdentity is present, prefer its replicated owner over Owner Mode.")]
        [SerializeField] private bool m_UseNetworkIdentityOwner = true;

        [Tooltip("When using NetworkIdentity ownership, wait for PurrNet to spawn the identity and replicate its owner before initializing.")]
        [SerializeField] private bool m_WaitForNetworkIdentityOwner = true;

        [Tooltip("Maximum time to wait for the host client half or NetworkIdentity ownership before falling back to Owner Mode.")]
        [Min(0.5f)]
        [SerializeField] private float m_StartupWaitTimeout = 8f;

        private NetworkCharacter m_Character;
        private NetworkIdentity m_Identity;
        private NetworkManager m_HookedManager;
        private NetworkManager m_InitializedManager;
        private bool m_Initialized;
        private bool m_InitializedUsingOwnerModeFallback;
        private Coroutine m_PendingInit;
        private PlayerID? m_SpawnedOwnerHint;
        private bool m_MissingManagerWarningLogged;
        private bool m_OwnerFallbackWarningLogged;

        private NetworkManager ActiveManager
        {
            get
            {
                if (m_NetworkManager != null) return m_NetworkManager;

                // Normalize Unity's destroyed-object pseudo-null so reference-based
                // manager change detection cannot mistake it for a live instance.
                NetworkManager main = NetworkManager.main;
                return main != null ? main : null;
            }
        }

        private void Awake()
        {
            m_Character = GetComponent<NetworkCharacter>();
            m_Identity = GetComponentInParent<NetworkIdentity>();
        }

        public void SetSpawnedOwnerHint(PlayerID owner)
        {
            m_SpawnedOwnerHint = owner;
        }

        public bool TryGetSpawnedOwnerHint(out PlayerID owner)
        {
            if (m_SpawnedOwnerHint.HasValue)
            {
                owner = m_SpawnedOwnerHint.Value;
                return true;
            }

            owner = default;
            return false;
        }

        private void OnEnable()
        {
            TryHook();
            ScheduleDeferredInit();
        }

        private void Start()
        {
            // NetworkManager.main may resolve only after its own Awake.
            TryHook();
            ScheduleDeferredInit();
        }

        private void Update()
        {
            NetworkManager manager = ActiveManager;
            bool initializedManagerChanged =
                m_Initialized && !ReferenceEquals(m_InitializedManager, manager);
            if (!ReferenceEquals(m_HookedManager, manager) || initializedManagerChanged)
            {
                // NetworkManager.main can change when a bootstrap scene is replaced.
                // A role derived from the previous manager is no longer valid.
                if (m_Initialized)
                {
                    ResetInitializedRole();
                }

                TryHook();
                ScheduleDeferredInit();
                return;
            }

            // OwnerMode is only a timeout escape hatch. If PurrNet ownership arrives
            // later, converge back to the identity-derived role instead of keeping a
            // spawned player permanently classified by the fallback.
            if (m_Initialized && m_InitializedUsingOwnerModeFallback)
            {
                RefreshResolvedIdentityOwner();
            }
        }

        private void OnDisable()
        {
            if (m_PendingInit != null)
            {
                StopCoroutine(m_PendingInit);
                m_PendingInit = null;
            }

            UnhookNetworkManager();
            if (m_Initialized) ResetInitializedRole();
            m_SpawnedOwnerHint = null;
            m_OwnerFallbackWarningLogged = false;
        }

        private void TryHook()
        {
            var nm = ActiveManager;
            if (ReferenceEquals(m_HookedManager, nm)) return;

            UnhookNetworkManager();
            if (nm == null) return;

            nm.onNetworkStarted += OnNetworkStarted;
            nm.onNetworkShutdown += OnNetworkShutdown;
            m_HookedManager = nm;
        }

        private void UnhookNetworkManager()
        {
            var nm = m_HookedManager;
            if (nm != null)
            {
                nm.onNetworkStarted -= OnNetworkStarted;
                nm.onNetworkShutdown -= OnNetworkShutdown;
            }

            m_HookedManager = null;
        }

        private void OnNetworkStarted(NetworkManager manager, bool asServer)
        {
            // Critical: during NetworkManager.StartHost() the server callback fires
            // BEFORE the client side has come up. If we initialized eagerly here,
            // nm.isHost would still be false and we'd resolve role=Server (which
            // wires UnitDriverNetworkServer + ProcessServerSimulation -> gravity
            // applied every server tick -> visible "falling" / lost-ground feel on
            // the host's own character). We defer one frame so both isServer and
            // isClient have settled before we ResolveRole.
            if (m_Initialized)
            {
                // This can also be the second half of a topology change on the same
                // manager (for example StartClient after a server-only session).
                ResetInitializedRole();
            }

            ScheduleDeferredInit();
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (m_PendingInit != null)
            {
                StopCoroutine(m_PendingInit);
                m_PendingInit = null;
            }

            // Allow re-init on the remaining half or next session.
            ResetInitializedRole();

            // PurrNet reports the client and server halves independently. If one
            // half remains alive, rebuild the role for that remaining topology.
            if (manager != null && (manager.isServer || manager.isClient))
            {
                ScheduleDeferredInit();
            }
        }

        private void ScheduleDeferredInit()
        {
            if (m_Initialized) return;
            if (m_PendingInit != null) return;
            if (!isActiveAndEnabled) return;
            m_PendingInit = StartCoroutine(DeferredInitRoutine());
        }

        private IEnumerator DeferredInitRoutine()
        {
            // Wait one frame for the host's StartHost() to finish wiring both the
            // server and client modules before we look at nm.isHost / isServer / isClient.
            yield return null;

            // A prefab cannot hold a scene-manager reference, so its normal path is
            // NetworkManager.main. Additive scenes and runtime-created managers can
            // publish that instance after this component's Start. Keep looking rather
            // than silently abandoning character initialization after one frame.
            float managerWarningDeadline =
                Time.unscaledTime + Mathf.Max(0.5f, m_StartupWaitTimeout);
            while (ActiveManager == null)
            {
                TryHook();

                if (!m_MissingManagerWarningLogged &&
                    Time.unscaledTime >= managerWarningDeadline)
                {
                    m_MissingManagerWarningLogged = true;
                    Debug.LogError(
                        "[PurrNetNetworkCharacterAuto] No PurrNet.NetworkManager is available. " +
                        "Leave the manager override empty on prefab assets and add a " +
                        "PurrNet/Network Manager to the scene. PurrNet.RawNetManager and " +
                        "similarly named managers from other packages are not compatible. " +
                        "Auto-initialization will keep waiting for NetworkManager.main.",
                        this);
                }

                yield return null;
            }

            TryHook();

            // If the manager is server-only at this point, also probe a short window
            // for the client side coming up. PurrNet's StartHost registers client
            // modules one frame later, and UDP only flips isClient after the local
            // connection completes. Bail out as soon as both are up, or after the
            // budget if the user really did mean a dedicated server.
            var nm = ActiveManager;
            if (nm != null && nm.isServer && !nm.isClient)
            {
                float deadline = Time.unscaledTime + Mathf.Max(0.5f, m_StartupWaitTimeout);
                while (Time.unscaledTime < deadline && ShouldWaitForHostClient(nm))
                {
                    yield return null;
                }
            }

            bool allowOwnerModeFallback = false;
            if (m_UseNetworkIdentityOwner && HasNetworkIdentity())
            {
                float deadline = Time.unscaledTime + Mathf.Max(0.5f, m_StartupWaitTimeout);
                while (Time.unscaledTime < deadline && ShouldWaitForNetworkIdentityOwner())
                {
                    yield return null;
                }

                // The timeout is an actual fallback boundary. Previously the routine
                // reached this point, called TryInitializeNow without carrying the
                // timeout state, and immediately began another full wait forever.
                allowOwnerModeFallback = ShouldWaitForNetworkIdentityOwner();
                if (allowOwnerModeFallback && !m_OwnerFallbackWarningLogged)
                {
                    m_OwnerFallbackWarningLogged = true;
                    Debug.LogWarning(
                        "[PurrNetNetworkCharacterAuto] NetworkIdentity ownership did not " +
                        $"become available within {Mathf.Max(0.5f, m_StartupWaitTimeout):0.##} seconds. " +
                        $"Falling back to Owner Mode '{m_OwnerMode}'. Ensure spawned player " +
                        "prefabs are given PurrNet ownership; legacy scene-placed characters " +
                        "may intentionally use this fallback.",
                        this);
                }
            }

            m_PendingInit = null;
            TryHook();
            if (!TryInitializeNow(allowOwnerModeFallback) && ShouldRetryInitialization())
            {
                ScheduleDeferredInit();
            }
        }

        private bool TryInitializeNow(bool allowOwnerModeFallback = false)
        {
            if (m_Initialized) return true;
            if (m_Character == null) return false;

            var nm = ActiveManager;
            if (nm == null) return false;

            // Wait until at least one of server/client is up. Otherwise we'd assign
            // the wrong role (e.g., default to RemoteClient before the host actually starts).
            bool serverActive = nm.isServer;
            bool clientActive = nm.isClient;
            if (!serverActive && !clientActive) return false;

            bool isServer = serverActive;
            bool isHost = nm.isHost;

            bool identityApplicable =
                TryResolveNetworkIdentityOwner(nm, out bool identityOwner, out bool identityReady);
            if (!TryResolveInitializationOwner(
                    identityApplicable,
                    identityReady,
                    identityOwner,
                    m_WaitForNetworkIdentityOwner,
                    allowOwnerModeFallback,
                    m_OwnerMode,
                    isServer,
                    clientActive,
                    isHost,
                    out bool isOwner))
            {
                return false;
            }

            m_Character.InitializeNetworkRole(isServer, isOwner, isHost);
            m_Initialized = true;
            m_InitializedManager = nm;
            m_InitializedUsingOwnerModeFallback = identityApplicable && !identityReady;
            return true;
        }

        private void RefreshResolvedIdentityOwner()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || (!manager.isServer && !manager.isClient)) return;

            bool identityApplicable =
                TryResolveNetworkIdentityOwner(manager, out _, out bool identityReady);
            if (!identityApplicable || !identityReady) return;

            ResetInitializedRole();

            // Ownership is ready now, so this does not enter the fallback path again.
            if (!TryInitializeNow() && ShouldRetryInitialization())
            {
                ScheduleDeferredInit();
            }
        }

        private void ResetInitializedRole()
        {
            m_Initialized = false;
            m_InitializedManager = null;
            m_InitializedUsingOwnerModeFallback = false;
            m_Character?.ResetNetworkRole();
        }

        private static bool TryResolveInitializationOwner(
            bool identityApplicable,
            bool identityReady,
            bool identityOwner,
            bool waitForIdentityOwner,
            bool allowOwnerModeFallback,
            OwnerMode ownerMode,
            bool isServer,
            bool isClient,
            bool isHost,
            out bool isOwner)
        {
            if (identityApplicable)
            {
                if (identityReady)
                {
                    isOwner = identityOwner;
                    return true;
                }

                if (waitForIdentityOwner && !allowOwnerModeFallback)
                {
                    isOwner = false;
                    return false;
                }
            }

            isOwner = ResolveOwnerMode(ownerMode, isServer, isClient, isHost);
            return true;
        }

        private static bool ResolveOwnerMode(
            OwnerMode ownerMode,
            bool isServer,
            bool isClient,
            bool isHost)
        {
            switch (ownerMode)
            {
                case OwnerMode.Everyone:
                    return true;
                case OwnerMode.HostOnly:
                default:
                    // Single-character demo: only the hosting peer owns and authoritatively
                    // simulates this character. Joining clients see it as a remote character
                    // and receive state from the host.
                    return isHost || (isServer && !isClient);
            }
        }

        private bool HasNetworkIdentity()
        {
            if (m_Identity == null) m_Identity = GetComponentInParent<NetworkIdentity>();
            return m_Identity != null;
        }

        private bool ShouldWaitForHostClient(NetworkManager nm)
        {
            if (nm == null || !nm.isServer || nm.isClient) return false;

            // StartHost() has a short window where the server is up but the client
            // coroutine has not yet flipped isClient. By the next frame StartClient()
            // has moved clientState away from Disconnected, which pendingHost exposes.
            // A true dedicated server leaves clientState disconnected and should not
            // pay the full host startup timeout.
            return nm.pendingHost;
        }

        private bool ShouldWaitForNetworkIdentityOwner()
        {
            var nm = ActiveManager;
            if (!HasNetworkIdentity()) return false;
            if (!m_WaitForNetworkIdentityOwner) return false;
            if (!m_Identity.isSpawned) return true;

            if (nm != null && nm.isClient)
            {
                if (!nm.isLocalPlayerReady)
                {
                    return true;
                }

                if (nm.isHost && m_SpawnedOwnerHint.HasValue)
                {
                    return false;
                }

                if (TryGetIdentityOwner(nm, false, out _)) return false;
                if (nm.isHost && TryGetIdentityOwner(nm, true, out _)) return false;
                return true;
            }

            if (nm != null && nm.isServer)
            {
                return !TryGetIdentityOwner(nm, true, out _);
            }

            return !m_Identity.owner.HasValue;
        }

        private bool TryResolveNetworkIdentityOwner(NetworkManager nm, out bool isOwner, out bool isReady)
        {
            isOwner = false;
            isReady = false;

            if (!m_UseNetworkIdentityOwner || !HasNetworkIdentity()) return false;
            if (!m_Identity.isSpawned) return true;

            if (nm != null && nm.isClient)
            {
                if (!nm.isLocalPlayerReady)
                {
                    return true;
                }

                if (nm.isHost && m_SpawnedOwnerHint.HasValue)
                {
                    isReady = true;
                    isOwner = m_SpawnedOwnerHint.Value == nm.localPlayer;
                    return true;
                }

                bool hasClientOwner = TryGetIdentityOwner(nm, false, out PlayerID clientOwner);
                PlayerID serverOwner = default;
                bool hasServerOwner = nm.isHost && TryGetIdentityOwner(nm, true, out serverOwner);

                if (!hasClientOwner && !hasServerOwner)
                {
                    return true;
                }

                PlayerID localPlayer = nm.localPlayer;
                isReady = true;
                isOwner = (hasClientOwner && clientOwner == localPlayer) ||
                          (hasServerOwner && serverOwner == localPlayer);
                return true;
            }

            if (nm != null && nm.isServer)
            {
                if (!TryGetIdentityOwner(nm, true, out _))
                {
                    return true;
                }

                // A dedicated/server-side instance is authoritative, not a local GC2 player.
                isReady = true;
                isOwner = false;
                return true;
            }

            if (!m_Identity.owner.HasValue)
            {
                return true;
            }

            isReady = true;
            return true;
        }

        private bool TryGetIdentityOwner(NetworkManager nm, bool asServer, out PlayerID owner)
        {
            owner = default;
            if (nm == null || !HasNetworkIdentity()) return false;

            return nm.TryGetModule(out GlobalOwnershipModule ownership, asServer) &&
                   ownership.TryGetOwner(m_Identity, out owner);
        }

        private bool ShouldRetryInitialization()
        {
            var nm = ActiveManager;
            // A delayed/additively loaded manager is a supported setup. The pending
            // coroutine waits without allocating a fresh coroutine every frame.
            if (nm == null) return true;
            if (nm.isServer || nm.isClient) return true;
            return nm.serverState != ConnectionState.Disconnected ||
                   nm.clientState != ConnectionState.Disconnected;
        }
    }
}
