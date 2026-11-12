using System;
using System.Collections.Generic;
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

        private Bitmap? _workBitmap;
        private Graphics? _workGraphics;
        private Bitmap? _stripBitmap;
        private Graphics? _stripGraphics;

        private readonly ulong[] _stripHashes = new ulong[StripCount];
        private int _forceSweepIndex = 0;

        public float ResolutionScale { get; private set; } = 0.85f;
        public string ActiveEngineName => _capturer.Name;
        public int ScreenWidth => _capturer.ScreenWidth;
        public int ScreenHeight => _capturer.ScreenHeight;

        public ScreenCaptureManager(int jpegQuality = 60, float scale = 0.85f)
        {
            ResolutionScale = Math.Clamp(scale, 0.25f, 1.0f);
            _jpegCodec = GetEncoder(ImageFormat.Jpeg);
            _encoderParams = new EncoderParameters(1);
            _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(jpegQuality, 10, 100));

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
            _encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)Math.Clamp(quality, 10, 100));
        }

        public void SetScale(float scale)
        {
            float clamped = Math.Clamp(scale, 0.25f, 1.0f);
            if (Math.Abs(clamped - ResolutionScale) > 0.01f)
            {
                ResolutionScale = clamped;
                Array.Clear(_stripHashes, 0, _stripHashes.Length);
            }
        }

        public void ResetForceFrame()
        {
            Array.Clear(_stripHashes, 0, _stripHashes.Length);
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

            DrawCursor(bmp);

            int targetW = Math.Max(16, ((int)(bmp.Width * ResolutionScale)) / 2 * 2);
            int targetH = Math.Max(StripCount * 8, ((int)(bmp.Height * ResolutionScale)) / StripCount * StripCount);
            int stripH = targetH / StripCount;

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
                Array.Clear(_stripHashes, 0, _stripHashes.Length);
            }

            if (_stripBitmap == null || _stripBitmap.Width != targetW || _stripBitmap.Height != stripH)
            {
                _stripGraphics?.Dispose();
                _stripBitmap?.Dispose();
                _stripBitmap = new Bitmap(targetW, stripH, PixelFormat.Format32bppRgb);
                _stripGraphics = Graphics.FromImage(_stripBitmap);
                _stripGraphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            }

            _workGraphics!.DrawImage(bmp, new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);

            var dirtyList = new List<byte[]>(StripCount);

            // Фоновое исцеление (Healing Sweep): 1 полоса в каждом кадре обновляется принудительно
            int sweepStrip = _forceSweepIndex;
            _forceSweepIndex = (_forceSweepIndex + 1) % StripCount;

            BitmapData fullData = _workBitmap.LockBits(new Rectangle(0, 0, targetW, targetH), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            bool[] isStripDirty = new bool[StripCount];

            try
            {
                byte* basePtr = (byte*)fullData.Scan0;
                int stride = fullData.Stride;

                for (int s = 0; s < StripCount; s++)
                {
                    int startY = s * stripH;
                    ulong hash = 14695981039346656037UL;

                    for (int y = startY; y < startY + stripH; y += 2)
                    {
                        uint* row = (uint*)(basePtr + y * stride);
                        for (int x = 0; x < targetW; x += 4)
                        {
                            hash ^= row[x];
                            hash *= 1099511628211UL;
                        }
                    }

                    if (forceAll || s == sweepStrip || _stripHashes[s] != hash)
                    {
                        _stripHashes[s] = hash;
                        isStripDirty[s] = true;
                    }
                }
            }
            finally
            {
                _workBitmap.UnlockBits(fullData);
            }

            for (int s = 0; s < StripCount; s++)
            {
                if (!isStripDirty[s]) continue;

                int startY = s * stripH;
                _stripGraphics!.DrawImage(_workBitmap, new Rectangle(0, 0, targetW, stripH), new Rectangle(0, startY, targetW, stripH), GraphicsUnit.Pixel);

                _compressionStream.SetLength(0);
                _compressionStream.WriteByte((byte)s);
                _compressionStream.WriteByte((byte)StripCount);

                _stripBitmap.Save(_compressionStream, _jpegCodec, _encoderParams);
                dirtyList.Add(_compressionStream.ToArray());
            }

            return dirtyList;
        }

        public byte[]? CaptureAndEncode(bool forceFrame = false)
        {
            Bitmap? bmp = _capturer.Capture();
            if (bmp == null) return null;
            DrawCursor(bmp);
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
            _stripGraphics?.Dispose();
            _stripBitmap?.Dispose();
            _workGraphics?.Dispose();
            _workBitmap?.Dispose();
            _capturer.Dispose();
            _encoderParams.Dispose();
            _compressionStream.Dispose();
        }
    }
}
