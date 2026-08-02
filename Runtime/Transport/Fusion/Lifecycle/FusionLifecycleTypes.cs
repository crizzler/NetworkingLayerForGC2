using System;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    public enum FusionSessionLifecycleState
    {
        Offline = 0,
        RunnerBound = 1,
        Starting = 2,
        Running = 3,
        Stopping = 4
    }

    public enum FusionSessionStopOrigin
    {
        Requested = 0,
        RunnerShutdown = 1,
        RunnerUnbound = 2,
        Destroyed = 3
    }

    public enum FusionSceneLifecyclePhase
    {
        LoadStarted = 0,
        LoadCompleted = 1
    }

    public enum FusionPlayerObjectLifecyclePhase
    {
        Spawned = 0,
        Despawned = 1
    }

    public enum FusionIdentityLifecyclePhase
    {
        Spawned = 0,
        Changed = 1,
        Despawned = 2
    }

    /// <summary>
    /// Immutable, short-lived view of the active Fusion session. Values are copied so
    /// visual scripting observers never need to retain or query Fusion's mutable SessionInfo.
    /// </summary>
    public readonly struct FusionSessionSnapshot
    {
        internal FusionSessionSnapshot(
            NetworkRunner runner,
            FusionDefaultLaunchMode launchMode,
            bool hasLaunchMode,
            string sessionName,
            string region,
            GameMode gameMode,
            int playerCount,
            int maxPlayers,
            bool isExternalRunner)
        {
            Runner = runner;
            LaunchMode = launchMode;
            HasLaunchMode = hasLaunchMode;
            SessionName = sessionName ?? string.Empty;
            Region = region ?? string.Empty;
            GameMode = gameMode;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            IsExternalRunner = isExternalRunner;
        }

        public NetworkRunner Runner { get; }
        public FusionDefaultLaunchMode LaunchMode { get; }
        public bool HasLaunchMode { get; }
        public string SessionName { get; }
        public string Region { get; }
        public GameMode GameMode { get; }
        public int PlayerCount { get; }
        public int MaxPlayers { get; }
        public bool IsExternalRunner { get; }

        internal static bool TryCapture(
            NetworkRunner runner,
            FusionDefaultLaunchMode launchMode,
            bool hasLaunchMode,
            bool isExternalRunner,
            out FusionSessionSnapshot snapshot)
        {
            snapshot = default;
            if (runner == null || !runner.IsRunning || runner.IsShutdown) return false;

            SessionInfo sessionInfo = runner.SessionInfo;
            string sessionName = sessionInfo.IsValid ? sessionInfo.Name : string.Empty;
            string region = sessionInfo.IsValid ? sessionInfo.Region : string.Empty;
            int playerCount = sessionInfo.IsValid ? sessionInfo.PlayerCount : 0;
            int maxPlayers = sessionInfo.IsValid ? sessionInfo.MaxPlayers : 0;
            snapshot = new FusionSessionSnapshot(
                runner,
                launchMode,
                hasLaunchMode,
                sessionName,
                region,
                runner.GameMode,
                playerCount,
                maxPlayers,
                isExternalRunner);
            return true;
        }
    }

    public readonly struct FusionSessionFailureInfo
    {
        internal FusionSessionFailureInfo(
            FusionDefaultLaunchMode launchMode,
            bool hasLaunchMode,
            string sessionName,
            ShutdownReason shutdownReason,
            string errorMessage,
            bool wasCancelled,
            string exceptionType)
        {
            LaunchMode = launchMode;
            HasLaunchMode = hasLaunchMode;
            SessionName = sessionName ?? string.Empty;
            ShutdownReason = shutdownReason;
            ErrorMessage = errorMessage ?? string.Empty;
            WasCancelled = wasCancelled;
            ExceptionType = exceptionType ?? string.Empty;
        }

        public FusionDefaultLaunchMode LaunchMode { get; }
        public bool HasLaunchMode { get; }
        public string SessionName { get; }
        public ShutdownReason ShutdownReason { get; }
        public string ErrorMessage { get; }
        public bool WasCancelled { get; }
        public string ExceptionType { get; }
    }

    /// <summary>
    /// Immutable view of Photon connectivity for the currently bound runner.
    /// </summary>
    public readonly struct FusionConnectionDiagnostics
    {
        internal FusionConnectionDiagnostics(
            NetworkRunner runner,
            ConnectionType connectionType,
            global::Fusion.Sockets.Stun.NATType natType,
            string sessionRegion,
            string authenticatedUserId)
        {
            Runner = runner;
            ConnectionType = connectionType;
            NATType = natType;
            SessionRegion = sessionRegion ?? string.Empty;
            AuthenticatedUserId = authenticatedUserId ?? string.Empty;
        }

        public NetworkRunner Runner { get; }
        public ConnectionType ConnectionType { get; }
        public global::Fusion.Sockets.Stun.NATType NATType { get; }
        public string SessionRegion { get; }
        public string AuthenticatedUserId { get; }
        public bool IsRelayed => ConnectionType == global::Fusion.ConnectionType.Relayed;
    }

    public readonly struct FusionSessionStopInfo
    {
        internal FusionSessionStopInfo(
            FusionSessionSnapshot session,
            FusionSessionStopOrigin origin,
            ShutdownReason shutdownReason,
            bool wasRequested)
        {
            Session = session;
            Origin = origin;
            ShutdownReason = shutdownReason;
            WasRequested = wasRequested;
        }

        public FusionSessionSnapshot Session { get; }
        public FusionSessionStopOrigin Origin { get; }
        public ShutdownReason ShutdownReason { get; }
        public bool WasRequested { get; }
    }

    public readonly struct FusionRunnerBindingInfo
    {
        internal FusionRunnerBindingInfo(NetworkRunner runner, bool isBound, bool isRunning)
        {
            Runner = runner;
            IsBound = isBound;
            IsRunning = isRunning;
        }

        public NetworkRunner Runner { get; }
        public bool IsBound { get; }
        public bool IsRunning { get; }
    }

    public readonly struct FusionRunnerShutdownInfo
    {
        internal FusionRunnerShutdownInfo(NetworkRunner runner, ShutdownReason reason)
        {
            Runner = runner;
            Reason = reason;
        }

        public NetworkRunner Runner { get; }
        public ShutdownReason Reason { get; }
    }

    public readonly struct FusionPlayerConnectionInfo
    {
        internal FusionPlayerConnectionInfo(
            NetworkRunner runner,
            PlayerRef player,
            uint clientId,
            bool isLocalPlayer)
        {
            Runner = runner;
            Player = player;
            ClientId = clientId;
            IsLocalPlayer = isLocalPlayer;
        }

        public NetworkRunner Runner { get; }
        public PlayerRef Player { get; }
        public uint ClientId { get; }
        public bool IsLocalPlayer { get; }
    }

    public readonly struct FusionAuthorityObservation
    {
        internal FusionAuthorityObservation(
            NetworkRunner runner,
            bool isAuthority,
            uint authorityEpoch,
            uint masterClientId)
        {
            Runner = runner;
            IsAuthority = isAuthority;
            AuthorityEpoch = authorityEpoch;
            MasterClientId = masterClientId;
        }

        public NetworkRunner Runner { get; }
        public bool IsAuthority { get; }
        public uint AuthorityEpoch { get; }
        public uint MasterClientId { get; }
    }

    public readonly struct FusionSceneLifecycleInfo
    {
        internal FusionSceneLifecycleInfo(
            NetworkRunner runner,
            FusionSceneLifecyclePhase phase,
            Scene scene)
        {
            Runner = runner;
            Phase = phase;
            SceneName = scene.IsValid() ? scene.name : string.Empty;
            SceneBuildIndex = scene.IsValid() ? scene.buildIndex : -1;
        }

        public NetworkRunner Runner { get; }
        public FusionSceneLifecyclePhase Phase { get; }
        public string SceneName { get; }
        public int SceneBuildIndex { get; }
    }

    public readonly struct FusionPlayerObjectLifecycleInfo
    {
        internal FusionPlayerObjectLifecycleInfo(
            FusionPlayerObjectLifecyclePhase phase,
            NetworkRunner runner,
            PlayerRef player,
            uint clientId,
            NetworkObject playerObject,
            uint networkId)
        {
            Phase = phase;
            Runner = runner;
            Player = player;
            ClientId = clientId;
            PlayerObject = playerObject;
            NetworkId = networkId;
        }

        public FusionPlayerObjectLifecyclePhase Phase { get; }
        public NetworkRunner Runner { get; }
        public PlayerRef Player { get; }
        public uint ClientId { get; }
        public NetworkObject PlayerObject { get; }
        public uint NetworkId { get; }
    }

    public readonly struct FusionIdentityObservation
    {
        internal FusionIdentityObservation(
            FusionIdentityLifecyclePhase phase,
            FusionNetworkIdentity identity,
            NetworkRunner runner,
            uint networkId,
            uint logicalOwnerClientId,
            bool transportAdmitted,
            bool hasAuthorityAdmission,
            bool isLogicalAuthority,
            bool isLocalLogicalOwner)
        {
            Phase = phase;
            Identity = identity;
            Runner = runner;
            NetworkId = networkId;
            LogicalOwnerClientId = logicalOwnerClientId;
            TransportAdmitted = transportAdmitted;
            HasAuthorityAdmission = hasAuthorityAdmission;
            IsLogicalAuthority = isLogicalAuthority;
            IsLocalLogicalOwner = isLocalLogicalOwner;
        }

        public FusionIdentityLifecyclePhase Phase { get; }
        public FusionNetworkIdentity Identity { get; }
        public NetworkRunner Runner { get; }
        public uint NetworkId { get; }
        public uint LogicalOwnerClientId { get; }
        public bool TransportAdmitted { get; }
        public bool HasAuthorityAdmission { get; }
        public bool IsLogicalAuthority { get; }
        public bool IsLocalLogicalOwner { get; }
    }
}
