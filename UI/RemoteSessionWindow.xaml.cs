using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VibeDesk.Input;
using VibeDesk.Network;

namespace VibeDesk.UI
{
    public partial class RemoteSessionWindow : Window
    {
        private readonly VibeClient _client;
        private readonly DispatcherTimer _statsTimer;
        private bool _isFullscreen = false;
        private WindowStyle _previousStyle = WindowStyle.SingleBorderWindow;
        private WindowState _previousState = WindowState.Normal;

        public RemoteSessionWindow(VibeClient client)
        {
            _client = client;
            InitializeComponent();

            _client.OnFrameReceived += OnFrameReceived;
            _client.OnDisconnected += OnDisconnected;
            _client.OnClipboardReceived += OnClipboardReceived;

            _statsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _statsTimer.Tick += (s, e) => UpdateStats();
            _statsTimer.Start();

            Closed += (s, e) =>
            {
                _statsTimer.Stop();
                _client.OnFrameReceived -= OnFrameReceived;
                _client.OnDisconnected -= OnDisconnected;
                _client.OnClipboardReceived -= OnClipboardReceived;
                _client.Disconnect();
            };
        }

        private volatile BitmapSource? _latestPendingFrame;
        private int _isRendering = 0;

        private void OnFrameReceived(byte[] jpegBytes)
        {
            try
            {
                var bitmap = new BitmapImage();
                using (var ms = new MemoryStream(jpegBytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze(); // Enables cross-thread access and high performance
                }

                _latestPendingFrame = bitmap;

                if (System.Threading.Interlocked.CompareExchange(ref _isRendering, 1, 0) == 0)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            var frame = _latestPendingFrame;
                            if (frame != null)
                            {
                                ImgScreen.Source = frame;
                                if (OverlayConnecting.Visibility == Visibility.Visible)
                                {
                                    OverlayConnecting.Visibility = Visibility.Collapsed;
                                }
                            }
                        }
                        finally
                        {
                            System.Threading.Interlocked.Exchange(ref _isRendering, 0);
                        }
                    }, DispatcherPriority.Render);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    TxtConnectingStatus.Text = $"Ошибка распаковки кадра: {ex.Message}";
                });
            }
        }

        private void OnDisconnected()
        {
            Dispatcher.InvokeAsync(() =>
            {
                MessageBox.Show(this, "Сеанс удаленного доступа завершен.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            });
        }

        private void OnClipboardReceived(string text)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch { }
            });
        }

        private void UpdateStats()
        {
            if (_client == null || !_client.IsConnected) return;
            TxtStats.Text = $"FPS: {_client.CurrentFps} | Пинг: {_client.PingMs} мс | {_client.IncomingKbps:F0} Кб/с";
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

        private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullscreen();
        }

        private void CmbLiveMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbLiveMode == null || _client == null || !_client.IsConnected) return;

            (float scale, int fps, int quality) = CmbLiveMode.SelectedIndex switch
            {
                0 => (1.0f, 60, 75),   // 100% Четкость
                1 => (0.75f, 60, 70),  // 75% Баланс
                2 => (0.50f, 60, 60),  // 50% Турбо FPS
                3 => (0.33f, 30, 50),  // 33% Эконом
                _ => (1.0f, 60, 70)
            };

            _client.SendStreamSettings(scale, fps, quality);
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
                    string text = Clipboard.GetText();
                    _client.SendClipboard(text);
                    MessageBox.Show(this, "Буфер обмена успешно передан на удаленный компьютер.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Ошибка доступа к буферу: {ex.Message}", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnTaskMgr_Click(object sender, RoutedEventArgs e)
        {
            // Send Ctrl+Shift+Esc
            _client.SendKey(0x11, true); // VK_CONTROL
            _client.SendKey(0x10, true); // VK_SHIFT
            _client.SendKey(0x1B, true); // VK_ESCAPE
            _client.SendKey(0x1B, false);
            _client.SendKey(0x10, false);
            _client.SendKey(0x11, false);
        }

        private void BtnWinKey_Click(object sender, RoutedEventArgs e)
        {
            // Send Windows Key (VK_LWIN = 0x5B)
            _client.SendKey(0x5B, true);
            _client.SendKey(0x5B, false);
        }

        private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
