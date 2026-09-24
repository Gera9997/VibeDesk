using System;
using System.IO;
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
        FrameAck = 8
    }

    public static class PacketBuilder
    {
        public const int MaxChunkPayloadSize = 1000; // Optimal for UDP MTU to prevent IP fragmentation and fit LiteNetLib limit (1023 bytes)

        public static byte[] CreateFrameChunk(uint frameId, ushort chunkIndex, ushort totalChunks, byte[] chunkData, int offset, int length)
        {
            byte[] packet = new byte[1 + 4 + 2 + 2 + 2 + length];
            using var ms = new MemoryStream(packet);
            using var writer = new BinaryWriter(ms);

            writer.Write((byte)PacketType.FrameChunk);
            writer.Write(frameId);
            writer.Write(chunkIndex);
            writer.Write(totalChunks);
            writer.Write((ushort)length);
            writer.Write(chunkData, offset, length);

            return packet;
        }

        public static byte[] CreateMouseMove(double normX, double normY)
        {
            ushort x = (ushort)Math.Clamp(Math.Round(normX * 65535.0), 0, 65535);
            ushort y = (ushort)Math.Clamp(Math.Round(normY * 65535.0), 0, 65535);

            byte[] packet = new byte[1 + 1 + 2 + 2];
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
            byte[] packet = new byte[1 + 1 + 2];
            packet[0] = (byte)PacketType.InputEvent;
            packet[1] = (byte)InputEventType.MouseWheel;
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), (short)delta);
            return packet;
        }

        public static byte[] CreateKey(ushort virtualKey, bool isDown)
        {
            byte[] packet = new byte[1 + 1 + 2 + 1];
            packet[0] = (byte)PacketType.InputEvent;
            packet[1] = isDown ? (byte)InputEventType.KeyDown : (byte)InputEventType.KeyUp;
            BitConverter.TryWriteBytes(packet.AsSpan(2, 2), virtualKey);
            packet[4] = isDown ? (byte)1 : (byte)0;
            return packet;
        }

        public static byte[] CreateClipboard(string text)
        {
            byte[] textBytes = Encoding.UTF8.GetBytes(text);
            byte[] packet = new byte[1 + 4 + textBytes.Length];
            packet[0] = (byte)PacketType.Clipboard;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), textBytes.Length);
            Buffer.BlockCopy(textBytes, 0, packet, 5, textBytes.Length);
            return packet;
        }

        public static byte[] CreatePing(long timestampTicks)
        {
            byte[] packet = new byte[1 + 8];
            packet[0] = (byte)PacketType.Ping;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 8), timestampTicks);
            return packet;
        }

        public static byte[] CreatePong(long timestampTicks)
        {
            byte[] packet = new byte[1 + 8];
            packet[0] = (byte)PacketType.Pong;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 8), timestampTicks);
            return packet;
        }

        public static byte[] CreateScreenInfo(int width, int height)
        {
            byte[] packet = new byte[1 + 4 + 4];
            packet[0] = (byte)PacketType.ScreenInfo;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), width);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), height);
            return packet;
        }

        public static byte[] CreateStreamSettings(float scale, int fps, int quality)
        {
            byte[] packet = new byte[1 + 4 + 4 + 4];
            packet[0] = (byte)PacketType.StreamSettings;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), scale);
            BitConverter.TryWriteBytes(packet.AsSpan(5, 4), fps);
            BitConverter.TryWriteBytes(packet.AsSpan(9, 4), quality);
            return packet;
        }

        public static byte[] CreateFrameAck(uint frameId)
        {
            byte[] packet = new byte[1 + 4];
            packet[0] = (byte)PacketType.FrameAck;
            BitConverter.TryWriteBytes(packet.AsSpan(1, 4), frameId);
            return packet;
        }
    }
}
