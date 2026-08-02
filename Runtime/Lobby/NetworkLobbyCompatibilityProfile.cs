using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Lobby
{
    [Serializable]
    public sealed class NetworkLobbyCompatibilityProfile
    {
        public const string ProductKey = "gc2p";
        public const string BuildKey = "gc2b";
        public const string ProtocolKey = "gc2v";
        public const string DisplayNameKey = "gc2n";
        public const string TopologyKey = "gc2t";

        [Tooltip("Stable product identifier. Empty uses Application.identifier.")]
        [SerializeField] private string m_ProductId = string.Empty;
        [Tooltip("Build compatibility identifier. Empty uses Application.version.")]
        [SerializeField] private string m_BuildId = string.Empty;
        [Min(1)]
        [SerializeField] private int m_ProtocolVersion = 1;
        [SerializeField] private bool m_RequireBuildMatch = true;

        public string ProductId => string.IsNullOrWhiteSpace(m_ProductId)
            ? Application.identifier ?? string.Empty
            : m_ProductId.Trim();

        public string BuildId => string.IsNullOrWhiteSpace(m_BuildId)
            ? Application.version ?? string.Empty
            : m_BuildId.Trim();

        public int ProtocolVersion => Math.Max(1, m_ProtocolVersion);
        public bool RequireBuildMatch => m_RequireBuildMatch;

        public bool IsCompatible(
            string productId,
            string buildId,
            int protocolVersion,
            out string reason)
        {
            if (!string.Equals(ProductId, productId ?? string.Empty, StringComparison.Ordinal))
            {
                reason = "Different product";
                return false;
            }

            if (protocolVersion != ProtocolVersion)
            {
                reason = "Different network protocol";
                return false;
            }

            if (m_RequireBuildMatch &&
                !string.Equals(BuildId, buildId ?? string.Empty, StringComparison.Ordinal))
            {
                reason = "Different game build";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }
}
