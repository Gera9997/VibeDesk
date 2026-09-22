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

        public string ActiveEngineName => _capturer.Name;
        public int ScreenWidth => _capturer.ScreenWidth;
        public int ScreenHeight => _capturer.ScreenHeight;

        public ScreenCaptureManager(int jpegQuality = 65)
        {
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

        public void ResetForceFrame()
        {
            // Ready for immediate next frame
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

            // Draw host's real cursor onto the bitmap so it's visible in remote stream
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
            var codecs = ImageCodecInfo.GetImageDecoders();
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
            _capturer.Dispose();
            _encoderParams.Dispose();
            _compressionStream.Dispose();
        }
    }
}
