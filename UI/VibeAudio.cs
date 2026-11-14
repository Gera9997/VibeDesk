using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace VibeDesk.UI
{
    public static class VibeAudio
    {
        private static byte[]? _softChimeWav;
        private static readonly object _lock = new();

        /// <summary>
        /// Воспроизводит мягкое, ненавязчивое тихое звучание уведомления в фоне,
        /// заменяя резкие системные звуки Windows.
        /// </summary>
        public static void PlaySoftChime()
        {
            try
            {
                if (_softChimeWav == null)
                {
                    lock (_lock)
                    {
                        _softChimeWav ??= GenerateSoftChimeWav();
                    }
                }

                Task.Run(() =>
                {
                    try
                    {
                        using var ms = new MemoryStream(_softChimeWav);
                        using var player = new SoundPlayer(ms);
                        player.PlaySync();
                    }
                    catch { }
                });
            }
            catch { }
        }

        private static byte[] GenerateSoftChimeWav()
        {
            const int sampleRate = 44100;
            const double durationSeconds = 0.45;
            int numSamples = (int)(sampleRate * durationSeconds);

            short[] pcm = new short[numSamples];
            const double freq1 = 587.33; // D5
            const double freq2 = 880.00; // A5

            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / sampleRate;

                // 25ms soft linear attack to avoid pops/clicks
                double attack = Math.Min(1.0, t / 0.025);

                // Smooth exponential decay
                double decay = Math.Exp(-t * 7.5);

                // Warm two-tone sine with subtle harmonic
                double sample = (Math.Sin(2.0 * Math.PI * freq1 * t) * 0.65 +
                                 Math.Sin(2.0 * Math.PI * freq2 * t) * 0.35) * attack * decay * 0.22;

                pcm[i] = (short)Math.Clamp((int)(sample * 32767.0), short.MinValue, short.MaxValue);
            }

            int byteRate = sampleRate * 2;
            int dataLength = numSamples * 2;
            int totalLength = 36 + dataLength;

            using var stream = new MemoryStream(44 + dataLength);
            using var writer = new BinaryWriter(stream);

            // RIFF header
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(totalLength);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });

            // "fmt " chunk
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16); // Subchunk1Size (16 for PCM)
            writer.Write((short)1); // AudioFormat (1 = PCM)
            writer.Write((short)1); // NumChannels (1 = Mono)
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)2); // BlockAlign (NumChannels * BitsPerSample / 8)
            writer.Write((short)16); // BitsPerSample

            // "data" chunk
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(dataLength);

            for (int i = 0; i < numSamples; i++)
            {
                writer.Write(pcm[i]);
            }

            return stream.ToArray();
        }
    }
}
