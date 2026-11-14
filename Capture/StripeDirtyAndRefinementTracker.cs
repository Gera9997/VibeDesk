using System;
using System.Runtime.InteropServices;

namespace VibeDesk.Capture
{
    public sealed class StripeDirtyAndRefinementTracker
    {
        private readonly int _stripesCount;
        private byte[][] _previousRawStripes;
        private readonly bool[] _needsHighQualityRefinement;
        private readonly long[] _lastMotionTimestampMs;
        private int _allocatedStripeByteLength;

        // Через сколько мс покоя досылать кристально четкую полосу (150 мс)
        public const long RefinementDelayMs = 150;

        public StripeDirtyAndRefinementTracker(int stripesCount, int initialStripeByteLength = 1920 * 200 * 4)
        {
            _stripesCount = stripesCount;
            _allocatedStripeByteLength = Math.Max(1024, initialStripeByteLength);
            _previousRawStripes = new byte[stripesCount][];
            _needsHighQualityRefinement = new bool[stripesCount];
            _lastMotionTimestampMs = new long[stripesCount];

            for (int i = 0; i < stripesCount; i++)
            {
                _previousRawStripes[i] = new byte[_allocatedStripeByteLength];
            }
        }

        private void EnsureBufferSize(int requiredSize)
        {
            if (requiredSize <= _allocatedStripeByteLength) return;

            _allocatedStripeByteLength = requiredSize;
            for (int i = 0; i < _stripesCount; i++)
            {
                Array.Resize(ref _previousRawStripes[i], _allocatedStripeByteLength);
            }
        }

        /// <summary>
        /// Возвращает true, если полосу нужно кодировать и отправлять, и выдает целевой JPEG Quality.
        /// </summary>
        public bool EvaluateStripe(
            int stripeIndex,
            ReadOnlySpan<byte> currentRawBgra,
            long nowMs,
            int presetHighQuality,
            bool forceFull,
            out int targetJpegQuality)
        {
            EnsureBufferSize(currentRawBgra.Length);
            Span<byte> prevSpan = _previousRawStripes[stripeIndex].AsSpan(0, currentRawBgra.Length);

            if (forceFull)
            {
                currentRawBgra.CopyTo(prevSpan);
                _needsHighQualityRefinement[stripeIndex] = false;
                _lastMotionTimestampMs[stripeIndex] = nowMs;
                targetJpegQuality = presetHighQuality;
                return true;
            }

            // Векторизованное сравнение памяти через Span<ulong>.SequenceEqual (< 0.05 мс на полосу)
            bool isChanged = !MemoryMarshal.Cast<byte, ulong>(currentRawBgra)
                .SequenceEqual(MemoryMarshal.Cast<byte, ulong>(prevSpan));

            if (isChanged)
            {
                currentRawBgra.CopyTo(prevSpan);
                _lastMotionTimestampMs[stripeIndex] = nowMs;
                _needsHighQualityRefinement[stripeIndex] = true;

                // В фазе активного движения снижаем качество JPEG, чтобы уменьшить размер в 2.5-3 раза
                targetJpegQuality = Math.Max(42, presetHighQuality - 30);
                return true;
            }

            // Если полоса остановилась, но еще висит в низком качестве движения -> досылаем 1 раз в высоком качестве!
            if (_needsHighQualityRefinement[stripeIndex] &&
                nowMs - _lastMotionTimestampMs[stripeIndex] >= RefinementDelayMs)
            {
                _needsHighQualityRefinement[stripeIndex] = false;
                targetJpegQuality = presetHighQuality;
                return true;
            }

            targetJpegQuality = presetHighQuality;
            return false;
        }

        public void Reset()
        {
            for (int i = 0; i < _stripesCount; i++)
            {
                Array.Clear(_previousRawStripes[i], 0, _previousRawStripes[i].Length);
                _needsHighQualityRefinement[i] = false;
                _lastMotionTimestampMs[i] = 0;
            }
        }
    }
}
