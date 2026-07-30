using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetCoreValuePackers
    {
        [RegisterPackers]
        private static void Register()
        {
            Hasher.PrepareType(typeof(NetworkRagdollRequest));
            Hasher.PrepareType(typeof(NetworkRagdollResponse));
            Hasher.PrepareType(typeof(NetworkRagdollBroadcast));
            Hasher.PrepareType(typeof(NetworkPropRequest));
            Hasher.PrepareType(typeof(NetworkPropResponse));
            Hasher.PrepareType(typeof(NetworkPropBroadcast));
            Hasher.PrepareType(typeof(NetworkPropAttachmentState));
            Hasher.PrepareType(typeof(NetworkInvincibilityRequest));
            Hasher.PrepareType(typeof(NetworkInvincibilityResponse));
            Hasher.PrepareType(typeof(NetworkInvincibilityBroadcast));
            Hasher.PrepareType(typeof(NetworkPoiseRequest));
            Hasher.PrepareType(typeof(NetworkPoiseResponse));
            Hasher.PrepareType(typeof(NetworkPoiseBroadcast));
            Hasher.PrepareType(typeof(NetworkBusyRequest));
            Hasher.PrepareType(typeof(NetworkBusyResponse));
            Hasher.PrepareType(typeof(NetworkBusyBroadcast));
            Hasher.PrepareType(typeof(NetworkInteractionRequest));
            Hasher.PrepareType(typeof(NetworkInteractionResponse));
            Hasher.PrepareType(typeof(NetworkInteractionBroadcast));
            Hasher.PrepareType(typeof(NetworkInteractionFocusBroadcast));
            Hasher.PrepareType(typeof(NetworkCoreState));
            Hasher.PrepareType(typeof(NetworkCoreSnapshot));
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkRagdollRequest v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.CharacterNetworkId); p.Write(v.ClientTime); p.Write((byte)v.ActionType);
            WriteVector3(p, v.Force); WriteVector3(p, v.ForcePoint);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkRagdollRequest v)
        {
            byte action = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.ClientTime); p.Read(ref action);
            ReadVector3(p, ref v.Force); ReadVector3(p, ref v.ForcePoint);
            v.ActionType = (RagdollActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkRagdollResponse v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.Approved); p.Write((byte)v.RejectReason);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkRagdollResponse v)
        {
            byte reason = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.Approved); p.Read(ref reason); v.RejectReason = (RagdollRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkRagdollBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write((byte)v.ActionType); p.Write(v.ServerTime);
            WriteVector3(p, v.Force); WriteVector3(p, v.ForcePoint);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkRagdollBroadcast v)
        {
            byte action = 0;
            p.Read(ref v.CharacterNetworkId); p.Read(ref action); p.Read(ref v.ServerTime);
            ReadVector3(p, ref v.Force); ReadVector3(p, ref v.ForcePoint);
            v.ActionType = (RagdollActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPropRequest v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.CharacterNetworkId); p.Write((byte)v.ActionType); p.Write(v.PropHash);
            p.Write(v.PropInstanceId); p.Write(v.BoneHash); WriteVector3(p, v.LocalPosition);
            p.Write(v.RotationX); p.Write(v.RotationY); p.Write(v.RotationZ);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPropRequest v)
        {
            byte action = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.CharacterNetworkId); p.Read(ref action); p.Read(ref v.PropHash);
            p.Read(ref v.PropInstanceId); p.Read(ref v.BoneHash); ReadVector3(p, ref v.LocalPosition);
            p.Read(ref v.RotationX); p.Read(ref v.RotationY); p.Read(ref v.RotationZ);
            v.ActionType = (PropActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPropResponse v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.Approved); p.Write((byte)v.RejectReason); p.Write(v.PropInstanceId);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPropResponse v)
        {
            byte reason = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.Approved); p.Read(ref reason); p.Read(ref v.PropInstanceId);
            v.RejectReason = (PropRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPropBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write((byte)v.ActionType); p.Write(v.PropHash);
            p.Write(v.BoneHash); p.Write(v.PropInstanceId); WriteVector3(p, v.LocalPosition);
            p.Write(v.RotationX); p.Write(v.RotationY); p.Write(v.RotationZ);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPropBroadcast v)
        {
            byte action = 0;
            p.Read(ref v.CharacterNetworkId); p.Read(ref action); p.Read(ref v.PropHash);
            p.Read(ref v.BoneHash); p.Read(ref v.PropInstanceId); ReadVector3(p, ref v.LocalPosition);
            p.Read(ref v.RotationX); p.Read(ref v.RotationY); p.Read(ref v.RotationZ);
            v.ActionType = (PropActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPropAttachmentState v)
        {
            p.Write(v.CharacterNetworkId); p.Write(v.PropInstanceId); p.Write(v.PropHash);
            p.Write(v.BoneHash); WriteVector3(p, v.LocalPosition);
            p.Write(v.RotationX); p.Write(v.RotationY); p.Write(v.RotationZ);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPropAttachmentState v)
        {
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.PropInstanceId); p.Read(ref v.PropHash);
            p.Read(ref v.BoneHash); ReadVector3(p, ref v.LocalPosition);
            p.Read(ref v.RotationX); p.Read(ref v.RotationY); p.Read(ref v.RotationZ);
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInvincibilityRequest v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.CharacterNetworkId); p.Write(v.Duration); p.Write(v.ClientTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInvincibilityRequest v)
        {
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.Duration); p.Read(ref v.ClientTime);
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInvincibilityResponse v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.Approved); p.Write((byte)v.RejectReason); p.Write(v.ApprovedDuration);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInvincibilityResponse v)
        {
            byte reason = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.Approved); p.Read(ref reason); p.Read(ref v.ApprovedDuration);
            v.RejectReason = (InvincibilityRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInvincibilityBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write(v.IsInvincible); p.Write(v.StartTime); p.Write(v.Duration);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInvincibilityBroadcast v)
        {
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.IsInvincible);
            p.Read(ref v.StartTime); p.Read(ref v.Duration);
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPoiseRequest v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.CharacterNetworkId); p.Write((byte)v.ActionType); p.Write(v.Value); p.Write(v.ClientTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPoiseRequest v)
        {
            byte action = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.CharacterNetworkId); p.Read(ref action); p.Read(ref v.Value); p.Read(ref v.ClientTime);
            v.ActionType = (PoiseActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPoiseResponse v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId); p.Write(v.Approved);
            p.Write((byte)v.RejectReason); p.Write(v.CurrentPoise); p.Write(v.IsBroken);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPoiseResponse v)
        {
            byte reason = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.Approved); p.Read(ref reason); p.Read(ref v.CurrentPoise); p.Read(ref v.IsBroken);
            v.RejectReason = (PoiseRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkPoiseBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write(v.CurrentPoise); p.Write(v.MaximumPoise);
            p.Write(v.IsBroken); p.Write(v.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkPoiseBroadcast v)
        {
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.CurrentPoise); p.Read(ref v.MaximumPoise);
            p.Read(ref v.IsBroken); p.Read(ref v.ServerTime);
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkBusyRequest v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.CharacterNetworkId); p.Write((byte)v.Limbs); p.Write(v.SetBusy); p.Write(v.Timeout);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkBusyRequest v)
        {
            byte limbs = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.CharacterNetworkId); p.Read(ref limbs); p.Read(ref v.SetBusy); p.Read(ref v.Timeout);
            v.Limbs = (BusyLimbs)limbs;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkBusyResponse v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.Approved); p.Write((byte)v.RejectReason);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkBusyResponse v)
        {
            byte reason = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.Approved); p.Read(ref reason); v.RejectReason = (BusyRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkBusyBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write((byte)v.CurrentBusyLimbs); p.Write(v.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkBusyBroadcast v)
        {
            byte limbs = 0;
            p.Read(ref v.CharacterNetworkId); p.Read(ref limbs); p.Read(ref v.ServerTime);
            v.CurrentBusyLimbs = (BusyLimbs)limbs;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInteractionRequest v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.CharacterNetworkId); p.Write(v.TargetNetworkId); p.Write(v.TargetHash);
            WriteVector3(p, v.InteractionPosition); p.Write(v.ClientTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInteractionRequest v)
        {
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.TargetNetworkId); p.Read(ref v.TargetHash);
            ReadVector3(p, ref v.InteractionPosition); p.Read(ref v.ClientTime);
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInteractionResponse v)
        {
            p.Write(v.RequestId); p.Write(v.ActorNetworkId); p.Write(v.CorrelationId);
            p.Write(v.Approved); p.Write((byte)v.RejectReason); p.Write(v.ResultData);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInteractionResponse v)
        {
            byte reason = 0;
            p.Read(ref v.RequestId); p.Read(ref v.ActorNetworkId); p.Read(ref v.CorrelationId);
            p.Read(ref v.Approved); p.Read(ref reason); p.Read(ref v.ResultData);
            v.RejectReason = (InteractionRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInteractionBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write(v.TargetNetworkId); p.Write(v.TargetHash);
            p.Write((byte)v.InteractionType); p.Write(v.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInteractionBroadcast v)
        {
            byte type = 0;
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.TargetNetworkId); p.Read(ref v.TargetHash);
            p.Read(ref type); p.Read(ref v.ServerTime); v.InteractionType = (InteractionType)type;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkInteractionFocusBroadcast v)
        {
            p.Write(v.CharacterNetworkId); p.Write(v.TargetNetworkId); p.Write(v.IsFocus);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkInteractionFocusBroadcast v)
        {
            p.Read(ref v.CharacterNetworkId); p.Read(ref v.TargetNetworkId); p.Read(ref v.IsFocus);
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkCoreState v)
        {
            p.Write(v.CharacterNetworkId); p.Write((byte)v.DeltaFlags); p.Write(v.IsRagdoll);
            p.Write(v.IsInvincible); p.Write(v.InvincibilityEndTime); p.Write(v.CurrentPoise);
            p.Write(v.MaximumPoise); p.Write(v.IsPoiseBroken); p.Write((byte)v.BusyLimbs);
            p.Write(v.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkCoreState v)
        {
            byte flags = 0; byte limbs = 0;
            p.Read(ref v.CharacterNetworkId); p.Read(ref flags); p.Read(ref v.IsRagdoll);
            p.Read(ref v.IsInvincible); p.Read(ref v.InvincibilityEndTime); p.Read(ref v.CurrentPoise);
            p.Read(ref v.MaximumPoise); p.Read(ref v.IsPoiseBroken); p.Read(ref limbs);
            p.Read(ref v.ServerTime); v.DeltaFlags = (CoreStateDeltaFlags)flags; v.BusyLimbs = (BusyLimbs)limbs;
        }

        [UsedByIL]
        public static void Write(this BitPacker p, NetworkCoreSnapshot v)
        {
            p.Write(v.State);
            int length = v.Props?.Length ?? 0;
            ushort count = (ushort)Mathf.Min(length, ushort.MaxValue);
            p.Write(count);
            for (int i = 0; i < count; i++) p.Write(v.Props[i]);
        }

        [UsedByIL]
        public static void Read(this BitPacker p, ref NetworkCoreSnapshot v)
        {
            p.Read(ref v.State);
            ushort count = 0;
            p.Read(ref count);
            v.Props = count == 0
                ? System.Array.Empty<NetworkPropAttachmentState>()
                : new NetworkPropAttachmentState[count];
            for (int i = 0; i < count; i++) p.Read(ref v.Props[i]);
        }

        private static void WriteVector3(BitPacker p, Vector3 v)
        {
            p.Write(v.x); p.Write(v.y); p.Write(v.z);
        }

        private static void ReadVector3(BitPacker p, ref Vector3 v)
        {
            p.Read(ref v.x); p.Read(ref v.y); p.Read(ref v.z);
        }
    }
}
