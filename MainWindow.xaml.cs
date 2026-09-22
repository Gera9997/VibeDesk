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
                // Method 1: Query OS routing table for outgoing internet interface
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    if (socket.LocalEndPoint is IPEndPoint endPoint && !IPAddress.IsLoopback(endPoint.Address))
                    {
                        _myLocalIp = endPoint.Address.ToString();
                        TxtLocalIp.Text = $"{_myLocalIp}:{_host.Port}";
                        AppendLog($"🌐 Физический LAN IP определен через таблицу маршрутизации: {_myLocalIp}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[Сеть] Маршрутизация: {ex.Message}");
            }

            // Method 2: Scan physical interfaces, ignoring virtual/Docker/WSL
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    string name = ni.Name.ToLower();
                    string desc = ni.Description.ToLower();
                    if (name.Contains("vethernet") || name.Contains("wsl") || name.Contains("docker") ||
                        name.Contains("virtual") || desc.Contains("virtual") || desc.Contains("hyper-v") ||
                        desc.Contains("wsl") || desc.Contains("docker") || desc.Contains("vmware"))
                    {
                        continue;
                    }

                    var ipProps = ni.GetIPProperties();
                    foreach (var addr in ipProps.UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                        {
                            _myLocalIp = addr.Address.ToString();
                            TxtLocalIp.Text = $"{_myLocalIp}:{_host.Port}";
                            AppendLog($"🌐 Физический LAN IP найден через интерфейс '{ni.Name}': {_myLocalIp}");
                            return;
                        }
                    }
                }
            }
            catch { }

            TxtLocalIp.Text = "127.0.0.1:15890";
            AppendLog("⚠️ Не удалось определить LAN IP, используется 127.0.0.1");
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
                    TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({_targetFps} FPS)";
                    AppendLog($"[Хост] {msg}");
                });
            };

            bool started = _host.Start(_targetFps);
            if (started)
            {
                TxtCaptureEngine.Text = $"{_host.ActiveCaptureEngine} ({_targetFps} FPS)";
                TxtHostStatus.Text = "Ожидание подключения...";
                AppendLog($"⚡ Хост слушает UDP порт {_host.Port}. Захват: {_host.ActiveCaptureEngine}");
            }
            else
            {
                TxtHostStatus.Text = "Ошибка запуска хоста";
                IndicatorHost.Background = new SolidColorBrush(Color.FromRgb(0xD5, 0x00, 0x00));
                AppendLog($"❌ Не удалось запустить хост на порту {_host.Port}!");
            }
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

                    // If client doesn't have public endpoint yet, quick resolve
                    if (_myPublicEndPoint == null)
                    {
                        var (cEp, _) = await StunResolver.ResolveAsync(0);
                        _myPublicEndPoint = cEp;
                    }

                    var myInfo = new PeerEndpointInfo
                    {
                        DeviceId = _myDeviceId,
                        PublicIp = _myPublicEndPoint?.Address.ToString() ?? "",
                        PublicPort = _myPublicEndPoint?.Port ?? 0,
                        LocalIp = _myLocalIp,
                        LocalPort = 15891
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

                    // Check if both devices are in the same local subnet (on the couch)
                    if (IsSameSubnet(_myLocalIp, hostInfo.LocalIp))
                    {
                        connectIp = hostInfo.LocalIp;
                        connectPort = hostInfo.LocalPort > 0 ? hostInfo.LocalPort : VibeHost.DefaultPort;
                        AppendLog($"🛋️ Обнаружена общая локальная сеть (диван)! Прямое подключение по LAN: {connectIp}:{connectPort}");
                    }
                    else
                    {
                        // Connecting across internet
                        if (!string.IsNullOrEmpty(hostInfo.PublicIp) && hostInfo.PublicPort > 0)
                        {
                            connectIp = hostInfo.PublicIp;
                            connectPort = hostInfo.PublicPort;
                            AppendLog($"🌐 Подключение через Интернет (P2P): {connectIp}:{connectPort}");
                        }
                        else
                        {
                            AppendLog("⚠️ Внимание: у удаленного ПК не определен внешний публичный IP (STUN). Попытка подключения по локальному адресу...");
                            connectIp = hostInfo.LocalIp;
                            connectPort = hostInfo.LocalPort > 0 ? hostInfo.LocalPort : VibeHost.DefaultPort;
                        }
                    }
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

                // Initialize client
                var client = new VibeClient();
                var tcs = new TaskCompletionSource<bool>();

                client.OnConnected += () =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        AppendLog("🎉 Соединение успешно установлено! Видеопоток активен.");
                        tcs.TrySetResult(true);
                    });
                };

                client.OnDisconnected += () =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        AppendLog("🔌 Соединение разорвано.");
                        tcs.TrySetResult(false);
                    });
                };

                client.OnStatusChanged += status =>
                {
                    Dispatcher.InvokeAsync(() => AppendLog($"[Клиент] {status}"));
                };

                // Punch packets towards target
                if (IPAddress.TryParse(connectIp, out var targetIp))
                {
                    var targetEp = new IPEndPoint(targetIp, connectPort);
                    AppendLog($"🥊 [Клиент] Отправка UDP Punch на {targetEp}...");
                    client.PunchNat(targetEp);
                }

                AppendLog($"⏳ Отправка запроса на подключение к {connectIp}:{connectPort}...");
                bool initiated = client.Connect(connectIp, connectPort);
                if (!initiated)
                {
                    AppendLog("❌ Ошибка запуска клиентского сетевого сокета.");
                    client.Dispose();
                    BtnConnect.IsEnabled = true;
                    return;
                }

                var timeoutTask = Task.Delay(6000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == tcs.Task && tcs.Task.Result)
                {
                    var sessionWindow = new RemoteSessionWindow(client);
                    sessionWindow.Owner = this;
                    sessionWindow.Show();
                }
                else
                {
                    AppendLog($"❌ Не удалось установить прямое соединение с {connectIp}:{connectPort}.");
                    AppendLog("   Возможные причины:");
                    AppendLog("   1. Роутер блокирует входящий UDP-трафик (симметричный NAT).");
                    AppendLog("   2. Брандмауэр Windows на удаленном ПК блокирует VibeDesk.");
                    AppendLog("   3. Удаленный хост находится за серым IP без открытого порта.");
                    client.Dispose();
                    MessageBox.Show(this, $"Не удалось подключиться к {connectIp}:{connectPort}.\n\nПодробности смотрите в журнале событий справа.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private void CmbFps_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _targetFps = CmbFps.SelectedIndex == 0 ? 60 : 30;

            if (_host != null && IsLoaded)
            {
                _host.Stop();
                _host.Start(_targetFps);
            }
        }
    }
}