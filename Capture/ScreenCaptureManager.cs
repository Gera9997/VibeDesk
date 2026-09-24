using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using VibeDesk.Native;

namespace VibeDesk.Capture
{
    public class ScreenCaptureManager : IDisposable
    {
        private IScreenCapturer _capturer;
        private readonly ImageCodecInfo _jpegCodec;
        private readonly EncoderParameters _encoderParams;
        private readonly MemoryStream _compressionStream = new(1024 * 512);

        private Bitmap? _scaledBitmap;
        private Graphics? _scaledGraphics;

        private int _lastCursorX = -1;
        private int _lastCursorY = -1;
        private IntPtr _lastCursorHandle = IntPtr.Zero;
        private bool _lastCursorVisible = false;
        private long _lastEncodedTimestampTicks = 0;

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

            // Attempt DXGI first, fallback to GDI
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
            ResolutionScale = Math.Clamp(scale, 0.25f, 1.0f);
        }

        public void ResetForceFrame()
        {
            _lastEncodedTimestampTicks = 0;
        }

        public unsafe byte[]? CaptureAndEncode(bool forceFrame = false)
        {
            Bitmap? bmp = _capturer.Capture();
            if (bmp == null)
            {
                // Fallback to GDI if current capturer failed
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

            // Check cursor changes
            bool cursorChanged = false;
            try
            {
                var ci = new Win32.CURSORINFO { cbSize = Marshal.SizeOf<Win32.CURSORINFO>() };
                if (Win32.GetCursorInfo(out ci))
                {
                    bool visible = (ci.flags & Win32.CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
                    if (visible != _lastCursorVisible ||
                        (visible && (ci.ptScreenPos.X != _lastCursorX || ci.ptScreenPos.Y != _lastCursorY || ci.hCursor != _lastCursorHandle)))
                    {
                        cursorChanged = true;
                        _lastCursorX = ci.ptScreenPos.X;
                        _lastCursorY = ci.ptScreenPos.Y;
                        _lastCursorHandle = ci.hCursor;
                        _lastCursorVisible = visible;
                    }
                }
            }
            catch { }

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long elapsedMsSinceLastEncode = _lastEncodedTimestampTicks == 0 ? long.MaxValue :
                (long)((nowTicks - _lastEncodedTimestampTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            // Screen dirty checking:
            // If screen pixels did not change, cursor did not move, not forced, and heartbeat interval (< 250ms / 4 FPS) has not elapsed:
            // return null to completely save network bandwidth & avoid bufferbloat!
            if (!_capturer.HasNewFrame && !cursorChanged && !forceFrame && elapsedMsSinceLastEncode < 250)
            {
                return null;
            }

            _lastEncodedTimestampTicks = nowTicks;

            // Draw host's real cursor onto the bitmap so it's visible in remote stream
            DrawCursor(bmp);

            _compressionStream.SetLength(0);

            // High-speed downscaling if resolution scale is less than 1.0
            if (ResolutionScale < 0.99f)
            {
                int targetW = Math.Max(16, ((int)(bmp.Width * ResolutionScale)) / 2 * 2);
                int targetH = Math.Max(16, ((int)(bmp.Height * ResolutionScale)) / 2 * 2);

                if (_scaledBitmap == null || _scaledBitmap.Width != targetW || _scaledBitmap.Height != targetH)
                {
                    _scaledGraphics?.Dispose();
                    _scaledBitmap?.Dispose();

                    _scaledBitmap = new Bitmap(targetW, targetH, PixelFormat.Format32bppRgb);
                    _scaledGraphics = Graphics.FromImage(_scaledBitmap);
                    _scaledGraphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
                    _scaledGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                    _scaledGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighSpeed;
                    _scaledGraphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
                }

                _scaledGraphics!.DrawImage(bmp, new Rectangle(0, 0, targetW, targetH), new Rectangle(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
                _scaledBitmap!.Save(_compressionStream, _jpegCodec, _encoderParams);
            }
            else
            {
                bmp.Save(_compressionStream, _jpegCodec, _encoderParams);
            }

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
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return codecs[0];
        }

        public void Dispose()
        {
            _scaledGraphics?.Dispose();
            _scaledGraphics = null;
            _scaledBitmap?.Dispose();
            _scaledBitmap = null;

            _capturer.Dispose();
            _encoderParams.Dispose();
            _compressionStream.Dispose();
        }
    }
}
