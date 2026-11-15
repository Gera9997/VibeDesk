#pragma warning disable CS0618
using System;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VibeDesk.Audio
{
    public class AudioPlaybackManager : IDisposable
    {
        private OpusDecoder? _decoder;
        private BufferedWaveProvider? _waveProvider;
        private IWavePlayer? _outputDevice;
        private readonly short[] _pcmBuffer = new short[960 * 2]; // 20ms at 48kHz stereo
        private readonly byte[] _byteBuffer = new byte[960 * 2 * 2]; // 3840 bytes per 20ms
        private volatile bool _isMuted = false;
        private readonly object _lock = new();

        public bool IsRunning { get; private set; }

        public AudioPlaybackManager()
        {
            try
            {
                _decoder = new OpusDecoder(48000, 2);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioPlayback] Opus decoder init error: {ex.Message}");
            }
        }

        public bool Start()
        {
            lock (_lock)
            {
                if (IsRunning) return true;

                try
                {
                    var waveFormat = new WaveFormat(48000, 16, 2);
                    _waveProvider = new BufferedWaveProvider(waveFormat)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(400),
                        DiscardOnBufferOverflow = true
                    };

                    var wasapi = new WasapiOut(AudioClientShareMode.Shared, 50);
                    wasapi.Init(_waveProvider);
                    wasapi.Play();
                    _outputDevice = wasapi;

                    IsRunning = true;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioPlayback] Failed to start audio output: {ex.Message}");
                    _outputDevice?.Dispose();
                    _outputDevice = null;
                    IsRunning = false;
                    return false;
                }
            }
        }

        public void SetMuted(bool muted)
        {
            _isMuted = muted;
            if (muted)
            {
                _waveProvider?.ClearBuffer();
            }
        }

        public void ProcessAudioChunk(byte[] opusData, int offset, int length)
        {
            if (_isMuted || _decoder == null || _waveProvider == null || length <= 0) return;

            try
            {
                int decodedSamples = _decoder.Decode(
                    opusData,
                    offset,
                    length,
                    _pcmBuffer,
                    0,
                    960,
                    false);

                if (decodedSamples > 0 && !_isMuted)
                {
                    int byteCount = decodedSamples * 4; // 2 channels * 2 bytes per sample
                    Buffer.BlockCopy(_pcmBuffer, 0, _byteBuffer, 0, byteCount);
                    _waveProvider.AddSamples(_byteBuffer, 0, byteCount);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioPlayback] Decode error: {ex.Message}");
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!IsRunning) return;
                try
                {
                    _outputDevice?.Stop();
                }
                catch { }
                finally
                {
                    _outputDevice?.Dispose();
                    _outputDevice = null;
                    _waveProvider?.ClearBuffer();
                    IsRunning = false;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
