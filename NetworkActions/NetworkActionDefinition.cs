using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [CreateAssetMenu(
        fileName = "NetworkAction",
        menuName = "Game Creator/Network/Action Definition",
        order = 530)]
    public sealed class NetworkActionDefinition : ScriptableObject
    {
        [Header("Identity")]
        [SerializeField] private string m_ActionId;
        [Min(1)]
        [SerializeField] private ushort m_SchemaVersion = 1;

        [Header("Contract")]
        [SerializeField] private NetworkActionPayloadType m_PayloadType =
            NetworkActionPayloadType.Boolean;
        [SerializeField] private NetworkActionEffectKind m_EffectKind =
            NetworkActionEffectKind.PersistentState;
        [SerializeField] private NetworkActionAuthorityPolicy m_AuthorityPolicy =
            NetworkActionAuthorityPolicy.OwnerRequest;
        [SerializeField] private NetworkActionRecipientPolicy m_RecipientPolicy =
            NetworkActionRecipientPolicy.RelevantObservers;
        [SerializeField] private bool m_Reliable = true;

        [Header("Authority Validation")]
        [Min(0f)]
        [SerializeField] private float m_Cooldown = 0.15f;
        [Min(0f)]
        [SerializeField] private float m_MaximumDistance = 4f;
        [SerializeField] private bool m_RequireLineOfSight;
        [SerializeField] private LayerMask m_LineOfSightMask = ~0;

        [Header("Persistent State")]
        [SerializeField] private bool m_IncludeInLateJoinSnapshot = true;
        [SerializeField] private NetworkActionPayload m_InitialPayload = new()
        {
            Type = NetworkActionPayloadType.Boolean,
            BooleanValue = false,
            StringValue = ""
        };

        public string ActionId => m_ActionId ?? string.Empty;
        public int ActionHash => StableHashUtility.GetStableHash(ActionId);
        public ushort SchemaVersion => m_SchemaVersion == 0 ? (ushort)1 : m_SchemaVersion;
        public NetworkActionPayloadType PayloadType => m_PayloadType;
        public NetworkActionEffectKind EffectKind => m_EffectKind;
        public NetworkActionAuthorityPolicy AuthorityPolicy => m_AuthorityPolicy;
        public NetworkActionRecipientPolicy RecipientPolicy => m_RecipientPolicy;
        public bool Reliable =>
            m_EffectKind == NetworkActionEffectKind.PersistentState || m_Reliable;
        public float Cooldown => Mathf.Max(0f, m_Cooldown);
        public float MaximumDistance => Mathf.Max(0f, m_MaximumDistance);
        public bool RequireLineOfSight => m_RequireLineOfSight;
        public LayerMask LineOfSightMask => m_LineOfSightMask;
        public bool IncludeInLateJoinSnapshot =>
            m_EffectKind == NetworkActionEffectKind.PersistentState &&
            m_IncludeInLateJoinSnapshot;
        public NetworkActionPayload InitialPayload => NormalizePayload(m_InitialPayload);

        public bool HasValidContract =>
            m_EffectKind != NetworkActionEffectKind.PersistentState ||
            m_RecipientPolicy == NetworkActionRecipientPolicy.RelevantObservers ||
            m_RecipientPolicy == NetworkActionRecipientPolicy.AllClients;

        public bool Matches(int actionHash, string actionId, ushort schemaVersion)
        {
            return actionHash == ActionHash &&
                   string.Equals(actionId ?? string.Empty, ActionId, StringComparison.Ordinal) &&
                   schemaVersion == SchemaVersion;
        }

        public bool TryValidatePayload(
            in NetworkActionPayload payload,
            out NetworkActionRejectReason rejectReason)
        {
            rejectReason = NetworkActionRejectReason.None;
            if (payload.Type != m_PayloadType)
            {
                rejectReason = NetworkActionRejectReason.InvalidPayload;
                return false;
            }

            // The wire serializer carries the full union. Bound even inactive string storage
            // before normalization so a Boolean/Number request cannot smuggle a large payload.
            if ((payload.StringValue?.Length ?? 0) >
                NetworkActionManager.MaxPayloadStringCharacters)
            {
                rejectReason = NetworkActionRejectReason.InvalidPayload;
                return false;
            }

            if (payload.Type == NetworkActionPayloadType.Number &&
                (float.IsNaN(payload.NumberValue) || float.IsInfinity(payload.NumberValue)))
            {
                rejectReason = NetworkActionRejectReason.InvalidPayload;
                return false;
            }

            if (payload.Type == NetworkActionPayloadType.Vector3 &&
                (!IsFinite(payload.Vector3Value.x) || !IsFinite(payload.Vector3Value.y) ||
                 !IsFinite(payload.Vector3Value.z)))
            {
                rejectReason = NetworkActionRejectReason.InvalidPayload;
                return false;
            }

            return true;
        }

        public NetworkActionPayload NormalizePayload(NetworkActionPayload payload)
        {
            // NetworkActionPayload is a serialized discriminated union. Reconstruct the value
            // through its active discriminator so stale serialized fields (or hostile inactive
            // wire fields) can never affect equality, revisions, or GC2 Conditions.
            return m_PayloadType switch
            {
                NetworkActionPayloadType.Boolean =>
                    NetworkActionPayload.FromBoolean(payload.BooleanValue),
                NetworkActionPayloadType.Number =>
                    NetworkActionPayload.FromNumber(payload.NumberValue),
                NetworkActionPayloadType.String => NetworkActionPayload.FromString(
                    (payload.StringValue ?? string.Empty).Length <=
                    NetworkActionManager.MaxPayloadStringCharacters
                        ? payload.StringValue ?? string.Empty
                        : payload.StringValue.Substring(
                            0, NetworkActionManager.MaxPayloadStringCharacters)),
                NetworkActionPayloadType.Vector3 =>
                    NetworkActionPayload.FromVector3(payload.Vector3Value),
                _ => NetworkActionPayload.None
            };
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(m_ActionId))
            {
                m_ActionId = Guid.NewGuid().ToString("N");
            }

            if (m_ActionId.Length > NetworkActionManager.MaxActionIdCharacters)
            {
                m_ActionId = m_ActionId.Substring(0, NetworkActionManager.MaxActionIdCharacters);
            }

            if (m_SchemaVersion == 0) m_SchemaVersion = 1;
            if (m_EffectKind == NetworkActionEffectKind.PersistentState)
            {
                m_Reliable = true;
                // Requester-only state cannot be reconstructed after authority migration, and
                // TargetOwner state changes meaning when ownership transfers. Persistent world
                // truth is therefore shared with all or the current relevant observers.
                if (m_RecipientPolicy == NetworkActionRecipientPolicy.Requester ||
                    m_RecipientPolicy == NetworkActionRecipientPolicy.TargetOwner)
                    m_RecipientPolicy = NetworkActionRecipientPolicy.RelevantObservers;
            }
            m_InitialPayload = NormalizePayload(m_InitialPayload);
        }
    }
}
