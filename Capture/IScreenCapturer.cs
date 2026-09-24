using System;
using System.Drawing;

namespace VibeDesk.Capture
{
    public interface IScreenCapturer : IDisposable
    {
        string Name { get; }
        bool Initialize();
        Bitmap? Capture();
        bool HasNewFrame { get; }
        int ScreenWidth { get; }
        int ScreenHeight { get; }
    }
}
