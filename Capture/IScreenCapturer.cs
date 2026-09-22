using System;
using System.Drawing;

namespace VibeDesk.Capture
{
    public interface IScreenCapturer : IDisposable
    {
        string Name { get; }
        bool Initialize();
        Bitmap? Capture();
        int ScreenWidth { get; }
        int ScreenHeight { get; }
    }
}
