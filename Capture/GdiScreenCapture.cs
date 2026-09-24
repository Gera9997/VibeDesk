using System;
using System.Drawing;
using System.Drawing.Imaging;
using VibeDesk.Native;

namespace VibeDesk.Capture
{
    public class GdiScreenCapture : IScreenCapturer
    {
        public string Name => "GDI+ (Win32)";
        public bool HasNewFrame => true;
        public int ScreenWidth { get; private set; }
        public int ScreenHeight { get; private set; }

        private Bitmap? _reusableBitmap;

        public bool Initialize()
        {
            ScreenWidth = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            ScreenHeight = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);

            if (ScreenWidth <= 0 || ScreenHeight <= 0)
            {
                ScreenWidth = 1920;
                ScreenHeight = 1080;
            }

            _reusableBitmap?.Dispose();
            _reusableBitmap = new Bitmap(ScreenWidth, ScreenHeight, PixelFormat.Format32bppRgb);
            return true;
        }

        public Bitmap? Capture()
        {
            if (_reusableBitmap == null)
            {
                if (!Initialize()) return null;
            }

            try
            {
                using (var g = Graphics.FromImage(_reusableBitmap!))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(ScreenWidth, ScreenHeight), CopyPixelOperation.SourceCopy);
                }
                return _reusableBitmap;
            }
            catch
            {
                // Fallback: Direct GDI DC copy
                try
                {
                    IntPtr hdcSrc = Win32.GetDC(IntPtr.Zero);
                    IntPtr hdcDest = Win32.CreateCompatibleDC(hdcSrc);
                    IntPtr hBmp = Win32.CreateCompatibleBitmap(hdcSrc, ScreenWidth, ScreenHeight);
                    IntPtr hOld = Win32.SelectObject(hdcDest, hBmp);

                    Win32.BitBlt(hdcDest, 0, 0, ScreenWidth, ScreenHeight, hdcSrc, 0, 0, Win32.SRCCOPY);

                    using (var captured = Image.FromHbitmap(hBmp))
                    using (var g = Graphics.FromImage(_reusableBitmap!))
                    {
                        g.DrawImage(captured, 0, 0, ScreenWidth, ScreenHeight);
                    }

                    Win32.SelectObject(hdcDest, hOld);
                    Win32.DeleteObject(hBmp);
                    Win32.DeleteDC(hdcDest);
                    Win32.ReleaseDC(IntPtr.Zero, hdcSrc);
                    return _reusableBitmap;
                }
                catch
                {
                    return null;
                }
            }
        }

        public void Dispose()
        {
            _reusableBitmap?.Dispose();
            _reusableBitmap = null;
        }
    }
}
