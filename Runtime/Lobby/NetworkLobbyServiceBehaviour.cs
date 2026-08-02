using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Lobby
{
    /// <summary>
    /// Shared observable state for concrete lobby providers. It deliberately owns no
    /// transport or provider SDK objects.
    /// </summary>
    public abstract class NetworkLobbyServiceBehaviour : MonoBehaviour, INetworkLobbyService
    {
        private readonly List<NetworkLobbyEntry> m_Sessions =
            new List<NetworkLobbyEntry>();
        private ReadOnlyCollection<NetworkLobbyEntry> m_ReadOnlySessions;
        private NetworkLobbyState m_State = NetworkLobbyState.Offline;
        private string m_StatusMessage = "Offline";
        private string m_LastError = string.Empty;
        private string m_CurrentSessionId = string.Empty;
        private string m_CurrentSessionName = string.Empty;

        public abstract string ServiceName { get; }
        public abstract NetworkLobbyCapabilities Capabilities { get; }
        public NetworkLobbyState State => m_State;
        public string StatusMessage => m_StatusMessage;
        public string LastError => m_LastError;
        public IReadOnlyList<NetworkLobbyEntry> Sessions =>
            m_ReadOnlySessions ??= m_Sessions.AsReadOnly();
        public string CurrentSessionId => m_CurrentSessionId;
        public string CurrentSessionName => m_CurrentSessionName;

        public event Action StateChanged;
        public event Action SessionsChanged;

        public abstract Task<NetworkLobbyOperationResult> InitializeAsync(
            CancellationToken cancellationToken = default);

        public abstract Task<NetworkLobbyOperationResult> RefreshAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default);

        public abstract Task<NetworkLobbyOperationResult> CreateAsync(
            NetworkLobbyCreateRequest request,
            CancellationToken cancellationToken = default);

        public abstract Task<NetworkLobbyOperationResult> QuickJoinAsync(
            NetworkLobbyQuery query,
            CancellationToken cancellationToken = default);

        public abstract Task<NetworkLobbyOperationResult> JoinAsync(
            NetworkLobbyJoinRequest request,
            CancellationToken cancellationToken = default);

        public abstract Task<NetworkLobbyOperationResult> LeaveAsync(
            CancellationToken cancellationToken = default);

        public bool TryGetSession(string id, out NetworkLobbyEntry entry)
        {
            for (int i = 0; i < m_Sessions.Count; i++)
            {
                NetworkLobbyEntry candidate = m_Sessions[i];
                if (!string.Equals(candidate.Id, id, StringComparison.Ordinal)) continue;
                entry = candidate;
                return true;
            }

            entry = null;
            return false;
        }

        protected void ReplaceSessions(IEnumerable<NetworkLobbyEntry> sessions)
        {
            m_Sessions.Clear();
            if (sessions != null)
            {
                foreach (NetworkLobbyEntry session in sessions)
                {
                    if (session != null) m_Sessions.Add(session);
                }
            }

            SessionsChanged?.Invoke();
        }

        protected void ClearSessions()
        {
            if (m_Sessions.Count == 0) return;
            m_Sessions.Clear();
            SessionsChanged?.Invoke();
        }

        protected void SetState(NetworkLobbyState state, string statusMessage)
        {
            string normalized = statusMessage ?? string.Empty;
            bool changed = m_State != state ||
                           !string.Equals(
                               m_StatusMessage,
                               normalized,
                               StringComparison.Ordinal);
            m_State = state;
            m_StatusMessage = normalized;
            if (state != NetworkLobbyState.Error) m_LastError = string.Empty;
            if (changed) StateChanged?.Invoke();
        }

        protected void SetConnected(string sessionId, string sessionName, string statusMessage)
        {
            m_CurrentSessionId = sessionId ?? string.Empty;
            m_CurrentSessionName = sessionName ?? string.Empty;
            SetState(NetworkLobbyState.Connected, statusMessage);
        }

        protected void SetDisconnected(string statusMessage = "Offline")
        {
            m_CurrentSessionId = string.Empty;
            m_CurrentSessionName = string.Empty;
            SetState(NetworkLobbyState.Offline, statusMessage);
        }

        protected NetworkLobbyOperationResult Fail(string code, string message)
        {
            m_LastError = message ?? string.Empty;
            m_State = NetworkLobbyState.Error;
            m_StatusMessage = m_LastError;
            StateChanged?.Invoke();
            return NetworkLobbyOperationResult.Failure(code, message);
        }

        protected static NetworkLobbyOperationResult Unsupported(string operation)
        {
            return NetworkLobbyOperationResult.Failure(
                "unsupported",
                $"{operation} is not supported by this lobby service.");
        }

        protected static bool IsBusyState(NetworkLobbyState state)
        {
            return state == NetworkLobbyState.Initializing ||
                   state == NetworkLobbyState.Browsing ||
                   state == NetworkLobbyState.Creating ||
                   state == NetworkLobbyState.Joining ||
                   state == NetworkLobbyState.Leaving;
        }
    }
}
