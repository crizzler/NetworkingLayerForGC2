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
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Header("Ownership")]
        [Tooltip("HostOnly: only the hosting peer owns this character (others see it as remote).\n" +
                 "Everyone: every peer owns its local copy (independent simulations).")]
        [SerializeField] private OwnerMode m_OwnerMode = OwnerMode.HostOnly;

        [Tooltip("When a PurrNet NetworkIdentity is present, prefer its replicated owner over Owner Mode.")]
        [SerializeField] private bool m_UseNetworkIdentityOwner = true;

        [Tooltip("When using NetworkIdentity ownership, wait for PurrNet to spawn the identity and replicate its owner before initializing.")]
        [SerializeField] private bool m_WaitForNetworkIdentityOwner = true;

        [Tooltip("Maximum time to wait for the host client half or NetworkIdentity ownership before falling back.")]
        [Min(0.5f)]
        [SerializeField] private float m_StartupWaitTimeout = 8f;

        private NetworkCharacter m_Character;
        private NetworkIdentity m_Identity;
        private bool m_Hooked;
        private bool m_Initialized;
        private Coroutine m_PendingInit;
        private PlayerID? m_SpawnedOwnerHint;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private void Awake()
        {
            m_Character = GetComponent<NetworkCharacter>();
            m_Identity = GetComponentInParent<NetworkIdentity>();
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
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

        private void OnDisable()
        {
            if (m_PendingInit != null)
            {
                StopCoroutine(m_PendingInit);
                m_PendingInit = null;
            }

            var nm = ActiveManager;
            if (nm == null || !m_Hooked) return;
            nm.onNetworkStarted -= OnNetworkStarted;
            nm.onNetworkShutdown -= OnNetworkShutdown;
            m_Hooked = false;
        }

        private void TryHook()
        {
            var nm = ActiveManager;
            if (nm == null || m_Hooked) return;
            nm.onNetworkStarted += OnNetworkStarted;
            nm.onNetworkShutdown += OnNetworkShutdown;
            m_Hooked = true;
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
            ScheduleDeferredInit();
        }

        private void OnNetworkShutdown(NetworkManager manager, bool asServer)
        {
            // Allow re-init on next session.
            m_Initialized = false;
            if (m_PendingInit != null)
            {
                StopCoroutine(m_PendingInit);
                m_PendingInit = null;
            }

            m_Character?.ResetNetworkRole();
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

            if (m_UseNetworkIdentityOwner && HasNetworkIdentity())
            {
                float deadline = Time.unscaledTime + Mathf.Max(0.5f, m_StartupWaitTimeout);
                while (Time.unscaledTime < deadline && ShouldWaitForNetworkIdentityOwner())
                {
                    yield return null;
                }
            }

            m_PendingInit = null;
            if (!TryInitializeNow() && ShouldRetryInitialization())
            {
                ScheduleDeferredInit();
            }
        }

        private bool TryInitializeNow()
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

            bool isOwner;
            if (TryResolveNetworkIdentityOwner(nm, out bool identityOwner, out bool identityReady))
            {
                if (!identityReady && m_WaitForNetworkIdentityOwner)
                {
                    return false;
                }

                isOwner = identityOwner;
            }
            else switch (m_OwnerMode)
            {
                case OwnerMode.Everyone:
                    isOwner = true;
                    break;
                case OwnerMode.HostOnly:
                default:
                    // Single-character demo: only the hosting peer owns and authoritatively
                    // simulates this character. Joining clients see it as a remote character
                    // and receive state from the host.
                    isOwner = isHost || (isServer && !clientActive);
                    break;
            }

            m_Character.InitializeNetworkRole(isServer, isOwner, isHost);
            m_Initialized = true;
            return true;
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
            // coroutine has not yet flipped clientState/pendingHost. Wait the budget
            // here instead of resolving a host-owned spawned identity as server-only.
            return true;
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
                    isReady = !m_WaitForNetworkIdentityOwner;
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
                    isReady = !m_WaitForNetworkIdentityOwner;
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
                    isReady = !m_WaitForNetworkIdentityOwner;
                    return true;
                }

                // A dedicated/server-side instance is authoritative, not a local GC2 player.
                isReady = true;
                isOwner = false;
                return true;
            }

            if (!m_Identity.owner.HasValue)
            {
                isReady = !m_WaitForNetworkIdentityOwner;
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
            if (nm == null) return false;
            if (nm.isServer || nm.isClient) return true;
            return nm.serverState != ConnectionState.Disconnected ||
                   nm.clientState != ConnectionState.Disconnected;
        }
    }
}
