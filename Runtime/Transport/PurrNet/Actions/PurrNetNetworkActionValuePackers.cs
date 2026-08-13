using System;
using System.Buffers;
using System.IO;
using System.Text;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetNetworkActionValuePackers
    {
        public const int MaxSnapshotEntries = 4096;

        private const int MaxActionIdUtf8Bytes =
            NetworkActionManager.MaxActionIdCharacters * 3;
        private const int MaxPayloadStringUtf8Bytes =
            NetworkActionManager.MaxPayloadStringCharacters * 3;

        private static readonly UTF8Encoding StrictUtf8 =
            new(false, true);

        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkActionPayload));
            Hasher.PrepareType(typeof(NetworkActionRequest));
            Hasher.PrepareType(typeof(NetworkActionResponse));
            Hasher.PrepareType(typeof(NetworkActionBroadcast));
            Hasher.PrepareType(typeof(NetworkActionSnapshot));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkActionPayload value)
        {
            packer.Write((byte)value.Type);
            packer.Write(value.BooleanValue);
            packer.Write(value.NumberValue);
            WriteBoundedUtf8(
                packer,
                value.StringValue,
                NetworkActionManager.MaxPayloadStringCharacters,
                MaxPayloadStringUtf8Bytes,
                nameof(NetworkActionPayload.StringValue));
            packer.Write(value.Vector3Value);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkActionPayload value)
        {
            byte type = 0;
            packer.Read(ref type);
            packer.Read(ref value.BooleanValue);
            packer.Read(ref value.NumberValue);
            value.StringValue = ReadBoundedUtf8(
                packer,
                NetworkActionManager.MaxPayloadStringCharacters,
                MaxPayloadStringUtf8Bytes,
                nameof(NetworkActionPayload.StringValue));
            packer.Read(ref value.Vector3Value);
            value.Type = (NetworkActionPayloadType)type;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkActionRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.EndpointHash);
            packer.Write(value.ActionHash);
            WriteBoundedUtf8(
                packer,
                value.ActionId,
                NetworkActionManager.MaxActionIdCharacters,
                MaxActionIdUtf8Bytes,
                nameof(NetworkActionRequest.ActionId));
            packer.Write(value.SchemaVersion);
            packer.Write(value.Payload);
            packer.Write(value.ExpectedRevision);
            packer.Write(value.ClientTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkActionRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.EndpointHash);
            packer.Read(ref value.ActionHash);
            value.ActionId = ReadActionId(packer);
            packer.Read(ref value.SchemaVersion);
            packer.Read(ref value.Payload);
            packer.Read(ref value.ExpectedRevision);
            packer.Read(ref value.ClientTime);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkActionResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.EndpointHash);
            packer.Write(value.ActionHash);
            WriteBoundedUtf8(
                packer,
                value.ActionId,
                NetworkActionManager.MaxActionIdCharacters,
                MaxActionIdUtf8Bytes,
                nameof(NetworkActionRequest.ActionId));
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectReason);
            packer.Write(value.CanonicalPayload);
            packer.Write(value.Revision);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkActionResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.EndpointHash);
            packer.Read(ref value.ActionHash);
            value.ActionId = ReadActionId(packer);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.CanonicalPayload);
            packer.Read(ref value.Revision);
            packer.Read(ref value.ServerTime);
            value.RejectReason = (NetworkActionRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkActionBroadcast value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.EndpointHash);
            packer.Write(value.ActionHash);
            WriteBoundedUtf8(
                packer,
                value.ActionId,
                NetworkActionManager.MaxActionIdCharacters,
                MaxActionIdUtf8Bytes,
                nameof(NetworkActionRequest.ActionId));
            packer.Write(value.SchemaVersion);
            packer.Write((byte)value.EffectKind);
            packer.Write((byte)value.RecipientPolicy);
            packer.Write(value.Reliable);
            packer.Write(value.Payload);
            packer.Write(value.Revision);
            packer.Write(value.AuthorityEpoch);
            packer.Write(value.ServerTime);
            packer.Write(value.IsSnapshot);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkActionBroadcast value)
        {
            byte effect = 0;
            byte recipients = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.EndpointHash);
            packer.Read(ref value.ActionHash);
            value.ActionId = ReadActionId(packer);
            packer.Read(ref value.SchemaVersion);
            packer.Read(ref effect);
            packer.Read(ref recipients);
            packer.Read(ref value.Reliable);
            packer.Read(ref value.Payload);
            packer.Read(ref value.Revision);
            packer.Read(ref value.AuthorityEpoch);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.IsSnapshot);
            value.EffectKind = (NetworkActionEffectKind)effect;
            value.RecipientPolicy = (NetworkActionRecipientPolicy)recipients;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkActionSnapshot value)
        {
            NetworkActionBroadcast[] entries = value.Entries;
            packer.Write(entries != null);
            if (entries != null)
            {
                if (entries.Length > MaxSnapshotEntries)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(value),
                        entries.Length,
                        $"A Network Action snapshot cannot contain more than " +
                        $"{MaxSnapshotEntries} entries.");
                }

                packer.Write((ushort)entries.Length);
                for (int i = 0; i < entries.Length; i++)
                {
                    packer.Write(entries[i]);
                }
            }

            packer.Write(value.AuthorityEpoch);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkActionSnapshot value)
        {
            bool hasEntries = false;
            packer.Read(ref hasEntries);
            if (!hasEntries)
            {
                value.Entries = null;
            }
            else
            {
                ushort count = 0;
                packer.Read(ref count);
                if (count > MaxSnapshotEntries)
                {
                    throw new InvalidDataException(
                        $"Network Action snapshot entry count {count} exceeds the " +
                        $"maximum of {MaxSnapshotEntries}.");
                }

                value.Entries = new NetworkActionBroadcast[count];
                for (int i = 0; i < count; i++)
                {
                    packer.Read(ref value.Entries[i]);
                }
            }

            packer.Read(ref value.AuthorityEpoch);
            packer.Read(ref value.ServerTime);
        }

        private static string ReadActionId(BitPacker packer)
        {
            return ReadBoundedUtf8(
                packer,
                NetworkActionManager.MaxActionIdCharacters,
                MaxActionIdUtf8Bytes,
                nameof(NetworkActionRequest.ActionId));
        }

        private static void WriteBoundedUtf8(
            BitPacker packer,
            string value,
            int maxCharacters,
            int maxBytes,
            string fieldName)
        {
            packer.Write(value != null);
            if (value == null) return;

            if (value.Length > maxCharacters)
            {
                throw new ArgumentOutOfRangeException(
                    fieldName,
                    value.Length,
                    $"The value cannot exceed {maxCharacters} UTF-16 characters.");
            }

            int byteCount;
            try
            {
                byteCount = StrictUtf8.GetByteCount(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new ArgumentException(
                    "The value contains invalid Unicode and cannot be encoded as UTF-8.",
                    fieldName,
                    exception);
            }

            if (byteCount > maxBytes || byteCount > ushort.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    fieldName,
                    byteCount,
                    $"The UTF-8 representation cannot exceed {maxBytes} bytes.");
            }

            packer.Write((ushort)byteCount);
            if (byteCount == 0) return;

            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = StrictUtf8.GetBytes(
                    value,
                    0,
                    value.Length,
                    rented,
                    0);
                packer.WriteBytes(rented.AsSpan(0, written));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        private static string ReadBoundedUtf8(
            BitPacker packer,
            int maxCharacters,
            int maxBytes,
            string fieldName)
        {
            bool hasValue = false;
            packer.Read(ref hasValue);
            if (!hasValue) return null;

            ushort byteCount = 0;
            packer.Read(ref byteCount);
            if (byteCount > maxBytes)
            {
                throw new InvalidDataException(
                    $"{fieldName} UTF-8 length {byteCount} exceeds the maximum of " +
                    $"{maxBytes} bytes.");
            }

            if (byteCount == 0) return string.Empty;

            byte[] rented = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                Span<byte> bytes = rented.AsSpan(0, byteCount);
                packer.ReadBytes(bytes);

                string result;
                try
                {
                    result = StrictUtf8.GetString(rented, 0, byteCount);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException(
                        $"{fieldName} contains malformed UTF-8 data.",
                        exception);
                }

                if (result.Length > maxCharacters)
                {
                    throw new InvalidDataException(
                        $"{fieldName} contains {result.Length} UTF-16 characters, " +
                        $"which exceeds the maximum of {maxCharacters}.");
                }

                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
