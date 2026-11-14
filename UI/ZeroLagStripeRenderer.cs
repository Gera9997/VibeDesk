using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeDesk.Capture;
using VibeDesk.Network.Protocol;

namespace VibeDesk.UI
{
    public sealed class ZeroLagStripeRenderer : IDisposable
    {
        private const int MaxStripes = 32;

        private sealed class CompressedSlot
        {
            public AssembledStripe Stripe;
        }

        private sealed class DecodedSlot
        {
            public ushort StripeY;
            public ushort Width;
            public ushort Height;
            public byte TotalStrips;
            public int Stride;
            public byte[] RawBgraBuffer = null!;
        }

        private readonly CompressedSlot?[] _compressedMailbox = new CompressedSlot?[MaxStripes];
        private readonly DecodedSlot?[] _decodedMailbox = new DecodedSlot?[MaxStripes];
        private readonly SemaphoreSlim _decodeSignal = new(0, MaxStripes);
        private readonly CancellationTokenSource _cts = new();

        private readonly Image _targetImage;
        private WriteableBitmap? _writeableBitmap;
        private int _bitmapWidth = 0;
        private int _bitmapHeight = 0;

        public event Action? OnFirstFrameRendered;
        public int RenderedFramesCount { get; private set; }
        public WriteableBitmap? CanvasBitmap => _writeableBitmap;
        public int CanvasWidth => _bitmapWidth;
        public int CanvasHeight => _bitmapHeight;

        public ZeroLagStripeRenderer(Image targetImage)
        {
            _targetImage = targetImage ?? throw new ArgumentNullException(nameof(targetImage));
            Task.Run(BackgroundDecodeLoopAsync);
            CompositionTarget.Rendering += OnWpfVSyncRender;
        }

        // 1. Вызывается из потока сокета/сборщика (наносекунды, никогда не блокирует сокет)
        public void EnqueueAssembledStripe(in AssembledStripe stripe)
        {
            if (stripe.StripeIndex >= MaxStripes)
            {
                ArrayPool<byte>.Shared.Return(stripe.RentedBuffer);
                return;
            }

            var newSlot = new CompressedSlot { Stripe = stripe };
            var oldSlot = Interlocked.Exchange(ref _compressedMailbox[stripe.StripeIndex], newSlot);

            if (oldSlot != null)
            {
                // Старая полоса для этого же Y устарела до декодирования — возвращаем память в пул
                ArrayPool<byte>.Shared.Return(oldSlot.Stripe.RentedBuffer);
            }
            else
            {
                try { _decodeSignal.Release(); } catch { }
            }
        }

        // 2. Фоновый поток декодирования JPEG (не нагружает ни сокет, ни UI-поток WPF)
        private async Task BackgroundDecodeLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await _decodeSignal.WaitAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                for (int i = 0; i < MaxStripes; i++)
                {
                    var slot = Interlocked.Exchange(ref _compressedMailbox[i], null);
                    if (slot == null) continue;

                    try
                    {
                        int startOffset = 0;
                        byte totalStrips = (byte)ScreenCaptureManager.StripCount;

                        // Проверяем наличие 2-байтового заголовка ([0]=stripeIdx, [1]=totalStrips) перед JPEG SOI (0xFF, 0xD8)
                        if (slot.Stripe.DataLength >= 4 &&
                            slot.Stripe.RentedBuffer[0] < 32 &&
                            slot.Stripe.RentedBuffer[2] == 0xFF &&
                            slot.Stripe.RentedBuffer[3] == 0xD8)
                        {
                            startOffset = 2;
                            if (slot.Stripe.RentedBuffer[1] > 0)
                            {
                                totalStrips = slot.Stripe.RentedBuffer[1];
                            }
                        }

                        int jpegLength = slot.Stripe.DataLength - startOffset;
                        if (jpegLength <= 4) continue;

                        using var ms = new MemoryStream(slot.Stripe.RentedBuffer, startOffset, jpegLength, writable: false);
                        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                        BitmapSource frame = decoder.Frames[0];

                        if (frame.Format != PixelFormats.Bgr32 && frame.Format != PixelFormats.Bgra32)
                        {
                            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
                        }

                        int width = frame.PixelWidth;
                        int height = frame.PixelHeight;
                        int stride = width * 4;
                        int byteLen = stride * height;

                        byte[] rawPixels = ArrayPool<byte>.Shared.Rent(byteLen);
                        frame.CopyPixels(rawPixels, stride, 0);

                        // Позиция полосы строго рассчитывается от ее реальной декодированной высоты
                        ushort actualY = (ushort)(slot.Stripe.StripeIndex * height);

                        var decodedSlot = new DecodedSlot
                        {
                            StripeY = actualY,
                            Width = (ushort)width,
                            Height = (ushort)height,
                            TotalStrips = totalStrips,
                            Stride = stride,
                            RawBgraBuffer = rawPixels
                        };

                        var oldDecoded = Interlocked.Exchange(ref _decodedMailbox[i], decodedSlot);
                        if (oldDecoded != null)
                        {
                            ArrayPool<byte>.Shared.Return(oldDecoded.RawBgraBuffer);
                        }
                    }
                    catch
                    {
                        // Игнорируем ошибки поврежденных пакетов
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(slot.Stripe.RentedBuffer);
                    }
                }
            }
        }

        // 3. Отрисовка ровно 1 раз за VSync монитора в едином блоке Lock/Unlock
        private void OnWpfVSyncRender(object? sender, EventArgs e)
        {
            bool locked = false;
            try
            {
                for (int i = 0; i < MaxStripes; i++)
                {
                    var decoded = Interlocked.Exchange(ref _decodedMailbox[i], null);
                    if (decoded == null) continue;

                    int fullW = decoded.Width;
                    int fullH = decoded.Height * decoded.TotalStrips;

                    if (_writeableBitmap == null || _bitmapWidth != fullW || _bitmapHeight != fullH)
                    {
                        if (locked && _writeableBitmap != null)
                        {
                            _writeableBitmap.Unlock();
                            locked = false;
                        }

                        _bitmapWidth = fullW;
                        _bitmapHeight = fullH;
                        _writeableBitmap = new WriteableBitmap(fullW, fullH, 96, 96, PixelFormats.Bgr32, null);
                        _targetImage.Source = _writeableBitmap;
                    }

                    if (!locked && _writeableBitmap != null)
                    {
                        _writeableBitmap.Lock();
                        locked = true;
                    }

                    if (_writeableBitmap != null)
                    {
                        int safeHeight = Math.Min(decoded.Height, _bitmapHeight - decoded.StripeY);
                        int safeWidth = Math.Min(decoded.Width, _bitmapWidth);

                        if (safeHeight > 0 && safeWidth > 0)
                        {
                            _writeableBitmap.WritePixels(
                                new Int32Rect(0, decoded.StripeY, safeWidth, safeHeight),
                                decoded.RawBgraBuffer,
                                decoded.Stride,
                                0);
                        }
                    }

                    ArrayPool<byte>.Shared.Return(decoded.RawBgraBuffer);
                    RenderedFramesCount++;

                    if (RenderedFramesCount == 1)
                    {
                        OnFirstFrameRendered?.Invoke();
                    }
                }
            }
            finally
            {
                if (locked && _writeableBitmap != null)
                {
                    _writeableBitmap.Unlock();
                }
            }
        }

        public void Dispose()
        {
            CompositionTarget.Rendering -= OnWpfVSyncRender;
            _cts.Cancel();
            _decodeSignal.Dispose();
        }
    }
}
