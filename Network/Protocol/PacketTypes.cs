using System;
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
        PictureLossIndication = 12
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
    }
}
