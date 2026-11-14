using System;
using System.Buffers.Binary;
using System.Text;
using VibeDesk.Input;

namespace VibeDesk.Network.Protocol
{
    public enum PacketType : byte
    {
        FrameChunk = 1,
        InputEvent = 2,
        Clipboard = 3,
        Ping = 4,
        Pong = 5,
        ScreenInfo = 6,
        StreamSettings = 7,
        FrameAck = 8,
        VibePunch = 9,
        VibePunchAck = 10,
        CursorPosition = 11,
        PictureLossIndication = 12,
        StreamMode = 13,
        VideoXorParity = 14, // Избыточный XOR-чанк для мгновенного восстановления 1 потери за 0 мс
        VideoNack = 15,      // Запрос повторной отправки конкретных чанков
        VideoChunk = 16      // Новый 14-байтовый чанк полосы со StripeIndex и StripeY
    }

    public static class ProtocolConstants
    {
        // 1000 байт полезной нагрузки + 14 байт заголовка = 1014 байт (гарантированно проходит лимит LiteNetLib 1023 байта и любой MTU)
        public const int MaxChunkPayloadSize = 1000;
        public const int VideoHeaderSize = 14;
        public const int SocketBufferSize = 1024 * 1024; // 1 МБ (защита от GC-пауз без Bufferbloat)
    }

    public readonly struct VideoChunkHeader
    {
        public readonly PacketType Type;      // 1 байт
        public readonly byte StripeIndex;     // 1 байт (индекс полосы 0..31)
        public readonly uint FrameId;         // 4 байта (монотонный ID полосы)
        public readonly ushort ChunkIndex;    // 2 байта (0..TotalChunks-1, для XOR = TotalChunks)
        public readonly ushort TotalChunks;   // 2 байта (количество основных чанков данных)
        public readonly ushort StripeY;       // 2 байта (координата Y на экране)
        public readonly ushort StripeHeight;  // 2 байта (высота полосы)

        public VideoChunkHeader(
            PacketType type,
            byte stripeIndex,
            uint frameId,
            ushort chunkIndex,
            ushort totalChunks,
            ushort stripeY,
            ushort stripeHeight)
        {
            Type = type;
            StripeIndex = stripeIndex;
            FrameId = frameId;
            ChunkIndex = chunkIndex;
            TotalChunks = totalChunks;
            StripeY = stripeY;
            StripeHeight = stripeHeight;
        }

        public void WriteTo(Span<byte> buffer)
        {
            buffer[0] = (byte)Type;
            buffer[1] = StripeIndex;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.Slice(2, 4), FrameId);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(6, 2), ChunkIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(8, 2), TotalChunks);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(10, 2), StripeY);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(12, 2), StripeHeight);
        }

        public static VideoChunkHeader ReadFrom(ReadOnlySpan<byte> buffer)
        {
            return new VideoChunkHeader(
                (PacketType)buffer[0],
                buffer[1],
                BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(2, 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(8, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(10, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(12, 2))
            );
        }
    }

    public static class PacketBuilder
    {
        public const int MaxChunkPayloadSize = 1000; // Оптимально для UDP MTU и лимита LiteNetLib

        public static byte[] CreateFrameChunk(uint frameId, ushort chunkIndex, ushort totalChunks, byte[] chunkData, int offset, int length)
        {
            byte[] packet = new byte[11 + length];
            packet[0] = (byte)PacketType.FrameChunk;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), frameId);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 2), chunkIndex);
            BitConverter.TryWriteBytes(packet.AsSpan(7, 2), totalChunks);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 2), (ushort)length);
            Buffer.BlockCopy(chunkData, offset, packet, 11, length);
            return packet;
        }

        public static byte[] CreateMouseMove(double normX, double normY)
        {
            ushort x = (ushort)Math.Clamp(Math.Round(normX * 65535.0), 0, 65535);
            ushort y = (ushort)Math.Clamp(Math.Round(normY * 65535.0), 0, 65535);

            byte[] packet = new byte[6];
            packet[0] = (byte)PacketType.InputEvent;
            packet[1] = (byte)InputEventType.MouseMove;
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), x);
            BitConverter.TryWriteBytes(packet.AsSpan(4, 2), y);
            return packet;
        }

        public static byte[] CreateMouseButton(VibeMouseButton button, bool isDown)
        {
            return new byte[]
            {
                (byte)PacketType.InputEvent,
                isDown ? (byte)InputEventType.MouseDown : (byte)InputEventType.MouseUp,
                (byte)button
            };
        }

        public static byte[] CreateMouseWheel(int delta)
        {
            byte[] packet = new byte[4];
            packet[0] = (byte)PacketType.InputEvent;
            packet[1] = (byte)InputEventType.MouseWheel;
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), (short)delta);
            return packet;
        }

        public static byte[] CreateKey(ushort virtualKey, bool isDown)
        {
            byte[] packet = new byte[5];
            packet[0] = (byte)PacketType.InputEvent;
            packet[1] = isDown ? (byte)InputEventType.KeyDown : (byte)InputEventType.KeyUp;
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), virtualKey);
            packet[4] = isDown ? (byte)1 : (byte)0;
            return packet;
        }

        public static byte[] CreateClipboard(string text)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] packet = new byte[5 + textBytes.Length];
            packet[0] = (byte)PacketType.Clipboard;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), textBytes.Length);
            Buffer.BlockCopy(textBytes, 0, packet, 5, textBytes.Length);
            return packet;
        }

        public static byte[] CreatePing(long timestampTicks)
        {
            byte[] packet = new byte[9];
            packet[0] = (byte)PacketType.Ping;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 8), timestampTicks);
            return packet;
        }

        public static byte[] CreatePong(long timestampTicks)
        {
            byte[] packet = new byte[9];
            packet[0] = (byte)PacketType.Pong;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 8), timestampTicks);
            return packet;
        }

        public static byte[] CreateScreenInfo(int width, int height)
        {
            byte[] packet = new byte[9];
            packet[0] = (byte)PacketType.ScreenInfo;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), width);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), height);
            return packet;
        }

        public static byte[] CreateStreamSettings(float scale, int fps, int quality)
        {
            byte[] packet = new byte[13];
            packet[0] = (byte)PacketType.StreamSettings;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), scale);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), fps);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), quality);
            return packet;
        }

        public static byte[] CreateFrameAck(uint frameId)
        {
            byte[] packet = new byte[5];
            packet[0] = (byte)PacketType.FrameAck;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), frameId);
            return packet;
        }

        public static byte[] CreatePunch(string deviceId)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(deviceId ?? "");
            byte[] packet = new byte[2 + idBytes.Length];
            packet[0] = (byte)PacketType.VibePunch;
            packet[1] = (byte)idBytes.Length;
            Buffer.BlockCopy(idBytes, 0, packet, 2, idBytes.Length);
            return packet;
        }

        public static byte[] CreatePunchAck(string deviceId)
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(deviceId ?? "");
            byte[] packet = new byte[2 + idBytes.Length];
            packet[0] = (byte)PacketType.VibePunchAck;
            packet[1] = (byte)idBytes.Length;
            Buffer.BlockCopy(idBytes, 0, packet, 2, idBytes.Length);
            return packet;
        }

        public static byte[] CreateCursorPosition(bool visible, int x, int y, int screenW, int screenH)
        {
            byte[] packet = new byte[11];
            packet[0] = (byte)PacketType.CursorPosition;
            packet[1] = visible ? (byte)1 : (byte)0;
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), (short)Math.Clamp(x, 0, short.MaxValue));
            BitConverter.TryWriteBytes(packet.AsSpan(4, 2), (short)Math.Clamp(y, 0, short.MaxValue));
            BitConverter.TryWriteBytes(packet.AsSpan(6, 2), (short)Math.Clamp(screenW, 0, short.MaxValue));
            BitConverter.TryWriteBytes(packet.AsSpan(8, 2), (short)Math.Clamp(screenH, 0, short.MaxValue));
            return packet;
        }

        public static byte[] CreatePli()
        {
            return new byte[] { (byte)PacketType.PictureLossIndication };
        }

        public static byte[] CreateStreamMode(byte mode)
        {
            return new byte[] { (byte)PacketType.StreamMode, mode };
        }

        public static byte[] CreateVideoNack(uint frameId, ReadOnlySpan<ushort> missingChunks)
        {
            byte[] packet = new byte[7 + missingChunks.Length * 2];
            packet[0] = (byte)PacketType.VideoNack;
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), frameId);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), (ushort)missingChunks.Length);
            for (int i = 0; i < missingChunks.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(7 + i * 2, 2), missingChunks[i]);
            }
            return packet;
        }
    }
}
