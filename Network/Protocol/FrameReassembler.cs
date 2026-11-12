using System;
using System.Diagnostics;

namespace VibeDesk.Network.Protocol
{
    public class FrameReassembler
    {
        private class FrameSlot
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

        private const int MaxActiveSlots = 32;
        private readonly FrameSlot[] _slots = new FrameSlot[MaxActiveSlots];
        private uint _highestFrameId = 0;

        public event Action<byte[]>? OnFrameReady;
        public event Action<uint, byte[]>? OnFrameReadyWithId;

        public FrameReassembler()
        {
            for (int i = 0; i < MaxActiveSlots; i++)
            {
                _slots[i] = new FrameSlot();
            }
        }

        public void ProcessChunk(ReadOnlySpan<byte> data)
        {
            if (data.Length < 11) return;

            uint frameId = BitConverter.ToUInt32(data.Slice(1, 4));
            ushort chunkIndex = BitConverter.ToUInt16(data.Slice(5, 2));
            ushort totalChunks = BitConverter.ToUInt16(data.Slice(7, 2));
            ushort payloadLength = BitConverter.ToUInt16(data.Slice(9, 2));

            if (data.Length < 11 + payloadLength || totalChunks == 0 || chunkIndex >= totalChunks) return;

            // Отбрасываем устаревшие пакеты (старше 16 полос / 2 кадров)
            if (_highestFrameId > 16 && frameId < _highestFrameId - 16)
            {
                return;
            }

            if (frameId > _highestFrameId)
            {
                _highestFrameId = frameId;
            }

            FrameSlot? slot = null;
            int freeIndex = -1;
            long oldestTicks = long.MaxValue;
            int oldestIndex = 0;

            for (int i = 0; i < MaxActiveSlots; i++)
            {
                if (_slots[i].IsActive && _slots[i].FrameId == frameId)
                {
                    slot = _slots[i];
                    break;
                }

                if (!_slots[i].IsActive && freeIndex == -1)
                {
                    freeIndex = i;
                }

                if (_slots[i].LastActivityTicks < oldestTicks)
                {
                    oldestTicks = _slots[i].LastActivityTicks;
                    oldestIndex = i;
                }
            }

            if (slot == null)
            {
                int targetIndex = freeIndex != -1 ? freeIndex : oldestIndex;
                slot = _slots[targetIndex];
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

                    // Очищаем только явно устаревшие слоты (отставшие более чем на 16 полос)
                    for (int j = 0; j < MaxActiveSlots; j++)
                    {
                        if (_slots[j].IsActive && _highestFrameId > 16 && _slots[j].FrameId < _highestFrameId - 16)
                        {
                            _slots[j].Reset();
                        }
                    }

                    OnFrameReady?.Invoke(fullFrame);
                    OnFrameReadyWithId?.Invoke(frameId, fullFrame);
                }
            }
        }
    }
}
