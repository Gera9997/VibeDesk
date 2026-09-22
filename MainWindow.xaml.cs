using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VibeDesk.Network;
using VibeDesk.Network.P2P;
using VibeDesk.UI;

namespace VibeDesk
{
    public partial class MainWindow : Window
    {
        private readonly string _myDeviceId;
        private readonly VibeHost _host;
        private readonly P2PSignaling _signaling;
        private IPEndPoint? _myPublicEndPoint;
        private string _myLocalIp = "127.0.0.1";
        private int _targetFps = 60;

        public MainWindow()
        {
            InitializeComponent();

            _myDeviceId = GetOrCreateDeviceId();
            TxtMyId.Text = FormatId(_myDeviceId);

            _host = new VibeHost(VibeHost.DefaultPort);
            _signaling = new P2PSignaling();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            FindLocalIp();
            StartLocalHost();
            await InitializeP2PAsync();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            _host.Dispose();
            _signaling.Dispose();
        }

        private string GetOrCreateDeviceId()
        {
            try
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeDesk");
                Directory.CreateDirectory(appData);
                string idFile = Path.Combine(appData, "device_id.txt");

                if (File.Exists(idFile))
                {
                    string savedId = File.ReadAllText(idFile).Trim();
                    if (savedId.Length == 6 && int.TryParse(savedId, out _))
                    {
                        return savedId;
                    }
                }

                // Generate new 6-digit ID
                int randomId = new Random().Next(100000, 999999);
                string newId = randomId.ToString();
                File.WriteAllText(idFile, newId);
                return newId;
            }
            catch
            {
                return (Math.Abs(Environment.MachineName.GetHashCode()) % 900000 + 100000).ToString();
            }
        }

        private static string FormatId(string id)
        {
            if (id.Length == 6)
            {
                return $"{id.Substring(0, 3)} {id.Substring(3, 3)}";
            }
            return id;
        }

        private void FindLocalIp()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (ip != null)
                {
                    _myLocalIp = ip.ToString();
                    TxtLocalIp.Text = $"{_myLocalIp}:{_host.Port}";
                }
            }
            catch
            {
                TxtLocalIp.Text = "127.0.0.1:15890";
            }
        }

        private void StartLocalHost()
        {
            _host.OnStatusChanged += msg =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    TxtHostStatus.Text = _host.HasClient ? "Сеанс активен" : "Ожидание подключения...";
                    IndicatorHost.Background = _host.HasClient ? new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)) : new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0x53));
                    TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({_targetFps} FPS)";
                });
            };

            bool started = _host.Start(_targetFps);
            if (started)
            {
                TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({_targetFps} FPS)";
                TxtHostStatus.Text = "Ожидание подключения...";
            }
            else
            {
                TxtHostStatus.Text = "Ошибка запуска хоста";
                IndicatorHost.Background = new SolidColorBrush(Color.FromRgb(0xD5, 0x00, 0x00));
            }
        }

        private async Task InitializeP2PAsync()
        {
            TxtPublicEndpoint.Text = "STUN поиск...";

            // 1. Discover public IP:Port via STUN
            _myPublicEndPoint = await StunResolver.QueryPublicEndPointAsync(_host.Port);
            if (_myPublicEndPoint != null)
            {
                TxtPublicEndpoint.Text = $"{_myPublicEndPoint.Address}:{_myPublicEndPoint.Port}";
            }
            else
            {
                TxtPublicEndpoint.Text = "Локальный режим";
            }

            // 2. Register with signaling broker
            _signaling.OnLog += log =>
            {
                Dispatcher.InvokeAsync(() => AppendClientLog(log));
            };

            await _signaling.RegisterHostAsync(_myDeviceId, clientReq =>
            {
                // Host receives connection request from remote client
                _ = Dispatcher.InvokeAsync(() =>
                {
                    AppendClientLog($"Запрос подключения от пира {clientReq.DeviceId}");
                });

                var responseInfo = new PeerEndpointInfo
                {
                    DeviceId = _myDeviceId,
                    PublicIp = _myPublicEndPoint?.Address.ToString() ?? _myLocalIp,
                    PublicPort = _myPublicEndPoint?.Port ?? _host.Port,
                    LocalIp = _myLocalIp,
                    LocalPort = _host.Port
                };

                return Task.FromResult(responseInfo);
            });
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string target = TxtRemoteAddress.Text.Trim().Replace(" ", "");
            if (string.IsNullOrEmpty(target))
            {
                MessageBox.Show(this, "Введите Vibe ID или IP:Порт для подключения.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnConnect.IsEnabled = false;
            AppendClientLog($"Инициализация подключения к {target}...");

            try
            {
                string connectIp = target;
                int connectPort = VibeHost.DefaultPort;

                // Check if target is a 6-digit Vibe ID
                if (target.Length == 6 && int.TryParse(target, out _))
                {
                    AppendClientLog($"Поиск устройства {FormatId(target)} в глобальной сети...");

                    var myInfo = new PeerEndpointInfo
                    {
                        DeviceId = _myDeviceId,
                        PublicIp = _myPublicEndPoint?.Address.ToString() ?? _myLocalIp,
                        PublicPort = _myPublicEndPoint?.Port ?? 0,
                        LocalIp = _myLocalIp,
                        LocalPort = _host.Port
                    };

                    var hostInfo = await _signaling.RequestHostEndpointsAsync(target, myInfo, timeoutMs: 6000);

                    if (hostInfo == null)
                    {
                        AppendClientLog($"Устройство {FormatId(target)} не найдено или не в сети.");
                        MessageBox.Show(this, $"Устройство с ID {FormatId(target)} не отвечает.\nУбедитесь, что VibeDesk запущен на удаленном ПК.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                        BtnConnect.IsEnabled = true;
                        return;
                    }

                    AppendClientLog($"Пир найден! Локальный IP: {hostInfo.LocalIp}, Публичный: {hostInfo.PublicIp}:{hostInfo.PublicPort}");

                    // If same local network / Wi-Fi subnet, use direct LAN IP for best performance
                    if (IsSameSubnet(_myLocalIp, hostInfo.LocalIp))
                    {
                        connectIp = hostInfo.LocalIp;
                        connectPort = hostInfo.LocalPort;
                        AppendClientLog($"Обнаружена локальная сеть (диван)! Подключение по LAN: {connectIp}:{connectPort}");
                    }
                    else
                    {
                        connectIp = !string.IsNullOrEmpty(hostInfo.PublicIp) ? hostInfo.PublicIp : hostInfo.LocalIp;
                        connectPort = hostInfo.PublicPort > 0 ? hostInfo.PublicPort : hostInfo.LocalPort;
                        AppendClientLog($"Подключение через Интернет (P2P): {connectIp}:{connectPort}");
                    }
                }
                else if (target.Contains(':'))
                {
                    var parts = target.Split(':');
                    connectIp = parts[0];
                    if (int.TryParse(parts[1], out int p))
                    {
                        connectPort = p;
                    }
                }

                // Start Client connection
                var client = new VibeClient();
                var tcs = new TaskCompletionSource<bool>();

                client.OnConnected += () => tcs.TrySetResult(true);
                client.OnDisconnected += () => tcs.TrySetResult(false);
                client.OnStatusChanged += status => Dispatcher.InvokeAsync(() => AppendClientLog(status));

                bool initiated = client.Connect(connectIp, connectPort);
                if (!initiated)
                {
                    AppendClientLog("Ошибка инициализации сокета.");
                    client.Dispose();
                    BtnConnect.IsEnabled = true;
                    return;
                }

                var timeoutTask = Task.Delay(5000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == tcs.Task && tcs.Task.Result)
                {
                    AppendClientLog("Сеанс успешно установлен!");
                    var sessionWindow = new RemoteSessionWindow(client);
                    sessionWindow.Owner = this;
                    sessionWindow.Show();
                }
                else
                {
                    AppendClientLog("Тайм-аут подключения. Удаленный хост не отвечает.");
                    client.Dispose();
                    MessageBox.Show(this, "Не удалось установить прямое соединение с удаленным ПК.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendClientLog($"Ошибка подключения: {ex.Message}");
                MessageBox.Show(this, $"Ошибка: {ex.Message}", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        private static bool IsSameSubnet(string ip1, string ip2)
        {
            try
            {
                var p1 = ip1.Split('.');
                var p2 = ip2.Split('.');
                if (p1.Length == 4 && p2.Length == 4)
                {
                    return p1[0] == p2[0] && p1[1] == p2[1] && p1[2] == p2[2];
                }
            }
            catch { }
            return false;
        }

        private void AppendClientLog(string text)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtClientLog.Text = $"[{time}] {text}\n" + TxtClientLog.Text;
        }

        private void BtnCopyId_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_myDeviceId);
                MessageBox.Show(this, $"Ваш Vibe ID ({FormatId(_myDeviceId)}) скопирован в буфер обмена!", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private void TxtRemoteAddress_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnConnect_Click(sender, e);
            }
        }

        private void CmbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFps.SelectedIndex == 0)
            {
                _targetFps = 60;
            }
            else
            {
                _targetFps = 30;
            }

            if (_host != null && IsLoaded)
            {
                _host.Stop();
                _host.Start(_targetFps);
            }
        }
    }
}