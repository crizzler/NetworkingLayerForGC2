using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby
{
    internal enum PurrNetLanPacketKind : byte
    {
        Query = 1,
        Advertisement = 2
    }

    internal readonly struct PurrNetLanAdvertisement
    {
        public PurrNetLanAdvertisement(
            Guid sessionId,
            string sessionName,
            string productId,
            string buildId,
            int protocolVersion,
            ushort gamePort,
            int playerCount,
            int maxPlayers,
            bool isOpen,
            bool isVisible)
        {
            SessionId = sessionId;
            SessionName = sessionName ?? string.Empty;
            ProductId = productId ?? string.Empty;
            BuildId = buildId ?? string.Empty;
            ProtocolVersion = protocolVersion;
            GamePort = gamePort;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            IsOpen = isOpen;
            IsVisible = isVisible;
        }

        public Guid SessionId { get; }
        public string SessionName { get; }
        public string ProductId { get; }
        public string BuildId { get; }
        public int ProtocolVersion { get; }
        public ushort GamePort { get; }
        public int PlayerCount { get; }
        public int MaxPlayers { get; }
        public bool IsOpen { get; }
        public bool IsVisible { get; }
    }

    internal readonly struct PurrNetLanPacket
    {
        public PurrNetLanPacket(
            PurrNetLanPacketKind kind,
            PurrNetLanAdvertisement advertisement,
            IPEndPoint source)
        {
            Kind = kind;
            Advertisement = advertisement;
            Source = source;
        }

        public PurrNetLanPacketKind Kind { get; }
        public PurrNetLanAdvertisement Advertisement { get; }
        public IPEndPoint Source { get; }
    }

    /// <summary>
    /// Small, bounded UDP discovery protocol. Gameplay never travels through this
    /// socket. In particular, advertisements contain no host address: consumers
    /// must use the datagram's source endpoint so a sender cannot redirect joins.
    /// </summary>
    internal sealed class PurrNetLanDiscovery : IDisposable
    {
        internal const int MaximumDatagramBytes = 768;

        private const uint Magic = 0x424C3247; // "G2LB" when read as bytes.
        private const byte PacketVersion = 1;
        private const int MaximumNameBytes = 128;
        private const int MaximumProductBytes = 96;
        private const int MaximumBuildBytes = 96;
        private const int MaximumPacketsPerPoll = 32;

        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        private readonly byte[] m_ReceiveBuffer =
            new byte[MaximumDatagramBytes + 1];
        private Socket m_Socket;
        private ushort m_Port;

        public bool IsOpen => m_Socket != null;
        public ushort Port => m_Port;

        public bool Open(ushort port, out string error)
        {
            error = string.Empty;
            if (port == 0)
            {
                error = "The LAN discovery port must be greater than zero.";
                return false;
            }

            if (m_Socket != null && m_Port == port) return true;
            Dispose();

            try
            {
                var socket = new Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);
                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress,
                    true);
                socket.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.Broadcast,
                    true);
                socket.Blocking = false;
                socket.Bind(new IPEndPoint(IPAddress.Any, port));

                m_Socket = socket;
                m_Port = port;
                return true;
            }
            catch (Exception exception) when (
                exception is SocketException ||
                exception is ObjectDisposedException ||
                exception is NotSupportedException)
            {
                error = $"Could not open LAN discovery UDP port {port}: {exception.Message}";
                Dispose();
                return false;
            }
        }

        public bool SendQuery(out string error)
        {
            byte[] packet = EncodeQuery();
            return SendToDiscoveryTargets(packet, out error);
        }

        public bool Broadcast(
            PurrNetLanAdvertisement advertisement,
            out string error)
        {
            if (!TryEncodeAdvertisement(advertisement, out byte[] packet, out error))
                return false;

            return SendToDiscoveryTargets(packet, out error);
        }

        public bool Reply(
            PurrNetLanAdvertisement advertisement,
            IPEndPoint target,
            out string error)
        {
            if (target == null)
            {
                error = "The LAN discovery reply endpoint is missing.";
                return false;
            }

            if (!TryEncodeAdvertisement(advertisement, out byte[] packet, out error))
                return false;

            return Send(packet, target, out error);
        }

        public void Poll(Action<PurrNetLanPacket> onPacket)
        {
            if (m_Socket == null || onPacket == null) return;

            for (int i = 0; i < MaximumPacketsPerPoll; i++)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                int length;
                try
                {
                    length = m_Socket.ReceiveFrom(
                        m_ReceiveBuffer,
                        0,
                        m_ReceiveBuffer.Length,
                        SocketFlags.None,
                        ref remote);
                }
                catch (SocketException exception) when (
                    exception.SocketErrorCode == SocketError.WouldBlock ||
                    exception.SocketErrorCode == SocketError.TryAgain ||
                    exception.SocketErrorCode == SocketError.Interrupted)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // Discovery is best-effort. A later frame may receive normally.
                    break;
                }

                if (length <= 0 || length > MaximumDatagramBytes) continue;
                if (!(remote is IPEndPoint source)) continue;

                if (TryDecode(m_ReceiveBuffer, length, source, out PurrNetLanPacket packet))
                    onPacket(packet);
            }
        }

        public void Dispose()
        {
            Socket socket = m_Socket;
            m_Socket = null;
            m_Port = 0;
            if (socket == null) return;

            try
            {
                socket.Close();
            }
            catch (ObjectDisposedException)
            {
                // Already closed by platform shutdown.
            }
            catch (SocketException)
            {
                // Closing discovery must never prevent gameplay shutdown.
            }
        }

        private bool SendToDiscoveryTargets(byte[] packet, out string error)
        {
            error = string.Empty;
            bool sent = false;

            if (Send(
                    packet,
                    new IPEndPoint(IPAddress.Broadcast, m_Port),
                    out string broadcastError))
            {
                sent = true;
            }

            // Some desktop firewalls and virtual adapters do not loop global
            // broadcasts back to a second local player. This also makes local
            // two-build testing deterministic.
            if (Send(
                    packet,
                    new IPEndPoint(IPAddress.Loopback, m_Port),
                    out string loopbackError))
            {
                sent = true;
            }

            if (sent) return true;
            error = !string.IsNullOrEmpty(broadcastError)
                ? broadcastError
                : loopbackError;
            return false;
        }

        private bool Send(byte[] packet, IPEndPoint target, out string error)
        {
            error = string.Empty;
            if (m_Socket == null)
            {
                error = "The LAN discovery socket is not open.";
                return false;
            }

            try
            {
                m_Socket.SendTo(packet, 0, packet.Length, SocketFlags.None, target);
                return true;
            }
            catch (Exception exception) when (
                exception is SocketException ||
                exception is ObjectDisposedException)
            {
                error = $"LAN discovery send failed: {exception.Message}";
                return false;
            }
        }

        internal static byte[] EncodeQuery()
        {
            using var stream = new MemoryStream(8);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Magic);
            writer.Write(PacketVersion);
            writer.Write((byte)PurrNetLanPacketKind.Query);
            return stream.ToArray();
        }

        internal static bool TryEncodeAdvertisement(
            PurrNetLanAdvertisement advertisement,
            out byte[] packet,
            out string error)
        {
            packet = null;
            error = string.Empty;

            if (advertisement.SessionId == Guid.Empty)
            {
                error = "A LAN advertisement requires a non-empty session ID.";
                return false;
            }

            if (advertisement.GamePort == 0)
            {
                error = "A LAN advertisement requires a gameplay port.";
                return false;
            }

            try
            {
                using var stream = new MemoryStream(384);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                writer.Write(Magic);
                writer.Write(PacketVersion);
                writer.Write((byte)PurrNetLanPacketKind.Advertisement);
                writer.Write(advertisement.SessionId.ToByteArray());
                writer.Write(advertisement.GamePort);
                writer.Write((ushort)Math.Min(
                    ushort.MaxValue,
                    Math.Max(0, advertisement.PlayerCount)));
                writer.Write((ushort)Math.Min(
                    ushort.MaxValue,
                    Math.Max(1, advertisement.MaxPlayers)));
                byte flags = 0;
                if (advertisement.IsOpen) flags |= 1;
                if (advertisement.IsVisible) flags |= 2;
                writer.Write(flags);
                writer.Write(Math.Max(1, advertisement.ProtocolVersion));
                WriteBoundedString(
                    writer,
                    advertisement.SessionName,
                    MaximumNameBytes);
                WriteBoundedString(
                    writer,
                    advertisement.ProductId,
                    MaximumProductBytes);
                WriteBoundedString(
                    writer,
                    advertisement.BuildId,
                    MaximumBuildBytes);
                writer.Flush();

                if (stream.Length > MaximumDatagramBytes)
                {
                    error = "The LAN advertisement exceeded its bounded packet size.";
                    return false;
                }

                packet = stream.ToArray();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is EncoderFallbackException)
            {
                error = $"Could not encode the LAN advertisement: {exception.Message}";
                return false;
            }
        }

        internal static bool TryDecode(
            byte[] buffer,
            int length,
            IPEndPoint source,
            out PurrNetLanPacket packet)
        {
            packet = default;

            try
            {
                using var stream = new MemoryStream(buffer, 0, length, false, true);
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                if (reader.ReadUInt32() != Magic) return false;
                if (reader.ReadByte() != PacketVersion) return false;

                var kind = (PurrNetLanPacketKind)reader.ReadByte();
                if (kind == PurrNetLanPacketKind.Query)
                {
                    if (stream.Position != stream.Length) return false;
                    packet = new PurrNetLanPacket(kind, default, source);
                    return true;
                }

                if (kind != PurrNetLanPacketKind.Advertisement) return false;

                byte[] guidBytes = reader.ReadBytes(16);
                if (guidBytes.Length != 16) return false;
                var sessionId = new Guid(guidBytes);
                if (sessionId == Guid.Empty) return false;

                ushort gamePort = reader.ReadUInt16();
                int playerCount = reader.ReadUInt16();
                int maxPlayers = reader.ReadUInt16();
                byte flags = reader.ReadByte();
                int protocolVersion = reader.ReadInt32();
                if (gamePort == 0 || maxPlayers < 1 || protocolVersion < 1)
                    return false;

                if (!TryReadBoundedString(
                        reader,
                        MaximumNameBytes,
                        out string sessionName) ||
                    !TryReadBoundedString(
                        reader,
                        MaximumProductBytes,
                        out string productId) ||
                    !TryReadBoundedString(
                        reader,
                        MaximumBuildBytes,
                        out string buildId))
                {
                    return false;
                }

                if (stream.Position != stream.Length) return false;

                var advertisement = new PurrNetLanAdvertisement(
                    sessionId,
                    sessionName,
                    productId,
                    buildId,
                    protocolVersion,
                    gamePort,
                    playerCount,
                    maxPlayers,
                    (flags & 1) != 0,
                    (flags & 2) != 0);
                packet = new PurrNetLanPacket(kind, advertisement, source);
                return true;
            }
            catch (Exception exception) when (
                exception is EndOfStreamException ||
                exception is IOException ||
                exception is DecoderFallbackException ||
                exception is ArgumentException)
            {
                return false;
            }
        }

        private static void WriteBoundedString(
            BinaryWriter writer,
            string value,
            int maximumBytes)
        {
            string text = value ?? string.Empty;
            byte[] bytes = StrictUtf8.GetBytes(text);
            while (bytes.Length > maximumBytes && text.Length > 0)
            {
                text = text.Substring(0, text.Length - 1);
                bytes = StrictUtf8.GetBytes(text);
            }

            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static bool TryReadBoundedString(
            BinaryReader reader,
            int maximumBytes,
            out string value)
        {
            value = string.Empty;
            ushort byteCount = reader.ReadUInt16();
            if (byteCount > maximumBytes) return false;
            if (reader.BaseStream.Length - reader.BaseStream.Position < byteCount)
                return false;

            byte[] bytes = reader.ReadBytes(byteCount);
            if (bytes.Length != byteCount) return false;
            value = StrictUtf8.GetString(bytes);
            return true;
        }
    }
}
