using System;
using System.Drawing;
using System.Drawing.Imaging;
using VibeDesk.Native;

namespace VibeDesk.Capture
{
    public class GdiScreenCapture : IScreenCapturer
    {
        public string Name => "GDI+ (Win32)";
        public int ScreenWidth { get; private set; }
        public int ScreenHeight { get; private set; }

        private Bitmap? _reusableBitmap;
        private Graphics? _graphics;

        public bool Initialize()
        {
            ScreenWidth = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            ScreenHeight = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            if (ScreenWidth <= 0 || ScreenHeight <= 0)
            {
                ScreenWidth = 1920;
                ScreenHeight = 1080;
            }

            _reusableBitmap = new Bitmap(ScreenWidth, ScreenHeight, PixelFormat.Format32bppRgb);
            _graphics = Graphics.FromImage(_reusableBitmap);
            return true;
        }

        public Bitmap? Capture()
        {
            if (_graphics == null || _reusableBitmap == null)
            {
                if (!Initialize()) return null;
            }

            try
            {
                // Capture primary screen including layered windows / mouse
                _graphics!.CopyFromScreen(0, 0, 0, 0, new Size(ScreenWidth, ScreenHeight), CopyPixelOperation.SourceCopy);
                return _reusableBitmap;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _graphics?.Dispose();
            _graphics = null;
            _reusableBitmap?.Dispose();
            _reusableBitmap = null;
        }
    }
}
