using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Stable module identifiers used by the Fusion transport wire protocol.
    /// Values are deliberately grouped so optional packages can evolve independently.
    /// </summary>
    public static class FusionModuleIds
    {
        public const ushort Core = 10;
        public const ushort Variables = 11;
        public const ushort AnimationMotion = 12;
        public const ushort Stats = 20;
        public const ushort Inventory = 21;
        public const ushort Melee = 30;
        public const ushort Shooter = 31;
        public const ushort Quests = 40;
        public const ushort Dialogue = 41;
        public const ushort Traversal = 50;
        public const ushort Abilities = 51;
    }

    /// <summary>
    /// Deterministic field serializer for the transport-safe GC2 message structs.
    /// Public instance fields are encoded in declaration order, with explicit compatibility
    /// overrides for legacy PurrNet packers whose wire order differs from declaration order.
    /// Arrays use an unsigned 16-bit count and strings use a signed UTF-8 byte count
    /// (-1 means null).
    /// </summary>
    public static class FusionWireSerializer
    {
        private const int MaxStringBytes = 1024 * 1024;
        private const int MaxDepth = 32;

        private static readonly Dictionary<Type, FieldInfo[]> FieldCache = new();
        private static readonly object FieldCacheLock = new();
        private static readonly UTF8Encoding Utf8 = new(false, true);

        public static byte[] Serialize<T>(T value)
        {
            using var stream = new MemoryStream(256);
            using var writer = new BinaryWriter(stream, Utf8, true);
            WriteValue(writer, typeof(T), value, 0);
            writer.Flush();
            return stream.ToArray();
        }

        public static T Deserialize<T>(ReadOnlyMemory<byte> payload)
        {
            using var stream = new MemoryStream(payload.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            object value = ReadValue(reader, typeof(T), 0);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException(
                    $"Fusion payload for {typeof(T).Name} has {stream.Length - stream.Position} trailing bytes.");
            }

            return (T)value;
        }

        private static void WriteValue(BinaryWriter writer, Type type, object value, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Fusion value nesting is too deep.");

            Type nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null)
            {
                bool hasValue = value != null;
                writer.Write(hasValue);
                if (hasValue) WriteValue(writer, nullableType, value, depth + 1);
                return;
            }

            if (type == typeof(string))
            {
                WriteString(writer, (string)value);
                return;
            }

            if (type.IsArray)
            {
                Array array = (Array)value;
                int length = array?.Length ?? 0;
                if (length > ushort.MaxValue)
                {
                    throw new InvalidDataException(
                        $"Fusion array {type.Name} contains {length} entries; maximum is {ushort.MaxValue}.");
                }

                writer.Write((ushort)length);
                if (array == null) return;
                Type elementType = type.GetElementType();
                for (int i = 0; i < length; i++)
                {
                    WriteValue(writer, elementType, array.GetValue(i), depth + 1);
                }

                return;
            }

            if (!type.IsValueType)
            {
                bool present = value != null;
                writer.Write(present);
                if (!present) return;
            }

            if (type.IsEnum)
            {
                WriteEnum(writer, type, value);
                return;
            }

            if (WritePrimitive(writer, type, value)) return;

            if (type == typeof(Vector2))
            {
                Vector2 vector = (Vector2)value;
                writer.Write(vector.x);
                writer.Write(vector.y);
                return;
            }

            if (type == typeof(Vector3))
            {
                Vector3 vector = (Vector3)value;
                writer.Write(vector.x);
                writer.Write(vector.y);
                writer.Write(vector.z);
                return;
            }

            if (type == typeof(Vector2Int))
            {
                Vector2Int vector = (Vector2Int)value;
                writer.Write(vector.x);
                writer.Write(vector.y);
                return;
            }

            if (type == typeof(Vector3Int))
            {
                Vector3Int vector = (Vector3Int)value;
                writer.Write(vector.x);
                writer.Write(vector.y);
                writer.Write(vector.z);
                return;
            }

            if (type == typeof(Vector4))
            {
                Vector4 vector = (Vector4)value;
                writer.Write(vector.x);
                writer.Write(vector.y);
                writer.Write(vector.z);
                writer.Write(vector.w);
                return;
            }

            if (type == typeof(Quaternion))
            {
                Quaternion quaternion = (Quaternion)value;
                writer.Write(quaternion.x);
                writer.Write(quaternion.y);
                writer.Write(quaternion.z);
                writer.Write(quaternion.w);
                return;
            }

            if (type == typeof(Color))
            {
                Color color = (Color)value;
                writer.Write(color.r);
                writer.Write(color.g);
                writer.Write(color.b);
                writer.Write(color.a);
                return;
            }

            if (type == typeof(Color32))
            {
                Color32 color = (Color32)value;
                writer.Write(color.r);
                writer.Write(color.g);
                writer.Write(color.b);
                writer.Write(color.a);
                return;
            }

            if (type == typeof(Guid))
            {
                writer.Write(((Guid)value).ToByteArray());
                return;
            }

            if (type == typeof(DateTime))
            {
                writer.Write(((DateTime)value).ToBinary());
                return;
            }

            // The established GC2 variable wire contract intentionally writes server time before
            // the declaration-ordered Changes field. Keep this explicit exception compatible
            // with the PurrNet value packer.
            if (type == typeof(NetworkVariableSnapshot))
            {
                NetworkVariableSnapshot snapshot = (NetworkVariableSnapshot)value;
                writer.Write(snapshot.ServerTime);
                WriteValue(
                    writer,
                    typeof(NetworkVariableBroadcast[]),
                    snapshot.Changes,
                    depth + 1);
                return;
            }

            FieldInfo[] fields = GetSerializableFields(type);
            if (fields.Length == 0)
            {
                throw new InvalidDataException(
                    $"Fusion wire type {type.FullName} has no supported public fields.");
            }

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                WriteValue(writer, field.FieldType, field.GetValue(value), depth + 1);
            }
        }

        private static object ReadValue(BinaryReader reader, Type type, int depth)
        {
            if (depth > MaxDepth) throw new InvalidDataException("Fusion value nesting is too deep.");

            Type nullableType = Nullable.GetUnderlyingType(type);
            if (nullableType != null)
            {
                return reader.ReadBoolean()
                    ? Activator.CreateInstance(type, ReadValue(reader, nullableType, depth + 1))
                    : null;
            }

            if (type == typeof(string)) return ReadString(reader);

            if (type.IsArray)
            {
                ushort count = reader.ReadUInt16();
                Type elementType = type.GetElementType();
                Array array = Array.CreateInstance(elementType, count);
                for (int i = 0; i < count; i++)
                {
                    array.SetValue(ReadValue(reader, elementType, depth + 1), i);
                }

                return array;
            }

            if (!type.IsValueType && !reader.ReadBoolean()) return null;
            if (type.IsEnum) return ReadEnum(reader, type);

            object primitive = ReadPrimitive(reader, type);
            if (primitive != null) return primitive;

            if (type == typeof(Vector2))
                return new Vector2(reader.ReadSingle(), reader.ReadSingle());
            if (type == typeof(Vector3))
                return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (type == typeof(Vector2Int))
                return new Vector2Int(reader.ReadInt32(), reader.ReadInt32());
            if (type == typeof(Vector3Int))
                return new Vector3Int(
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            if (type == typeof(Vector4))
                return new Vector4(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (type == typeof(Quaternion))
                return new Quaternion(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (type == typeof(Color))
                return new Color(
                    reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (type == typeof(Color32))
                return new Color32(
                    reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
            if (type == typeof(Guid)) return new Guid(ReadExactBytes(reader, 16));
            if (type == typeof(DateTime)) return DateTime.FromBinary(reader.ReadInt64());
            if (type == typeof(NetworkVariableSnapshot))
            {
                return new NetworkVariableSnapshot
                {
                    ServerTime = reader.ReadSingle(),
                    Changes = (NetworkVariableBroadcast[])ReadValue(
                        reader, typeof(NetworkVariableBroadcast[]), depth + 1)
                };
            }

            FieldInfo[] fields = GetSerializableFields(type);
            if (fields.Length == 0)
            {
                throw new InvalidDataException(
                    $"Fusion wire type {type.FullName} has no supported public fields.");
            }

            object value = Activator.CreateInstance(type);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                field.SetValue(value, ReadValue(reader, field.FieldType, depth + 1));
            }

            return value;
        }

        private static bool WritePrimitive(BinaryWriter writer, Type type, object value)
        {
            if (type == typeof(bool)) writer.Write((bool)value);
            else if (type == typeof(byte)) writer.Write((byte)value);
            else if (type == typeof(sbyte)) writer.Write((sbyte)value);
            else if (type == typeof(short)) writer.Write((short)value);
            else if (type == typeof(ushort)) writer.Write((ushort)value);
            else if (type == typeof(int)) writer.Write((int)value);
            else if (type == typeof(uint)) writer.Write((uint)value);
            else if (type == typeof(long)) writer.Write((long)value);
            else if (type == typeof(ulong)) writer.Write((ulong)value);
            else if (type == typeof(float)) writer.Write((float)value);
            else if (type == typeof(double)) writer.Write((double)value);
            else if (type == typeof(decimal)) writer.Write((decimal)value);
            else if (type == typeof(char)) writer.Write((char)value);
            else return false;
            return true;
        }

        private static object ReadPrimitive(BinaryReader reader, Type type)
        {
            if (type == typeof(bool)) return reader.ReadBoolean();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadInt16();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(float)) return reader.ReadSingle();
            if (type == typeof(double)) return reader.ReadDouble();
            if (type == typeof(decimal)) return reader.ReadDecimal();
            if (type == typeof(char)) return reader.ReadChar();
            return null;
        }

        private static void WriteEnum(BinaryWriter writer, Type enumType, object value)
        {
            Type underlying = Enum.GetUnderlyingType(enumType);
            WritePrimitive(writer, underlying, Convert.ChangeType(value, underlying));
        }

        private static object ReadEnum(BinaryReader reader, Type enumType)
        {
            object raw = ReadPrimitive(reader, Enum.GetUnderlyingType(enumType));
            return Enum.ToObject(enumType, raw);
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            if (value == null)
            {
                writer.Write(-1);
                return;
            }

            byte[] bytes = Utf8.GetBytes(value);
            if (bytes.Length > MaxStringBytes)
                throw new InvalidDataException($"Fusion string contains {bytes.Length} bytes.");
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            if (length == -1) return null;
            if (length < 0 || length > MaxStringBytes)
                throw new InvalidDataException($"Invalid Fusion string length {length}.");
            return Utf8.GetString(ReadExactBytes(reader, length));
        }

        private static byte[] ReadExactBytes(BinaryReader reader, int count)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return bytes;
        }

        private static FieldInfo[] GetSerializableFields(Type type)
        {
            lock (FieldCacheLock)
            {
                if (FieldCache.TryGetValue(type, out FieldInfo[] cached)) return cached;

                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));

                // The established PurrNet variable snapshot wire format writes the timestamp
                // before the change collection, although the struct declares those fields in
                // the opposite order. Keep the Fusion field ordering transport-compatible.
                if (type.FullName ==
                    "Arawn.GameCreator2.Networking.NetworkVariableSnapshot")
                {
                    fields = fields
                        .OrderBy(field => field.Name == "ServerTime" ? 0 : 1)
                        .ToArray();
                }

                FieldCache[type] = fields;
                return fields;
            }
        }
    }

    /// <summary>
    /// Shared lifecycle and typed send helpers for GC2 Fusion module bridges.
    /// </summary>
    public abstract class FusionModuleTransportBridgeBase : MonoBehaviour,
        IFusionFullSnapshotProducer
    {
        [Header("Fusion")]
        [Tooltip("Fusion transport used for module routing. Leave empty to use the active bridge.")]
        [SerializeField] protected FusionTransportBridge m_TransportBridge;

        private FusionTransportBridge m_BoundBridge;

        protected abstract ushort ModuleId { get; }
        protected FusionTransportBridge TransportBridge => m_BoundBridge;
        public FusionTransportBridge BoundTransportBridge => m_BoundBridge;
        public ushort FullSnapshotModuleId => ModuleId;
        public virtual string FullSnapshotProducerName => GetType().Name;
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;

        public virtual void Configure(FusionTransportBridge transportBridge)
        {
            if (m_TransportBridge == transportBridge && m_BoundBridge == transportBridge) return;
            Unbind();
            m_TransportBridge = transportBridge;
            TryBind();
        }

        protected virtual void OnEnable()
        {
            TryBind();
            OnModuleEnabled();
        }

        protected virtual void Start()
        {
            TryBind();
            OnModuleStarted();
        }

        protected virtual void Update()
        {
            TryBind();
            OnModuleUpdate();
        }

        protected virtual void OnDisable()
        {
            OnModuleDisabled();
            Unbind();
        }

        protected virtual void OnModuleEnabled() { }
        protected virtual void OnModuleStarted() { }
        protected virtual void OnModuleUpdate() { }
        protected virtual void OnModuleDisabled() { }
        protected virtual void OnAuthorityChanged(bool isAuthority, uint authorityEpoch) { }
        protected abstract FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context);
        protected abstract void HandleModuleMessage(FusionModuleMessage message);

        public FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context)
        {
            if (context == null || context.ModuleId != ModuleId ||
                m_BoundBridge == null || context.TransportBridge != m_BoundBridge ||
                !m_BoundBridge.IsServer)
            {
                return context != null
                    ? context.Fail("The module bridge is not bound as the current authority.")
                    : default;
            }

            return ProduceFullSnapshotForClient(context);
        }

        protected bool SendToAuthority<T>(ushort messageType, T value, bool reliable = true)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsClient) return false;
            return bridge.SendModuleToAuthority(
                ModuleId, messageType, FusionWireSerializer.Serialize(value), reliable);
        }

        protected bool SendToClient<T>(
            uint clientId, ushort messageType, T value, bool reliable = true)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsServer) return false;
            return bridge.SendModuleToClient(
                clientId, ModuleId, messageType, FusionWireSerializer.Serialize(value), reliable);
        }

        protected void Broadcast<T>(ushort messageType, T value, bool reliable = true)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsServer) return;
            bridge.BroadcastModule(
                ModuleId, messageType, FusionWireSerializer.Serialize(value), reliable);
        }

        protected bool TryRead<T>(FusionModuleMessage message, out T value)
        {
            try
            {
                value = FusionWireSerializer.Deserialize<T>(message.Payload);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[{GetType().Name}] Rejected malformed module={message.ModuleId} " +
                    $"message={message.MessageType}: {exception.Message}",
                    this);
                value = default;
                return false;
            }
        }

        private void TryBind()
        {
            FusionTransportBridge candidate = m_TransportBridge;
            if (candidate == null)
                candidate = NetworkTransportBridge.Active as FusionTransportBridge;
            if (candidate == null)
                candidate = FindFirstObjectByType<FusionTransportBridge>(FindObjectsInactive.Include);

            if (candidate == m_BoundBridge) return;
            Unbind();
            if (candidate == null) return;

            if (!candidate.RegisterModuleHandler(ModuleId, HandleModuleMessage)) return;

            m_BoundBridge = candidate;
            if (!m_BoundBridge.RegisterFullSnapshotProducer(this))
            {
                m_BoundBridge.UnregisterModuleHandler(ModuleId, HandleModuleMessage);
                m_BoundBridge = null;
                return;
            }
            m_BoundBridge.AuthorityChanged += HandleAuthorityChanged;
            OnAuthorityChanged(m_BoundBridge.IsServer, m_BoundBridge.AuthorityEpoch);
        }

        private void Unbind()
        {
            if (m_BoundBridge == null) return;
            m_BoundBridge.UnregisterFullSnapshotProducer(this);
            m_BoundBridge.UnregisterModuleHandler(ModuleId, HandleModuleMessage);
            m_BoundBridge.AuthorityChanged -= HandleAuthorityChanged;
            m_BoundBridge = null;
        }

        private void HandleAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            OnAuthorityChanged(isAuthority, authorityEpoch);
        }
    }
}
