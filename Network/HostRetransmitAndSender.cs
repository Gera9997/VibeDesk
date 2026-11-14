using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading;
using LiteNetLib;
using VibeDesk.Native;
using VibeDesk.Network.Protocol;

namespace VibeDesk.Network
{
    public sealed class HostRetransmitAndSender
    {
        private const int RingBufferCapacity = 2048; // Храним последние 2048 чанков (~2.6 МБ)

        private struct CachedPacket
        {
            public uint FrameId;
            public ushort ChunkIndex;
            public int Length;
            public byte[] Data;
        }

        private readonly CachedPacket[] _ringBuffer = new CachedPacket[RingBufferCapacity];
        private readonly object _ringLock = new();
        private int _ringWritePos;

        public HostRetransmitAndSender()
        {
            try
            {
                Win32.timeBeginPeriod(1); // Переводим системный таймер Windows с 15.6 мс на 1 мс
            }
            catch { }

            int maxPacketSize = ProtocolConstants.VideoHeaderSize + ProtocolConstants.MaxChunkPayloadSize + 4;
            for (int i = 0; i < RingBufferCapacity; i++)
            {
                _ringBuffer[i].Data = new byte[maxPacketSize];
            }
        }

        public void SendStripeWithXorAndCache(
            NetPeer peer,
            byte stripeIndex,
            uint frameId,
            ushort stripeY,
            ushort stripeHeight,
            ReadOnlySpan<byte> compressedJpeg,
            ref long bytesSentCounter,
            ref double tokenBucket)
        {
            int maxPayload = ProtocolConstants.MaxChunkPayloadSize;
            int headerSize = ProtocolConstants.VideoHeaderSize;
            ushort totalChunks = (ushort)((compressedJpeg.Length + maxPayload - 1) / maxPayload);
            if (totalChunks == 0) return;

            byte[]? xorAccumulator = null;
            if (totalChunks >= 3)
            {
                xorAccumulator = ArrayPool<byte>.Shared.Rent(maxPayload);
                Array.Clear(xorAccumulator, 0, maxPayload);
            }

            byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(headerSize + maxPayload);
            int lastChunkLen = 0;

            try
            {
                for (ushort i = 0; i < totalChunks; i++)
                {
                    int offset = i * maxPayload;
                    int len = Math.Min(maxPayload, compressedJpeg.Length - offset);
                    lastChunkLen = len;

                    var header = new VideoChunkHeader(
                        PacketType.VideoChunk,
                        stripeIndex,
                        frameId,
                        i,
                        totalChunks,
                        stripeY,
                        stripeHeight);

                    header.WriteTo(packetBuffer.AsSpan(0, headerSize));
                    ReadOnlySpan<byte> chunkSlice = compressedJpeg.Slice(offset, len);
                    chunkSlice.CopyTo(packetBuffer.AsSpan(headerSize, len));

                    int totalPacketLen = headerSize + len;

                    // Сохраняем в кольцевой буфер для мгновенного ответа на NACK
                    CacheSentPacket(frameId, i, packetBuffer.AsSpan(0, totalPacketLen));

                    // Обновляем XOR-паритет
                    if (xorAccumulator != null)
                    {
                        AccumulateXor(xorAccumulator.AsSpan(0, maxPayload), chunkSlice);
                    }

                    // Микро-темпирование (Pacing): каждые 8 пакетов даем микропаузу для сетевой карты
                    if (i > 0 && (i % 8) == 0)
                    {
                        Thread.SpinWait(200);
                    }

                    peer.Send(packetBuffer, 0, totalPacketLen, DeliveryMethod.Unreliable);
                    bytesSentCounter += totalPacketLen;
                    tokenBucket -= totalPacketLen;
                }

                // Отправляем 1 пакет XOR-паритета (2 байта длины последнего чанка + XOR данные)
                if (xorAccumulator != null)
                {
                    int xorPacketLen = headerSize + 2 + maxPayload;
                    byte[] xorPacket = ArrayPool<byte>.Shared.Rent(xorPacketLen);
                    try
                    {
                        var xorHeader = new VideoChunkHeader(
                            PacketType.VideoXorParity,
                            stripeIndex,
                            frameId,
                            totalChunks, // Индекс равен TotalChunks для XOR пакета
                            totalChunks,
                            stripeY,
                            stripeHeight);

                        xorHeader.WriteTo(xorPacket.AsSpan(0, headerSize));
                        BinaryPrimitives.WriteUInt16LittleEndian(xorPacket.AsSpan(headerSize, 2), (ushort)lastChunkLen);
                        xorAccumulator.AsSpan(0, maxPayload).CopyTo(xorPacket.AsSpan(headerSize + 2, maxPayload));

                        peer.Send(xorPacket, 0, xorPacketLen, DeliveryMethod.Unreliable);
                        bytesSentCounter += xorPacketLen;
                        tokenBucket -= xorPacketLen;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(xorPacket);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packetBuffer);
                if (xorAccumulator != null)
                {
                    ArrayPool<byte>.Shared.Return(xorAccumulator);
                }
            }
        }

        // Вызывается в VibeHost при получении пакета PacketType.VideoNack от клиента
        public void HandleNackRequest(NetPeer peer, uint frameId, ReadOnlySpan<ushort> missingChunkIndices)
        {
            foreach (ushort missingIdx in missingChunkIndices)
            {
                lock (_ringLock)
                {
                    for (int i = 0; i < RingBufferCapacity; i++)
                    {
                        ref var entry = ref _ringBuffer[i];
                        if (entry.FrameId == frameId && entry.ChunkIndex == missingIdx && entry.Length > 0)
                        {
                            peer.Send(entry.Data, 0, entry.Length, DeliveryMethod.Unreliable);
                            break;
                        }
                    }
                }
            }
        }

        private void CacheSentPacket(uint frameId, ushort chunkIndex, ReadOnlySpan<byte> packetData)
        {
            lock (_ringLock)
            {
                ref var slot = ref _ringBuffer[_ringWritePos];
                slot.FrameId = frameId;
                slot.ChunkIndex = chunkIndex;
                slot.Length = packetData.Length;
                packetData.CopyTo(slot.Data);
                _ringWritePos = (_ringWritePos + 1) % RingBufferCapacity;
            }
        }

        private static void AccumulateXor(Span<byte> accumulator, ReadOnlySpan<byte> chunk)
        {
            int i = 0;
            if (chunk.Length >= 8)
            {
                Span<ulong> acc64 = MemoryMarshal.Cast<byte, ulong>(accumulator);
                ReadOnlySpan<ulong> chk64 = MemoryMarshal.Cast<byte, ulong>(chunk.Slice(0, chunk.Length & ~7));
                for (int u = 0; u < chk64.Length; u++)
                {
                    acc64[u] ^= chk64[u];
                }
                i = chk64.Length * 8;
            }
            for (; i < chunk.Length; i++)
            {
                accumulator[i] ^= chunk[i];
            }
        }
    }
}
