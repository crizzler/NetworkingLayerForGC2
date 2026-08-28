using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkBotSlotOccupantType : byte
    {
        Vacant = 0,
        Bot = 1,
        PendingHuman = 2,
        Human = 3
    }

    [Serializable]
    public sealed class NetworkBotSlotDefinition
    {
        [SerializeField] private string m_StableSlotId = "combatant-slot";
        [SerializeField] private Transform m_Anchor;
        [SerializeField] private GameObject m_BotPrefab;

        public string StableSlotId => m_StableSlotId;
        public Transform Anchor => m_Anchor;
        public GameObject BotPrefab => m_BotPrefab;
        public uint SlotIdHash
        {
            get
            {
                uint hash = unchecked((uint)StableHashUtility.GetStableHash(
                    m_StableSlotId ?? string.Empty));
                return hash == 0 ? 1u : hash;
            }
        }
    }

    [Serializable]
    public struct NetworkBotSlotState
    {
        public uint SlotIdHash;
        public NetworkBotSlotOccupantType OccupantType;
        public uint HumanClientId;
        public uint ActorNetworkId;
        public Vector3 Position;
        public Quaternion Rotation;
        public uint Revision;
    }

    public readonly struct NetworkBotSlotReservation
    {
        internal NetworkBotSlotReservation(
            int token,
            int slotIndex,
            uint slotIdHash,
            uint humanClientId,
            Vector3 position,
            Quaternion rotation)
        {
            Token = token;
            SlotIndex = slotIndex;
            SlotIdHash = slotIdHash;
            HumanClientId = humanClientId;
            Position = position;
            Rotation = rotation;
        }

        internal int Token { get; }
        internal int SlotIndex { get; }
        public uint SlotIdHash { get; }
        public uint HumanClientId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public bool IsValid => Token != 0 && SlotIdHash != 0;
    }
}
