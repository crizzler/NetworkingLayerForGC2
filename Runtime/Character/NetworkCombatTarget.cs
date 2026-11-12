using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>Routes native GC2 selection through authenticated Core motion commands.
    /// Selection is durable; rotation remains owned by the movement transport.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Character), typeof(NetworkCharacter))]
    [AddComponentMenu("Game Creator/Network/Network Combat Target")]
    public sealed class NetworkCombatTarget : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float m_MaxTargetDistance = 30f;
        private Character m_Character;
        private NetworkCharacter m_NetworkCharacter;
        private bool m_Applying;
        private uint m_TargetId;
        private uint m_Revision = 1;
        private bool m_HasConfirmedState;

        public uint TargetNetworkId => m_TargetId;
        public uint Revision => m_Revision;

        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }

        private void OnEnable()
        {
            if (m_Character == null) Awake();
            m_Character.Combat.Targets.EventChangeTarget += OnTargetChanged;
        }

        private void OnDisable()
        {
            if (m_Character != null) m_Character.Combat.Targets.EventChangeTarget -= OnTargetChanged;
        }

        private void OnTargetChanged(GameObject target)
        {
            if (m_Applying || m_NetworkCharacter.Role == NetworkCharacter.NetworkRole.None) return;
            uint id = target != null ? target.GetComponentInParent<NetworkCharacter>()?.NetworkId ?? 0 : 0;
            // Native selection is an intent. Restore confirmed state until authority accepts it.
            ResolveTarget();
            if (target != null && id == 0) return;
            if (!m_NetworkCharacter.IsOwnerInstance) return;
            NetworkCiTrace.Log("targeting", "native-selection-request", m_NetworkCharacter.NetworkId, 0, $"target={id}", this);
            m_NetworkCharacter.MotionController?.RequestCombatTarget(id);
        }

        private void Update()
        {
            if (m_NetworkCharacter.Role == NetworkCharacter.NetworkRole.None) return;
            if (m_NetworkCharacter.IsServerInstance && m_TargetId != 0 && !CanSelect(m_TargetId))
            {
                // Authority-owned cleanup is not a client request for an unowned actor.
                m_NetworkCharacter.MotionController?.ClearInvalidCombatTarget();
            }
            ResolveTarget(); // Also resolves snapshots that arrived before the selected actor spawned.
        }

        public bool CanSelect(uint targetId)
        {
            if (m_Character == null || m_Character.IsDead || !m_NetworkCharacter.IsPlayerOwnedActor ||
                !m_NetworkCharacter.HasAuthenticatedPlayerOwner) return false;
            if (targetId == 0) return true;
            Character target = NetworkTransportBridge.Active?.ResolveCharacter(targetId);
            NetworkCharacter actor = target != null ? target.GetComponent<NetworkCharacter>() : null;
            return actor != null && actor != m_NetworkCharacter && actor.NetworkId == targetId &&
                   target.isActiveAndEnabled && !target.IsDead &&
                   (actor.IsServerAuthoritativeNPC || (actor.IsPlayerOwnedActor && actor.HasAuthenticatedPlayerOwner)) &&
                   (target.transform.position - transform.position).sqrMagnitude <= m_MaxTargetDistance * m_MaxTargetDistance;
        }

        internal NetworkMotionCommand Commit(uint targetId, ushort sequence)
        {
            m_TargetId = targetId;
            m_HasConfirmedState = true;
            if (++m_Revision == 0) m_Revision = 1;
            ResolveTarget();
            NetworkCiTrace.Log("targeting", "authority-commit", m_NetworkCharacter.NetworkId, sequence, $"target={m_TargetId} revision={m_Revision}", this);
            return Capture(sequence);
        }

        public NetworkMotionCommand Capture(ushort sequence = 0) => new NetworkMotionCommand
        {
            commandType = NetworkMotionCommandType.SetCombatTarget,
            targetNetworkId = m_TargetId,
            dataX = unchecked((int)m_Revision),
            sequenceNumber = sequence
        };

        public void ApplyConfirmed(NetworkMotionCommand command)
        {
            uint revision = unchecked((uint)command.dataX);
            if (command.commandType != NetworkMotionCommandType.SetCombatTarget || revision == 0) return;
            if (m_HasConfirmedState && revision != m_Revision && unchecked((int)(revision - m_Revision)) <= 0) return;
            if (m_HasConfirmedState && revision == m_Revision && command.targetNetworkId != m_TargetId) return;
            m_HasConfirmedState = true;
            m_Revision = revision;
            m_TargetId = command.targetNetworkId;
            ResolveTarget();
            NetworkCiTrace.Log("targeting", "confirmed-apply", m_NetworkCharacter.NetworkId, command.sequenceNumber, $"target={m_TargetId} revision={m_Revision}", this);
        }

        private void ResolveTarget()
        {
            m_Applying = true;
            try
            {
                m_Character.Combat.Targets.Primary = m_TargetId == 0
                    ? null : NetworkTransportBridge.Active?.ResolveCharacter(m_TargetId)?.gameObject;
            }
            finally { m_Applying = false; }
        }
    }
}
