using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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
        private IPEndPoint? _lastPunchReceivedFrom;
        private string _myLocalIp = "127.0.0.1";
        private int _targetFps = 60;
        private float _targetScale = 1.0f;

        public MainWindow()
        {
            InitializeComponent();

            _myDeviceId = GetOrCreateDeviceId();
            TxtMyId.Text = FormatId(_myDeviceId);

            _host = new VibeHost(VibeHost.DefaultPort);
            _signaling = new P2PSignaling();

            _host.OnPunchReceived += ep =>
            {
                _lastPunchReceivedFrom = ep;
            };

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AppendLog("=== Инициализация VibeDesk ===");
            FindRealLocalIp();

            // 1. Resolve STUN before starting host to discover public endpoint
            await ResolveStunAsync();

            // 2. Start local host
            StartLocalHost();

            // 3. Register with global P2P signaling
            await InitializeSignalingAsync();
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

        private void FindRealLocalIp()
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                 ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .OrderByDescending(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                             ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1 : 0);

                foreach (var ni in interfaces)
                {
                    string name = ni.Name.ToLower();
                    string desc = ni.Description.ToLower();

                    // Strictly filter out VPN, proxy, Docker, WSL, virtual tunnels
                    if (name.Contains("xray") || name.Contains("outline") || name.Contains("tap") ||
                        name.Contains("tun") || name.Contains("docker") || name.Contains("vethernet") ||
                        name.Contains("wsl") || name.Contains("virtual") || name.Contains("hyper-v") ||
                        name.Contains("vpn") || desc.Contains("virtual") || desc.Contains("hyper-v") ||
                        desc.Contains("tap-windows") || desc.Contains("wsl") || desc.Contains("xray") ||
                        desc.Contains("vpn"))
                    {
                        continue;
                    }

                    var ipProps = ni.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr.Address) &&
                            !addr.Address.ToString().StartsWith("169.254.") &&
                            !addr.Address.ToString().StartsWith("172.18.") &&
                            !addr.Address.ToString().StartsWith("172.19."))
                        {
                            _myLocalIp = addr.Address.ToString();
                            TxtLocalIp.Text = $"{_myLocalIp}:{_host.Port}";
                            AppendLog($"🌐 Физический LAN IP найден через '{ni.Name}': {_myLocalIp}");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Сеть] Ошибка поиска LAN IP: {ex.Message}");
            }

            TxtLocalIp.Text = "127.0.0.1:15890";
            AppendLog("⚠️ Физический LAN адаптер не найден, используется 127.0.0.1");
        }

        private async Task ResolveStunAsync()
        {
            TxtPublicEndpoint.Text = "STUN поиск...";
            AppendLog("🔍 Запрос STUN (определение внешнего IP и порта)...");

            var (ep, msg) = await StunResolver.ResolveAsync(_host.Port);
            _myPublicEndPoint = ep;

            if (_myPublicEndPoint != null)
            {
                TxtPublicEndpoint.Text = $"{_myPublicEndPoint.Address}:{_myPublicEndPoint.Port}";
                AppendLog($"✅ {msg}");
            }
            else
            {
                TxtPublicEndpoint.Text = "Локальный режим";
                AppendLog($"⚠️ {msg}");
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
                    TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({(int)(_targetScale * 100)}% / {_targetFps} FPS)";
                    AppendLog($"[Хост] {msg}");
                });
            };

            bool started = _host.Start(_targetFps, jpegQuality: 70, scale: _targetScale);
            if (started)
            {
                TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({(int)(_targetScale * 100)}% / {_targetFps} FPS)";
                TxtHostStatus.Text = "Ожидание подключения...";
                AppendLog($"⚡ Хост слушает UDP порт {_host.Port}. Захват: {_host.ActiveCaptureEngine} ({(int)(_targetScale * 100)}% / {_targetFps} FPS)");
            }
            else
            {
                TxtHostStatus.Text = "Ошибка запуска хоста";
                IndicatorHost.Background = new SolidColorBrush(Color.FromRgb(0xD5, 0x00, 0x00));
                AppendLog($"❌ Не удалось запустить хост на порту {_host.Port}!");
            }
        }

        private void CmbResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbResolution == null || _host == null) return;
            _targetScale = CmbResolution.SelectedIndex switch
            {
                0 => 1.0f,   // 100%
                1 => 0.75f,  // 75%
                2 => 0.50f,  // 50%
                3 => 0.33f,  // 33%
                _ => 1.0f
            };
            _host.SetStreamSettings(_targetScale, _targetFps, 70);
            TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({(int)(_targetScale * 100)}% / {_targetFps} FPS)";
        }

        private void CmbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbFps == null || _host == null) return;
            _targetFps = CmbFps.SelectedIndex switch
            {
                0 => 120, // 120 FPS
                1 => 60,  // 60 FPS
                2 => 30,  // 30 FPS
                _ => 60
            };
            _host.SetStreamSettings(_targetScale, _targetFps, 70);
            TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({(int)(_targetScale * 100)}% / {_targetFps} FPS)";
        }

        private async Task InitializeSignalingAsync()
        {
            AppendLog("📡 Подключение к глобальной сети VibeDesk (MQTT)...");

            _signaling.OnLog += log =>
            {
                Dispatcher.InvokeAsync(() => AppendLog($"[Signaling] {log}"));
            };

            await _signaling.RegisterHostAsync(_myDeviceId, clientReq =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    AppendLog($"📩 Получен запрос на сеанс от пира ID {clientReq.DeviceId}");
                    AppendLog($"   -> Клиент локальный IP: {clientReq.LocalIp}:{clientReq.LocalPort}");
                    AppendLog($"   -> Клиент публичный IP: {clientReq.PublicIp}:{clientReq.PublicPort}");
                });

                // Host immediately punches UDP packets towards client to open router NAT!
                if (IPAddress.TryParse(clientReq.PublicIp, out var clientPubIp) && clientReq.PublicPort > 0)
                {
                    var pubTarget = new IPEndPoint(clientPubIp, clientReq.PublicPort);
                    _host.PunchNat(pubTarget);
                    _ = Dispatcher.InvokeAsync(() => AppendLog($"🥊 [Хост] Отправлен встречный UDP-punch на {pubTarget}"));
                }

                if (IPAddress.TryParse(clientReq.LocalIp, out var clientLocIp) && clientReq.LocalPort > 0)
                {
                    var locTarget = new IPEndPoint(clientLocIp, clientReq.LocalPort);
                    _host.PunchNat(locTarget);
                }

                var responseInfo = new PeerEndpointInfo
                {
                    DeviceId = _myDeviceId,
                    PublicIp = _myPublicEndPoint?.Address.ToString() ?? "",
                    PublicPort = _myPublicEndPoint?.Port ?? 0,
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
            AppendLog($"\n--- Запуск подключения к: {target} ---");

            try
            {
                string connectIp = target;
                int connectPort = VibeHost.DefaultPort;

                // Scenario A: 6-digit Vibe ID
                if (target.Length == 6 && int.TryParse(target, out _))
                {
                    AppendLog($"🔎 Запрос координат хоста с ID {FormatId(target)} через глобальную сеть...");

                    var myInfo = new PeerEndpointInfo
                    {
                        DeviceId = _myDeviceId,
                        PublicIp = _myPublicEndPoint?.Address.ToString() ?? "",
                        PublicPort = _myPublicEndPoint?.Port ?? 0,
                        LocalIp = _myLocalIp,
                        LocalPort = _host.Port
                    };

                    AppendLog($"📤 Отправка своих координат хосту (Локальный: {myInfo.LocalIp}, Внешний: {myInfo.PublicIp}:{myInfo.PublicPort})...");

                    var hostInfo = await _signaling.RequestHostEndpointsAsync(target, myInfo, timeoutMs: 7000);

                    if (hostInfo == null)
                    {
                        AppendLog($"❌ Хост с ID {FormatId(target)} не ответил. Проверьте, запущен ли VibeDesk на удаленном компьютере.");
                        MessageBox.Show(this, $"Устройство с ID {FormatId(target)} не отвечает.\n\nУбедитесь, что VibeDesk запущен на удаленном ПК и подключен к сети.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                        BtnConnect.IsEnabled = true;
                        return;
                    }

                    AppendLog($"📥 Ответ от хоста {FormatId(target)} получен!");
                    AppendLog($"   -> Локальный IP хоста: {hostInfo.LocalIp}:{hostInfo.LocalPort}");
                    AppendLog($"   -> Внешний IP хоста: {hostInfo.PublicIp}:{hostInfo.PublicPort}");

                    var client = new VibeClient();
                    client.OnStatusChanged += status => Dispatcher.InvokeAsync(() => AppendLog($"[Клиент] {status}"));

                    bool connected = false;

                    // 0. Priority: If we already received an incoming UDP punch packet directly from the host!
                    if (_lastPunchReceivedFrom != null)
                    {
                        AppendLog($"🎯 Обнаружен живой адрес пира по входящему UDP Punch: {_lastPunchReceivedFrom}! Мгновенное подключение...");
                        connected = await TryConnectAsync(client, _lastPunchReceivedFrom.Address.ToString(), _lastPunchReceivedFrom.Port, timeoutMs: 2500);
                        if (connected)
                        {
                            AppendLog($"⚡ Успешно подключено по прямому каналу {_lastPunchReceivedFrom}!");
                        }
                    }

                    // 1. First: Test Direct LAN connection (optimal for the couch)
                    if (!connected && !string.IsNullOrEmpty(hostInfo.LocalIp) && hostInfo.LocalIp != "127.0.0.1")
                    {
                        AppendLog($"🛋️ Проверка прямого LAN-подключения (диван) к {hostInfo.LocalIp}:{hostInfo.LocalPort}...");
                        connected = await TryConnectAsync(client, hostInfo.LocalIp, hostInfo.LocalPort, timeoutMs: 1800);
                        if (connected)
                        {
                            AppendLog($"⚡ Успешно подключено напрямую по домашней локальной сети (LAN)!");
                        }
                    }

                    // 2. Second: If LAN not available, test Internet P2P via STUN port
                    if (!connected && !string.IsNullOrEmpty(hostInfo.PublicIp) && hostInfo.PublicPort > 0)
                    {
                        client.Disconnect();
                        AppendLog($"🌐 Проверка P2P через Интернет к {hostInfo.PublicIp}:{hostInfo.PublicPort}...");
                        connected = await TryConnectAsync(client, hostInfo.PublicIp, hostInfo.PublicPort, timeoutMs: 3000);
                    }

                    // 3. Third: Try Internet P2P via default port 15890 (if router did 1:1 mapping)
                    if (!connected && !string.IsNullOrEmpty(hostInfo.PublicIp) && hostInfo.PublicPort != VibeHost.DefaultPort)
                    {
                        client.Disconnect();
                        AppendLog($"🌐 Проверка прямого порта к {hostInfo.PublicIp}:{VibeHost.DefaultPort}...");
                        connected = await TryConnectAsync(client, hostInfo.PublicIp, VibeHost.DefaultPort, timeoutMs: 2500);
                    }

                    if (connected)
                    {
                        var sessionWindow = new RemoteSessionWindow(client);
                        sessionWindow.Owner = this;
                        sessionWindow.Show();
                    }
                    else
                    {
                        client.Dispose();
                        AppendLog("❌ Не удалось установить прямое соединение.");
                        AppendLog("   Диагностика:");
                        AppendLog("   1. Если ПК в одной квартире: проверьте, что оба в одной Wi-Fi сети.");
                        AppendLog("   2. Если через интернет с VPN/Xray: прокси может блокировать входящий UDP.");
                        AppendLog("   3. Брандмауэр Windows на удаленном ПК должен иметь разрешение для VibeDesk.");
                        MessageBox.Show(this, $"Не удалось установить прямое соединение с удаленным ПК.\n\nПодробная диагностика выведена в журнале справа.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                    return;
                }
                else if (target.Contains(':'))
                {
                    var parts = target.Split(':');
                    connectIp = parts[0];
                    if (int.TryParse(parts[1], out int p)) connectPort = p;
                    AppendLog($"🎯 Прямой режим IP: {connectIp}:{connectPort}");
                }
                else
                {
                    connectIp = target;
                    AppendLog($"🎯 Прямой режим IP (порт по умолчанию): {connectIp}:{connectPort}");
                }

                // Direct IP mode
                var directClient = new VibeClient();
                directClient.OnStatusChanged += status => Dispatcher.InvokeAsync(() => AppendLog($"[Клиент] {status}"));
                bool directConnected = await TryConnectAsync(directClient, connectIp, connectPort, timeoutMs: 5000);

                if (directConnected)
                {
                    var sessionWindow = new RemoteSessionWindow(directClient);
                    sessionWindow.Owner = this;
                    sessionWindow.Show();
                }
                else
                {
                    directClient.Dispose();
                    AppendLog($"❌ Не удалось подключиться к {connectIp}:{connectPort}.");
                    MessageBox.Show(this, $"Не удалось подключиться к {connectIp}:{connectPort}.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Ошибка: {ex.Message}");
                MessageBox.Show(this, $"Ошибка: {ex.Message}", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        private async Task<bool> TryConnectAsync(VibeClient client, string ip, int port, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            Action onConn = () => tcs.TrySetResult(true);
            Action onDis = () => tcs.TrySetResult(false);

            client.OnConnected += onConn;
            client.OnDisconnected += onDis;

            try
            {
                if (IPAddress.TryParse(ip, out var targetIp))
                {
                    var targetEp = new IPEndPoint(targetIp, port);
                    client.PunchNat(targetEp);
                }

                bool initiated = client.Connect(ip, port);
                if (!initiated) return false;

                var timeoutTask = Task.Delay(timeoutMs);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
                return completedTask == tcs.Task && tcs.Task.Result;
            }
            catch
            {
                return false;
            }
            finally
            {
                client.OnConnected -= onConn;
                client.OnDisconnected -= onDis;
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

        private void AppendLog(string text)
        {
            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtClientLog.AppendText($"[{time}] {text}\n");
            ScrollLogs.ScrollToEnd();
        }

        private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtClientLog.Text);
                MessageBox.Show(this, "Журнал скопирован в буфер обмена!", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
        {
            TxtClientLog.Clear();
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
    }
}