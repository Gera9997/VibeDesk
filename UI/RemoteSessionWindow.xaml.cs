using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeDesk.Capture;
using VibeDesk.Input;
using VibeDesk.Network;

namespace VibeDesk.UI
{
    public partial class RemoteSessionWindow : Window
    {
        private readonly VibeClient _client;
        private readonly ZeroLagStripeRenderer _stripeRenderer;
        private readonly DispatcherTimer _statsTimer;
        private bool _isFullscreen = false;
        private WindowStyle _previousStyle = WindowStyle.SingleBorderWindow;
        private WindowState _previousState = WindowState.Normal;

        // Хранит только самый последний полученный пакет для каждой из 8 полос
        private readonly byte[]?[] _latestStripBytes = new byte[]?[ScreenCaptureManager.StripCount];
        private byte[]? _latestFullFrameBytes = null;
        private byte _currentStreamMode = 0; // 0 = TurboMjpeg (полосы), 1 = VideoStream (full frame)
        private readonly AutoResetEvent _frameSignal = new AutoResetEvent(false);
        private readonly Thread? _renderThread;
        private volatile bool _renderRunning = true;

        private WriteableBitmap? _canvasBitmap;
        private int _canvasW = 0;
        private int _canvasH = 0;

        private struct DecodedStrip
        {
            public int StripIndex;
            public int TotalStrips;
            public int Width;
            public int Height;
            public int Stride;
            public byte[] Pixels;
            public uint FrameId;
            public float HostCapMs;
            public float HostScaleMs;
            public float HostEncodeMs;
            public double DecodeMs;
            public long QueueTimestampTicks;
            public int PayloadSizeBytes;
        }

        private readonly List<string> _perfLogEntries = new(300);
        private readonly object _perfLogLock = new();
        private string? _perfLogFilePath;
        private double _avgCapMs = 0;
        private double _avgScaleMs = 0;
        private double _avgEncodeMs = 0;
        private double _avgHostMs = 0;
        private double _avgQueueMs = 0;
        private double _avgDecodeMs = 0;
        private double _avgRenderMs = 0;
        private double _avgTotalMs = 0;
        private int _sampleCount = 0;

        private readonly ConcurrentQueue<DecodedStrip> _decodedQueue = new();
        private int _uiUpdateScheduled = 0;
        private readonly string? _remoteDeviceId;
        private int _renderedStripsCount = 0;
        private bool _hasSavedInitialThumbnail = false;

        public RemoteSessionWindow(VibeClient client, string? remoteDeviceId = null)
        {
            _client = client;
            _remoteDeviceId = remoteDeviceId;
            InitializeComponent();

            _stripeRenderer = new ZeroLagStripeRenderer(ImgScreen);
            _stripeRenderer.OnFirstFrameRendered += () =>
            {
                Dispatcher.InvokeAsync(() => OverlayConnecting.Visibility = Visibility.Collapsed);
            };

            _client.OnStripeReady += stripe => _stripeRenderer.EnqueueAssembledStripe(stripe);
            _client.OnFrameReceived += OnFrameReceived;
            _client.OnDisconnected += OnDisconnected;
            _client.OnClipboardReceived += OnClipboardReceived;
            _client.OnCursorPositionReceived += OnCursorPositionReceived;

            _renderRunning = true;
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "VibeDesk_DecodeLoop",
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();

            _statsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _statsTimer.Tick += (s, e) => UpdateStats();
            _statsTimer.Start();

            Closed += (s, e) =>
            {
                SaveThumbnail();
                _renderRunning = false;
                _frameSignal.Set();
                _statsTimer.Stop();
                _client.OnFrameReceived -= OnFrameReceived;
                _client.OnDisconnected -= OnDisconnected;
                _client.OnClipboardReceived -= OnClipboardReceived;
                _client.OnCursorPositionReceived -= OnCursorPositionReceived;
                _client.Disconnect();
                _renderThread?.Join(200);
                _stripeRenderer.Dispose();
            };
        }

        private void OnFrameReceived(byte[] payload)
        {
            if (payload == null || payload.Length < 4) return;

            if (payload[0] < 32 && payload[1] == ScreenCaptureManager.StripCount && payload[2] == 0xFF && payload[3] == 0xD8)
            {
                // Полосовой режим (Turbo-MJPEG)
                int stripIdx = payload[0];
                Interlocked.Exchange(ref _latestStripBytes[stripIdx], payload);
                _frameSignal.Set();
            }
            else if (payload[0] == 0xAA && (payload[1] == 0x01 || payload[1] == 0x02))
            {
                // Полнокадровый видеопоток (Full Frame mode, v1 или v2 с телеметрией)
                Interlocked.Exchange(ref _latestFullFrameBytes, payload);
                _frameSignal.Set();
            }
        }

        private void RenderLoop()
        {
            using var decodeStream = new MemoryStream(1024 * 512);

            while (_renderRunning)
            {
                _frameSignal.WaitOne(50);
                if (!_renderRunning) break;

                bool decodedAny = false;

                // 1. Прием и декодирование полного кадра (Видеорежим)
                byte[]? fullFramePayload = Interlocked.Exchange(ref _latestFullFrameBytes, null);
                if (fullFramePayload != null)
                {
                    try
                    {
                        int jpegOffset = 2;
                        uint frameId = 0;
                        float hostCapMs = 0;
                        float hostScaleMs = 0;
                        float hostEncodeMs = 0;

                        if (fullFramePayload.Length >= 26 && fullFramePayload[0] == 0xAA && fullFramePayload[1] == 0x02)
                        {
                            jpegOffset = 26;
                            frameId = BinaryPrimitives.ReadUInt32LittleEndian(fullFramePayload.AsSpan(2, 4));
                            hostCapMs = BinaryPrimitives.ReadSingleLittleEndian(fullFramePayload.AsSpan(6, 4));
                            hostScaleMs = BinaryPrimitives.ReadSingleLittleEndian(fullFramePayload.AsSpan(10, 4));
                            hostEncodeMs = BinaryPrimitives.ReadSingleLittleEndian(fullFramePayload.AsSpan(14, 4));
                        }

                        long tDecodeStart = Stopwatch.GetTimestamp();
                        decodeStream.Position = 0;
                        decodeStream.SetLength(0);
                        decodeStream.Write(fullFramePayload, jpegOffset, fullFramePayload.Length - jpegOffset);
                        decodeStream.Position = 0;

                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = decodeStream;
                        bmp.EndInit();
                        bmp.Freeze();

                        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
                        converted.Freeze();
                        int w = converted.PixelWidth;
                        int h = converted.PixelHeight;
                        int stride = w * 4;
                        int bufferSize = stride * h;
                        byte[] pixels = ArrayPool<byte>.Shared.Rent(bufferSize);
                        converted.CopyPixels(pixels, stride, 0);

                        long tDecodeEnd = Stopwatch.GetTimestamp();
                        double decodeMs = (tDecodeEnd - tDecodeStart) * 1000.0 / Stopwatch.Frequency;

                        while (_decodedQueue.Count > 8)
                        {
                            if (_decodedQueue.TryDequeue(out var stale))
                            {
                                ArrayPool<byte>.Shared.Return(stale.Pixels);
                            }
                        }

                        _decodedQueue.Enqueue(new DecodedStrip
                        {
                            StripIndex = -1, // Маркер полного кадра
                            TotalStrips = 1,
                            Width = w,
                            Height = h,
                            Stride = stride,
                            Pixels = pixels,
                            FrameId = frameId,
                            HostCapMs = hostCapMs,
                            HostScaleMs = hostScaleMs,
                            HostEncodeMs = hostEncodeMs,
                            DecodeMs = decodeMs,
                            QueueTimestampTicks = Stopwatch.GetTimestamp(),
                            PayloadSizeBytes = fullFramePayload.Length
                        });
                        decodedAny = true;
                    }
                    catch { }
                }

                // 2. Прием и декодирование полос (Турбо-MJPEG)
                for (int s = 0; s < ScreenCaptureManager.StripCount; s++)
                {
                    byte[]? stripPayload = Interlocked.Exchange(ref _latestStripBytes[s], null);
                    if (stripPayload == null) continue;

                    try
                    {
                        int stripIdx = stripPayload[0];
                        int totalStrips = stripPayload[1];

                        decodeStream.Position = 0;
                        decodeStream.SetLength(0);
                        decodeStream.Write(stripPayload, 2, stripPayload.Length - 2);
                        decodeStream.Position = 0;

                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = decodeStream;
                        bmp.EndInit();
                        bmp.Freeze();

                        var converted = new FormatConvertedBitmap(bmp, PixelFormats.Bgr32, null, 0);
                        converted.Freeze();
                        int w = converted.PixelWidth;
                        int h = converted.PixelHeight;
                        int stride = w * 4;
                        int bufferSize = stride * h;
                        byte[] pixels = ArrayPool<byte>.Shared.Rent(bufferSize);
                        converted.CopyPixels(pixels, stride, 0);

                        // Защита от накопления очереди в случае подвисания окна: возвращаем буферы в пул
                        while (_decodedQueue.Count > 16)
                        {
                            if (_decodedQueue.TryDequeue(out var stale))
                            {
                                ArrayPool<byte>.Shared.Return(stale.Pixels);
                            }
                        }

                        _decodedQueue.Enqueue(new DecodedStrip
                        {
                            StripIndex = stripIdx,
                            TotalStrips = totalStrips,
                            Width = w,
                            Height = h,
                            Stride = stride,
                            Pixels = pixels
                        });
                        decodedAny = true;
                    }
                    catch { }
                }

                if (decodedAny && Interlocked.CompareExchange(ref _uiUpdateScheduled, 1, 0) == 0)
                {
                    Dispatcher.InvokeAsync(ApplyDecodedStripsToUi, DispatcherPriority.Render);
                }
            }
        }

        private void ApplyDecodedStripsToUi()
        {
            try
            {
                while (_decodedQueue.TryDequeue(out var strip))
                {
                    try
                    {
                        int fullW = strip.Width;
                        int fullH = strip.StripIndex == -1 ? strip.Height : (strip.Height * strip.TotalStrips);

                        if (_canvasBitmap == null || _canvasW != fullW || _canvasH != fullH)
                        {
                            _canvasW = fullW;
                            _canvasH = fullH;
                            _canvasBitmap = new WriteableBitmap(fullW, fullH, 96, 96, PixelFormats.Bgr32, null);
                            ImgScreen.Source = _canvasBitmap;
                        }

                        if (strip.StripIndex == -1)
                        {
                            // Отрисовка полного кадра в один проход с замером времени
                            long tRenderStart = Stopwatch.GetTimestamp();
                            _canvasBitmap.WritePixels(
                                new Int32Rect(0, 0, strip.Width, strip.Height),
                                strip.Pixels,
                                strip.Stride,
                                0);
                            long tRenderEnd = Stopwatch.GetTimestamp();
                            double renderMs = (tRenderEnd - tRenderStart) * 1000.0 / Stopwatch.Frequency;
                            double queueMs = (tRenderStart - strip.QueueTimestampTicks) * 1000.0 / Stopwatch.Frequency;

                            RecordFrameTiming(strip, queueMs, renderMs);
                        }
                        else
                        {
                            // Отрисовка отдельной полосы
                            int destY = strip.StripIndex * strip.Height;
                            if (destY + strip.Height <= _canvasH)
                            {
                                _canvasBitmap.WritePixels(
                                    new Int32Rect(0, destY, strip.Width, strip.Height),
                                    strip.Pixels,
                                    strip.Stride,
                                    0);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(strip.Pixels);
                    }
                }

                if (OverlayConnecting.Visibility == Visibility.Visible && _canvasBitmap != null)
                {
                    OverlayConnecting.Visibility = Visibility.Collapsed;
                }

                _renderedStripsCount++;
                if (!_hasSavedInitialThumbnail && _renderedStripsCount >= 10 && _canvasBitmap != null)
                {
                    _hasSavedInitialThumbnail = true;
                    SaveThumbnail();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _uiUpdateScheduled, 0);
                if (!_decodedQueue.IsEmpty && Interlocked.CompareExchange(ref _uiUpdateScheduled, 1, 0) == 0)
                {
                    Dispatcher.InvokeAsync(ApplyDecodedStripsToUi, DispatcherPriority.Render);
                }
            }
        }

        private void RecordFrameTiming(DecodedStrip strip, double queueMs, double renderMs)
        {
            double totalHostMs = strip.HostCapMs + strip.HostScaleMs + strip.HostEncodeMs;
            double clientPipelineMs = strip.DecodeMs + queueMs + renderMs;
            double totalPipelineMs = totalHostMs + clientPipelineMs;

            _sampleCount++;
            double alpha = _sampleCount == 1 ? 1.0 : 0.05;
            _avgCapMs = (1 - alpha) * _avgCapMs + alpha * strip.HostCapMs;
            _avgScaleMs = (1 - alpha) * _avgScaleMs + alpha * strip.HostScaleMs;
            _avgEncodeMs = (1 - alpha) * _avgEncodeMs + alpha * strip.HostEncodeMs;
            _avgHostMs = (1 - alpha) * _avgHostMs + alpha * totalHostMs;
            _avgQueueMs = (1 - alpha) * _avgQueueMs + alpha * queueMs;
            _avgDecodeMs = (1 - alpha) * _avgDecodeMs + alpha * strip.DecodeMs;
            _avgRenderMs = (1 - alpha) * _avgRenderMs + alpha * renderMs;
            _avgTotalMs = (1 - alpha) * _avgTotalMs + alpha * totalPipelineMs;

            string line = $"{DateTime.Now:HH:mm:ss.fff} [Кадр #{strip.FrameId}] Захват: {strip.HostCapMs:F1}мс | Ресайз: {strip.HostScaleMs:F1}мс | JPEG: {strip.HostEncodeMs:F1}мс | Очередь: {queueMs:F1}мс | Декод: {strip.DecodeMs:F1}мс | UI: {renderMs:F1}мс | Всего: {totalPipelineMs:F1}мс | {strip.PayloadSizeBytes / 1024.0:F1} КБ";

            lock (_perfLogLock)
            {
                if (_perfLogEntries.Count >= 500)
                {
                    _perfLogEntries.RemoveAt(0);
                }
                _perfLogEntries.Add(line);
            }

            AppendPerfLogToFile(line);

            if (DrawerPerfLogs.Visibility == Visibility.Visible)
            {
                TxtLiveSummary.Text =
                    $"Кадр: #{strip.FrameId} ({strip.Width}x{strip.Height}, {strip.PayloadSizeBytes / 1024.0:F1} КБ)\n" +
                    $"• Захват + Сжатие (Хост): {totalHostMs:F1} мс (Захват: {strip.HostCapMs:F1}мс, Ресайз: {strip.HostScaleMs:F1}мс, JPEG: {strip.HostEncodeMs:F1}мс)\n" +
                    $"• Очередь кадра:          {queueMs:F1} мс\n" +
                    $"• Декодирование (Клиент):  {strip.DecodeMs:F1} мс\n" +
                    $"• Отрисовка WPF UI:       {renderMs:F1} мс\n" +
                    $"────────────────────────────────────────\n" +
                    $"Суммарный пайплайн:       {totalPipelineMs:F1} мс (~{1000.0 / Math.Max(1, totalPipelineMs):F0} FPS макс) | Среднее: {_avgTotalMs:F1} мс";

                TxtPerfLog.AppendText(line + "\n");
                TxtPerfLog.ScrollToEnd();
            }
        }

        private void AppendPerfLogToFile(string line)
        {
            try
            {
                if (_perfLogFilePath == null)
                {
                    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeDesk", "logs");
                    System.IO.Directory.CreateDirectory(dir);
                    _perfLogFilePath = System.IO.Path.Combine(dir, "videostream_perf.log");
                }
                System.IO.File.AppendAllText(_perfLogFilePath, line + Environment.NewLine);
            }
            catch { }
        }

        private string GenerateFormattedPerfLog()
        {
            return $"====================================================================================\n" +
                   $" VibeDesk VideoStream Performance Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                   $"====================================================================================\n" +
                   $"Сводная статистика за сессию:\n" +
                   $"• Захват экрана на Хосте (DXGI/GDI): {_avgCapMs:F2} мс\n" +
                   $"• Скейлинг/ресайз на Хосте:          {_avgScaleMs:F2} мс\n" +
                   $"• Сжатие в Turbo-JPEG на Хосте:      {_avgEncodeMs:F2} мс\n" +
                   $"• Суммарное время Хоста:             {_avgHostMs:F2} мс\n" +
                   $"• Сеть + Очередь на Клиенте:         {_avgQueueMs:F2} мс\n" +
                   $"• Декодирование JPEG на Клиенте:     {_avgDecodeMs:F2} мс\n" +
                   $"• Отрисовка WPF UI на Клиенте:       {_avgRenderMs:F2} мс\n" +
                   $"────────────────────────────────────────────────────────────────────────────────────\n" +
                   $"Полная сквозная задержка (Total):    {_avgTotalMs:F2} мс (~{(1000.0 / Math.Max(1, _avgTotalMs)):F0} FPS)\n" +
                   $"Всего записано кадров в буфере:      {_perfLogEntries.Count}\n" +
                   $"====================================================================================\n\n" +
                   string.Join(Environment.NewLine, _perfLogEntries);
        }

        private void BtnTogglePerfDrawer_Click(object sender, RoutedEventArgs e)
        {
            DrawerPerfLogs.Visibility = DrawerPerfLogs.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (DrawerPerfLogs.Visibility == Visibility.Visible)
            {
                lock (_perfLogLock)
                {
                    TxtPerfLog.Text = string.Join("\n", _perfLogEntries) + "\n";
                    TxtPerfLog.ScrollToEnd();
                }
            }
        }

        private void BtnClosePerfDrawer_Click(object sender, RoutedEventArgs e)
        {
            DrawerPerfLogs.Visibility = Visibility.Collapsed;
        }

        private void BtnClearPerfLog_Click(object sender, RoutedEventArgs e)
        {
            lock (_perfLogLock)
            {
                _perfLogEntries.Clear();
            }
            TxtPerfLog.Clear();
            try
            {
                if (_perfLogFilePath != null && System.IO.File.Exists(_perfLogFilePath))
                {
                    System.IO.File.WriteAllText(_perfLogFilePath, "");
                }
            }
            catch { }
        }

        private void BtnExportTxt_Click(object sender, RoutedEventArgs e)
        {
            string content;
            lock (_perfLogLock)
            {
                content = GenerateFormattedPerfLog();
            }

            var saveDlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт лога таймингов видеопотока",
                Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*",
                FileName = $"VibeDesk_VideoStream_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            };

            if (saveDlg.ShowDialog(this) == true)
            {
                try
                {
                    System.IO.File.WriteAllText(saveDlg.FileName, content, System.Text.Encoding.UTF8);

                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(saveDlg.FileName) { UseShellExecute = true });
                    }
                    catch { }

                    VibeMessageBox.Show(this, $"Лог успешно сохранен:\n{saveDlg.FileName}\n\nФайл открыт в Блокноте.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    VibeMessageBox.Show(this, $"Ошибка при сохранении лога:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnOpenPerfLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_perfLogFilePath == null)
                {
                    string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeDesk", "logs");
                    System.IO.Directory.CreateDirectory(dir);
                    _perfLogFilePath = System.IO.Path.Combine(dir, "videostream_perf.log");
                }

                if (!System.IO.File.Exists(_perfLogFilePath))
                {
                    lock (_perfLogLock)
                    {
                        System.IO.File.WriteAllText(_perfLogFilePath, GenerateFormattedPerfLog(), System.Text.Encoding.UTF8);
                    }
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_perfLogFilePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                VibeMessageBox.Show(this, $"Не удалось открыть файл лога:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCopyPerfLog_Click(object sender, RoutedEventArgs e)
        {
            string content;
            lock (_perfLogLock)
            {
                content = GenerateFormattedPerfLog();
            }
            try
            {
                Clipboard.SetText(content);

                // Также сохраняем файл на Рабочий стол для максимального удобства (так как лог может быть огромным)
                string desktopFile = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"VibeDesk_VideoStream_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                System.IO.File.WriteAllText(desktopFile, content, System.Text.Encoding.UTF8);

                VibeMessageBox.Show(this, 
                    $"Лог скопирован в буфер обмена!\n\nТакже автоматически сохранен в .txt файл на Рабочем столе:\n{desktopFile}", 
                    "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                VibeMessageBox.Show(this, $"Не удалось скопировать лог:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void SaveThumbnail()
        {
            var targetBitmap = _stripeRenderer.CanvasBitmap ?? _canvasBitmap;
            if (string.IsNullOrWhiteSpace(_remoteDeviceId) || targetBitmap == null) return;
            try
            {
                string thumbDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeDesk", "thumbnails");
                System.IO.Directory.CreateDirectory(thumbDir);
                string thumbPath = System.IO.Path.Combine(thumbDir, $"{_remoteDeviceId}.jpg");

                int targetW = 320;
                int targetH = 180;
                var rtb = new RenderTargetBitmap(targetW, targetH, 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(targetBitmap, new Rect(0, 0, targetW, targetH));
                }
                rtb.Render(dv);

                var encoder = new JpegBitmapEncoder { QualityLevel = 75 };
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = new System.IO.FileStream(thumbPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
                encoder.Save(fs);
            }
            catch { }
        }

        private void OnDisconnected()
        {
            Dispatcher.InvokeAsync(() =>
            {
                SaveThumbnail();
                VibeMessageBox.Show(this, "Сеанс удаленного доступа завершен.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            });
        }

        private void OnClipboardReceived(string text)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try { Clipboard.SetText(text); } catch { }
            });
        }

        private void UpdateStats()
        {
            if (_client == null || !_client.IsConnected) return;
            string codecTag = _currentStreamMode == 0 ? "⚡ Турбо-MJPEG" : "🎬 Видеопоток";
            string fpsLabel = _currentStreamMode == 0 ? $"Полос/c: {_client.CurrentFps}" : $"Кадров/c: {_client.CurrentFps}";
            TxtStats.Text = $"[{codecTag}] {fpsLabel} | Пинг: {_client.PingMs} мс | {_client.IncomingKbps:F0} Кбит/с";
        }

        private void CmbStreamCodec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbStreamCodec == null || _client == null || !_client.IsConnected) return;
            _currentStreamMode = (byte)CmbStreamCodec.SelectedIndex;
            _client.SendStreamMode(_currentStreamMode);
            UpdateStats();
        }

        private void OnCursorPositionReceived(bool visible, int x, int y, int screenW, int screenH)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!visible)
                {
                    RemoteCursor.Visibility = Visibility.Collapsed;
                    return;
                }

                int effectiveW = _stripeRenderer.CanvasWidth > 0 ? _stripeRenderer.CanvasWidth : _canvasW;
                int effectiveH = _stripeRenderer.CanvasHeight > 0 ? _stripeRenderer.CanvasHeight : _canvasH;

                double scaleX = effectiveW > 0 && screenW > 0 ? (double)effectiveW / screenW : 1.0;
                double scaleY = effectiveH > 0 && screenH > 0 ? (double)effectiveH / screenH : 1.0;

                Canvas.SetLeft(RemoteCursor, x * scaleX);
                Canvas.SetTop(RemoteCursor, y * scaleY);
                RemoteCursor.Visibility = Visibility.Visible;
            }, DispatcherPriority.Render);
        }

        private (double normX, double normY)? GetNormalizedCoordinates(MouseEventArgs e)
        {
            if (ImgScreen.ActualWidth <= 0 || ImgScreen.ActualHeight <= 0) return null;

            var pos = e.GetPosition(ImgScreen);
            double nx = Math.Clamp(pos.X / ImgScreen.ActualWidth, 0.0, 1.0);
            double ny = Math.Clamp(pos.Y / ImgScreen.ActualHeight, 0.0, 1.0);
            return (nx, ny);
        }

        private void ImgScreen_MouseMove(object sender, MouseEventArgs e)
        {
            if (ImgScreen.Cursor != Cursors.None)
            {
                ImgScreen.Cursor = Cursors.None;
            }

            var coords = GetNormalizedCoordinates(e);
            if (coords.HasValue)
            {
                _client.SendMouseMove(coords.Value.normX, coords.Value.normY);
            }
        }

        private void ImgScreen_MouseDown(object sender, MouseButtonEventArgs e)
        {
            ImgScreen.Focus();
            VibeMouseButton? btn = e.ChangedButton switch
            {
                MouseButton.Left => VibeMouseButton.Left,
                MouseButton.Right => VibeMouseButton.Right,
                MouseButton.Middle => VibeMouseButton.Middle,
                _ => null
            };
            if (btn.HasValue)
            {
                _client.SendMouseButton(btn.Value, true);
                e.Handled = true;
            }
        }

        private void ImgScreen_MouseUp(object sender, MouseButtonEventArgs e)
        {
            VibeMouseButton? btn = e.ChangedButton switch
            {
                MouseButton.Left => VibeMouseButton.Left,
                MouseButton.Right => VibeMouseButton.Right,
                MouseButton.Middle => VibeMouseButton.Middle,
                _ => null
            };
            if (btn.HasValue)
            {
                _client.SendMouseButton(btn.Value, false);
                e.Handled = true;
            }
        }

        private void ImgScreen_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            _client.SendMouseWheel(e.Delta);
            e.Handled = true;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F3)
            {
                BtnTogglePerfDrawer_Click(sender, e);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }
            int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
            if (vk > 0)
            {
                _client.SendKey((ushort)vk, true);
                e.Handled = true;
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
            if (vk > 0)
            {
                _client.SendKey((ushort)vk, false);
                e.Handled = true;
            }
        }

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void CmbLiveMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLiveMode == null || _client == null || !_client.IsConnected) return;
            (float scale, int fps, int quality, int bitrate) = CmbLiveMode.SelectedIndex switch
            {
                0 => (1.0f, 60, 75, 25000), // 100% Чёткость (до 25 Мбит/с)
                1 => (0.75f, 60, 65, 15000), // 75% Баланс (до 15 Мбит/с)
                2 => (0.50f, 60, 55, 8000),  // 50% Турбо FPS (до 8 Мбит/с)
                3 => (0.33f, 30, 45, 4000),  // 33% Эконом (до 4 Мбит/с)
                _ => (0.75f, 60, 65, 15000)
            };
            _client.SendStreamSettings(scale, fps, quality, bitrate);
        }

        private void ToggleFullscreen()
        {
            if (!_isFullscreen)
            {
                _previousStyle = WindowStyle;
                _previousState = WindowState;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                _isFullscreen = true;
            }
            else
            {
                WindowStyle = _previousStyle;
                WindowState = _previousState;
                _isFullscreen = false;
            }
        }

        private void BtnSyncClipboard_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    _client.SendClipboard(Clipboard.GetText());
                }
            }
            catch { }
        }

        private void BtnTaskMgr_Click(object sender, RoutedEventArgs e)
        {
            _client.SendKey(0x11, true);
            _client.SendKey(0x10, true);
            _client.SendKey(0x1B, true);
            _client.SendKey(0x1B, false);
            _client.SendKey(0x10, false);
            _client.SendKey(0x11, false);
        }

        private void BtnWinKey_Click(object sender, RoutedEventArgs e)
        {
            _client.SendKey(0x5B, true);
            _client.SendKey(0x5B, false);
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e) => Close();
    }
}
