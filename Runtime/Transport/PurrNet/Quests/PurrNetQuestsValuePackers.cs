#if GC2_QUESTS
using Arawn.GameCreator2.Networking.Quests;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Quests.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetQuestsValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkQuestRequest));
            Hasher.PrepareType(typeof(NetworkQuestResponse));
            Hasher.PrepareType(typeof(NetworkQuestBroadcast));
            Hasher.PrepareType(typeof(NetworkQuestSnapshotEntry));
            Hasher.PrepareType(typeof(NetworkTaskSnapshotEntry));
            Hasher.PrepareType(typeof(NetworkQuestsSnapshot));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkQuestRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write((byte)value.Action);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.Value);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkQuestRequest value)
        {
            byte shareMode = 0;
            byte action = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref action);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.Value);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
            value.Action = (QuestActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkQuestResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write((byte)value.Action);
            packer.Write(value.Authorized);
            packer.Write(value.Applied);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.QuestState);
            packer.Write(value.TaskState);
            packer.Write(value.IsTracking);
            packer.Write(value.TaskValue);
            packer.Write(value.Error);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkQuestResponse value)
        {
            byte shareMode = 0;
            byte action = 0;
            byte rejection = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref action);
            packer.Read(ref value.Authorized);
            packer.Read(ref value.Applied);
            packer.Read(ref rejection);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.QuestState);
            packer.Read(ref value.TaskState);
            packer.Read(ref value.IsTracking);
            packer.Read(ref value.TaskValue);
            packer.Read(ref value.Error);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
            value.Action = (QuestActionType)action;
            value.RejectionReason = (QuestRejectionReason)rejection;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkQuestBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write((byte)value.Action);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.QuestState);
            packer.Write(value.TaskState);
            packer.Write(value.IsTracking);
            packer.Write(value.TaskValue);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkQuestBroadcast value)
        {
            byte shareMode = 0;
            byte action = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref action);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.QuestState);
            packer.Read(ref value.TaskState);
            packer.Read(ref value.IsTracking);
            packer.Read(ref value.TaskValue);
            packer.Read(ref value.ServerTime);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
            value.Action = (QuestActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkQuestSnapshotEntry value)
        {
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.State);
            packer.Write(value.IsTracking);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkQuestSnapshotEntry value)
        {
            byte shareMode = 0;

            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.State);
            packer.Read(ref value.IsTracking);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTaskSnapshotEntry value)
        {
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.State);
            packer.Write(value.Value);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTaskSnapshotEntry value)
        {
            byte shareMode = 0;

            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.State);
            packer.Read(ref value.Value);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkQuestsSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ServerTime);
            packer.WriteList(value.QuestEntries);
            packer.WriteList(value.TaskEntries);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkQuestsSnapshot value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ServerTime);
            packer.ReadArray(ref value.QuestEntries);
            packer.ReadArray(ref value.TaskEntries);
        }
    }
}
#endif
