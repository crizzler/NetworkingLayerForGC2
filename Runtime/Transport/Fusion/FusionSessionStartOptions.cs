using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Fusion;
using Photon.Realtime;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Per-start overrides for matchmaking and authentication. This type deliberately
    /// contains no Steamworks dependency: a lobby or invite adapter only needs to copy
    /// its metadata into these values before starting Fusion.
    /// </summary>
    public readonly struct FusionSessionStartOptions
    {
        private static readonly IReadOnlyDictionary<string, SessionProperty> EmptyProperties =
            new ReadOnlyDictionary<string, SessionProperty>(
                new Dictionary<string, SessionProperty>());

        public FusionSessionStartOptions(
            string sessionName,
            string region = null,
            AuthenticationValues authenticationValues = null,
            bool forcePhotonRelay = false,
            bool? isOpen = null,
            bool? isVisible = null,
            string customLobbyName = null,
            IReadOnlyDictionary<string, SessionProperty> sessionProperties = null,
            int? maxPlayers = null)
        {
            SessionName = sessionName;
            Region = region;
            AuthenticationValues = authenticationValues;
            ForcePhotonRelay = forcePhotonRelay;
            IsOpen = isOpen;
            IsVisible = isVisible;
            CustomLobbyName = customLobbyName;
            SessionProperties = CopyProperties(sessionProperties);
            MaxPlayers = maxPlayers.HasValue && maxPlayers.Value > 0
                ? maxPlayers
                : null;
        }

        public string SessionName { get; }
        public string Region { get; }
        public AuthenticationValues AuthenticationValues { get; }
        public bool ForcePhotonRelay { get; }
        /// <summary>
        /// Optional per-start override. Null preserves Fusion's existing default.
        /// </summary>
        public bool? IsOpen { get; }
        /// <summary>
        /// Optional per-start override. Null preserves Fusion's existing default.
        /// </summary>
        public bool? IsVisible { get; }
        public string CustomLobbyName { get; }
        /// <summary>
        /// Defensive, read-only copy of the properties advertised through Photon matchmaking.
        /// </summary>
        public IReadOnlyDictionary<string, SessionProperty> SessionProperties { get; }
        /// <summary>
        /// Optional per-start capacity. Null keeps the bootstrap Inspector value.
        /// </summary>
        public int? MaxPlayers { get; }

        internal Dictionary<string, SessionProperty> CopySessionProperties()
        {
            if (SessionProperties == null || SessionProperties.Count == 0) return null;
            return new Dictionary<string, SessionProperty>(SessionProperties);
        }

        private static IReadOnlyDictionary<string, SessionProperty> CopyProperties(
            IReadOnlyDictionary<string, SessionProperty> source)
        {
            if (source == null || source.Count == 0) return EmptyProperties;

            var copy = new Dictionary<string, SessionProperty>(source.Count);
            foreach (KeyValuePair<string, SessionProperty> pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
                copy[pair.Key] = pair.Value;
            }

            return copy.Count == 0
                ? EmptyProperties
                : new ReadOnlyDictionary<string, SessionProperty>(copy);
        }
    }

    /// <summary>
    /// Optional project-owned authentication source. Implement this in a separate assembly
    /// when Steamworks, a console SDK, or another identity provider supplies Photon credentials.
    /// </summary>
    public interface IFusionAuthenticationProvider
    {
        Task<AuthenticationValues> CreateAuthenticationValuesAsync(
            CancellationToken cancellationToken);

        /// <summary>
        /// Called exactly once after a provider-backed start attempt completes. Providers
        /// should release short-lived native tickets here, including after failure or cancellation.
        /// </summary>
        void OnAuthenticationCompleted(FusionAuthenticationCompletion completion);
    }

    public enum FusionAuthenticationCompletionStatus
    {
        Succeeded = 0,
        Failed = 1,
        Cancelled = 2
    }

    /// <summary>
    /// Immutable outcome delivered to the authentication provider that participated in a start.
    /// </summary>
    public readonly struct FusionAuthenticationCompletion
    {
        internal FusionAuthenticationCompletion(
            FusionAuthenticationCompletionStatus status,
            ShutdownReason shutdownReason,
            string errorMessage,
            string exceptionType)
        {
            Status = status;
            ShutdownReason = shutdownReason;
            ErrorMessage = errorMessage ?? string.Empty;
            ExceptionType = exceptionType ?? string.Empty;
        }

        public FusionAuthenticationCompletionStatus Status { get; }
        public ShutdownReason ShutdownReason { get; }
        public string ErrorMessage { get; }
        public string ExceptionType { get; }
        public bool Succeeded => Status == FusionAuthenticationCompletionStatus.Succeeded;
        public bool WasCancelled => Status == FusionAuthenticationCompletionStatus.Cancelled;
    }
}
