using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VibeDesk.Capture
{
    public class DxgiScreenCapture : IScreenCapturer
    {
        public string Name => "DirectX 11 (DXGI)";
        public int ScreenWidth { get; private set; }
        public int ScreenHeight { get; private set; }

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIOutputDuplication? _deskDupl;
        private ID3D11Texture2D? _stagingTexture;
        private Bitmap? _reusableBitmap;

        public bool Initialize()
        {
            try
            {
                Cleanup();

                var featureLevels = new[]
                {
                    FeatureLevel.Level_11_1,
                    FeatureLevel.Level_11_0,
                    FeatureLevel.Level_10_1,
                    FeatureLevel.Level_10_0
                };

                var res = D3D11.D3D11CreateDevice(
                    null,
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out _device,
                    out _context);

                if (res.Failure || _device == null || _context == null)
                {
                    return false;
                }

                using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
                if (dxgiDevice == null) return false;

                using var adapter = dxgiDevice.GetAdapter();
                if (adapter == null) return false;

                if (adapter.EnumOutputs(0, out var output).Failure || output == null)
                {
                    return false;
                }

                using (output)
                {
                    using var output1 = output.QueryInterface<IDXGIOutput1>();
                    if (output1 == null) return false;

                    var desc = output.Description;
                    ScreenWidth = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                    ScreenHeight = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                    _deskDupl = output1.DuplicateOutput(_device);
                    if (_deskDupl == null) return false;

                    var textureDesc = new Texture2DDescription
                    {
                        Width = (uint)ScreenWidth,
                        Height = (uint)ScreenHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };

                    _stagingTexture = _device.CreateTexture2D(textureDesc);
                    _reusableBitmap = new Bitmap(ScreenWidth, ScreenHeight, PixelFormat.Format32bppRgb);

                    return true;
                }
            }
            catch (Exception)
            {
                Cleanup();
                return false;
            }
        }

        public unsafe Bitmap? Capture()
        {
            if (_deskDupl == null || _device == null || _context == null || _stagingTexture == null || _reusableBitmap == null)
            {
                if (!Initialize()) return null;
            }

            try
            {
                var acquireResult = _deskDupl!.AcquireNextFrame(20, out _, out var desktopResource);

                if (acquireResult.Failure)
                {
                    // DXGI_ERROR_WAIT_TIMEOUT is normal if screen did not update
                    if (acquireResult.Code == unchecked((int)0x887A0027)) // DXGI_ERROR_WAIT_TIMEOUT
                    {
                        return _reusableBitmap; // return last frame
                    }

                    // On access lost (UAC / resolution change), reinitialize
                    if (acquireResult.Code == unchecked((int)0x887A0026)) // DXGI_ERROR_ACCESS_LOST
                    {
                        Initialize();
                    }
                    return null;
                }

                using (desktopResource)
                {
                    using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    if (desktopTexture != null)
                    {
                        _context!.CopyResource(_stagingTexture, desktopTexture);

                        var mapped = _context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                        try
                        {
                            var bmpData = _reusableBitmap!.LockBits(
                                new Rectangle(0, 0, ScreenWidth, ScreenHeight),
                                ImageLockMode.WriteOnly,
                                PixelFormat.Format32bppRgb);

                            try
                            {
                                byte* srcPtr = (byte*)mapped.DataPointer;
                                byte* dstPtr = (byte*)bmpData.Scan0;
                                int bytesPerLine = ScreenWidth * 4;

                                for (int y = 0; y < ScreenHeight; y++)
                                {
                                    Buffer.MemoryCopy(
                                        srcPtr + (y * mapped.RowPitch),
                                        dstPtr + (y * bmpData.Stride),
                                        bytesPerLine,
                                        bytesPerLine);
                                }
                            }
                            finally
                            {
                                _reusableBitmap.UnlockBits(bmpData);
                            }
                        }
                        finally
                        {
                            _context.Unmap(_stagingTexture, 0);
                        }
                    }
                }

                _deskDupl.ReleaseFrame();
                return _reusableBitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Cleanup()
        {
            _reusableBitmap?.Dispose();
            _reusableBitmap = null;

            _stagingTexture?.Dispose();
            _stagingTexture = null;

            _deskDupl?.Dispose();
            _deskDupl = null;

            _context?.Dispose();
            _context = null;

            _device?.Dispose();
            _device = null;
        }

        public void Dispose()
        {
            Cleanup();
        }
    }
}
