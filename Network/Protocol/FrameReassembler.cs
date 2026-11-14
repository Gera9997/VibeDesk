using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeDesk.Network.Protocol
{
    public readonly struct AssembledStripe
    {
        public readonly uint FrameId;
        public readonly byte StripeIndex;
        public readonly ushort StripeY;
        public readonly ushort StripeHeight;
        public readonly byte[] RentedBuffer; // Буфер из ArrayPool, возвращается после декодирования
        public readonly int DataLength;

        public AssembledStripe(uint frameId, byte stripeIndex, ushort y, ushort height, byte[] buffer, int length)
        {
            FrameId = frameId;
            StripeIndex = stripeIndex;
            StripeY = y;
            StripeHeight = height;
            RentedBuffer = buffer;
            DataLength = length;
        }

        public byte[] ToByteArray()
        {
            byte[] copy = new byte[DataLength];
            Buffer.BlockCopy(RentedBuffer, 0, copy, 0, DataLength);
            return copy;
        }
    }

    public sealed class FrameReassembler
    {
        private const int MaxWindowSize = 128; // Вместо 16: держим до 128 полос в полете
        private const int MaxStripesCount = 32;

        private sealed class StripeAssembly
        {
            public uint FrameId;
            public byte StripeIndex;
            public ushort StripeY;
            public ushort StripeHeight;
            public ushort TotalChunks;
            public ushort ReceivedDataChunks;
            public ushort HighestSeenChunk;
            public long CreatedTickMs;
            public long LastPacketTickMs;
            public bool NackSent;

            public byte[][]? Chunks;
            public int[]? ChunkLengths;
            public byte[]? XorParityChunk;
            public int LastChunkActualLength;

            public void Reset()
            {
                if (Chunks != null)
                {
                    for (int i = 0; i < Chunks.Length; i++)
                    {
                        if (Chunks[i] != null)
                        {
                            ArrayPool<byte>.Shared.Return(Chunks[i]);
                            Chunks[i] = null!;
                        }
                    }
                    ArrayPool<byte[]>.Shared.Return(Chunks);
                    Chunks = null;
                }
                if (ChunkLengths != null)
                {
                    ArrayPool<int>.Shared.Return(ChunkLengths);
                    ChunkLengths = null;
                }
                if (XorParityChunk != null)
                {
                    ArrayPool<byte>.Shared.Return(XorParityChunk);
                    XorParityChunk = null;
                }
            }
        }

        private readonly Dictionary<uint, StripeAssembly> _activeAssemblies = new(MaxWindowSize);
        private readonly uint[] _latestCompletedFramePerStripe = new uint[MaxStripesCount];
        private readonly uint[] _latestSeenFramePerStripe = new uint[MaxStripesCount];
        private uint _highestFrameId;

        public event Action<byte[]>? OnFrameReady;
        public event Action<uint, byte[]>? OnFrameReadyWithId;
        public event Action<AssembledStripe>? OnStripeReady;
        public event Action<uint, ReadOnlyMemory<ushort>>? OnSendNackRequested;
        public event Action? OnPliNeeded;

        public bool TryAddPacket(ReadOnlySpan<byte> packet, long nowMs, out AssembledStripe completedStripe)
        {
            completedStripe = default;
            if (packet.Length <= ProtocolConstants.VideoHeaderSize)
                return false;

            var header = VideoChunkHeader.ReadFrom(packet);
            ReadOnlySpan<byte> payload = packet.Slice(ProtocolConstants.VideoHeaderSize);

            if (header.TotalChunks == 0 || header.StripeIndex >= MaxStripesCount)
                return false;

            // 1. Если для ЭТОЙ ЖЕ полосы (StripeIndex) мы уже собрали и отрисовали более свежий FrameId — игнорируем старье
            if (header.FrameId <= _latestCompletedFramePerStripe[header.StripeIndex])
                return false;

            // 2. Если для ЭТОЙ ЖЕ полосы начался более свежий кадр, удаляем старую недособранную сборку этой полосы
            uint prevSeenForStripe = _latestSeenFramePerStripe[header.StripeIndex];
            if (header.FrameId > prevSeenForStripe)
            {
                if (prevSeenForStripe != 0 && _activeAssemblies.Remove(prevSeenForStripe, out var obsoleteAssembly))
                {
                    obsoleteAssembly.Reset();
                }
                _latestSeenFramePerStripe[header.StripeIndex] = header.FrameId;
            }
            else if (header.FrameId < prevSeenForStripe)
            {
                // Пришел запоздавший пакет от старой полосы, когда уже собирается новая для этого же Y
                return false;
            }

            // 3. Глобальная защита окна (128 слотов вместо 16)
            if (header.FrameId > _highestFrameId)
            {
                _highestFrameId = header.FrameId;
                CleanupOldAssemblies(nowMs);
            }
            else if (_highestFrameId > MaxWindowSize && _highestFrameId - header.FrameId > MaxWindowSize)
            {
                return false;
            }

            if (!_activeAssemblies.TryGetValue(header.FrameId, out var assembly))
            {
                assembly = new StripeAssembly
                {
                    FrameId = header.FrameId,
                    StripeIndex = header.StripeIndex,
                    StripeY = header.StripeY,
                    StripeHeight = header.StripeHeight,
                    TotalChunks = header.TotalChunks,
                    CreatedTickMs = nowMs,
                    LastPacketTickMs = nowMs,
                    Chunks = ArrayPool<byte[]>.Shared.Rent(header.TotalChunks),
                    ChunkLengths = ArrayPool<int>.Shared.Rent(header.TotalChunks)
                };
                Array.Clear(assembly.Chunks, 0, header.TotalChunks);
                Array.Clear(assembly.ChunkLengths, 0, header.TotalChunks);
                _activeAssemblies[header.FrameId] = assembly;
            }

            assembly.LastPacketTickMs = nowMs;

            // Обработка XOR-паритета или обычного чанка
            if (header.Type == PacketType.VideoXorParity)
            {
                if (assembly.XorParityChunk == null && payload.Length >= 2)
                {
                    // Первые 2 байта в XOR-пейлоаде хранят точную длину последнего чанка данных
                    assembly.LastChunkActualLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
                    ReadOnlySpan<byte> xorData = payload.Slice(2);
                    byte[] xorBuf = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxChunkPayloadSize);
                    Array.Clear(xorBuf, 0, ProtocolConstants.MaxChunkPayloadSize);
                    xorData.CopyTo(xorBuf);
                    assembly.XorParityChunk = xorBuf;
                }
            }
            else
            {
                if (header.ChunkIndex >= header.TotalChunks)
                    return false;

                if (assembly.Chunks![header.ChunkIndex] == null)
                {
                    byte[] chunkBuf = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxChunkPayloadSize);
                    Array.Clear(chunkBuf, 0, ProtocolConstants.MaxChunkPayloadSize);
                    payload.CopyTo(chunkBuf);

                    assembly.Chunks[header.ChunkIndex] = chunkBuf;
                    assembly.ChunkLengths![header.ChunkIndex] = payload.Length;
                    assembly.ReceivedDataChunks++;

                    if (header.ChunkIndex == header.TotalChunks - 1)
                    {
                        assembly.LastChunkActualLength = payload.Length;
                    }
                    if (header.ChunkIndex > assembly.HighestSeenChunk)
                    {
                        assembly.HighestSeenChunk = header.ChunkIndex;
                    }
                }
            }

            // Сценарий А: Все чанки данных пришли естественным путем
            if (assembly.ReceivedDataChunks == assembly.TotalChunks)
            {
                completedStripe = FinalizeAssembly(assembly);
                _activeAssemblies.Remove(header.FrameId);
                return true;
            }

            // Сценарий Б: Не хватает РОВНО 1 чанка, но уже пришел пакет XOR-паритета -> восстанавливаем за 0 мс!
            if (assembly.ReceivedDataChunks == assembly.TotalChunks - 1 && assembly.XorParityChunk != null)
            {
                RecoverSingleMissingChunkViaXor(assembly);
                completedStripe = FinalizeAssembly(assembly);
                _activeAssemblies.Remove(header.FrameId);
                return true;
            }

            // Сценарий В: Обнаружена дырка в 2+ чанка -> запрашиваем Fast NACK
            // (Если не хватает ровно 1 чанка, ждем XOR-пакет для 0 мс восстановления без лишнего NACK трафика)
            int missingCount = assembly.TotalChunks - assembly.ReceivedDataChunks;
            if (!assembly.NackSent && missingCount >= 2 &&
                (assembly.HighestSeenChunk == assembly.TotalChunks - 1 ||
                 assembly.HighestSeenChunk - assembly.ReceivedDataChunks >= 2))
            {
                TriggerFastNack(assembly);
            }

            return false;
        }

        // Вызывается таймером раз в 5-8 мс для проверки Tail Loss (когда потерялся хвост полосы)
        public void CheckPendingNacksAndTimeouts(long nowMs)
        {
            foreach (var kvp in _activeAssemblies)
            {
                var asm = kvp.Value;
                if (!asm.NackSent && nowMs - asm.LastPacketTickMs >= 8 && asm.ReceivedDataChunks < asm.TotalChunks)
                {
                    TriggerFastNack(asm);
                }
            }
        }

        private void TriggerFastNack(StripeAssembly asm)
        {
            asm.NackSent = true;
            ushort[] missing = new ushort[asm.TotalChunks - asm.ReceivedDataChunks];
            int idx = 0;
            for (ushort i = 0; i < asm.TotalChunks; i++)
            {
                if (asm.Chunks![i] == null && idx < missing.Length)
                {
                    missing[idx++] = i;
                }
            }
            if (idx > 0)
            {
                OnSendNackRequested?.Invoke(asm.FrameId, new ReadOnlyMemory<ushort>(missing, 0, idx));
            }
        }

        private static void RecoverSingleMissingChunkViaXor(StripeAssembly asm)
        {
            int missingIndex = -1;
            for (int i = 0; i < asm.TotalChunks; i++)
            {
                if (asm.Chunks![i] == null)
                {
                    missingIndex = i;
                    break;
                }
            }
            if (missingIndex < 0) return;

            byte[] recovered = ArrayPool<byte>.Shared.Rent(ProtocolConstants.MaxChunkPayloadSize);
            Buffer.BlockCopy(asm.XorParityChunk!, 0, recovered, 0, ProtocolConstants.MaxChunkPayloadSize);

            Span<ulong> recUlong = MemoryMarshal.Cast<byte, ulong>(recovered.AsSpan(0, ProtocolConstants.MaxChunkPayloadSize));

            for (int i = 0; i < asm.TotalChunks; i++)
            {
                if (i == missingIndex) continue;
                ReadOnlySpan<ulong> chunkUlong = MemoryMarshal.Cast<byte, ulong>(
                    asm.Chunks![i].AsSpan(0, ProtocolConstants.MaxChunkPayloadSize));

                for (int u = 0; u < recUlong.Length; u++)
                {
                    recUlong[u] ^= chunkUlong[u];
                }
            }

            asm.Chunks![missingIndex] = recovered;
            asm.ChunkLengths![missingIndex] = (missingIndex == asm.TotalChunks - 1)
                ? asm.LastChunkActualLength
                : ProtocolConstants.MaxChunkPayloadSize;
            asm.ReceivedDataChunks = asm.TotalChunks;
        }

        private AssembledStripe FinalizeAssembly(StripeAssembly asm)
        {
            int totalBytes = 0;
            for (int i = 0; i < asm.TotalChunks; i++)
            {
                totalBytes += asm.ChunkLengths![i];
            }

            byte[] fullStripe = ArrayPool<byte>.Shared.Rent(totalBytes);
            int offset = 0;
            for (int i = 0; i < asm.TotalChunks; i++)
            {
                Buffer.BlockCopy(asm.Chunks![i], 0, fullStripe, offset, asm.ChunkLengths![i]);
                offset += asm.ChunkLengths![i];
            }

            _latestCompletedFramePerStripe[asm.StripeIndex] = asm.FrameId;
            var result = new AssembledStripe(asm.FrameId, asm.StripeIndex, asm.StripeY, asm.StripeHeight, fullStripe, totalBytes);
            asm.Reset();
            return result;
        }

        private void CleanupOldAssemblies(long nowMs)
        {
            List<uint>? toRemove = null;
            foreach (var kvp in _activeAssemblies)
            {
                if ((_highestFrameId > MaxWindowSize && _highestFrameId - kvp.Key > MaxWindowSize) ||
                    nowMs - kvp.Value.CreatedTickMs > 250)
                {
                    toRemove ??= new List<uint>();
                    toRemove.Add(kvp.Key);
                }
            }
            if (toRemove != null)
            {
                foreach (uint key in toRemove)
                {
                    if (_activeAssemblies.Remove(key, out var asm))
                    {
                        asm.Reset();
                    }
                }
            }
        }

        // Legacy slot-based storage for backward compatibility with PacketType.FrameChunk
        private class LegacyFrameSlot
        {
            public uint FrameId;
            public ushort TotalChunks;
            public ushort ReceivedCount;
            public byte[][]? Chunks;
            public ushort[]? ChunkSizes;
            public long LastActivityTicks;
            public bool IsActive;

            public void Reset()
            {
                FrameId = 0;
                TotalChunks = 0;
                ReceivedCount = 0;
                Chunks = null;
                ChunkSizes = null;
                IsActive = false;
            }
        }

        private const int LegacyMaxActiveSlots = 32;
        private readonly LegacyFrameSlot[] _legacySlots = new LegacyFrameSlot[LegacyMaxActiveSlots];
        private uint _legacyHighestFrameId = 0;

        public FrameReassembler()
        {
            for (int i = 0; i < LegacyMaxActiveSlots; i++)
            {
                _legacySlots[i] = new LegacyFrameSlot();
            }
        }

        /// <summary>
        /// Универсальный метод обработки чанков (поддерживает как новый 14-байтовый VideoChunkHeader,
        /// так и классический 11-байтовый FrameChunk для обратной совместимости).
        /// </summary>
        public void ProcessChunk(ReadOnlySpan<byte> data)
        {
            long nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

            // Обработка нового 14-байтового формата полос (VideoChunk / VideoXorParity)
            if (data.Length >= ProtocolConstants.VideoHeaderSize &&
                (data[0] == (byte)PacketType.VideoChunk || data[0] == (byte)PacketType.VideoXorParity))
            {
                if (TryAddPacket(data, nowMs, out var completed))
                {
                    byte[] copy = completed.ToByteArray();
                    ArrayPool<byte>.Shared.Return(completed.RentedBuffer);

                    OnStripeReady?.Invoke(completed);
                    OnFrameReady?.Invoke(copy);
                    OnFrameReadyWithId?.Invoke(completed.FrameId, copy);
                }
                return;
            }

            // Обработка классического 11-байтового FrameChunk (legacy/tests)
            if (data.Length >= 11 && data[0] == (byte)PacketType.FrameChunk)
            {
                ProcessLegacyChunk(data);
            }
        }

        private void ProcessLegacyChunk(ReadOnlySpan<byte> data)
        {
            uint frameId = BitConverter.ToUInt32(data.Slice(1, 4));
            ushort chunkIndex = BitConverter.ToUInt16(data.Slice(5, 2));
            ushort totalChunks = BitConverter.ToUInt16(data.Slice(7, 2));
            ushort payloadLength = BitConverter.ToUInt16(data.Slice(9, 2));

            if (data.Length < 11 + payloadLength || totalChunks == 0 || chunkIndex >= totalChunks) return;

            if (_legacyHighestFrameId > 16 && frameId < _legacyHighestFrameId - 16)
            {
                return;
            }

            if (frameId > _legacyHighestFrameId)
            {
                _legacyHighestFrameId = frameId;
            }

            LegacyFrameSlot? slot = null;
            int freeIndex = -1;
            long oldestTicks = long.MaxValue;
            int oldestIndex = 0;

            for (int i = 0; i < LegacyMaxActiveSlots; i++)
            {
                if (_legacySlots[i].IsActive && _legacySlots[i].FrameId == frameId)
                {
                    slot = _legacySlots[i];
                    break;
                }

                if (!_legacySlots[i].IsActive && freeIndex == -1)
                {
                    freeIndex = i;
                }

                if (_legacySlots[i].LastActivityTicks < oldestTicks)
                {
                    oldestTicks = _legacySlots[i].LastActivityTicks;
                    oldestIndex = i;
                }
            }

            if (slot == null)
            {
                int targetIndex = freeIndex != -1 ? freeIndex : oldestIndex;
                slot = _legacySlots[targetIndex];
                slot.Reset();
                slot.FrameId = frameId;
                slot.TotalChunks = totalChunks;
                slot.Chunks = new byte[totalChunks][];
                slot.ChunkSizes = new ushort[totalChunks];
                slot.IsActive = true;
            }

            slot.LastActivityTicks = Stopwatch.GetTimestamp();

            if (slot.Chunks != null && slot.Chunks[chunkIndex] == null)
            {
                byte[] chunkBytes = new byte[payloadLength];
                data.Slice(11, payloadLength).CopyTo(chunkBytes);
                slot.Chunks[chunkIndex] = chunkBytes;
                slot.ChunkSizes![chunkIndex] = payloadLength;
                slot.ReceivedCount++;

                if (slot.ReceivedCount == slot.TotalChunks)
                {
                    int totalSize = 0;
                    for (int i = 0; i < slot.TotalChunks; i++)
                    {
                        totalSize += slot.ChunkSizes[i];
                    }

                    byte[] fullFrame = new byte[totalSize];
                    int offset = 0;
                    for (int i = 0; i < slot.TotalChunks; i++)
                    {
                        if (slot.Chunks[i] != null)
                        {
                            Buffer.BlockCopy(slot.Chunks[i], 0, fullFrame, offset, slot.ChunkSizes[i]);
                            offset += slot.ChunkSizes[i];
                        }
                    }

                    slot.Reset();

                    bool droppedSlot = false;
                    for (int j = 0; j < LegacyMaxActiveSlots; j++)
                    {
                        if (_legacySlots[j].IsActive && _legacyHighestFrameId > 16 && _legacySlots[j].FrameId < _legacyHighestFrameId - 16)
                        {
                            _legacySlots[j].Reset();
                            droppedSlot = true;
                        }
                    }

                    if (droppedSlot)
                    {
                        OnPliNeeded?.Invoke();
                    }

                    OnFrameReady?.Invoke(fullFrame);
                    OnFrameReadyWithId?.Invoke(frameId, fullFrame);
                }
            }
        }
    }
}
