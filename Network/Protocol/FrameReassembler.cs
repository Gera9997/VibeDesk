using System;
using System.IO;

namespace VibeDesk.Network.Protocol
{
    public class FrameReassembler
    {
        private uint _currentFrameId = 0;
        private int _totalChunks = 0;
        private int _receivedChunksCount = 0;
        private byte[][]? _chunks;
        private int[]? _chunkSizes;

        public event Action<byte[]>? OnFrameReady;

        public void ProcessChunk(ReadOnlySpan<byte> data)
        {
            if (data.Length < 11) return; // 1 (type) + 4 (id) + 2 (idx) + 2 (total) + 2 (len)

            uint frameId = BitConverter.ToUInt32(data.Slice(1, 4));
            ushort chunkIndex = BitConverter.ToUInt16(data.Slice(5, 2));
            ushort totalChunks = BitConverter.ToUInt16(data.Slice(7, 2));
            ushort payloadLength = BitConverter.ToUInt16(data.Slice(9, 2));

            if (data.Length < 11 + payloadLength) return;

            // Discard older frame chunks
            if (frameId < _currentFrameId)
            {
                return;
            }

            // If a newer frame arrives, discard previous incomplete frame
            if (frameId > _currentFrameId)
            {
                _currentFrameId = frameId;
                _totalChunks = totalChunks;
                _receivedChunksCount = 0;
                _chunks = new byte[totalChunks][];
                _chunkSizes = new int[totalChunks];
            }

            if (_chunks != null && chunkIndex < _totalChunks && _chunks[chunkIndex] == null)
            {
                byte[] chunkBytes = new byte[payloadLength];
                data.Slice(11, payloadLength).CopyTo(chunkBytes);
                _chunks[chunkIndex] = chunkBytes;
                _chunkSizes![chunkIndex] = payloadLength;
                _receivedChunksCount++;

                if (_receivedChunksCount == _totalChunks)
                {
                    // Reassemble full frame
                    int totalSize = 0;
                    for (int i = 0; i < _totalChunks; i++)
                    {
                        totalSize += _chunkSizes[i];
                    }

                    byte[] fullFrame = new byte[totalSize];
                    int offset = 0;
                    for (int i = 0; i < _totalChunks; i++)
                    {
                        Buffer.BlockCopy(_chunks[i], 0, fullFrame, offset, _chunkSizes[i]);
                        offset += _chunkSizes[i];
                    }

                    OnFrameReady?.Invoke(fullFrame);

                    // Clear buffers
                    _chunks = null;
                    _chunkSizes = null;
                }
            }
        }
    }
}
