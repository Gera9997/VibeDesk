using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace VibeDesk.Capture
{
    public class ScreenCaptureManager : IDisposable
    {
        private IScreenCapturer _capturer;
        private readonly ImageCodecInfo _jpegCodec;
        private readonly EncoderParameters _encoderParams;
        private readonly MemoryStream _compressionStream = new(1024 * 512);

        private long _lastFrameHash = 0;
        private int _consecutiveIdenticalFrames = 0;

        public string ActiveEngineName => _capturer.Name;
        public int ScreenWidth => _capturer.ScreenWidth;
        public int ScreenHeight => _capturer.ScreenHeight;

        public ScreenCaptureManager(int jpegQuality = 70)
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
            _lastFrameHash = 0;
            _consecutiveIdenticalFrames = 0;
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

            // Quick sample hash to check if desktop changed (sample 64 points)
            if (!forceFrame)
            {
                long sampleHash = CalculateSampleHash(bmp);
                if (sampleHash == _lastFrameHash)
                {
                    _consecutiveIdenticalFrames++;
                    // Send at least 1 frame every 30 skipped frames as a keepalive
                    if (_consecutiveIdenticalFrames < 30)
                    {
                        return null; // Screen unchanged, skip frame
                    }
                }
                _lastFrameHash = sampleHash;
                _consecutiveIdenticalFrames = 0;
            }

            _compressionStream.SetLength(0);
            bmp.Save(_compressionStream, _jpegCodec, _encoderParams);
            return _compressionStream.ToArray();
        }

        private static unsafe long CalculateSampleHash(Bitmap bmp)
        {
            try
            {
                int w = bmp.Width;
                int h = bmp.Height;
                if (w < 10 || h < 10) return 0;

                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                long hash = 17;
                try
                {
                    int* ptr = (int*)data.Scan0;
                    int strideInts = data.Stride / 4;
                    // Sample a 8x8 grid across the screen
                    int stepX = w / 8;
                    int stepY = h / 8;

                    for (int y = 0; y < 8; y++)
                    {
                        int rowIdx = y * stepY * strideInts;
                        for (int x = 0; x < 8; x++)
                        {
                            int val = ptr[rowIdx + (x * stepX)];
                            hash = unchecked(hash * 31 + val);
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
                return hash;
            }
            catch
            {
                return 0;
            }
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
