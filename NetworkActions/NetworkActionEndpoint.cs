using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Serializable]
    public sealed class NetworkActionBinding
    {
        [Tooltip("Trusted action contract accepted by this endpoint. The requesting GC2 " +
                 "Instruction must use the same definition.")]
        [SerializeField] private NetworkActionDefinition m_Definition;

        [Header("Authority")]
        [Tooltip("Checked only by the logical gameplay authority before committing this action.")]
        [SerializeField] private RunConditionsList m_AuthorityConditions = new();
        [Tooltip("Runs once on the logical authority after the canonical action has already " +
                 "been committed. It cannot approve, reject, or change the payload.")]
        [SerializeField] private RunInstructionsList m_OnAuthorityCommitted = new();

        [Header("Replicated Application")]
        [Tooltip("Runs on the authority and each receiving live replica for a confirmed " +
                 "non-snapshot action.")]
        [SerializeField] private RunInstructionsList m_OnApplied = new();
        [Tooltip("Reconstructs persistent initial/late-join/re-registration state. Use absolute, " +
                 "idempotent Instructions without toggles or one-shot effects.")]
        [SerializeField] private RunInstructionsList m_OnSnapshotApplied = new();

        public NetworkActionDefinition Definition => m_Definition;

        public NetworkActionBinding()
        { }

        public NetworkActionBinding(NetworkActionDefinition definition)
        {
            m_Definition = definition;
        }

        internal bool CheckAuthority(Args args) => m_AuthorityConditions.Check(args);

        internal Task RunAuthority(Args args) => m_OnAuthorityCommitted.Run(args);

        internal Task RunApplied(Args args, bool snapshot) => snapshot
            ? m_OnSnapshotApplied.Run(args)
            : m_OnApplied.Run(args);
    }

    [AddComponentMenu("Game Creator/Network/Actions/Network Action Endpoint")]
    [DisallowMultipleComponent]
    public sealed class NetworkActionEndpoint : MonoBehaviour
    {
        [Header("Actions")]
        [Tooltip("Explicit allow-list of Action Definitions accepted by this endpoint, with " +
                 "authority Conditions and local application Instructions.")]
        [SerializeField] private NetworkActionBinding[] m_Actions =
            Array.Empty<NetworkActionBinding>();

        [Header("Identity")]
        [Tooltip("Automatically generated stable sub-address beneath the transport identity. " +
                 "Normally leave it unchanged; sibling endpoints under the same identity need " +
                 "different values.")]
        [SerializeField] private string m_EndpointId;
        [Tooltip("Optional Network Character identity source. Leave empty for ordinary world " +
                 "objects; never assign an unrelated character.")]
        [SerializeField] private NetworkCharacter m_NetworkCharacter;
        [Tooltip("At startup, find a Network Character on this object or a parent. Disable when " +
                 "a separately networked world endpoint is intentionally nested under a Character.")]
        [SerializeField] private bool m_AutoFindNetworkCharacter = true;

        [Header("Debug")]
        [Tooltip("Logs endpoint registration diagnostics. Manager and transport bridge logging " +
                 "provide the complete request and routing path.")]
        [SerializeField] private bool m_LogNetworkMessages;

        private uint m_TransportNetworkId;
        private bool m_IsTransportController;
        private bool m_HasTransportOwner;
        private uint m_TransportOwnerClientId = NetworkTransportBridge.InvalidClientId;
        private uint m_RegisteredNetworkId;
        private uint m_LastGenerationNetworkId;
        private int m_LastGenerationEndpointHash;

        public IReadOnlyList<NetworkActionBinding> Actions =>
            m_Actions ?? Array.Empty<NetworkActionBinding>();
        public NetworkCharacter NetworkCharacter => m_NetworkCharacter;
        public string EndpointId => m_EndpointId ?? string.Empty;
        public int EndpointHash => StableHashUtility.GetStableHash(EndpointId);
        public uint NetworkId => m_NetworkCharacter != null && m_NetworkCharacter.NetworkId != 0
            ? m_NetworkCharacter.NetworkId
            : m_TransportNetworkId;
        public uint ActorNetworkId => m_NetworkCharacter != null
            ? m_NetworkCharacter.NetworkId
            : 0u;
        public bool IsTransportController => m_IsTransportController;

        private void Awake()
        {
            if (m_AutoFindNetworkCharacter && m_NetworkCharacter == null)
            {
                m_NetworkCharacter = GetComponent<NetworkCharacter>();
                if (m_NetworkCharacter == null)
                    m_NetworkCharacter = GetComponentInParent<NetworkCharacter>();
            }
        }

        private void OnEnable() => TryRegisterWithManager();
        private void Start() => TryRegisterWithManager();
        private void Update() => TryRegisterWithManager();
        private void OnDisable() => UnregisterFromManager();
        private void OnDestroy()
        {
            uint networkId = m_LastGenerationNetworkId != 0
                ? m_LastGenerationNetworkId
                : m_RegisteredNetworkId;
            int endpointHash = m_LastGenerationEndpointHash != 0
                ? m_LastGenerationEndpointHash
                : EndpointHash;
            if (networkId != 0 && endpointHash != 0)
            {
                NetworkActionManager.Instance?.UnregisterEndpoint(
                    new NetworkActionEndpointKey(networkId, endpointHash),
                    true);
            }

            m_RegisteredNetworkId = 0;
        }

        public void ApplyTransportNetworkIdentity(
            uint networkId,
            bool isController,
            uint ownerClientId = NetworkTransportBridge.InvalidClientId,
            bool registerWithManager = true)
        {
            if (networkId == 0) return;

            bool changed = m_TransportNetworkId != networkId;
            m_TransportNetworkId = networkId;
            m_LastGenerationNetworkId = networkId;
            m_LastGenerationEndpointHash = EndpointHash;
            m_IsTransportController = isController;
            m_HasTransportOwner = NetworkTransportBridge.IsValidClientId(ownerClientId);
            m_TransportOwnerClientId = m_HasTransportOwner
                ? ownerClientId
                : NetworkTransportBridge.InvalidClientId;

            if (changed && m_RegisteredNetworkId != 0 && m_RegisteredNetworkId != NetworkId)
                UnregisterFromManager();
            if (registerWithManager) TryRegisterWithManager();
        }

        public void ClearTransportNetworkIdentity(uint networkId)
        {
            if (networkId != 0 && m_TransportNetworkId != networkId) return;
            bool registeredByTransport = m_RegisteredNetworkId != 0 &&
                                         m_RegisteredNetworkId == m_TransportNetworkId;
            m_TransportNetworkId = 0;
            m_IsTransportController = false;
            m_HasTransportOwner = false;
            m_TransportOwnerClientId = NetworkTransportBridge.InvalidClientId;
            if (registeredByTransport) UnregisterFromManager();
        }

        public bool TryGetOwnerClientId(out uint ownerClientId)
        {
            ownerClientId = m_TransportOwnerClientId;
            if (m_HasTransportOwner && NetworkTransportBridge.IsValidClientId(ownerClientId))
                return true;

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            return NetworkId != 0 && bridge != null &&
                   bridge.TryGetCharacterOwner(NetworkId, out ownerClientId);
        }

        public bool TryGetBinding(
            int actionHash,
            string actionId,
            ushort schemaVersion,
            out NetworkActionBinding binding,
            out NetworkActionRejectReason rejectReason)
        {
            binding = null;
            rejectReason = NetworkActionRejectReason.ActionNotFound;
            NetworkActionBinding[] actions = m_Actions ?? Array.Empty<NetworkActionBinding>();
            for (int i = 0; i < actions.Length; i++)
            {
                NetworkActionDefinition definition = actions[i]?.Definition;
                if (definition == null || definition.ActionHash != actionHash ||
                    !string.Equals(definition.ActionId, actionId ?? string.Empty,
                        StringComparison.Ordinal)) continue;

                if (definition.SchemaVersion != schemaVersion)
                {
                    rejectReason = NetworkActionRejectReason.SchemaMismatch;
                    return false;
                }

                if (!definition.HasValidContract)
                {
                    rejectReason = NetworkActionRejectReason.InvalidPayload;
                    return false;
                }

                binding = actions[i];
                rejectReason = NetworkActionRejectReason.None;
                return true;
            }

            return false;
        }

        internal bool CheckAuthority(
            NetworkActionBinding binding,
            in NetworkActionExecutionContext context)
        {
            if (binding == null) return false;
            try
            {
                using (NetworkActionContext.Push(context))
                {
                    return binding.CheckAuthority(BuildArgs(context));
                }
            }
            catch (Exception exception)
            {
                // Authored Conditions are part of the authority boundary. A project-specific
                // exception must fail closed and still allow the manager to send a rejection.
                Debug.LogException(exception, this);
                return false;
            }
        }

        internal async Task ExecuteAuthorityAsync(
            NetworkActionBinding binding,
            NetworkActionExecutionContext context)
        {
            if (binding == null) return;
            try
            {
                await Task.Yield();
                if (this == null || binding == null) return;
                using (NetworkActionContext.Push(context))
                {
                    await binding.RunAuthority(BuildArgs(context));
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        internal async Task ExecuteAppliedAsync(
            NetworkActionBinding binding,
            NetworkActionExecutionContext context)
        {
            if (binding == null) return;
            try
            {
                await Task.Yield();
                if (this == null || binding == null) return;
                using (NetworkActionContext.Push(context))
                {
                    await binding.RunApplied(BuildArgs(context), context.IsSnapshot);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        internal bool TryValidateWithHandlers(
            NetworkActionDefinition definition,
            in NetworkActionRequest request,
            ref NetworkActionPayload payload,
            out NetworkActionRejectReason rejectReason)
        {
            rejectReason = NetworkActionRejectReason.None;
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is not INetworkActionAuthorityHandler handler) continue;
                try
                {
                    if (!handler.TryValidateAndNormalize(
                            this, definition, in request, ref payload, out rejectReason))
                    {
                        if (rejectReason == NetworkActionRejectReason.None)
                            rejectReason = NetworkActionRejectReason.HandlerRejected;
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, behaviours[i]);
                    rejectReason = NetworkActionRejectReason.HandlerRejected;
                    return false;
                }
            }

            return true;
        }

        internal void NotifyAuthorityHandlers(
            NetworkActionDefinition definition,
            in NetworkActionBroadcast broadcast)
        {
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is not INetworkActionAuthorityHandler handler) continue;
                try
                {
                    handler.OnAuthorityCommitted(this, definition, in broadcast);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, behaviours[i]);
                }
            }
        }

        private Args BuildArgs(in NetworkActionExecutionContext context)
        {
            GameObject actor = context.Actor != null ? context.Actor : gameObject;
            GameObject target = context.Target != null ? context.Target : gameObject;
            return new Args(actor, target);
        }

        private void TryRegisterWithManager()
        {
            uint networkId = NetworkId;
            NetworkActionManager manager = NetworkActionManager.Instance;
            if (networkId == 0 || EndpointHash == 0 || manager == null) return;
            if (m_RegisteredNetworkId == networkId && manager.IsRegistered(networkId, this)) return;
            if (m_RegisteredNetworkId != 0) UnregisterFromManager();
            if (manager.RegisterEndpoint(networkId, this))
            {
                m_RegisteredNetworkId = networkId;
                m_LastGenerationNetworkId = networkId;
                m_LastGenerationEndpointHash = EndpointHash;
                Log($"registered endpoint networkId={networkId}");
            }
        }

        private void UnregisterFromManager()
        {
            if (m_RegisteredNetworkId == 0) return;
            NetworkActionManager.Instance?.UnregisterEndpoint(m_RegisteredNetworkId, this);
            m_RegisteredNetworkId = 0;
        }

        private void Log(string message)
        {
            if (m_LogNetworkMessages) Debug.Log($"[NetworkActionEndpoint] {message}", this);
        }

#if UNITY_EDITOR
        private void Reset() => EnsureEndpointId();

        private void OnValidate()
        {
            EnsureEndpointId();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            NetworkActionBinding[] actions = m_Actions ?? Array.Empty<NetworkActionBinding>();
            for (int i = 0; i < actions.Length; i++)
            {
                NetworkActionDefinition definition = actions[i]?.Definition;
                if (definition == null) continue;
                string key = $"{definition.ActionHash}:{definition.ActionId}";
                if (!identities.Add(key))
                {
                    Debug.LogWarning(
                        $"[NetworkActionEndpoint] Duplicate action '{definition.ActionId}' on '{name}'.",
                        this);
                }
            }
        }

        private void EnsureEndpointId()
        {
            if (string.IsNullOrWhiteSpace(m_EndpointId))
                m_EndpointId = Guid.NewGuid().ToString("N");
            else
                m_EndpointId = m_EndpointId.Trim();
        }
#endif
    }
}
