#pragma warning disable CS0618
using System;
using Concentus.Enums;
using Concentus.Structs;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VibeDesk.Audio
{
    public class AudioCaptureManager : IDisposable
    {
        private WasapiLoopbackCapture? _capture;
        private OpusEncoder? _encoder;
        private readonly object _lock = new();
        private volatile bool _isStreamingEnabled = true;
        private readonly short[] _resampleBuffer = new short[960 * 2]; // 20ms at 48kHz stereo (960 samples per channel)
        private int _samplesInBuffer = 0; // accumulated stereo sample pairs
        private readonly byte[] _opusOutputBuffer = new byte[1275];

        public event Action<byte[], int>? OnAudioChunkReady;
        public bool IsRunning { get; private set; }

        public AudioCaptureManager()
        {
            try
            {
                _encoder = new OpusEncoder(48000, 2, OpusApplication.OPUS_APPLICATION_AUDIO);
                _encoder.Bitrate = 64000; // 64 kbps - transparent stereo speech & music
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioCapture] Opus init error: {ex.Message}");
            }
        }

        public void SetStreamingEnabled(bool enabled)
        {
            lock (_lock)
            {
                _isStreamingEnabled = enabled;
                if (!enabled)
                {
                    _samplesInBuffer = 0;
                }
            }
        }

        public bool Start()
        {
            lock (_lock)
            {
                if (IsRunning) return true;

                try
                {
                    _capture = new WasapiLoopbackCapture();
                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += (s, e) =>
                    {
                        IsRunning = false;
                    };
                    _capture.StartRecording();
                    IsRunning = true;
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AudioCapture] WasapiLoopbackCapture start error: {ex.Message}");
                    _capture?.Dispose();
                    _capture = null;
                    IsRunning = false;
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!IsRunning) return;
                try
                {
                    _capture?.StopRecording();
                }
                catch { }
                finally
                {
                    _capture?.Dispose();
                    _capture = null;
                    IsRunning = false;
                }
            }
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (!_isStreamingEnabled || _encoder == null || e.BytesRecorded <= 0) return;

            var waveFormat = _capture?.WaveFormat;
            if (waveFormat == null) return;

            lock (_lock)
            {
                if (!_isStreamingEnabled) return;

                int channels = waveFormat.Channels;
                int bytesPerSample = waveFormat.BitsPerSample / 8;
                bool isFloat = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

                if (bytesPerSample <= 0 || channels <= 0) return;

                int totalSamples = e.BytesRecorded / bytesPerSample;
                int totalFrames = totalSamples / channels;

                for (int f = 0; f < totalFrames; f++)
                {
                    float left = 0;
                    float right = 0;

                    int offset = f * channels * bytesPerSample;
                    if (isFloat && bytesPerSample == 4)
                    {
                        left = BitConverter.ToSingle(e.Buffer, offset);
                        right = channels > 1 ? BitConverter.ToSingle(e.Buffer, offset + 4) : left;
                    }
                    else if (!isFloat && bytesPerSample == 2)
                    {
                        short sL = BitConverter.ToInt16(e.Buffer, offset);
                        short sR = channels > 1 ? BitConverter.ToInt16(e.Buffer, offset + 2) : sL;
                        left = sL / 32768f;
                        right = sR / 32768f;
                    }

                    short pcmLeft = (short)Math.Clamp((int)(left * 32767f), short.MinValue, short.MaxValue);
                    short pcmRight = (short)Math.Clamp((int)(right * 32767f), short.MinValue, short.MaxValue);

                    _resampleBuffer[_samplesInBuffer * 2] = pcmLeft;
                    _resampleBuffer[_samplesInBuffer * 2 + 1] = pcmRight;
                    _samplesInBuffer++;

                    if (_samplesInBuffer >= 960) // 20 ms Opus frame
                    {
                        EncodeAndDispatch();
                        _samplesInBuffer = 0;
                    }
                }
            }
        }

        private void EncodeAndDispatch()
        {
            if (_encoder == null) return;

            try
            {
                int encodedLen = _encoder.Encode(
                    _resampleBuffer,
                    0,
                    960,
                    _opusOutputBuffer,
                    0,
                    _opusOutputBuffer.Length);

                if (encodedLen > 0)
                {
                    OnAudioChunkReady?.Invoke(_opusOutputBuffer, encodedLen);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioCapture] Encode error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
