using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using VibeDesk.Native;

namespace VibeDesk.Capture
{
    public class ScreenCaptureManager : IDisposable
    {
        public const int StripCount = 8;

        private IScreenCapturer _capturer;
        private readonly ImageCodecInfo _jpegCodec;
        private readonly EncoderParameters _encoderParams;
        private readonly MemoryStream _compressionStream = new(1024 * 256);
        private readonly MemoryStream[] _stripStreams = new MemoryStream[StripCount];

        private Bitmap? _workBitmap;
        private Graphics? _workGraphics;

        private readonly StripeDirtyAndRefinementTracker _tracker = new(StripCount);
        private int _forceSweepIndex = 0;
        private volatile int _currentQuality = 60;

        public float ResolutionScale { get; private set; } = 0.85f;
        public string ActiveEngineName => _capturer.Name;
        public int ScreenWidth => _capturer.ScreenWidth;
        public int ScreenHeight => _capturer.ScreenHeight;

        public ScreenCaptureManager(int jpegQuality = 60, float scale = 0.85f)
        {
            ResolutionScale = Math.Clamp(scale, 0.25f, 1.0f);
            _currentQuality = Math.Clamp(jpegQuality, 10, 100);
            _jpegCodec = GetEncoder(ImageFormat.Jpeg);
            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_currentQuality);

            for (int i = 0; i < StripCount; i++)
            {
                _stripStreams[i] = new MemoryStream(1024 * 128);
            }

            var dxgi = new DxgiScreenCapture();
            if (dxgi.Initialize())
            {
                _capturer = dxgi;
            }
            else
            {
                dxgi.Dispose();
                var gdi = new GdiScreenCapture();
                gdi.Initialize();
                _capturer = gdi;
            }
        }

        public void SetQuality(int quality)
        {
            _currentQuality = Math.Clamp(quality, 10, 100);
            _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)_currentQuality);
        }

        public void SetScale(float scale)
        {
            float clamped = Math.Clamp(scale, 0.25f, 1.0f);
            if (Math.Abs(clamped - ResolutionScale) > 0.01f)
            {
                ResolutionScale = clamped;
                _tracker.Reset();
            }
        }

        public void ResetForceFrame()
        {
            _tracker.Reset();
        }

        /// <summary>
        /// Возвращает список изменившихся горизонтальных полос экрана.
        /// Первые 2 байта каждого элемента: [0] = индекс полосы (0..7), [1] = всего полос (8), далее JPEG-поток.
        /// </summary>
        public unsafe List<byte[]>? CaptureDirtyStrips(bool forceAll = false)
        {
            Bitmap? bmp = _capturer.Capture();
            if (bmp == null)
            {
                if (_capturer is DxgiScreenCapture)
                {
                    _capturer.Dispose();
                    var gdi = new GdiScreenCapture();
                    gdi.Initialize();
                    _capturer = gdi;
                    bmp = _capturer.Capture();
                }
                if (bmp == null) return null;
            }

            if (!forceAll && !_capturer.HasNewFrame)
            {
                return null;
            }

            int targetW = Math.Max(16, ((int)(bmp.Width * ResolutionScale)) / 2 * 2);
            int targetH = Math.Max(StripCount * 8, ((int)(bmp.Height * ResolutionScale)) / StripCount * StripCount);
            int stripH = targetH / StripCount;

            bool isFullScale = Math.Abs(ResolutionScale - 1.0f) < 0.01f && bmp.Width == targetW && bmp.Height == targetH;
            Bitmap sourceBmp;

            if (isFullScale)
            {
                sourceBmp = bmp;
            }
            else
            {
                if (_workBitmap == null || _workBitmap.Width != targetW || _workBitmap.Height != targetH)
                {
                    _workGraphics?.Dispose();
                    _workBitmap?.Dispose();
                    _workBitmap = new Bitmap(targetW, targetH, PixelFormat.Format32bppRgb);
                    _workGraphics = Graphics.FromImage(_workBitmap);
                    _workGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    _workGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    _workGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    _workGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    _workGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                    _tracker.Reset();
                }

                _workGraphics!.DrawImage(bmp, new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
                sourceBmp = _workBitmap;
            }

            var dirtyList = new List<byte[]>(StripCount);

            // Фоновое исцеление (Healing Sweep): 1 полоса в каждом кадре обновляется принудительно
            int sweepStrip = _forceSweepIndex;
            _forceSweepIndex = (_forceSweepIndex + 1) % StripCount;

            BitmapData fullData = sourceBmp.LockBits(new Rectangle(0, 0, targetW, targetH), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            bool[] isStripDirty = new bool[StripCount];
            int[] stripQualities = new int[StripCount];
            byte[]?[] stripResults = new byte[]?[StripCount];
            long nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;

            try
            {
                byte* basePtr = (byte*)fullData.Scan0;
                int stride = fullData.Stride;
                int stripeByteLen = stripH * stride;

                for (int s = 0; s < StripCount; s++)
                {
                    int startY = s * stripH;
                    byte* sliceScan0 = basePtr + startY * stride;
                    var stripeSpan = new ReadOnlySpan<byte>(sliceScan0, stripeByteLen);

                    bool forceThisStrip = forceAll || (s == sweepStrip);
                    if (_tracker.EvaluateStripe(s, stripeSpan, nowMs, _currentQuality, forceThisStrip, out int targetQ))
                    {
                        isStripDirty[s] = true;
                        stripQualities[s] = targetQ;
                    }
                }

                // Этап 1 & 2: Многопоточное сжатие независимых полос со срезом памяти без промежуточных Graphics.DrawImage
                Parallel.For(0, StripCount, s =>
                {
                    if (!isStripDirty[s]) return;

                    int startY = s * stripH;
                    IntPtr sliceScan0 = (IntPtr)(basePtr + startY * stride);
                    using var sliceBmp = new Bitmap(targetW, stripH, stride, PixelFormat.Format32bppRgb, sliceScan0);

                    using var encParams = new EncoderParameters(1);
                    encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)stripQualities[s]);

                    var stream = _stripStreams[s];
                    lock (stream)
                    {
                        stream.SetLength(0);
                        stream.WriteByte((byte)s);
                        stream.WriteByte((byte)StripCount);
                        sliceBmp.Save(stream, _jpegCodec, encParams);
                        stripResults[s] = stream.ToArray();
                    }
                });
            }
            finally
            {
                sourceBmp.UnlockBits(fullData);
            }

            for (int s = 0; s < StripCount; s++)
            {
                if (stripResults[s] != null)
                {
                    dirtyList.Add(stripResults[s]!);
                }
            }

            return dirtyList;
        }

        public byte[]? CaptureFullFrame(bool forceFrame = false)
        {
            Bitmap? bmp = _capturer.Capture();
            if (bmp == null)
            {
                if (_capturer is DxgiScreenCapture)
                {
                    _capturer.Dispose();
                    var gdi = new GdiScreenCapture();
                    gdi.Initialize();
                    _capturer = gdi;
                    bmp = _capturer.Capture();
                }
                if (bmp == null) return null;
            }

            if (!forceFrame && !_capturer.HasNewFrame)
            {
                return null;
            }

            int targetW = Math.Max(16, ((int)(bmp.Width * ResolutionScale)) / 2 * 2);
            int targetH = Math.Max(16, ((int)(bmp.Height * ResolutionScale)) / 2 * 2);

            Bitmap sourceBmp;
            if (Math.Abs(ResolutionScale - 1.0f) < 0.01f && bmp.Width == targetW && bmp.Height == targetH)
            {
                sourceBmp = bmp;
            }
            else
            {
                if (_workBitmap == null || _workBitmap.Width != targetW || _workBitmap.Height != targetH)
                {
                    _workGraphics?.Dispose();
                    _workBitmap?.Dispose();
                    _workBitmap = new Bitmap(targetW, targetH, PixelFormat.Format32bppRgb);
                    _workGraphics = Graphics.FromImage(_workBitmap);
                    _workGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    _workGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    _workGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    _workGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                    _workGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                }

                _workGraphics!.DrawImage(bmp, new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
                sourceBmp = _workBitmap;
            }

            lock (_compressionStream)
            {
                _compressionStream.SetLength(0);
                // Префикс полного кадра: [0] = 0xAA (FullFrame marker), [1] = 0x01 (version)
                _compressionStream.WriteByte(0xAA);
                _compressionStream.WriteByte(0x01);
                sourceBmp.Save(_compressionStream, _jpegCodec, _encoderParams);
                return _compressionStream.ToArray();
            }
        }

        public byte[]? CaptureAndEncode(bool forceFrame = false)
        {
            Bitmap? bmp = _capturer.Capture();
            if (bmp == null) return null;
            _compressionStream.SetLength(0);
            bmp.Save(_compressionStream, _jpegCodec, _encoderParams);
            return _compressionStream.ToArray();
        }

        private static void DrawCursor(Bitmap bmp)
        {
            try
            {
                var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
                if (Win32.GetCursorInfo(out ci) && (ci.flags & Win32.CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
                {
                    using var g = Graphics.FromImage(bmp);
                    IntPtr hdc = g.GetHdc();
                    try
                    {
                        Win32.DrawIcon(hdc, ci.ptScreenPos.X, ci.ptScreenPos.Y, ci.hCursor);
                    }
                    finally
                    {
                        g.ReleaseHdc(hdc);
                    }
                }
            }
            catch { }
        }

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageEncoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid) return codec;
            }
            return codecs[0];
        }

        public void Dispose()
        {
            _workGraphics?.Dispose();
            _workBitmap?.Dispose();
            _capturer.Dispose();
            _encoderParams.Dispose();
            _compressionStream.Dispose();
            for (int i = 0; i < StripCount; i++)
            {
                _stripStreams[i]?.Dispose();
            }
        }
    }
}
