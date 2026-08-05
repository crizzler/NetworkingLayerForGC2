using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;
using Arawn.GameCreator2.Networking.Security;
using Arawn.NetworkingCore.LagCompensation;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Transport Bridge")]
    [DefaultExecutionOrder(-400)]
    [DisallowMultipleComponent]
    public sealed class FusionTransportBridge : NetworkTransportBridge, INetworkRunnerCallbacks
    {
        private sealed class OrderedReceiveState
        {
            public uint Expected = 1;
            public readonly SortedDictionary<uint, FusionPacketEnvelope> Pending =
                new SortedDictionary<uint, FusionPacketEnvelope>();
            public float GapStartedAt = -1f;
        }

        private struct RateWindow
        {
            public float StartedAt;
            public int Count;
        }

        private struct RejectedInputLogState
        {
            public float LastLoggedAt;
            public int Suppressed;
        }

        private const uint NoSequenceTarget = uint.MaxValue;
        private const float GameplayReadyRetrySeconds = 1f;
        private const float RejectedInputLogIntervalSeconds = 5f;

        private static readonly Dictionary<NetworkRunner, FusionTransportBridge> s_Bridges =
            new Dictionary<NetworkRunner, FusionTransportBridge>();

        [Header("Fusion Runner")]
        [SerializeField] private NetworkRunner m_Runner;
        [SerializeField] private FusionSessionBootstrap m_SessionBootstrap;
        [SerializeField] private FusionRpcRouter m_RpcRouter;
        [SerializeField] private bool m_AutoBindSingleRunner = true;

        [Header("Transport Safety")]
        [Min(32)]
        [SerializeField] private int m_MaxInboundPacketsPerSecond = 512;
        [SerializeField] private bool m_LogRejectedPackets = true;

        [Header("Diagnostics")]
        [Tooltip("Logs low-volume authority, readiness, and snapshot lifecycle transitions.")]
        [SerializeField] private bool m_LogLifecycleDiagnostics = true;

        private readonly Dictionary<ushort, Action<FusionModuleMessage>> m_ModuleHandlers =
            new Dictionary<ushort, Action<FusionModuleMessage>>();
        private readonly Dictionary<ushort, IFusionFullSnapshotProducer> m_SnapshotProducers =
            new Dictionary<ushort, IFusionFullSnapshotProducer>();
        private readonly HashSet<ushort> m_RegularModuleHandlers = new HashSet<ushort>();
        private readonly HashSet<uint> m_PreGameplayMessageHandlers = new HashSet<uint>();
        private readonly HashSet<uint> m_ConnectedClientIds = new HashSet<uint>();
        private readonly HashSet<uint> m_SceneReadyClients = new HashSet<uint>();
        private readonly HashSet<uint> m_GameplayReadyClients = new HashSet<uint>();
        private readonly HashSet<uint> m_SnapshotInProgressClients = new HashSet<uint>();
        private readonly HashSet<uint> m_PendingGameplayReadyClients = new HashSet<uint>();
        private readonly Dictionary<uint, uint> m_PendingSnapshotTokens =
            new Dictionary<uint, uint>();
        private readonly Dictionary<uint, float> m_LastSnapshotStartedAt =
            new Dictionary<uint, float>();
        private readonly Dictionary<ulong, uint> m_OutgoingSequences = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, OrderedReceiveState> m_IncomingSequences =
            new Dictionary<ulong, OrderedReceiveState>();
        private readonly HashSet<ulong> m_SequenceBaselineResets = new HashSet<ulong>();
        private readonly Dictionary<uint, RateWindow> m_RateWindows = new Dictionary<uint, RateWindow>();
        private readonly Dictionary<uint, RejectedInputLogState> m_RejectedInputLogs =
            new Dictionary<uint, RejectedInputLogState>();
        private readonly List<ulong> m_ExpiredSequenceKeys = new List<ulong>();
        private readonly List<uint> m_ClientScratch = new List<uint>();
        private readonly List<IFusionFullSnapshotProducer> m_SnapshotProducerScratch =
            new List<IFusionFullSnapshotProducer>();

        private PlayerRef m_LastMaster = PlayerRef.Invalid;
        private uint m_AuthorityEpoch = 1;
        private uint m_NextSnapshotToken;
        private bool m_WasAuthority;
        private bool m_LocalSceneReady;
        private bool m_LocalGameplayReadyIntent;
        private bool m_MultipleRunnerWarningIssued;
        private bool m_AuthorityFailureShutdownInProgress;
        private bool m_AuthorityTransitionInProgress;
        private bool m_RpcSendFailureLatched;
        private string m_LastRpcSendFailure = string.Empty;
        private FusionFullSnapshotContext m_ActiveSnapshotContext;
        private LagCompensationBootstrap m_LagCompensationBootstrap;
        private bool m_HasLastRunnerShutdown;
        private FusionRunnerShutdownInfo m_LastRunnerShutdown;
        private bool m_HasLastAuthorityObservation;
        private FusionAuthorityObservation m_LastAuthorityObservation;
        private bool m_HasLastLocalSceneObservation;
        private FusionSceneLifecycleInfo m_LastLocalSceneObservation;
        private uint m_LocalSnapshotCompletedEpoch;
        private float m_NextGameplayReadyRetryAt;
        private uint m_GameplayReadySendEpoch;
        private int m_GameplayReadySendCount;

        public event Action<uint> ClientSceneReady;
        public event Action<uint> ClientSnapshotAcknowledged;
        public event Action<bool, uint> AuthorityChanged;
        /// <summary>
        /// Raised locally after Fusion reports that a newly loaded scene is ready.
        /// Persistent player objects use it to rebuild scene-local GC2 registrations.
        /// </summary>
        public event Action LocalSceneReady;
        public event Action<NetworkRunner, ShutdownReason> RunnerShutdown;

        // Observational callbacks are isolated from the critical authority/readiness events
        // above. They are safe for presentation code and Game Creator visual scripting.
        public event Action<FusionRunnerBindingInfo> RunnerObservedBound;
        public event Action<FusionRunnerBindingInfo> RunnerObservedUnbound;
        public event Action<FusionRunnerShutdownInfo> RunnerObservedShutdown;
        public event Action<FusionPlayerConnectionInfo> PlayerObservedJoined;
        public event Action<FusionPlayerConnectionInfo> PlayerObservedLeft;
        public event Action<FusionAuthorityObservation> AuthorityObservedChanged;
        public event Action<FusionSceneLifecycleInfo> LocalSceneObservedStarted;
        public event Action<FusionSceneLifecycleInfo> LocalSceneObservedCompleted;

        public NetworkRunner Runner => m_Runner;
        public uint AuthorityEpoch => m_AuthorityEpoch;
        public override IReadOnlyCollection<uint> ConnectedClientIds => m_ConnectedClientIds;
        public bool HasLastRunnerShutdown => m_HasLastRunnerShutdown;
        public FusionRunnerShutdownInfo LastRunnerShutdown => m_LastRunnerShutdown;
        public bool HasLastAuthorityObservation => m_HasLastAuthorityObservation;
        public FusionAuthorityObservation LastAuthorityObservation =>
            m_LastAuthorityObservation;
        public bool HasLastLocalSceneObservation => m_HasLastLocalSceneObservation;
        public FusionSceneLifecycleInfo LastLocalSceneObservation =>
            m_LastLocalSceneObservation;
        public bool IsLocalSceneReady => m_LocalSceneReady;
        public ConnectionType CurrentConnectionType =>
            IsRunnerUsable ? m_Runner.CurrentConnectionType : ConnectionType.None;
        public global::Fusion.Sockets.Stun.NATType CurrentNATType =>
            IsRunnerUsable
                ? m_Runner.NATType
                : global::Fusion.Sockets.Stun.NATType.Invalid;
        public string CurrentSessionRegion =>
            IsRunnerUsable && m_Runner.SessionInfo.IsValid
                ? m_Runner.SessionInfo.Region ?? string.Empty
                : string.Empty;
        public string AuthenticatedUserId =>
            IsRunnerUsable ? m_Runner.UserId ?? string.Empty : string.Empty;
        internal bool AuthorityTransitionInProgress => m_AuthorityTransitionInProgress;

        public override bool IsServer =>
            IsRunnerUsable &&
            (m_Runner.IsServer ||
             (m_Runner.GameMode == GameMode.Shared && m_Runner.IsSharedModeMasterClient));

        public override bool IsClient => IsRunnerUsable && m_Runner.IsPlayer;
        public override bool IsHost => IsRunnerUsable && m_Runner.GameMode == GameMode.Host;
        public override bool IsRunning => IsRunnerUsable;
        public override bool IsStarting => ResolveSessionBootstrap()?.IsStarting ?? false;
        public override string LastSessionError
        {
            get
            {
                if (!string.IsNullOrEmpty(m_LastRpcSendFailure))
                {
                    return m_LastRpcSendFailure;
                }

                return ResolveSessionBootstrap() is { HasLastStartFailure: true } bootstrap
                    ? bootstrap.LastStartFailure.ErrorMessage
                    : string.Empty;
            }
        }
        public override string LastSessionStopReason
        {
            get
            {
                FusionSessionBootstrap bootstrap = ResolveSessionBootstrap();
                if (bootstrap != null && bootstrap.HasLastStop)
                {
                    return bootstrap.LastStop.ShutdownReason.ToString();
                }
                return m_HasLastRunnerShutdown
                    ? m_LastRunnerShutdown.Reason.ToString()
                    : string.Empty;
            }
        }
        public override float ServerTime =>
            IsRunnerTimeReady ? m_Runner.SimulationTime : Time.time;

        private bool IsRunnerUsable => m_Runner != null && m_Runner.IsRunning && !m_Runner.IsShutdown;

        // A Shared runner reports IsRunning before its first server state installs
        // RuntimeConfig. Tick is public and remains default until that state arrives;
        // reading SimulationTime any earlier throws inside Fusion.
        private bool IsRunnerTimeReady =>
            IsRunnerUsable &&
            m_Runner.Tick.Raw > 0;

        public bool TryGetConnectionDiagnostics(out FusionConnectionDiagnostics diagnostics)
        {
            diagnostics = default;
            if (!IsRunnerUsable) return false;

            diagnostics = new FusionConnectionDiagnostics(
                m_Runner,
                m_Runner.CurrentConnectionType,
                m_Runner.NATType,
                m_Runner.SessionInfo.IsValid
                    ? m_Runner.SessionInfo.Region
                    : string.Empty,
                m_Runner.UserId);
            return true;
        }

        protected override void Awake()
        {
            base.Awake();
            EnsureLagCompensationBootstrap();
            if (m_Runner != null)
            {
                Bind(m_Runner);
            }
        }

        private void OnEnable()
        {
            EnsureLagCompensationBootstrap();
            if (m_Runner != null)
            {
                Bind(m_Runner);
            }
        }

        private void Update()
        {
            if (m_Runner == null && m_AutoBindSingleRunner)
            {
                TryAutoBind();
            }

            if (!IsRunnerUsable) return;

            PollAuthority();
            ProcessSequenceTimeouts();

            if (!m_RpcSendFailureLatched &&
                m_LocalSceneReady &&
                TryGetLocalClientId(out uint localClientId))
            {
                if (!m_SceneReadyClients.Contains(localClientId))
                {
                    SendSceneReady();
                }

                // GameplayReady is an intent, not a one-shot edge. Re-announce it until the
                // authority's snapshot-complete marker proves that this epoch reached the
                // client. This also recovers if readiness was first sent during an epoch race.
                if (m_LocalGameplayReadyIntent &&
                    m_LocalSnapshotCompletedEpoch != m_AuthorityEpoch &&
                    Time.unscaledTime >= m_NextGameplayReadyRetryAt)
                {
                    SendGameplayReadyIntent();
                }
            }
        }

        protected override void OnDestroy()
        {
            Unbind();
            base.OnDestroy();
        }

        public bool Bind(NetworkRunner runner)
        {
            if (runner == null) return false;
            if (FusionLobbyDiscoveryRunnerMarker.IsDiscoveryRunner(runner))
            {
                // A Photon lobby listener is connected to matchmaking only. Treating it as
                // gameplay would make every server-authoritative GC2 path target the wrong peer.
                return false;
            }
            if (ReferenceEquals(m_Runner, runner) && s_Bridges.TryGetValue(runner, out var current) && current == this)
            {
                return true;
            }

            if (s_Bridges.TryGetValue(runner, out var existing) && existing != null && existing != this)
            {
                Debug.LogError(
                    $"[FusionTransport] Runner '{runner.name}' is already bound to bridge '{existing.name}'.",
                    this);
                return false;
            }

            Unbind();
            ResetRpcSendFailure();
            // A new runner is a new session. Epochs are session-local; carrying a larger
            // value from an earlier runner could make this peer reject the new authority.
            m_AuthorityEpoch = 1;
            m_NextSnapshotToken = 0;
            m_Runner = runner;
            s_Bridges[runner] = this;
            runner.RemoveCallbacks(this);
            runner.AddCallbacks(this);
            EnsureLagCompensationBootstrap();

            if (runner.GetComponent<FusionRpcRouter>() == null)
            {
                runner.gameObject.AddComponent<FusionRpcRouter>();
            }

            RebuildConnectedClients();
            m_LastMaster = GetCurrentMaster();
            m_WasAuthority = IsServer;
            SetLagCompensationAuthority(m_WasAuthority);
            bool authorityReady = InvokeAuthorityChanged(m_WasAuthority, m_AuthorityEpoch, true);
            if (authorityReady)
            {
                PublishRunnerBinding(runner, true);
                if (!ReferenceEquals(m_Runner, runner)) return false;
                PublishAuthorityObservation(runner, m_WasAuthority, m_AuthorityEpoch);
            }
            return authorityReady;
        }

        public void Unbind()
        {
            NetworkRunner runner = m_Runner;
            if (runner != null)
            {
                runner.RemoveCallbacks(this);
                if (s_Bridges.TryGetValue(runner, out var bridge) && bridge == this)
                {
                    s_Bridges.Remove(runner);
                }
            }

            bool wasAuthority = m_WasAuthority;
            bool ownedLagCompensationAuthority =
                wasAuthority ||
                (m_LagCompensationBootstrap != null && m_LagCompensationBootstrap.IsServer);
            if (ownedLagCompensationAuthority)
            {
                SetLagCompensationAuthority(false);
            }
            m_Runner = null;
            m_LastMaster = PlayerRef.Invalid;
            m_WasAuthority = false;
            m_LocalSceneReady = false;
            m_LocalGameplayReadyIntent = false;
            ClearDeliveryState(true);

            if (wasAuthority)
            {
                InvokeAuthorityChanged(false, m_AuthorityEpoch, false);
                PublishAuthorityObservation(runner, false, m_AuthorityEpoch);
            }

            if (runner != null) PublishRunnerBinding(runner, false);
        }

        private void EnsureLagCompensationBootstrap()
        {
            if (m_LagCompensationBootstrap == null)
            {
#if UNITY_2023_1_OR_NEWER
                m_LagCompensationBootstrap = FindFirstObjectByType<LagCompensationBootstrap>(
                    FindObjectsInactive.Include);
#else
                m_LagCompensationBootstrap = FindObjectOfType<LagCompensationBootstrap>(true);
#endif
            }

            if (m_LagCompensationBootstrap == null)
            {
                m_LagCompensationBootstrap = gameObject.AddComponent<LagCompensationBootstrap>();
            }

            m_LagCompensationBootstrap.GetServerTimeFunc = GetLagCompensationServerTime;
        }

        private void SetLagCompensationAuthority(bool isAuthority)
        {
            if (!isAuthority)
            {
                // Teardown must never create a new component while its GameObject is being
                // destroyed. If this bridge never owned a bootstrap, there is nothing to stop.
                if (m_LagCompensationBootstrap == null) return;
                m_LagCompensationBootstrap.SetServerMode(false);
                LogLifecycle("lag compensation authority stopped");
                return;
            }

            EnsureLagCompensationBootstrap();
            if (m_LagCompensationBootstrap == null) return;
            m_LagCompensationBootstrap.SetServerMode(true);

            // A restarted session or Shared-mode promotion replaces the global history
            // manager. Re-register authoritative characters that already spawned before
            // this bridge observed the new authority role.
#if UNITY_2023_1_OR_NEWER
            CharacterLagCompensation[] adapters = FindObjectsByType<CharacterLagCompensation>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
#else
            CharacterLagCompensation[] adapters = FindObjectsOfType<CharacterLagCompensation>();
#endif
            int registered = 0;
            for (int i = 0; i < adapters.Length; i++)
            {
                CharacterLagCompensation adapter = adapters[i];
                NetworkCharacter networkCharacter =
                    adapter != null ? adapter.GetComponent<NetworkCharacter>() : null;
                if (networkCharacter == null ||
                    !networkCharacter.IsServerInstance ||
                    networkCharacter.NetworkId == 0)
                {
                    continue;
                }

                adapter.Configure(networkCharacter.NetworkId, true);
                if (adapter.IsRegistered) registered++;
            }

            LogLifecycle(
                $"lag compensation authority ready; trackedAdapters={registered}/{adapters.Length}");
        }

        private double GetLagCompensationServerTime()
        {
            return ServerTime;
        }

        public bool RegisterModuleHandler(ushort moduleId, Action<FusionModuleMessage> handler)
        {
            if (!TryRegisterHandler(moduleId, handler)) return false;
            m_RegularModuleHandlers.Add(moduleId);
            return true;
        }

        /// <summary>
        /// Registers one exact client-to-authority message that may be dispatched before
        /// SceneReady/GameplayReady. This is intentionally message-scoped: registering a
        /// normal module handler never bypasses readiness, and other messages in the same
        /// module remain blocked.
        /// </summary>
        public bool RegisterPreGameplayModuleHandler(
            ushort moduleId,
            ushort messageType,
            Action<FusionModuleMessage> handler)
        {
            if (moduleId == FusionProtocol.TransportModuleId || handler == null) return false;
            if (!TryRegisterHandler(moduleId, handler)) return false;
            m_PreGameplayMessageHandlers.Add(ModuleMessageKey(moduleId, messageType));
            return true;
        }

        public void UnregisterModuleHandler(ushort moduleId, Action<FusionModuleMessage> handler)
        {
            if (!m_ModuleHandlers.TryGetValue(moduleId, out var existing)) return;
            if (handler != null && existing != handler) return;
            m_RegularModuleHandlers.Remove(moduleId);
            if (!HasPreGameplayRegistration(moduleId))
            {
                m_ModuleHandlers.Remove(moduleId);
            }
        }

        public void UnregisterPreGameplayModuleHandler(
            ushort moduleId,
            ushort messageType,
            Action<FusionModuleMessage> handler)
        {
            if (!m_ModuleHandlers.TryGetValue(moduleId, out var existing)) return;
            if (handler != null && existing != handler) return;

            m_PreGameplayMessageHandlers.Remove(ModuleMessageKey(moduleId, messageType));
            if (!HasPreGameplayRegistration(moduleId) &&
                !m_RegularModuleHandlers.Contains(moduleId))
            {
                m_ModuleHandlers.Remove(moduleId);
            }
        }

        /// <summary>
        /// Registers the explicit full-state producer for a module handler. A producer remains
        /// registered only while its bridge is enabled and bound to this transport.
        /// </summary>
        public bool RegisterFullSnapshotProducer(IFusionFullSnapshotProducer producer)
        {
            if (producer == null ||
                producer.FullSnapshotModuleId == FusionProtocol.TransportModuleId ||
                !m_ModuleHandlers.ContainsKey(producer.FullSnapshotModuleId))
            {
                return false;
            }

            ushort moduleId = producer.FullSnapshotModuleId;
            if (m_SnapshotProducers.TryGetValue(moduleId, out var existing))
            {
                if (ReferenceEquals(existing, producer)) return true;
                Debug.LogError(
                    $"[FusionTransport] Module ID {moduleId} already has full snapshot producer " +
                    $"'{GetSnapshotProducerName(existing)}'.",
                    this);
                return false;
            }

            m_SnapshotProducers.Add(moduleId, producer);
            return true;
        }

        public void UnregisterFullSnapshotProducer(IFusionFullSnapshotProducer producer)
        {
            if (producer == null) return;
            ushort moduleId = producer.FullSnapshotModuleId;
            if (m_SnapshotProducers.TryGetValue(moduleId, out var existing) &&
                ReferenceEquals(existing, producer))
            {
                m_SnapshotProducers.Remove(moduleId);
            }
        }

        public bool SendModuleToAuthority(
            ushort moduleId,
            ushort messageType,
            byte[] payload,
            bool reliable = true)
        {
            if (moduleId == FusionProtocol.TransportModuleId) return false;
            return SendToAuthorityInternal(moduleId, messageType, payload, reliable);
        }

        public bool SendModuleToClient(
            uint clientId,
            ushort moduleId,
            ushort messageType,
            byte[] payload,
            bool reliable = true)
        {
            bool delivered =
                moduleId != FusionProtocol.TransportModuleId &&
                (m_GameplayReadyClients.Contains(clientId) ||
                 m_SnapshotInProgressClients.Contains(clientId)) &&
                SendToClientInternal(clientId, moduleId, messageType, payload, reliable);
            m_ActiveSnapshotContext?.RecordDelivery(moduleId, clientId, delivered);
            return delivered;
        }

        public int BroadcastModule(
            ushort moduleId,
            ushort messageType,
            byte[] payload,
            bool reliable = true,
            uint? excludeClientId = null)
        {
            if (!IsServer || moduleId == FusionProtocol.TransportModuleId) return 0;

            int sent = 0;
            m_ClientScratch.Clear();
            m_ClientScratch.AddRange(m_ConnectedClientIds);
            for (int i = 0; i < m_ClientScratch.Count; i++)
            {
                uint clientId = m_ClientScratch[i];
                if (excludeClientId.HasValue && excludeClientId.Value == clientId) continue;
                if (!m_GameplayReadyClients.Contains(clientId)) continue;
                if (SendToClientInternal(clientId, moduleId, messageType, payload, reliable)) sent++;
            }

            return sent;
        }

        public override bool TryGetLocalClientId(out uint clientId)
        {
            clientId = InvalidClientId;
            return IsRunnerUsable && TryPlayerToClientId(m_Runner.LocalPlayer, out clientId);
        }

        public override bool TryGetLocalPlayer(out GameObject player)
        {
            player = null;
            if (IsRunnerUsable &&
                m_Runner.TryGetPlayerObject(m_Runner.LocalPlayer, out NetworkObject playerObject) &&
                playerObject != null && playerObject.IsValid)
            {
                player = playerObject.gameObject;
                return true;
            }

            return base.TryGetLocalPlayer(out player);
        }

        public bool TryGetActiveSession(out FusionSessionSnapshot session)
        {
            FusionSessionBootstrap bootstrap = ResolveSessionBootstrap();
            if (bootstrap != null &&
                ReferenceEquals(bootstrap.Runner, m_Runner) &&
                bootstrap.TryGetActiveSession(out session))
            {
                return true;
            }

            bool hasLaunchMode = TryInferLaunchMode(m_Runner, out FusionDefaultLaunchMode launchMode);
            bool isExternalRunner =
                bootstrap == null ||
                !ReferenceEquals(bootstrap.Runner, m_Runner) ||
                bootstrap.IsExternalRunner;
            return FusionSessionSnapshot.TryCapture(
                m_Runner,
                launchMode,
                hasLaunchMode,
                isExternalRunner,
                out session);
        }

        /// <summary>Returns round-trip time in seconds for a currently active player.</summary>
        public bool TryGetPlayerRtt(uint clientId, out double seconds)
        {
            seconds = 0d;
            if (!TryGetPlayerRef(clientId, out PlayerRef player)) return false;
            seconds = m_Runner.GetPlayerRtt(player);
            return !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0d;
        }

        public bool IsClientReady(uint clientId) => m_GameplayReadyClients.Contains(clientId);
        public bool IsClientSceneReady(uint clientId) => m_SceneReadyClients.Contains(clientId);

        /// <summary>
        /// Fail-closed termination used when a promoted authority cannot produce a full,
        /// internally consistent GC2 snapshot. Continuing would risk two logical authorities.
        /// </summary>
        public async void ShutdownSessionForAuthorityFailure(string reason)
        {
            if (m_AuthorityFailureShutdownInProgress) return;

            string detail = string.IsNullOrWhiteSpace(reason)
                ? "The promoted Fusion authority could not restore authoritative GC2 state."
                : reason.Trim();
            Debug.LogError($"[FusionTransport] Authority failure; shutting down session: {detail}", this);

            NetworkRunner runner = m_Runner;
            if (runner == null) return;

            m_AuthorityFailureShutdownInProgress = true;
            try
            {
                FusionSessionBootstrap bootstrap = m_SessionBootstrap;
                if (bootstrap == null)
                {
                    bootstrap = GetComponentInParent<FusionSessionBootstrap>() ??
                                FindFirstObjectByType<FusionSessionBootstrap>();
                }

                if (bootstrap != null && bootstrap.Runner == runner)
                {
                    await bootstrap.ShutdownAsync();
                }
                else if (runner.IsRunning && !runner.IsShutdown)
                {
                    await runner.Shutdown();
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                m_AuthorityFailureShutdownInProgress = false;
            }
        }

        public void NotifyLocalSceneReady()
        {
            m_LocalSceneReady = true;
            LogLifecycle($"local scene ready; epoch={m_AuthorityEpoch}");
            InvokeLocalSceneReady();
            SendSceneReady();
            SendGameplayReadyIntent();
            PublishSceneObservation(FusionSceneLifecyclePhase.LoadCompleted);
        }

        public void NotifyLocalGameplayReady()
        {
            if (!m_LocalGameplayReadyIntent)
            {
                LogLifecycle($"local gameplay readiness armed; epoch={m_AuthorityEpoch}");
            }
            m_LocalGameplayReadyIntent = true;
            m_NextGameplayReadyRetryAt = 0f;
            SendGameplayReadyIntent();
        }

        public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
        {
            if (!IsClient || characterNetworkId == 0 || inputs == null || inputs.Length == 0) return;

            var writer = new FusionPacketWriter(8 + inputs.Length * 24);
            writer.WriteUInt32(characterNetworkId);
            writer.WriteUInt16(checked((ushort)inputs.Length));
            for (int i = 0; i < inputs.Length; i++)
            {
                WriteInput(writer, inputs[i]);
            }

            SendToAuthorityInternal(
                FusionProtocol.TransportModuleId,
                (ushort)FusionTransportMessageType.CharacterInput,
                writer.ToArray(),
                false);
        }

        public override void SendToOwner(
            uint ownerClientId,
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime)
        {
            if (!IsServer || characterNetworkId == 0 ||
                !m_GameplayReadyClients.Contains(ownerClientId))
            {
                return;
            }

            byte[] payload = EncodeState(characterNetworkId, state, serverTime);
            SendToClientInternal(
                ownerClientId,
                FusionProtocol.TransportModuleId,
                (ushort)FusionTransportMessageType.CharacterState,
                payload,
                false);
        }

        public override void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null)
        {
            if (!IsServer || characterNetworkId == 0) return;

            byte[] payload = EncodeState(characterNetworkId, state, serverTime);
            m_ClientScratch.Clear();
            m_ClientScratch.AddRange(m_ConnectedClientIds);

            for (int i = 0; i < m_ClientScratch.Count; i++)
            {
                uint clientId = m_ClientScratch[i];
                if (clientId == excludeClientId) continue;
                if (!m_GameplayReadyClients.Contains(clientId)) continue;
                if (!ShouldSendToClient(clientId, characterNetworkId, state, serverTime, relevanceFilter)) continue;

                SendToClientInternal(
                    clientId,
                    FusionProtocol.TransportModuleId,
                    (ushort)FusionTransportMessageType.CharacterState,
                    payload,
                    false);
            }
        }

        public override bool TryVerifyActorOwnership(
            uint senderClientId,
            uint actorNetworkId,
            out uint ownerClientId)
        {
            ownerClientId = InvalidClientId;
            if (TryResolveNetworkCharacter(actorNetworkId, out NetworkCharacter character))
            {
                FusionNetworkIdentity identity = character.GetComponentInParent<FusionNetworkIdentity>();
                if (identity != null &&
                    identity.TransportAdmitted &&
                    FusionAuthoritySpawnRegistry.TryGet(m_Runner, out var registry) &&
                    registry.IsAdmitted(identity) &&
                    identity.TryGetLogicalOwnerClientId(out ownerClientId))
                {
                    return ownerClientId == senderClientId;
                }
            }

            // Fusion gameplay ownership is valid only while the replicated identity remains
            // authority-admitted. Never fall back to a stale transport-neutral ownership
            // cache after an identity has been quarantined or despawned.
            ownerClientId = InvalidClientId;
            return false;
        }

        protected override bool TryResolveOwnerClientId(NetworkCharacter networkCharacter, out uint ownerClientId)
        {
            ownerClientId = InvalidClientId;
            if (networkCharacter == null) return false;

            FusionNetworkIdentity identity = networkCharacter.GetComponentInParent<FusionNetworkIdentity>();
            return identity != null &&
                   identity.TransportAdmitted &&
                   FusionAuthoritySpawnRegistry.TryGet(m_Runner, out var registry) &&
                   registry.IsAdmitted(identity) &&
                   identity.TryGetLogicalOwnerClientId(out ownerClientId);
        }

        protected override bool TryResolveServerIssuedNetworkId(
            NetworkCharacter networkCharacter,
            out uint networkId)
        {
            networkId = 0;
            if (networkCharacter == null) return false;

            FusionNetworkIdentity identity = networkCharacter.GetComponentInParent<FusionNetworkIdentity>();
            if (identity == null ||
                !identity.TransportAdmitted ||
                identity.NetworkId == 0 ||
                !FusionAuthoritySpawnRegistry.TryGet(m_Runner, out var registry) ||
                !registry.IsAdmitted(identity))
            {
                return false;
            }
            networkId = identity.NetworkId;
            return true;
        }

        internal static void RouteRpc(
            NetworkRunner runner,
            byte[] packet,
            RpcInfo info,
            FusionPacketDirection expectedDirection,
            bool reliable,
            bool largeData)
        {
            if (runner == null || !s_Bridges.TryGetValue(runner, out var bridge) || bridge == null)
            {
                Debug.LogWarning("[FusionTransport] Dropped RPC because its runner has no bound transport bridge.");
                return;
            }

            bridge.ReceiveRpc(packet, info.Source, expectedDirection, reliable, largeData);
        }

        internal static bool TryGetBoundBridge(NetworkRunner runner, out FusionTransportBridge bridge)
        {
            bridge = null;
            return runner != null &&
                   s_Bridges.TryGetValue(runner, out bridge) &&
                   bridge != null;
        }

        internal static bool TryPlayerToClientId(PlayerRef player, out uint clientId)
        {
            clientId = InvalidClientId;
            if (!player.IsRealPlayer || player.RawEncoded <= 0) return false;
            clientId = unchecked((uint)player.RawEncoded);
            return IsValidClientId(clientId);
        }

        internal bool TryGetPlayerRef(uint clientId, out PlayerRef player)
        {
            player = PlayerRef.Invalid;
            if (!IsRunnerUsable || clientId == InvalidClientId || clientId > int.MaxValue) return false;

            PlayerRef candidate = PlayerRef.FromRaw((int)clientId);
            if (!candidate.IsRealPlayer || !m_Runner.IsPlayerValid(candidate)) return false;
            player = candidate;
            return true;
        }

        private void ReceiveRpc(
            byte[] packet,
            PlayerRef source,
            FusionPacketDirection expectedDirection,
            bool reliable,
            bool largeData)
        {
            if (!IsRunnerUsable || packet == null || packet.Length == 0 ||
                packet.Length > FusionProtocol.MaximumPacketLength)
            {
                Reject("empty, oversized, or inactive-runner RPC");
                return;
            }

            if (!largeData && packet.Length > FusionProtocol.RpcPayloadLimit)
            {
                Reject(
                    $"regular Fusion RPC exceeds the {FusionProtocol.RpcPayloadLimit}-byte encoded limit");
                return;
            }

            if (largeData && !reliable)
            {
                Reject("large-data RPC must use the reliable channel");
                return;
            }

            uint senderClientId = InvalidClientId;
            if (expectedDirection == FusionPacketDirection.ToAuthority)
            {
                if (!IsServer || !TryPlayerToClientId(source, out senderClientId) ||
                    !m_Runner.IsPlayerValid(source))
                {
                    Reject($"non-player or non-authority request source {source}");
                    return;
                }
            }
            else if (!IsValidAuthoritySource(source))
            {
                Reject($"response from non-authority source {source}");
                return;
            }

            if (senderClientId != InvalidClientId && !AcceptRate(senderClientId))
            {
                Reject($"client {senderClientId} exceeded {m_MaxInboundPacketsPerSecond} packets/sec");
                return;
            }

            if (!FusionPacketCodec.TryDecode(packet, out FusionPacketEnvelope envelope, out string error))
            {
                Reject(error);
                return;
            }

            if (envelope.Direction != expectedDirection)
            {
                Reject($"RPC/envelope direction mismatch ({expectedDirection}/{envelope.Direction})");
                return;
            }

            ProcessReceivedEnvelope(envelope, senderClientId, reliable);
        }

        private void ProcessReceivedEnvelope(
            FusionPacketEnvelope envelope,
            uint senderClientId,
            bool reliable)
        {
            bool authorityAnnouncement =
                envelope.ModuleId == FusionProtocol.TransportModuleId &&
                envelope.MessageType == (ushort)FusionTransportMessageType.AuthorityAnnouncement &&
                envelope.Direction == FusionPacketDirection.FromAuthority;

            if (envelope.AuthorityEpoch != m_AuthorityEpoch)
            {
                if (authorityAnnouncement && envelope.AuthorityEpoch > m_AuthorityEpoch)
                {
                    AdoptAuthorityEpoch(envelope.AuthorityEpoch);
                }
                else
                {
                    Reject(
                        $"stale/future authority epoch {envelope.AuthorityEpoch}; current {m_AuthorityEpoch}");
                    return;
                }
            }

            if (reliable)
            {
                if (envelope.Sequence == 0)
                {
                    Reject("reliable packet has sequence 0");
                    return;
                }

                ReceiveOrdered(envelope, senderClientId);
                return;
            }

            if (envelope.Sequence != 0)
            {
                Reject("unreliable packet unexpectedly contains an ordering sequence");
                return;
            }

            DispatchEnvelope(envelope, senderClientId);
        }

        private void ReceiveOrdered(FusionPacketEnvelope envelope, uint senderClientId)
        {
            ulong key = IncomingSequenceKey(senderClientId, envelope.Direction);
            if (!m_IncomingSequences.TryGetValue(key, out OrderedReceiveState state))
            {
                state = new OrderedReceiveState();
                if (m_SequenceBaselineResets.Remove(key))
                {
                    // A five-second gap or an out-of-window packet explicitly starts a
                    // resynchronization cycle. The first reliable packet after that request
                    // establishes the new stream baseline; otherwise a long-running sender
                    // could never recover because its sequence does not restart at one.
                    state.Expected = envelope.Sequence;
                }
                m_IncomingSequences.Add(key, state);
            }

            int distance = unchecked((int)(envelope.Sequence - state.Expected));
            if (distance < 0) return; // Duplicate/late packet.

            if (distance == 0)
            {
                DispatchEnvelope(envelope, senderClientId);
                state.Expected++;

                while (state.Pending.TryGetValue(state.Expected, out FusionPacketEnvelope pending))
                {
                    state.Pending.Remove(state.Expected);
                    DispatchEnvelope(pending, senderClientId);
                    state.Expected++;
                }

                state.GapStartedAt = state.Pending.Count == 0 ? -1f : Time.unscaledTime;
                return;
            }

            if (distance > FusionProtocol.ReorderWindow)
            {
                Reject(
                    $"reliable sequence {envelope.Sequence} exceeds reorder window from {state.Expected}");
                m_IncomingSequences.Remove(key);
                m_SequenceBaselineResets.Add(key);
                RequestResync(senderClientId, envelope.Direction);
                return;
            }

            if (!state.Pending.ContainsKey(envelope.Sequence))
            {
                state.Pending.Add(envelope.Sequence, envelope);
            }

            if (state.GapStartedAt < 0f) state.GapStartedAt = Time.unscaledTime;
        }

        private void DispatchEnvelope(FusionPacketEnvelope envelope, uint senderClientId)
        {
            if (envelope.ModuleId == FusionProtocol.TransportModuleId)
            {
                DispatchTransportMessage(envelope, senderClientId);
                return;
            }

            if (envelope.Direction == FusionPacketDirection.ToAuthority)
            {
                if (!IsValidClientId(senderClientId))
                {
                    Reject($"module {envelope.ModuleId} request has no validated player source");
                    return;
                }

                if (!m_GameplayReadyClients.Contains(senderClientId) &&
                    !m_PreGameplayMessageHandlers.Contains(
                        ModuleMessageKey(envelope.ModuleId, envelope.MessageType)))
                {
                    Reject($"module {envelope.ModuleId} request from unready client {senderClientId}");
                    return;
                }
            }

            if (!m_ModuleHandlers.TryGetValue(envelope.ModuleId, out var handler) || handler == null)
            {
                Reject($"unknown module ID {envelope.ModuleId}");
                return;
            }

            try
            {
                handler.Invoke(new FusionModuleMessage(
                    envelope.ModuleId,
                    envelope.MessageType,
                    senderClientId,
                    envelope.Payload,
                    envelope.Direction == FusionPacketDirection.FromAuthority,
                    envelope.AuthorityEpoch,
                    envelope.Sequence));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void DispatchTransportMessage(FusionPacketEnvelope envelope, uint senderClientId)
        {
            FusionTransportMessageType messageType = (FusionTransportMessageType)envelope.MessageType;
            if (!IsValidTransportMessageDirection(messageType, envelope.Direction))
            {
                Reject(
                    $"transport message {messageType} is invalid for direction " +
                    $"{envelope.Direction}");
                return;
            }

            switch (messageType)
            {
                case FusionTransportMessageType.CharacterInput:
                    if (envelope.Direction == FusionPacketDirection.ToAuthority)
                    {
                        ReceiveCharacterInput(senderClientId, envelope.Payload);
                    }
                    break;

                case FusionTransportMessageType.CharacterState:
                    if (envelope.Direction == FusionPacketDirection.FromAuthority)
                    {
                        ReceiveCharacterState(envelope.Payload);
                    }
                    break;

                case FusionTransportMessageType.SceneReady:
                    if (envelope.Direction == FusionPacketDirection.ToAuthority && IsServer)
                    {
                        MarkSceneReady(senderClientId);
                    }
                    break;

                case FusionTransportMessageType.GameplayReady:
                    if (envelope.Direction == FusionPacketDirection.ToAuthority && IsServer)
                    {
                        if (m_SceneReadyClients.Contains(senderClientId))
                        {
                            MarkGameplayReady(senderClientId, false);
                        }
                        else
                        {
                            if (m_PendingGameplayReadyClients.Add(senderClientId))
                            {
                                LogLifecycle(
                                    $"queued GameplayReady until SceneReady; " +
                                    $"client={senderClientId} epoch={m_AuthorityEpoch}");
                            }
                        }
                    }
                    break;

                case FusionTransportMessageType.AuthorityAnnouncement:
                    if (envelope.Direction == FusionPacketDirection.FromAuthority)
                    {
                        HandleAuthorityAnnouncement(envelope.Payload, envelope.AuthorityEpoch);
                    }
                    break;

                case FusionTransportMessageType.ResyncRequest:
                    if (envelope.Direction == FusionPacketDirection.ToAuthority && IsServer)
                    {
                        BeginClientSnapshot(senderClientId, true);
                    }
                    else if (envelope.Direction == FusionPacketDirection.FromAuthority)
                    {
                        NotifyLocalGameplayReady();
                    }
                    break;

                case FusionTransportMessageType.SnapshotComplete:
                    if (envelope.Direction == FusionPacketDirection.FromAuthority)
                    {
                        if (TryReadControlToken(envelope.Payload, out uint snapshotToken))
                        {
                            LogLifecycle(
                                $"received SnapshotComplete; token={snapshotToken} " +
                                $"epoch={m_AuthorityEpoch}");
                            var writer = new FusionPacketWriter(4);
                            writer.WriteUInt32(snapshotToken);
                            if (SendControlToAuthority(
                                    FusionTransportMessageType.SnapshotAcknowledged,
                                    writer.ToArray()))
                            {
                                m_LocalSnapshotCompletedEpoch = m_AuthorityEpoch;
                                LogLifecycle(
                                    $"sent SnapshotAcknowledged; token={snapshotToken} " +
                                    $"epoch={m_AuthorityEpoch}");
                            }
                        }
                    }
                    break;

                case FusionTransportMessageType.SnapshotAcknowledged:
                    if (envelope.Direction == FusionPacketDirection.ToAuthority && IsServer)
                    {
                        if (TryReadControlToken(envelope.Payload, out uint snapshotToken))
                        {
                            CompleteClientSnapshot(senderClientId, snapshotToken);
                        }
                    }
                    break;

                default:
                    Reject($"unknown transport message type {envelope.MessageType}");
                    break;
            }
        }

        private static bool IsValidTransportMessageDirection(
            FusionTransportMessageType messageType,
            FusionPacketDirection direction)
        {
            switch (messageType)
            {
                case FusionTransportMessageType.CharacterInput:
                case FusionTransportMessageType.SceneReady:
                case FusionTransportMessageType.GameplayReady:
                case FusionTransportMessageType.SnapshotAcknowledged:
                    return direction == FusionPacketDirection.ToAuthority;

                case FusionTransportMessageType.CharacterState:
                case FusionTransportMessageType.AuthorityAnnouncement:
                case FusionTransportMessageType.SnapshotComplete:
                    return direction == FusionPacketDirection.FromAuthority;

                case FusionTransportMessageType.ResyncRequest:
                    return direction == FusionPacketDirection.ToAuthority ||
                           direction == FusionPacketDirection.FromAuthority;

                default:
                    // The switch above reports unknown message IDs with its existing diagnostic.
                    return true;
            }
        }

        private bool SendToAuthorityInternal(
            ushort moduleId,
            ushort messageType,
            byte[] payload,
            bool reliable)
        {
            if (!IsRunnerUsable || !IsClient || !ValidatePayload(payload, reliable))
            {
                return false;
            }

            PlayerRef target =
                m_Runner.GameMode == GameMode.Shared
                    ? m_Runner.GetMasterClient()
                    : PlayerRef.None;

            if (!IsServer && m_RpcSendFailureLatched) return false;

            uint sequenceTarget = TryPlayerToClientId(target, out uint targetId) ? targetId : NoSequenceTarget;
            var envelope = CreateEnvelope(
                FusionPacketDirection.ToAuthority,
                moduleId,
                messageType,
                payload,
                reliable,
                sequenceTarget);
            byte[] packet = FusionPacketCodec.Encode(envelope);

            if (IsServer)
            {
                uint sender = TryGetLocalClientId(out uint localClientId)
                    ? localClientId
                    : InvalidClientId;
                ProcessReceivedEnvelope(envelope, sender, reliable);
                return true;
            }

            if (m_Runner.GameMode == GameMode.Shared && !target.IsRealPlayer) return false;
            return TrySendRpc(
                () => FusionRpcRouter.SendToAuthority(m_Runner, target, packet, reliable),
                "client-to-authority");
        }

        private bool SendToClientInternal(
            uint clientId,
            ushort moduleId,
            ushort messageType,
            byte[] payload,
            bool reliable)
        {
            if (!IsRunnerUsable ||
                !IsServer ||
                !ValidatePayload(payload, reliable) ||
                !TryGetPlayerRef(clientId, out PlayerRef target))
            {
                return false;
            }

            if (target != m_Runner.LocalPlayer && m_RpcSendFailureLatched) return false;

            var envelope = CreateEnvelope(
                FusionPacketDirection.FromAuthority,
                moduleId,
                messageType,
                payload,
                reliable,
                clientId);
            byte[] packet = FusionPacketCodec.Encode(envelope);

            if (target == m_Runner.LocalPlayer)
            {
                ProcessReceivedEnvelope(envelope, InvalidClientId, reliable);
                return true;
            }

            return TrySendRpc(
                () => FusionRpcRouter.SendFromAuthority(m_Runner, target, packet, reliable),
                "authority-to-client");
        }

        private bool TrySendRpc(Action send, string route)
        {
            if (m_RpcSendFailureLatched || send == null) return false;

            try
            {
                send();
                return true;
            }
            catch (MethodAccessException exception)
            {
                // A generated RPC failure is not transient. Latch it for this runner so
                // readiness retries cannot flood the log every frame while leaving the
                // original exception and remediation visible.
                m_RpcSendFailureLatched = true;
                m_LastRpcSendFailure =
                    $"Fusion RPC send failed on the {route} route. Remote Fusion RPC sends " +
                    "are disabled for this runner. Verify that the Arawn Fusion runtime assembly " +
                    "is in Assemblies To Weave and has Allow Unsafe Code enabled. Mono player " +
                    "builds also require Managed Stripping Level Disabled; other stripping " +
                    "levels remove Fusion's woven RPC verification metadata. Make a clean " +
                    "player rebuild after correcting the settings.";
                Debug.LogError(
                    $"[FusionTransport] {m_LastRpcSendFailure}\n{exception}",
                    this);
                return false;
            }
        }

        private void ResetRpcSendFailure()
        {
            m_RpcSendFailureLatched = false;
            m_LastRpcSendFailure = string.Empty;
        }

        private FusionPacketEnvelope CreateEnvelope(
            FusionPacketDirection direction,
            ushort moduleId,
            ushort messageType,
            byte[] payload,
            bool reliable,
            uint sequenceTarget)
        {
            uint sequence = reliable ? NextOutgoingSequence(direction, sequenceTarget) : 0;
            return new FusionPacketEnvelope(
                m_AuthorityEpoch,
                direction,
                moduleId,
                messageType,
                sequence,
                payload == null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(payload));
        }

        private bool ValidatePayload(byte[] payload, bool reliable)
        {
            int length = payload?.Length ?? 0;
            if (length > FusionProtocol.MaximumPayloadLength)
            {
                Debug.LogError(
                    $"[FusionTransport] Refusing {length}-byte payload; maximum is " +
                    $"{FusionProtocol.MaximumPayloadLength}.",
                    this);
                return false;
            }

            if (!reliable &&
                length + FusionProtocol.EnvelopeHeaderLength > FusionProtocol.RpcPayloadLimit)
            {
                Debug.LogError(
                    $"[FusionTransport] Refusing oversized unreliable payload ({length} bytes).",
                    this);
                return false;
            }

            return true;
        }

        private uint NextOutgoingSequence(FusionPacketDirection direction, uint target)
        {
            ulong key = OutgoingSequenceKey(target, direction);
            m_OutgoingSequences.TryGetValue(key, out uint sequence);
            sequence++;
            if (sequence == 0) sequence = 1;
            m_OutgoingSequences[key] = sequence;
            return sequence;
        }

        private void ReceiveCharacterInput(uint senderClientId, ReadOnlyMemory<byte> payload)
        {
            if (!m_GameplayReadyClients.Contains(senderClientId))
            {
                RejectUnreadyCharacterInput(senderClientId);
                return;
            }

            try
            {
                var reader = new FusionPacketReader(payload);
                uint characterId = reader.ReadUInt32();
                int count = reader.ReadUInt16();
                if (count <= 0 || count > 64) throw new FormatException($"Invalid input count {count}.");

                var inputs = new NetworkInputState[count];
                for (int i = 0; i < count; i++)
                {
                    inputs[i] = ReadInput(reader);
                }

                if (!reader.End) throw new FormatException("Character input packet contains trailing bytes.");
                if (!TryAcceptInputFromSender(senderClientId, characterId)) return;
                RaiseInputReceivedServer(senderClientId, characterId, inputs);
            }
            catch (FormatException exception)
            {
                Reject(exception.Message);
            }
        }

        private void ReceiveCharacterState(ReadOnlyMemory<byte> payload)
        {
            try
            {
                var reader = new FusionPacketReader(payload);
                uint characterId = reader.ReadUInt32();
                NetworkPositionState state = ReadState(reader);
                float serverTime = reader.ReadSingle();
                if (!reader.End) throw new FormatException("Character state packet contains trailing bytes.");
                RaiseStateReceivedClient(characterId, state, serverTime);
            }
            catch (FormatException exception)
            {
                Reject(exception.Message);
            }
        }

        private static byte[] EncodeState(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime)
        {
            var writer = new FusionPacketWriter(64);
            writer.WriteUInt32(characterNetworkId);
            WriteState(writer, state);
            writer.WriteSingle(serverTime);
            return writer.ToArray();
        }

        private static void WriteInput(FusionPacketWriter writer, NetworkInputState value)
        {
            writer.WriteInt16(value.inputX);
            writer.WriteInt16(value.inputY);
            writer.WriteUInt16(value.sequenceNumber);
            writer.WriteByte(value.flags);
            writer.WriteByte(value.deltaTimeMs);
            writer.WriteUInt16(value.rotationY);
            writer.WriteByte(value.authorityFlags);
            writer.WriteInt32(value.authorityPositionX);
            writer.WriteInt32(value.authorityPositionY);
            writer.WriteInt32(value.authorityPositionZ);
            writer.WriteInt16(value.traversalDirectionX);
            writer.WriteInt16(value.traversalDirectionY);
            writer.WriteInt16(value.traversalDirectionZ);
        }

        private static NetworkInputState ReadInput(FusionPacketReader reader)
        {
            return new NetworkInputState
            {
                inputX = reader.ReadInt16(),
                inputY = reader.ReadInt16(),
                sequenceNumber = reader.ReadUInt16(),
                flags = reader.ReadByte(),
                deltaTimeMs = reader.ReadByte(),
                rotationY = reader.ReadUInt16(),
                authorityFlags = reader.ReadByte(),
                authorityPositionX = reader.ReadInt32(),
                authorityPositionY = reader.ReadInt32(),
                authorityPositionZ = reader.ReadInt32(),
                traversalDirectionX = reader.ReadInt16(),
                traversalDirectionY = reader.ReadInt16(),
                traversalDirectionZ = reader.ReadInt16()
            };
        }

        private static void WriteState(FusionPacketWriter writer, NetworkPositionState value)
        {
            writer.WriteInt32(value.positionX);
            writer.WriteInt32(value.positionY);
            writer.WriteInt32(value.positionZ);
            writer.WriteUInt16(value.rotationY);
            writer.WriteInt16(value.verticalVelocity);
            writer.WriteInt16(value.moveVelocityX);
            writer.WriteInt16(value.moveVelocityY);
            writer.WriteInt16(value.moveVelocityZ);
            writer.WriteUInt32(value.supportId);
            writer.WriteInt32(value.supportLocalPositionX);
            writer.WriteInt32(value.supportLocalPositionY);
            writer.WriteInt32(value.supportLocalPositionZ);
            writer.WriteUInt16(value.supportLocalYaw);
            writer.WriteByte(value.flags);
            writer.WriteUInt16(value.lastProcessedInput);
        }

        private static NetworkPositionState ReadState(FusionPacketReader reader)
        {
            return new NetworkPositionState
            {
                positionX = reader.ReadInt32(),
                positionY = reader.ReadInt32(),
                positionZ = reader.ReadInt32(),
                rotationY = reader.ReadUInt16(),
                verticalVelocity = reader.ReadInt16(),
                moveVelocityX = reader.ReadInt16(),
                moveVelocityY = reader.ReadInt16(),
                moveVelocityZ = reader.ReadInt16(),
                supportId = reader.ReadUInt32(),
                supportLocalPositionX = reader.ReadInt32(),
                supportLocalPositionY = reader.ReadInt32(),
                supportLocalPositionZ = reader.ReadInt32(),
                supportLocalYaw = reader.ReadUInt16(),
                flags = reader.ReadByte(),
                lastProcessedInput = reader.ReadUInt16()
            };
        }

        private void SendSceneReady()
        {
            if (!IsRunnerUsable || !IsClient || !m_LocalSceneReady) return;
            if (TryGetLocalClientId(out uint clientId))
            {
                bool firstSendForEpoch = !m_SceneReadyClients.Contains(clientId);
                if (!SendControlToAuthority(
                        FusionTransportMessageType.SceneReady,
                        Array.Empty<byte>()))
                {
                    return;
                }

                // Reliable delivery means one send per scene/epoch is sufficient. Clients
                // keep this local marker so Update does not emit SceneReady every frame.
                m_SceneReadyClients.Add(clientId);
                if (firstSendForEpoch)
                {
                    LogLifecycle(
                        $"sent SceneReady; client={clientId} epoch={m_AuthorityEpoch}");
                }
            }
        }

        private void SendGameplayReadyIntent()
        {
            if (!m_LocalSceneReady || !m_LocalGameplayReadyIntent) return;
            m_NextGameplayReadyRetryAt =
                Time.unscaledTime + GameplayReadyRetrySeconds;
            if (!SendControlToAuthority(
                    FusionTransportMessageType.GameplayReady,
                    Array.Empty<byte>()))
            {
                return;
            }

            if (m_GameplayReadySendEpoch != m_AuthorityEpoch)
            {
                m_GameplayReadySendEpoch = m_AuthorityEpoch;
                m_GameplayReadySendCount = 0;
            }

            m_GameplayReadySendCount++;
            if (m_GameplayReadySendCount == 1 ||
                m_GameplayReadySendCount == 5 ||
                m_GameplayReadySendCount % 10 == 0)
            {
                LogLifecycle(
                    $"sent GameplayReady; epoch={m_AuthorityEpoch} " +
                    $"attempt={m_GameplayReadySendCount}");
            }
        }

        private bool SendControlToAuthority(FusionTransportMessageType type, byte[] payload)
        {
            return SendToAuthorityInternal(
                FusionProtocol.TransportModuleId,
                (ushort)type,
                payload,
                true);
        }

        private void SendAuthorityAnnouncement()
        {
            if (!IsServer) return;

            var writer = new FusionPacketWriter(8);
            writer.WriteUInt32(m_AuthorityEpoch);
            byte[] payload = writer.ToArray();

            m_ClientScratch.Clear();
            m_ClientScratch.AddRange(m_ConnectedClientIds);
            for (int i = 0; i < m_ClientScratch.Count; i++)
            {
                SendAuthorityAnnouncementToClient(m_ClientScratch[i], payload);
            }
        }

        private void SendAuthorityAnnouncementToClient(uint clientId, byte[] payload = null)
        {
            if (!IsServer) return;
            if (payload == null)
            {
                var writer = new FusionPacketWriter(8);
                writer.WriteUInt32(m_AuthorityEpoch);
                payload = writer.ToArray();
            }

            SendToClientInternal(
                clientId,
                FusionProtocol.TransportModuleId,
                (ushort)FusionTransportMessageType.AuthorityAnnouncement,
                payload,
                true);
        }

        private void HandleAuthorityAnnouncement(ReadOnlyMemory<byte> payload, uint envelopeEpoch)
        {
            try
            {
                var reader = new FusionPacketReader(payload);
                uint announcedEpoch = reader.ReadUInt32();
                if (!reader.End || announcedEpoch != envelopeEpoch)
                {
                    throw new FormatException("Authority announcement epoch is inconsistent.");
                }

                if (announcedEpoch > m_AuthorityEpoch) AdoptAuthorityEpoch(announcedEpoch);
                if (m_LocalSceneReady)
                {
                    SendSceneReady();
                    SendGameplayReadyIntent();
                }
            }
            catch (FormatException exception)
            {
                Reject(exception.Message);
            }
        }

        private void MarkSceneReady(uint clientId)
        {
            if (!IsValidClientId(clientId)) return;
            bool added = m_SceneReadyClients.Add(clientId);
            if (added)
            {
                LogLifecycle(
                    $"received SceneReady; client={clientId} epoch={m_AuthorityEpoch}");
            }
            if (added && !InvokeClientSceneReady(clientId))
            {
                m_SceneReadyClients.Remove(clientId);
                ShutdownSessionForAuthorityFailure(
                    $"A SceneReady handler failed for client {clientId}.");
                return;
            }

            if (m_PendingGameplayReadyClients.Remove(clientId))
            {
                BeginClientSnapshot(clientId, false);
            }
        }

        private void MarkGameplayReady(uint clientId, bool forceSnapshot)
        {
            if (!IsValidClientId(clientId) || !m_SceneReadyClients.Contains(clientId)) return;
            BeginClientSnapshot(clientId, forceSnapshot);
        }

        private void BeginClientSnapshot(uint clientId, bool forceSnapshot)
        {
            if (!IsValidClientId(clientId) || !m_SceneReadyClients.Contains(clientId)) return;
            float now = Time.unscaledTime;
            if (!forceSnapshot &&
                (m_GameplayReadyClients.Contains(clientId) ||
                 m_SnapshotInProgressClients.Contains(clientId)))
            {
                return;
            }
            if (forceSnapshot && m_SnapshotInProgressClients.Contains(clientId))
            {
                if (m_LastSnapshotStartedAt.TryGetValue(clientId, out float inProgressSince) &&
                    now - inProgressSince < FusionProtocol.ReorderTimeoutSeconds)
                {
                    return;
                }

                // A missing reliable marker/ack must not leave this client permanently
                // trapped in SnapshotInProgress. Once the normal reorder timeout expires,
                // a resync request replaces the stale token with a complete new snapshot.
                uint staleToken = m_PendingSnapshotTokens.TryGetValue(
                    clientId, out uint pendingToken)
                        ? pendingToken
                        : 0;
                m_SnapshotInProgressClients.Remove(clientId);
                m_PendingSnapshotTokens.Remove(clientId);
                LogLifecycle(
                    $"replacing stale snapshot; client={clientId} token={staleToken} " +
                    $"epoch={m_AuthorityEpoch}");
            }
            else if (forceSnapshot &&
                     m_LastSnapshotStartedAt.TryGetValue(clientId, out float lastStartedAt) &&
                     now - lastStartedAt < FusionProtocol.ReorderTimeoutSeconds)
            {
                return;
            }

            m_GameplayReadyClients.Remove(clientId);
            m_SnapshotInProgressClients.Add(clientId);
            m_LastSnapshotStartedAt[clientId] = now;
            uint snapshotToken = ++m_NextSnapshotToken;
            if (snapshotToken == 0) snapshotToken = ++m_NextSnapshotToken;
            m_PendingSnapshotTokens[clientId] = snapshotToken;
            LogLifecycle(
                $"begin snapshot; client={clientId} token={snapshotToken} " +
                $"epoch={m_AuthorityEpoch} producers={m_SnapshotProducers.Count} " +
                $"forced={forceSnapshot}");
            if (!TryProduceFullSnapshots(clientId, out string snapshotFailure))
            {
                m_SnapshotInProgressClients.Remove(clientId);
                m_PendingSnapshotTokens.Remove(clientId);
                ShutdownSessionForAuthorityFailure(
                    $"Full snapshot production failed for client {clientId}: {snapshotFailure}");
                return;
            }

            var writer = new FusionPacketWriter(4);
            writer.WriteUInt32(snapshotToken);
            bool markerEnqueued;
            try
            {
                markerEnqueued = SendToClientInternal(
                    clientId,
                    FusionProtocol.TransportModuleId,
                    (ushort)FusionTransportMessageType.SnapshotComplete,
                    writer.ToArray(),
                    true);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                markerEnqueued = false;
            }

            if (!markerEnqueued)
            {
                m_SnapshotInProgressClients.Remove(clientId);
                m_PendingSnapshotTokens.Remove(clientId);
                ShutdownSessionForAuthorityFailure(
                    $"Could not enqueue the snapshot-complete marker for client {clientId}.");
            }
            else
            {
                LogLifecycle(
                    $"sent SnapshotComplete; client={clientId} token={snapshotToken} " +
                    $"epoch={m_AuthorityEpoch}");
            }
        }

        private void CompleteClientSnapshot(uint clientId, uint snapshotToken)
        {
            if (!m_PendingSnapshotTokens.TryGetValue(clientId, out uint expectedToken) ||
                snapshotToken == 0 ||
                snapshotToken != expectedToken)
            {
                Reject($"stale snapshot acknowledgement {snapshotToken} from client {clientId}");
                return;
            }

            if (!m_SnapshotInProgressClients.Remove(clientId)) return;
            m_PendingSnapshotTokens.Remove(clientId);
            m_GameplayReadyClients.Add(clientId);
            LogLifecycle(
                $"received SnapshotAcknowledged; client={clientId} token={snapshotToken} " +
                $"epoch={m_AuthorityEpoch}; gameplay ready");
            ClearRejectedInputLog(clientId, true);
            if (!InvokeClientSnapshotAcknowledged(clientId))
            {
                m_GameplayReadyClients.Remove(clientId);
                ShutdownSessionForAuthorityFailure(
                    $"A snapshot acknowledgement handler failed for client {clientId}.");
            }
        }

        private bool TryReadControlToken(ReadOnlyMemory<byte> payload, out uint token)
        {
            token = 0;
            try
            {
                var reader = new FusionPacketReader(payload);
                token = reader.ReadUInt32();
                if (token == 0 || !reader.End)
                {
                    throw new FormatException("Snapshot control token is invalid.");
                }

                return true;
            }
            catch (FormatException exception)
            {
                Reject(exception.Message);
                token = 0;
                return false;
            }
        }

        private void PollAuthority()
        {
            PlayerRef currentMaster = GetCurrentMaster();
            bool authority = IsServer;
            bool masterChanged =
                m_Runner.GameMode == GameMode.Shared &&
                currentMaster != m_LastMaster;
            if (!masterChanged && authority == m_WasAuthority) return;

            m_LastMaster = currentMaster;
            m_WasAuthority = authority;
            m_AuthorityEpoch++;
            if (m_AuthorityEpoch == 0) m_AuthorityEpoch = 1;
            ClearDeliveryState(false);
            SetLagCompensationAuthority(authority);
            // Promotion handlers and the authority spawn registry must rebuild before a
            // locally-owned character can announce GameplayReady and trigger snapshots.
            m_AuthorityTransitionInProgress = true;
            bool promotionSucceeded;
            try
            {
                promotionSucceeded = InvokeAuthorityChanged(authority, m_AuthorityEpoch, true);
            }
            finally
            {
                m_AuthorityTransitionInProgress = false;
            }
            if (!promotionSucceeded) return;
            RefreshNetworkIdentities();

            if (authority)
            {
                RebuildConnectedClients();
                SendAuthorityAnnouncement();
                if (m_LocalSceneReady)
                {
                    SendSceneReady();
                    SendGameplayReadyIntent();
                }
            }
            else if (m_LocalSceneReady)
            {
                SendSceneReady();
                SendGameplayReadyIntent();
            }

            PublishAuthorityObservation(m_Runner, authority, m_AuthorityEpoch);
        }

        private PlayerRef GetCurrentMaster()
        {
            if (!IsRunnerUsable || m_Runner.GameMode != GameMode.Shared) return PlayerRef.None;
            return m_Runner.GetMasterClient();
        }

        private void AdoptAuthorityEpoch(uint epoch)
        {
            if (epoch <= m_AuthorityEpoch) return;
            uint previousEpoch = m_AuthorityEpoch;
            LogLifecycle($"adopting authority epoch {previousEpoch}->{epoch}");
            m_AuthorityEpoch = epoch;
            ClearDeliveryState(false);
            m_WasAuthority = IsServer;
            SetLagCompensationAuthority(m_WasAuthority);
            m_AuthorityTransitionInProgress = true;
            bool promotionSucceeded;
            try
            {
                promotionSucceeded =
                    InvokeAuthorityChanged(m_WasAuthority, m_AuthorityEpoch, true);
            }
            finally
            {
                m_AuthorityTransitionInProgress = false;
            }
            if (!promotionSucceeded) return;
            RefreshNetworkIdentities();
            LogLifecycle($"authority epoch {m_AuthorityEpoch} adopted");
            PublishAuthorityObservation(m_Runner, m_WasAuthority, m_AuthorityEpoch);
        }

        private void RefreshNetworkIdentities()
        {
            FusionNetworkIdentity[] identities = FindObjectsByType<FusionNetworkIdentity>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            int refreshed = 0;
            int deferred = 0;
            int otherRunner = 0;
            int failures = 0;
            var deferredNames = new List<string>(4);
            for (int i = 0; i < identities.Length; i++)
            {
                FusionNetworkIdentity identity = identities[i];
                if (identity == null) continue;
                if (!identity.IsSpawned)
                {
                    deferred++;
                    if (deferredNames.Count < 4) deferredNames.Add(identity.name);
                    continue;
                }
                if (identity.Runner != m_Runner)
                {
                    otherRunner++;
                    continue;
                }

                try
                {
                    if (identity.RefreshAuthorityRole()) refreshed++;
                    else deferred++;
                }
                catch (Exception exception)
                {
                    // Authority adoption is a reliable control-stream boundary. One bad
                    // optional identity must never abort it and poison all later sequences.
                    failures++;
                    Debug.LogError(
                        $"[FusionTransport] Identity refresh failed for '{identity.name}' " +
                        $"during authority epoch {m_AuthorityEpoch}.",
                        identity);
                    Debug.LogException(exception, identity);
                }
            }

            if (deferred > 0 || otherRunner > 0 || failures > 0)
            {
                string names = deferredNames.Count > 0
                    ? $" deferredObjects=[{string.Join(", ", deferredNames)}]"
                    : string.Empty;
                LogLifecycle(
                    $"identity refresh; epoch={m_AuthorityEpoch} refreshed={refreshed} " +
                    $"deferredUntilSpawned={deferred} otherRunner={otherRunner} " +
                    $"failures={failures}{names}");
            }
        }

        private void RebuildConnectedClients()
        {
            m_ConnectedClientIds.Clear();
            if (!IsRunnerUsable) return;

            foreach (PlayerRef player in m_Runner.ActivePlayers)
            {
                if (TryPlayerToClientId(player, out uint clientId))
                {
                    m_ConnectedClientIds.Add(clientId);
                }
            }
        }

        private void ClearDeliveryState(bool clearConnections)
        {
            m_OutgoingSequences.Clear();
            m_IncomingSequences.Clear();
            m_SequenceBaselineResets.Clear();
            m_RateWindows.Clear();
            m_SceneReadyClients.Clear();
            m_GameplayReadyClients.Clear();
            m_SnapshotInProgressClients.Clear();
            m_PendingGameplayReadyClients.Clear();
            m_PendingSnapshotTokens.Clear();
            m_LastSnapshotStartedAt.Clear();
            m_RejectedInputLogs.Clear();
            m_LocalSnapshotCompletedEpoch = 0;
            m_NextGameplayReadyRetryAt = 0f;
            m_GameplayReadySendEpoch = 0;
            m_GameplayReadySendCount = 0;
            if (clearConnections) m_ConnectedClientIds.Clear();
        }

        private void ClearClientDeliveryState(uint clientId)
        {
            m_ExpiredSequenceKeys.Clear();
            foreach (ulong key in m_SequenceBaselineResets)
            {
                if ((uint)(key >> 8) == clientId) m_ExpiredSequenceKeys.Add(key);
            }
            for (int i = 0; i < m_ExpiredSequenceKeys.Count; i++)
            {
                m_SequenceBaselineResets.Remove(m_ExpiredSequenceKeys[i]);
            }
            m_ExpiredSequenceKeys.Clear();
            foreach (ulong key in m_OutgoingSequences.Keys)
            {
                if ((uint)(key >> 8) == clientId) m_ExpiredSequenceKeys.Add(key);
            }
            for (int i = 0; i < m_ExpiredSequenceKeys.Count; i++)
            {
                m_OutgoingSequences.Remove(m_ExpiredSequenceKeys[i]);
            }

            m_ExpiredSequenceKeys.Clear();
            foreach (ulong key in m_IncomingSequences.Keys)
            {
                if ((uint)(key >> 8) == clientId) m_ExpiredSequenceKeys.Add(key);
            }
            for (int i = 0; i < m_ExpiredSequenceKeys.Count; i++)
            {
                m_IncomingSequences.Remove(m_ExpiredSequenceKeys[i]);
            }
            m_ExpiredSequenceKeys.Clear();
        }

        private bool AcceptRate(uint senderClientId)
        {
            if (m_MaxInboundPacketsPerSecond <= 0) return true;

            float now = Time.unscaledTime;
            if (!m_RateWindows.TryGetValue(senderClientId, out RateWindow window) ||
                now - window.StartedAt >= 1f)
            {
                m_RateWindows[senderClientId] = new RateWindow { StartedAt = now, Count = 1 };
                return true;
            }

            window.Count++;
            m_RateWindows[senderClientId] = window;
            return window.Count <= m_MaxInboundPacketsPerSecond;
        }

        private void ProcessSequenceTimeouts()
        {
            float now = Time.unscaledTime;
            m_ExpiredSequenceKeys.Clear();

            foreach (var pair in m_IncomingSequences)
            {
                OrderedReceiveState state = pair.Value;
                if (state.GapStartedAt < 0f ||
                    now - state.GapStartedAt < FusionProtocol.ReorderTimeoutSeconds)
                {
                    continue;
                }

                m_ExpiredSequenceKeys.Add(pair.Key);
            }

            for (int i = 0; i < m_ExpiredSequenceKeys.Count; i++)
            {
                ulong key = m_ExpiredSequenceKeys[i];
                m_IncomingSequences.Remove(key);
                m_SequenceBaselineResets.Add(key);
                uint sender = unchecked((uint)(key >> 8));
                FusionPacketDirection direction = (FusionPacketDirection)(key & 0xff);
                RequestResync(sender, direction);
            }
        }

        private void RequestResync(uint senderClientId, FusionPacketDirection direction)
        {
            if (direction == FusionPacketDirection.ToAuthority && IsServer)
            {
                BeginClientSnapshot(senderClientId, true);
                return;
            }

            if (IsClient)
            {
                SendControlToAuthority(FusionTransportMessageType.ResyncRequest, Array.Empty<byte>());
            }
        }

        private void TryAutoBind()
        {
            NetworkRunner candidate = null;
            int count = 0;
            foreach (NetworkRunner runner in NetworkRunner.Instances)
            {
                if (runner == null ||
                    runner.IsShutdown ||
                    FusionLobbyDiscoveryRunnerMarker.IsDiscoveryRunner(runner))
                {
                    continue;
                }
                candidate = runner;
                count++;
                if (count > 1) break;
            }

            if (count == 1)
            {
                m_MultipleRunnerWarningIssued = false;
                Bind(candidate);
            }
            else if (count > 1 && !m_MultipleRunnerWarningIssued)
            {
                m_MultipleRunnerWarningIssued = true;
                Debug.LogError(
                    "[FusionTransport] Automatic binding found multiple NetworkRunners. " +
                    "Bind the intended PeerMode.Single runner explicitly.",
                    this);
            }
        }

        private bool IsValidAuthoritySource(PlayerRef source)
        {
            if (IsServer) return false; // Authority loopback never travels through an RPC.

            if (m_Runner.GameMode == GameMode.Shared)
            {
                return source == m_Runner.GetMasterClient();
            }

            return source.IsNone;
        }

        private void Reject(string reason)
        {
            if (!m_LogRejectedPackets) return;
            Debug.LogWarning($"[FusionTransport] Rejected packet: {reason}", this);
        }

        private void RejectUnreadyCharacterInput(uint clientId)
        {
            if (!m_LogRejectedPackets) return;

            float now = Time.unscaledTime;
            bool found = m_RejectedInputLogs.TryGetValue(
                clientId, out RejectedInputLogState state);
            if (!found || now - state.LastLoggedAt >= RejectedInputLogIntervalSeconds)
            {
                string suppressed = state.Suppressed > 0
                    ? $" ({state.Suppressed} identical packets suppressed)"
                    : string.Empty;
                Debug.LogWarning(
                    $"[FusionTransport] Rejected packet: character input from unready " +
                    $"client {clientId}{suppressed}",
                    this);
                state.LastLoggedAt = now;
                state.Suppressed = 0;
            }
            else
            {
                state.Suppressed++;
            }

            m_RejectedInputLogs[clientId] = state;
        }

        private void ClearRejectedInputLog(uint clientId, bool reportSuppressed)
        {
            if (!m_RejectedInputLogs.TryGetValue(
                    clientId, out RejectedInputLogState state))
            {
                return;
            }

            m_RejectedInputLogs.Remove(clientId);
            if (reportSuppressed && state.Suppressed > 0)
            {
                LogLifecycle(
                    $"client={clientId} reached gameplay ready; suppressed " +
                    $"{state.Suppressed} additional pre-ready input rejections");
            }
        }

        private void LogLifecycle(string message)
        {
            if (!m_LogLifecycleDiagnostics) return;
            Debug.Log($"[FusionTransport][Lifecycle] {message}", this);
        }

        private static ulong OutgoingSequenceKey(uint target, FusionPacketDirection direction)
        {
            return ((ulong)target << 8) | (byte)direction;
        }

        private static ulong IncomingSequenceKey(uint sender, FusionPacketDirection direction)
        {
            return ((ulong)sender << 8) | (byte)direction;
        }

        private static uint ModuleMessageKey(ushort moduleId, ushort messageType)
        {
            return ((uint)moduleId << 16) | messageType;
        }

        private bool TryRegisterHandler(
            ushort moduleId,
            Action<FusionModuleMessage> handler)
        {
            if (moduleId == FusionProtocol.TransportModuleId || handler == null) return false;

            if (m_ModuleHandlers.TryGetValue(moduleId, out var existing))
            {
                if (existing == handler) return true;

                Debug.LogError(
                    $"[FusionTransport] Module ID {moduleId} is already registered by another handler.",
                    this);
                return false;
            }

            m_ModuleHandlers.Add(moduleId, handler);
            return true;
        }

        private bool HasPreGameplayRegistration(ushort moduleId)
        {
            uint prefix = (uint)moduleId << 16;
            foreach (uint key in m_PreGameplayMessageHandlers)
            {
                if ((key & 0xffff0000u) == prefix) return true;
            }

            return false;
        }

        private bool TryProduceFullSnapshots(uint clientId, out string failureReason)
        {
            failureReason = string.Empty;
            if (m_ActiveSnapshotContext != null)
            {
                failureReason = "Nested full snapshot production is not supported.";
                return false;
            }

            if (m_SnapshotProducers.Count == 0)
            {
                failureReason = "No full snapshot producers are registered.";
                return false;
            }

            foreach (ushort moduleId in m_RegularModuleHandlers)
            {
                if (m_SnapshotProducers.ContainsKey(moduleId)) continue;
                failureReason =
                    $"Registered module {moduleId} has no full snapshot producer.";
                return false;
            }

            if (!m_SnapshotProducers.ContainsKey(FusionModuleIds.Core) ||
                !m_SnapshotProducers.ContainsKey(FusionModuleIds.Variables) ||
                !m_SnapshotProducers.ContainsKey(FusionModuleIds.AnimationMotion))
            {
                failureReason =
                    "Mandatory Core, Variables, and Animation/Motion snapshot producers " +
                    "must all be registered.";
                return false;
            }

            m_SnapshotProducerScratch.Clear();
            foreach (IFusionFullSnapshotProducer producer in m_SnapshotProducers.Values)
            {
                m_SnapshotProducerScratch.Add(producer);
            }
            m_SnapshotProducerScratch.Sort(
                (left, right) => left.FullSnapshotModuleId.CompareTo(right.FullSnapshotModuleId));

            for (int i = 0; i < m_SnapshotProducerScratch.Count; i++)
            {
                IFusionFullSnapshotProducer producer = m_SnapshotProducerScratch[i];
                if (producer == null ||
                    (producer is UnityEngine.Object producerObject && producerObject == null))
                {
                    failureReason = "A registered full snapshot producer was destroyed.";
                    m_SnapshotProducerScratch.Clear();
                    return false;
                }

                ushort moduleId = producer.FullSnapshotModuleId;
                string producerName = GetSnapshotProducerName(producer);
                if (!m_SnapshotProducers.TryGetValue(moduleId, out var registered) ||
                    !ReferenceEquals(registered, producer) ||
                    !m_ModuleHandlers.ContainsKey(moduleId))
                {
                    failureReason =
                        $"Snapshot producer '{producerName}' is no longer registered with module {moduleId}.";
                    m_SnapshotProducerScratch.Clear();
                    return false;
                }

                if (producer is UnityEngine.Behaviour behaviour && !behaviour.isActiveAndEnabled)
                {
                    failureReason = $"Snapshot producer '{producerName}' is not active and enabled.";
                    m_SnapshotProducerScratch.Clear();
                    return false;
                }

                var context = new FusionFullSnapshotContext(this, producer, moduleId, clientId);
                FusionFullSnapshotResult result;
                m_ActiveSnapshotContext = context;
                try
                {
                    result = producer.ProduceFullSnapshot(context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    failureReason =
                        $"Snapshot producer '{producerName}' threw {exception.GetType().Name}.";
                    m_SnapshotProducerScratch.Clear();
                    return false;
                }
                finally
                {
                    m_ActiveSnapshotContext = null;
                }

                if (!result.IsComplete)
                {
                    failureReason = string.IsNullOrWhiteSpace(result.FailureReason)
                        ? $"Snapshot producer '{producerName}' reported incomplete state."
                        : $"Snapshot producer '{producerName}': {result.FailureReason}";
                    m_SnapshotProducerScratch.Clear();
                    return false;
                }

                if (result.PacketsEnqueued != context.PacketsEnqueued)
                {
                    failureReason =
                        $"Snapshot producer '{producerName}' reported {result.PacketsEnqueued} packets " +
                        $"but the transport accepted {context.PacketsEnqueued}.";
                    m_SnapshotProducerScratch.Clear();
                    return false;
                }
            }

            m_SnapshotProducerScratch.Clear();
            return true;
        }

        private static string GetSnapshotProducerName(IFusionFullSnapshotProducer producer)
        {
            if (producer == null) return "<null>";
            try
            {
                return string.IsNullOrWhiteSpace(producer.FullSnapshotProducerName)
                    ? producer.GetType().Name
                    : producer.FullSnapshotProducerName;
            }
            catch (Exception)
            {
                return producer.GetType().Name;
            }
        }

        private bool InvokeAuthorityChanged(bool isAuthority, uint epoch, bool failClosed)
        {
            Action<bool, uint> handlers = AuthorityChanged;
            if (handlers == null) return true;

            bool succeeded = true;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<bool, uint>)callback)(isAuthority, epoch);
                }
                catch (Exception exception)
                {
                    succeeded = false;
                    Debug.LogException(exception, this);
                }
            }

            if (!succeeded && failClosed)
            {
                ShutdownSessionForAuthorityFailure(
                    $"A GC2 authority promotion handler failed for epoch {epoch}.");
            }
            return succeeded;
        }

        private bool InvokeClientSceneReady(uint clientId)
        {
            Action<uint> handlers = ClientSceneReady;
            return InvokeClientEvent(handlers, clientId);
        }

        private bool InvokeClientSnapshotAcknowledged(uint clientId)
        {
            Action<uint> handlers = ClientSnapshotAcknowledged;
            return InvokeClientEvent(handlers, clientId);
        }

        private bool InvokeClientEvent(Action<uint> handlers, uint clientId)
        {
            if (handlers == null) return true;

            bool succeeded = true;
            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<uint>)callback)(clientId);
                }
                catch (Exception exception)
                {
                    succeeded = false;
                    Debug.LogException(exception, this);
                }
            }
            return succeeded;
        }

        private void InvokeRunnerShutdown(NetworkRunner runner, ShutdownReason reason)
        {
            Action<NetworkRunner, ShutdownReason> handlers = RunnerShutdown;
            if (handlers == null) return;

            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<NetworkRunner, ShutdownReason>)callback)(runner, reason);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private void InvokeLocalSceneReady()
        {
            Action handlers = LocalSceneReady;
            if (handlers == null) return;

            foreach (Delegate callback in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)callback)();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                    ShutdownSessionForAuthorityFailure(
                        "A local scene-readiness handler failed.");
                    return;
                }
            }
        }

        private void PublishRunnerBinding(NetworkRunner runner, bool isBound)
        {
            var info = new FusionRunnerBindingInfo(
                runner,
                isBound,
                runner != null && runner.IsRunning && !runner.IsShutdown);
            FusionLifecycleEventUtility.InvokeBestEffort(
                isBound ? RunnerObservedBound : RunnerObservedUnbound,
                info,
                this,
                isBound ? nameof(RunnerObservedBound) : nameof(RunnerObservedUnbound));
        }

        private FusionSessionBootstrap ResolveSessionBootstrap()
        {
            if (m_SessionBootstrap != null) return m_SessionBootstrap;
            m_SessionBootstrap = GetComponent<FusionSessionBootstrap>();
            if (m_SessionBootstrap == null)
            {
                m_SessionBootstrap = GetComponentInParent<FusionSessionBootstrap>();
            }
            return m_SessionBootstrap;
        }

        private void PublishAuthorityObservation(
            NetworkRunner runner,
            bool isAuthority,
            uint epoch)
        {
            uint masterClientId = InvalidClientId;
            if (runner != null && runner.IsRunning && !runner.IsShutdown &&
                runner.GameMode == GameMode.Shared)
            {
                TryPlayerToClientId(runner.GetMasterClient(), out masterClientId);
            }

            var info = new FusionAuthorityObservation(
                runner,
                isAuthority,
                epoch,
                masterClientId);
            m_LastAuthorityObservation = info;
            m_HasLastAuthorityObservation = true;
            FusionLifecycleEventUtility.InvokeBestEffort(
                AuthorityObservedChanged,
                m_LastAuthorityObservation,
                this,
                nameof(AuthorityObservedChanged));
        }

        private void PublishPlayerObservation(
            Action<FusionPlayerConnectionInfo> handlers,
            NetworkRunner runner,
            PlayerRef player,
            uint clientId,
            string eventName)
        {
            var info = new FusionPlayerConnectionInfo(
                runner,
                player,
                clientId,
                runner != null && player == runner.LocalPlayer);
            FusionLifecycleEventUtility.InvokeBestEffort(
                handlers,
                info,
                this,
                eventName);
        }

        private void PublishSceneObservation(FusionSceneLifecyclePhase phase)
        {
            var info = new FusionSceneLifecycleInfo(
                m_Runner,
                phase,
                SceneManager.GetActiveScene());
            m_LastLocalSceneObservation = info;
            m_HasLastLocalSceneObservation = true;
            Action<FusionSceneLifecycleInfo> handlers =
                phase == FusionSceneLifecyclePhase.LoadStarted
                    ? LocalSceneObservedStarted
                    : LocalSceneObservedCompleted;
            FusionLifecycleEventUtility.InvokeBestEffort(
                handlers,
                m_LastLocalSceneObservation,
                this,
                phase == FusionSceneLifecyclePhase.LoadStarted
                    ? nameof(LocalSceneObservedStarted)
                    : nameof(LocalSceneObservedCompleted));
        }

        private void PublishRunnerShutdown(NetworkRunner runner, ShutdownReason reason)
        {
            m_LastRunnerShutdown = new FusionRunnerShutdownInfo(runner, reason);
            m_HasLastRunnerShutdown = true;
            FusionLifecycleEventUtility.InvokeBestEffort(
                RunnerObservedShutdown,
                m_LastRunnerShutdown,
                this,
                nameof(RunnerObservedShutdown));
        }

        private static bool TryInferLaunchMode(
            NetworkRunner runner,
            out FusionDefaultLaunchMode launchMode)
        {
            launchMode = FusionDefaultLaunchMode.Host;
            if (runner == null) return false;
            switch (runner.GameMode)
            {
                case GameMode.Host:
                    launchMode = FusionDefaultLaunchMode.Host;
                    return true;
                case GameMode.Client:
                    launchMode = FusionDefaultLaunchMode.JoinHost;
                    return true;
                case GameMode.Shared:
                    // An externally owned Shared runner does not retain whether this peer
                    // created or joined the room. Report the topology without inventing intent.
                    launchMode = FusionDefaultLaunchMode.Shared;
                    return false;
                default:
                    return false;
            }
        }

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            if (runner != m_Runner || !TryPlayerToClientId(player, out uint clientId)) return;
            m_ConnectedClientIds.Add(clientId);

            if (IsServer)
            {
                SendAuthorityAnnouncementToClient(clientId);
            }

            if (player == runner.LocalPlayer && m_LocalSceneReady)
            {
                SendSceneReady();
                SendGameplayReadyIntent();
            }

            PublishPlayerObservation(
                PlayerObservedJoined,
                runner,
                player,
                clientId,
                nameof(PlayerObservedJoined));
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            if (runner != m_Runner || !TryPlayerToClientId(player, out uint clientId)) return;
            m_ConnectedClientIds.Remove(clientId);
            m_SceneReadyClients.Remove(clientId);
            m_GameplayReadyClients.Remove(clientId);
            m_SnapshotInProgressClients.Remove(clientId);
            m_PendingGameplayReadyClients.Remove(clientId);
            m_PendingSnapshotTokens.Remove(clientId);
            m_LastSnapshotStartedAt.Remove(clientId);
            m_RateWindows.Remove(clientId);
            ClearRejectedInputLog(clientId, false);
            ClearClientDeliveryState(clientId);
            NetworkSecurityManager.Instance?.OnClientDisconnected(clientId);
            PublishPlayerObservation(
                PlayerObservedLeft,
                runner,
                player,
                clientId,
                nameof(PlayerObservedLeft));
        }

        public void OnSceneLoadDone(NetworkRunner runner)
        {
            if (runner != m_Runner) return;
            NotifyLocalSceneReady();
        }

        public void OnSceneLoadStart(NetworkRunner runner)
        {
            if (runner != m_Runner) return;
            m_LocalSceneReady = false;
            m_LocalGameplayReadyIntent = false;
            // Pause the whole logical session during a scene transition. Keeping remote
            // readiness here would allow state/input traffic while their infrastructure is
            // being torn down and recreated.
            m_SceneReadyClients.Clear();
            m_GameplayReadyClients.Clear();
            m_SnapshotInProgressClients.Clear();
            m_PendingGameplayReadyClients.Clear();
            m_PendingSnapshotTokens.Clear();
            m_LastSnapshotStartedAt.Clear();
            m_RejectedInputLogs.Clear();
            m_LocalSnapshotCompletedEpoch = 0;
            m_NextGameplayReadyRetryAt = 0f;
            m_GameplayReadySendEpoch = 0;
            m_GameplayReadySendCount = 0;
            PublishSceneObservation(FusionSceneLifecyclePhase.LoadStarted);
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            if (runner != m_Runner) return;
            InvokeRunnerShutdown(runner, shutdownReason);
            PublishRunnerShutdown(runner, shutdownReason);
            // Shutdown observers may bind a replacement runner immediately. Never unbind
            // that replacement while unwinding the callback for the old runner.
            if (runner == m_Runner) Unbind();
        }

        public void OnConnectedToServer(NetworkRunner runner)
        {
            if (runner != m_Runner) return;
            RebuildConnectedClients();
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            if (runner != m_Runner) return;
            ClearDeliveryState(true);
        }

        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            if (runner != m_Runner || !IsRunnerUsable ||
                runner.GameMode == GameMode.Shared ||
                !runner.LocalPlayer.IsRealPlayer)
            {
                return;
            }

            // Fusion exposes one input value per player/tick. Keep collection centralized on
            // the runner callback instead of registering every character as a callback (where
            // multiple behaviours could overwrite each other's NetworkInput value).
            if (!runner.TryGetPlayerObject(runner.LocalPlayer, out NetworkObject playerObject) ||
                playerObject == null)
            {
                return;
            }

            FusionNativeNetworkCharacterMotor motor =
                playerObject.GetComponent<FusionNativeNetworkCharacterMotor>();
            motor?.TryConsumeNetworkInput(runner, input);
        }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectRequest(
            NetworkRunner runner,
            NetworkRunnerCallbackArgs.ConnectRequest request,
            byte[] token) { }
        public void OnConnectFailed(
            NetworkRunner runner,
            NetAddress remoteAddress,
            NetConnectFailedReason reason) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(
            NetworkRunner runner,
            Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(
            NetworkRunner runner,
            PlayerRef player,
            ReliableKey key,
            float progress) { }
    }
}
