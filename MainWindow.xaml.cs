using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VibeDesk.Native;
using VibeDesk.Network;
using VibeDesk.Network.P2P;
using VibeDesk.UI;
using VibeDesk.Update;

namespace VibeDesk
{
    // ─── Recent connections model ───────────────────────────────────────────────
    public class RecentConnection : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool? _isOnline;

        public string DeviceId { get; set; } = "";
        public string? Alias { get; set; }
        public DateTime LastConnected { get; set; }

        public bool? IsOnline
        {
            get => _isOnline;
            set
            {
                if (_isOnline != value)
                {
                    _isOnline = value;
                    OnPropertyChanged(nameof(IsOnline));
                    OnPropertyChanged(nameof(StatusBrush));
                    OnPropertyChanged(nameof(StatusSubtitle));
                    OnPropertyChanged(nameof(StatusToolTip));
                }
            }
        }

        public Brush StatusBrush => IsOnline switch
        {
            true => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)), // Green
            false => new SolidColorBrush(Color.FromRgb(0x66, 0x5C, 0x56)), // Muted Gray
            null => new SolidColorBrush(Color.FromRgb(0xC2, 0xA8, 0x78))   // Amber checking
        };

        public string StatusToolTip => IsOnline switch
        {
            true => "Порт доступен (хост в сети)",
            false => "Недоступен (хост не в сети)",
            null => "Проверка доступности хоста..."
        };

        public string FormattedId => DeviceId.Length == 6
            ? $"{DeviceId[..3]} {DeviceId[3..]}"
            : DeviceId;

        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? FormattedId : Alias;

        public string LastConnectedStr
        {
            get
            {
                var delta = DateTime.Now - LastConnected;
                if (delta.TotalMinutes < 2) return "только что";
                if (delta.TotalHours < 1) return $"{(int)delta.TotalMinutes} мин. назад";
                if (delta.TotalDays < 1) return $"{(int)delta.TotalHours} ч. назад";
                if (delta.TotalDays < 7) return $"{(int)delta.TotalDays} дн. назад";
                return LastConnected.ToString("dd.MM.yyyy");
            }
        }

        public string StatusSubtitle
        {
            get
            {
                string state = IsOnline switch
                {
                    true => "В сети",
                    false => "Не в сети",
                    null => "Проверка..."
                };
                return $"{state} • {LastConnectedStr}";
            }
        }

        public ImageSource ThumbnailSource
        {
            get
            {
                try
                {
                    string thumbDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeDesk", "thumbnails");
                    string thumbFile = Path.Combine(thumbDir, $"{DeviceId}.jpg");
                    if (File.Exists(thumbFile))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(thumbFile, UriKind.Absolute);
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
                catch { }

                try
                {
                    var defaultBmp = new BitmapImage();
                    defaultBmp.BeginInit();
                    defaultBmp.CacheOption = BitmapCacheOption.OnLoad;
                    defaultBmp.UriSource = new Uri("pack://application:,,,/Assets/background.jpg");
                    defaultBmp.EndInit();
                    defaultBmp.Freeze();
                    return defaultBmp;
                }
                catch
                {
                    return null!;
                }
            }
        }

        public void NotifyThumbnailChanged()
        {
            OnPropertyChanged(nameof(ThumbnailSource));
        }

        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public partial class MainWindow : Window
    {
        private readonly string _myDeviceId;
        private readonly VibeHost _host;
        private readonly P2PSignaling _signaling;
        private readonly UpdateManager _updateManager = new("Gera9997", "VibeDesk");
        private UpdateInfo? _latestUpdateInfo;

        private IPEndPoint? _myPublicEndPoint;
        private IPEndPoint? _physicalPublicEndPoint;
        private IPEndPoint? _lastPunchReceivedFrom;
        private string _myLocalIp = "127.0.0.1";
        private int _targetFps = 25;
        private float _targetScale = 0.75f;

        // Recent connections
        private ObservableCollection<RecentConnection> _recentConnections = new();
        private static string RecentFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VibeDesk", "recent.json");

        public MainWindow()
        {
            InitializeComponent();

            TxtAppVersion.Text = AppVersion.FullTitle;
            TxtFooterVersion.Text = $"VibeDesk {AppVersion.FullTitle}";
            UpdateManager.CleanupLeftoverBackup();

            LoadRecentConnections();

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

            _host.DeviceId = _myDeviceId;

            // Silently apply firewall rules if already running elevated
            if (FirewallHelper.IsAdministrator())
            {
                FirewallHelper.AddFirewallRules(elevateIfNeed: false);
            }
            else
            {
                AppendLog("💡 Совет: если к этому ПК не удается подключиться, нажмите кнопку '🛡️ Брандмауэр' для открытия портов.");
            }

            // 1. Находим актуальный LAN IP
            FindRealLocalIp();

            // 2. Выполняем STUN probe чистым UDP-сокетом на порту хоста ДО запуска LiteNetLib
            await ResolveStunAsync();

            // 3. Запускаем локальный хост LiteNetLib на порту 15890
            StartLocalHost();

            // 4. Регистрируемся в глобальном сигналинге
            _ = InitializeSignalingAsync();

            // 5. Фоновая проверка обновлений
            _ = CheckForUpdatesQuietlyAsync();
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            try
            {
                Task.Run(() =>
                {
                    try { _host.Dispose(); } catch { }
                    try { _signaling.Dispose(); } catch { }
                });
            }
            catch { }
            finally
            {
                Environment.Exit(0);
            }
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
            AppendLog("🔍 Запрос STUN (Cloudflare / Google)...");

            var (stunEp, isSym, msg) = await StunResolver.ProbeAsync(_host.Port, _myLocalIp);
            if (stunEp != null)
            {
                _physicalPublicEndPoint = stunEp;
                _myPublicEndPoint = stunEp;
                TxtPublicEndpoint.Text = $"{stunEp.Address}:{stunEp.Port}";
                AppendLog($"✅ {msg}");
                return;
            }

            AppendLog("🔍 Резервное определение внешнего IP через веб-сервис...");
            var httpIp = await StunResolver.GetPublicIpHttpAsync(2500);
            if (httpIp != null)
            {
                var fallbackEp = new IPEndPoint(httpIp, _host.Port);
                _physicalPublicEndPoint = fallbackEp;
                _myPublicEndPoint = fallbackEp;
                TxtPublicEndpoint.Text = $"{fallbackEp.Address}:{fallbackEp.Port}";
                AppendLog($"✅ Внешний IP определен через HTTP: {fallbackEp}");
            }
            else
            {
                TxtPublicEndpoint.Text = "Локальный режим";
                AppendLog("⚠️ Не удалось определить внешний IP, работа в локальной сети.");
            }
        }

        private void TryOpenUpnpPort(int port)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var natType = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
                    if (natType != null)
                    {
                        dynamic nat = Activator.CreateInstance(natType)!;
                        dynamic mappings = nat.StaticPortMappingCollection;
                        if (mappings != null)
                        {
                            mappings.Add(port, "UDP", port, _myLocalIp, true, "VibeDesk");
                            Dispatcher.InvokeAsync(() => AppendLog($"⚡ [UPnP] Порт {port} (UDP) открыт на домашнем роутере!"));
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() => AppendLog($"ℹ️ [UPnP] UPnP не поддерживается или отключен на роутере: {ex.Message}"));
                    return;
                }
                Dispatcher.InvokeAsync(() => AppendLog("ℹ️ [UPnP] UPnP не ответил на текущем интерфейсе."));
            });
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

            bool started = _host.Start(_targetFps, jpegQuality: 55, scale: _targetScale, bindIp: _myLocalIp, maxBitrateKbps: 3500);
            if (started)
            {
                TryOpenUpnpPort(_host.Port);
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

            await _signaling.RegisterHostAsync(_myDeviceId, async clientReq =>
            {
                _ = Dispatcher.InvokeAsync(() =>
                {
                    AppendLog($"📩 Получен запрос на сеанс от пира ID {clientReq.DeviceId}");
                    AppendLog($"   -> Клиент локальный IP: {clientReq.LocalIp}:{clientReq.LocalPort}");
                    AppendLog($"   -> Клиент публичный IP: {clientReq.PublicIp}:{clientReq.PublicPort}");
                });

                // Host punches UDP packets continuously for 8 seconds towards client to keep router NAT pinholes open!
                var punchTargets = new List<IPEndPoint>();
                if (IPAddress.TryParse(clientReq.PhysicalPublicIp, out var clientPhysIp) && clientReq.PhysicalPublicPort > 0)
                {
                    punchTargets.Add(new IPEndPoint(clientPhysIp, clientReq.PhysicalPublicPort));
                }
                if (IPAddress.TryParse(clientReq.PublicIp, out var clientPubIp))
                {
                    if (clientReq.PublicPort > 0 && (clientPhysIp == null || !clientPubIp.Equals(clientPhysIp) || clientReq.PublicPort != clientReq.PhysicalPublicPort))
                    {
                        punchTargets.Add(new IPEndPoint(clientPubIp, clientReq.PublicPort));
                    }
                    if (VibeClient.DefaultClientPort != clientReq.PublicPort)
                    {
                        punchTargets.Add(new IPEndPoint(clientPubIp, VibeClient.DefaultClientPort));
                    }
                }
                if (IPAddress.TryParse(clientReq.LocalIp, out var clientLocIp) && clientReq.LocalPort > 0)
                {
                    punchTargets.Add(new IPEndPoint(clientLocIp, clientReq.LocalPort));
                }

                _ = Task.Run(async () =>
                {
                    using var punchCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    while (!punchCts.Token.IsCancellationRequested && !_host.HasClient)
                    {
                        foreach (var target in punchTargets)
                        {
                            _host.PunchNat(target);
                        }
                        try { await Task.Delay(70, punchCts.Token); } catch { break; }
                    }
                });

                _ = Dispatcher.InvokeAsync(() => AppendLog($"🥊 [Хост] Запущен непрерывный встречный UDP-punch по адресам клиента ({punchTargets.Count} эндпоинтов)..."));

                var bestPubEp = _physicalPublicEndPoint ?? _myPublicEndPoint;
                string pubIpStr = bestPubEp?.Address.ToString() ?? "";
                int pubPort = bestPubEp?.Port ?? _host.Port;

                if (string.IsNullOrEmpty(pubIpStr))
                {
                    try
                    {
                        var httpIp = await StunResolver.GetPublicIpHttpAsync(2000);
                        if (httpIp != null)
                        {
                            pubIpStr = httpIp.ToString();
                        }
                    }
                    catch { }
                }

                var responseInfo = new PeerEndpointInfo
                {
                    DeviceId = _myDeviceId,
                    PublicIp = pubIpStr,
                    PublicPort = pubPort > 0 ? pubPort : _host.Port,
                    PhysicalPublicIp = pubIpStr,
                    PhysicalPublicPort = pubPort > 0 ? pubPort : _host.Port,
                    LocalIp = _myLocalIp,
                    LocalPort = _host.Port
                };

                return responseInfo;
            });
        }

        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string target = TxtRemoteAddress.Text.Trim().Replace(" ", "");
            await ConnectToTargetAsync(target);
        }

        private async Task ConnectToTargetAsync(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                VibeMessageBox.Show(this, "Введите Vibe ID или IP:Порт для подключения.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!BtnConnect.IsEnabled) return;

            BtnConnect.IsEnabled = false;
            _lastPunchReceivedFrom = null;
            AppendLog($"\n--- Запуск подключения к: {target} ---");

            try
            {
                string connectIp = target;
                int connectPort = VibeHost.DefaultPort;

                // Scenario A: 6-digit Vibe ID
                if (target.Length == 6 && int.TryParse(target, out _))
                {
                    AppendLog($"🔎 Поиск хоста {FormatId(target)} в локальной сети (LAN) и через глобальные серверы...");

                    // 1. Обновляем локальный IP на случай смены сети (Wi-Fi <-> Hotspot)
                    FindRealLocalIp();

                    // 2. Узнаем реальный внешний IP:Port открытого сокета клиента через STUN ДО запуска клиента!
                    var (clientStunEp, _, clientStunMsg) = await StunResolver.ProbeAsync(VibeClient.DefaultClientPort, _myLocalIp);
                    if (clientStunEp != null)
                    {
                        AppendLog($"🌐 [STUN Клиент] {clientStunMsg}");
                    }

                    // 3. Запускаем клиентский сокет на порту 15891
                    var client = new VibeClient();
                    client.Start(VibeClient.DefaultClientPort, bindIp: _myLocalIp);
                    client.OnStatusChanged += status => Dispatcher.InvokeAsync(() => AppendLog($"[Клиент] {status}"));
                    client.OnPunchReceived += ep => _lastPunchReceivedFrom = ep;
                    client.OnPunchAckReceived += ep => _lastPunchReceivedFrom = ep;

                    int clientLocalPort = client.LocalPort;
                    string clientPubIp = clientStunEp?.Address.ToString() ?? (_physicalPublicEndPoint?.Address.ToString() ?? (_myPublicEndPoint?.Address.ToString() ?? ""));
                    int clientPubPort = clientStunEp?.Port ?? (clientLocalPort > 0 ? clientLocalPort : VibeClient.DefaultClientPort);

                    if (string.IsNullOrEmpty(clientPubIp))
                    {
                        var httpIp = await StunResolver.GetPublicIpHttpAsync(2000);
                        if (httpIp != null)
                        {
                            clientPubIp = httpIp.ToString();
                        }
                    }

                    // 1. Concurrent FAST LAN Discovery (UDP Broadcast on home network, bound to real physical adapter)
                    var lanDiscoveryTask = LanDiscovery.DiscoverHostAsync(target, _myLocalIp, timeoutMs: 1500);

                    // Check LAN Discovery first (instant 1-20ms in local Wi-Fi)
                    var lanEp = await lanDiscoveryTask;
                    if (lanEp != null)
                    {
                        AppendLog($"🛋️ Хост {FormatId(target)} обнаружен в домашней сети: {lanEp}! Мгновенное подключение...");

                        bool lanConnected = await TryConnectAsync(client, lanEp.Address.ToString(), lanEp.Port, timeoutMs: 3500);
                        if (lanConnected)
                        {
                            AppendLog($"⚡ Успешно подключено напрямую по локальной сети к {lanEp}!");
                            AddRecentConnection(target);
                            Dispatcher.Invoke(() =>
                            {
                                var sessionWindow = new RemoteSessionWindow(client, target) { Owner = this };
                                sessionWindow.Closed += (s, ev) => Dispatcher.InvokeAsync(LoadRecentConnections);
                                sessionWindow.Show();
                            });
                            return;
                        }
                        else
                        {
                            AppendLog($"⚠️ Хост ответил на бродкаст {lanEp}, но соединение не завершилось. Переход к глобальной сети...");
                        }
                    }
                    else
                    {
                        AppendLog("📡 В домашней сети хост не ответил на бродкаст, ожидание глобального сервера...");
                    }

                    // 2. Prepare global signaling with real client socket public port!
                    var myInfo = new PeerEndpointInfo
                    {
                        DeviceId = _myDeviceId,
                        PublicIp = clientPubIp,
                        PublicPort = clientPubPort,
                        PhysicalPublicIp = clientPubIp,
                        PhysicalPublicPort = clientPubPort,
                        LocalIp = _myLocalIp,
                        LocalPort = clientLocalPort
                    };

                    AppendLog($"📤 Запрос хосту через глобальную сеть (клиентский сокет: {clientPubIp}:{clientPubPort})...");
                    var hostInfo = await _signaling.RequestHostEndpointsAsync(target, myInfo, timeoutMs: 7000);

                    if (hostInfo == null)
                    {
                        client.Dispose();
                        AppendLog($"❌ Хост с ID {FormatId(target)} не ответил ни в локальной сети, ни через глобальный сервер.");
                        AppendLog("   Подсказка: убедитесь, что VibeDesk запущен на удаленном ПК и нажмите на нем кнопку '🛡️ Брандмауэр'.");
                        VibeMessageBox.Show(this, $"Устройство с ID {FormatId(target)} не отвечает.\n\nУбедитесь, что VibeDesk запущен на удаленном ПК и разрешен в Брандмауэре Windows.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                        BtnConnect.IsEnabled = true;
                        return;
                    }

                    AppendLog($"📥 Ответ от хоста {FormatId(target)} получен!");
                    AppendLog($"   -> Локальный IP хоста: {hostInfo.LocalIp}:{hostInfo.LocalPort}");
                    if (!string.IsNullOrEmpty(hostInfo.PhysicalPublicIp))
                    {
                        AppendLog($"   -> Физический IP хоста: {hostInfo.PhysicalPublicIp}:{hostInfo.PhysicalPublicPort}");
                    }
                    AppendLog($"   -> Внешний IP хоста: {hostInfo.PublicIp}:{hostInfo.PublicPort}");

                    // Launch continuous client-side UDP punch towards host endpoints
                    var clientPunchTargets = new List<IPEndPoint>();
                    if (IPAddress.TryParse(hostInfo.PhysicalPublicIp, out var clientPhysTarget) && hostInfo.PhysicalPublicPort > 0)
                        clientPunchTargets.Add(new IPEndPoint(clientPhysTarget, hostInfo.PhysicalPublicPort));
                    if (IPAddress.TryParse(hostInfo.PublicIp, out var clientPubTarget) && hostInfo.PublicPort > 0)
                        clientPunchTargets.Add(new IPEndPoint(clientPubTarget, hostInfo.PublicPort));
                    if (clientPubTarget != null && hostInfo.PublicPort != VibeHost.DefaultPort)
                        clientPunchTargets.Add(new IPEndPoint(clientPubTarget, VibeHost.DefaultPort));
                    if (clientPhysTarget != null && hostInfo.PhysicalPublicPort != VibeHost.DefaultPort)
                        clientPunchTargets.Add(new IPEndPoint(clientPhysTarget, VibeHost.DefaultPort));
                    if (IPAddress.TryParse(hostInfo.LocalIp, out var clientLocTarget) && hostInfo.LocalPort > 0)
                        clientPunchTargets.Add(new IPEndPoint(clientLocTarget, hostInfo.LocalPort));

                    using var clientPunchCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    _ = Task.Run(async () =>
                    {
                        while (!clientPunchCts.Token.IsCancellationRequested && !client.IsConnected)
                        {
                            foreach (var targetEp in clientPunchTargets)
                            {
                                client.Punch(targetEp, _myDeviceId);
                            }
                            try { await Task.Delay(70, clientPunchCts.Token); } catch { break; }
                        }
                    });

                    bool isLocal = IsDirectLanAccessible(hostInfo);
                    bool connected = false;
                    string lastTriedEp = "";

                    // 1. Priority 0: If we already received an incoming UDP punch packet directly from the host
                    if (_lastPunchReceivedFrom != null)
                    {
                        string punchEp = $"{_lastPunchReceivedFrom.Address}:{_lastPunchReceivedFrom.Port}";
                        lastTriedEp = punchEp;
                        AppendLog($"🎯 Обнаружен живой встречный канал пира: {punchEp}! Мгновенное подключение...");
                        connected = await TryConnectAsync(client, _lastPunchReceivedFrom.Address.ToString(), _lastPunchReceivedFrom.Port, timeoutMs: 3500);
                        if (connected) AppendLog($"⚡ Успешно подключено по встречному каналу {punchEp}!");
                    }

                    // 2. Priority 1: Direct LAN IP if on same subnet / router
                    if (!connected && isLocal)
                    {
                        string hostLanEp = $"{hostInfo.LocalIp}:{hostInfo.LocalPort}";
                        lastTriedEp = hostLanEp;
                        AppendLog($"🛋️ Обнаружена общая домашняя сеть! Мгновенное подключение к {hostLanEp}...");
                        connected = await TryConnectAsync(client, hostInfo.LocalIp, hostInfo.LocalPort, timeoutMs: 3500);
                        if (connected) AppendLog($"⚡ Успешно подключено напрямую по домашней локальной сети (LAN)!");
                    }

                    // 3. Priority 2: P2P Internet punch & waterfall
                    if (!connected)
                    {
                        // Check if a punch arrived while checking LAN
                        if (_lastPunchReceivedFrom != null)
                        {
                            string punchEp = $"{_lastPunchReceivedFrom.Address}:{_lastPunchReceivedFrom.Port}";
                            if (punchEp != lastTriedEp)
                            {
                                lastTriedEp = punchEp;
                                AppendLog($"🎯 Подключение по входящему UDP Punch: {punchEp}...");
                                connected = await TryConnectAsync(client, _lastPunchReceivedFrom.Address.ToString(), _lastPunchReceivedFrom.Port, timeoutMs: 3500);
                                if (connected) AppendLog($"⚡ Успешно подключено по встречному UDP-каналу {punchEp}!");
                            }
                        }

                        if (!connected)
                        {
                            AppendLog($"🌐 Запуск P2P пробития NAT через Интернет...");

                            // Step A1: Physical Public Endpoint (bypasses host's VPN completely!)
                            if (!connected && !string.IsNullOrEmpty(hostInfo.PhysicalPublicIp) && hostInfo.PhysicalPublicPort > 0)
                            {
                                string hostPhysEp = $"{hostInfo.PhysicalPublicIp}:{hostInfo.PhysicalPublicPort}";
                                if (hostPhysEp != lastTriedEp)
                                {
                                    lastTriedEp = hostPhysEp;
                                    AppendLog($"🌐 Проверка прямого физического канала к {hostPhysEp}...");
                                    connected = await TryConnectAsync(client, hostInfo.PhysicalPublicIp, hostInfo.PhysicalPublicPort, timeoutMs: 4000);
                                    if (connected) AppendLog($"⚡ Успешно подключено по прямому физическому каналу к {hostPhysEp}!");
                                }
                            }

                            // Step A2: Physical Public IP on port 15890 (UPnP / default port)
                            if (!connected && !string.IsNullOrEmpty(hostInfo.PhysicalPublicIp))
                            {
                                string hostPhysDefEp = $"{hostInfo.PhysicalPublicIp}:{VibeHost.DefaultPort}";
                                if (hostPhysDefEp != lastTriedEp)
                                {
                                    lastTriedEp = hostPhysDefEp;
                                    AppendLog($"🌐 Проверка прямого порта на физическом IP {hostPhysDefEp}...");
                                    connected = await TryConnectAsync(client, hostInfo.PhysicalPublicIp, VibeHost.DefaultPort, timeoutMs: 3500);
                                    if (connected) AppendLog($"⚡ Успешно подключено к {hostPhysDefEp}!");
                                }
                            }

                            // Step B1: Standard Public IP via STUN
                            if (!connected && !string.IsNullOrEmpty(hostInfo.PublicIp) && hostInfo.PublicPort > 0)
                            {
                                string hostPublicEp = $"{hostInfo.PublicIp}:{hostInfo.PublicPort}";
                                if (hostPublicEp != lastTriedEp)
                                {
                                    lastTriedEp = hostPublicEp;
                                    AppendLog($"🌐 Проверка P2P через Интернет к {hostPublicEp}...");
                                    connected = await TryConnectAsync(client, hostInfo.PublicIp, hostInfo.PublicPort, timeoutMs: 4000);
                                    if (connected) AppendLog($"⚡ Успешно подключено по P2P через Интернет к {hostPublicEp}!");
                                }
                            }

                            // Step B2: Standard Public IP on port 15890
                            if (!connected && !string.IsNullOrEmpty(hostInfo.PublicIp))
                            {
                                string hostDefaultEp = $"{hostInfo.PublicIp}:{VibeHost.DefaultPort}";
                                if (hostDefaultEp != lastTriedEp)
                                {
                                    lastTriedEp = hostDefaultEp;
                                    AppendLog($"🌐 Проверка прямого порта к {hostDefaultEp}...");
                                    connected = await TryConnectAsync(client, hostInfo.PublicIp, VibeHost.DefaultPort, timeoutMs: 3500);
                                    if (connected) AppendLog($"⚡ Успешно подключено к {hostDefaultEp}!");
                                }
                            }

                            // Step C: Fallback check on any newly arrived UDP Punch
                            if (!connected && _lastPunchReceivedFrom != null)
                            {
                                string punchEp = $"{_lastPunchReceivedFrom.Address}:{_lastPunchReceivedFrom.Port}";
                                if (punchEp != lastTriedEp)
                                {
                                    lastTriedEp = punchEp;
                                    AppendLog($"🎯 Подключение по входящему UDP Punch: {punchEp}...");
                                    connected = await TryConnectAsync(client, _lastPunchReceivedFrom.Address.ToString(), _lastPunchReceivedFrom.Port, timeoutMs: 3500);
                                    if (connected) AppendLog($"⚡ Успешно подключено по встречному UDP-каналу {punchEp}!");
                                }
                            }

                            // Step D: Fallback direct LAN check if not tried yet
                            if (!connected && !string.IsNullOrEmpty(hostInfo.LocalIp) && hostInfo.LocalIp != "127.0.0.1")
                            {
                                string hostLanEp = $"{hostInfo.LocalIp}:{hostInfo.LocalPort}";
                                if (hostLanEp != lastTriedEp)
                                {
                                    lastTriedEp = hostLanEp;
                                    AppendLog($"🛋️ Резервная проверка прямого локального адреса к {hostLanEp}...");
                                    connected = await TryConnectAsync(client, hostInfo.LocalIp, hostInfo.LocalPort, timeoutMs: 2500);
                                    if (connected) AppendLog($"⚡ Успешно подключено напрямую по LAN!");
                                }
                            }
                        }
                    }

                    if (connected)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                AddRecentConnection(target);
                                var sessionWindow = new RemoteSessionWindow(client, target) { Owner = this };
                                sessionWindow.Closed += (s, ev) => Dispatcher.InvokeAsync(LoadRecentConnections);
                                sessionWindow.Show();
                            }
                            catch (Exception winEx)
                            {
                                AppendLog($"❌ Ошибка создания окна: {winEx.GetType().Name}: {winEx.Message}");
                            }
                        });
                    }
                    else
                    {
                        client.Dispose();
                        AppendLog("❌ Не удалось установить прямое соединение.");
                        AppendLog("   Диагностика:");
                        AppendLog("   1. Если ПК в одной квартире: проверьте, что оба в одной Wi-Fi сети.");
                        AppendLog("   2. На удаленном ПК нажмите кнопку '🛡️ Брандмауэр' для снятия блокировки Windows.");
                        AppendLog("   3. Если через интернет с VPN/Xray: прокси может фильтровать входящий UDP.");
                        VibeMessageBox.Show(this, $"Не удалось установить прямое соединение с удаленным ПК.\n\nПодробная диагностика выведена в журнале справа.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
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

                if (!IsSameSubnet(_myLocalIp, connectIp) && !connectIp.StartsWith("127."))
                {
                    AppendLog($"⚠️ Внимание: введённый IP ({connectIp}) находится в другой подсети относительно вашего LAN ({_myLocalIp}). Проверьте правильность адреса!");
                }

                // Direct IP mode
                var directClient = new VibeClient();
                directClient.Start(VibeClient.DefaultClientPort);
                directClient.OnStatusChanged += status => Dispatcher.InvokeAsync(() => AppendLog($"[Клиент] {status}"));
                bool directConnected = await TryConnectAsync(directClient, connectIp, connectPort, timeoutMs: 6000);

                if (directConnected)
                {
                    Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            var sessionWindow = new RemoteSessionWindow(directClient, connectIp) { Owner = this };
                            sessionWindow.Closed += (s, ev) => Dispatcher.InvokeAsync(LoadRecentConnections);
                            sessionWindow.Show();
                        }
                        catch (Exception winEx)
                        {
                            AppendLog($"❌ Ошибка создания окна: {winEx.GetType().Name}: {winEx.Message}");
                        }
                    });
                }
                else
                {
                    directClient.Dispose();
                    AppendLog($"❌ Не удалось подключиться к {connectIp}:{connectPort}.");
                    AppendLog("💡 Если ПК в одной локальной сети: нажмите на удаленном ПК кнопку '🛡️ Брандмауэр' в VibeDesk, чтобы разрешить входящие пакеты.");
                    VibeMessageBox.Show(this, $"Не удалось подключиться к {connectIp}:{connectPort}.\n\nУбедитесь, что на удаленном ПК в VibeDesk разрешен Брандмауэр.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Ошибка: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                VibeMessageBox.Show(this, $"Ошибка: {ex.GetType().Name}: {ex.Message}", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        private async Task<bool> TryConnectAsync(VibeClient client, string ip, int port, int timeoutMs)
        {
            if (client.IsConnected) return true;

            client.ResetPeer();
            await Task.Delay(20);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action onConn = () => tcs.TrySetResult(true);
            Action onDis = () => tcs.TrySetResult(false);

            // Если во время ожидания прилетел живой Punch от хоста с реального порта NAT — сразу коннектимся к нему!
            Action<IPEndPoint> onPunch = ep =>
            {
                if (!client.IsConnected && (!ep.Address.ToString().Equals(ip) || ep.Port != port))
                {
                    ip = ep.Address.ToString();
                    port = ep.Port;
                    client.Connect(ep);
                }
            };

            client.OnConnected += onConn;
            client.OnDisconnected += onDis;
            client.OnPunchAckReceived += onPunch;
            client.OnPunchReceived += onPunch;

            using var punchCts = new CancellationTokenSource();

            try
            {
                bool initiated = client.Connect(ip, port);
                if (!initiated) return false;

                if (IPAddress.TryParse(ip, out var targetIp))
                {
                    var targetEp = new IPEndPoint(targetIp, port);
                    _ = Task.Run(async () =>
                    {
                        while (!punchCts.Token.IsCancellationRequested && !client.IsConnected)
                        {
                            client.Punch(targetEp, _myDeviceId);
                            try { await Task.Delay(60, punchCts.Token); } catch { break; }
                        }
                    });
                }

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
                punchCts.Cancel();
                client.OnConnected -= onConn;
                client.OnDisconnected -= onDis;
                client.OnPunchAckReceived -= onPunch;
                client.OnPunchReceived -= onPunch;
            }
        }

        private void BtnFirewall_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("🛡️ Настройка Брандмауэра Windows для входящих подключений VibeDesk...");
                bool ok = FirewallHelper.AddFirewallRules(elevateIfNeed: true);
                if (ok)
                {
                    AppendLog("✅ Правило для входящего UDP 15890 и VibeDesk успешно добавлено в Брандмауэр Windows!");
                    VibeMessageBox.Show(this, "Порт 15890 и приложение VibeDesk успешно разрешены в Брандмауэре Windows.\n\nТеперь другие компьютеры могут беспрепятственно подключаться к этому ПК.", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppendLog("⚠️ Настройка брандмауэра отменена или не применилась.");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Ошибка настройки брандмауэра: {ex.Message}");
            }
        }

        private static bool IsPrivateIp(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false;
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            return false;
        }

        private bool IsDirectLanAccessible(PeerEndpointInfo hostInfo)
        {
            if (string.IsNullOrEmpty(hostInfo.LocalIp) || hostInfo.LocalIp == "127.0.0.1") return false;

            // 1. If in the same private subnet (e.g. both on 192.168.0.x or 10.0.0.x), it is directly local LAN!
            if (IsSameSubnet(_myLocalIp, hostInfo.LocalIp))
            {
                return true;
            }

            // 2. If both peers share the same physical public IP, they are on the same local router
            if (!string.IsNullOrEmpty(hostInfo.PhysicalPublicIp) && _physicalPublicEndPoint != null &&
                !string.IsNullOrEmpty(_physicalPublicEndPoint.Address.ToString()) &&
                hostInfo.PhysicalPublicIp == _physicalPublicEndPoint.Address.ToString())
            {
                return true;
            }

            return false;
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
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => AppendLog(text));
                return;
            }

            string time = DateTime.Now.ToString("HH:mm:ss");
            TxtClientLog.AppendText($"[{time}] {text}\n");
            ScrollLogs.ScrollToEnd();
        }

        private void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtClientLog.Text);
                VibeMessageBox.Show(this, "Журнал скопирован в буфер обмена!", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
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
                VibeMessageBox.Show(this, $"Ваш Vibe ID ({FormatId(_myDeviceId)}) скопирован в буфер обмена!", "VibeDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        }

        // ─── Recent connections ──────────────────────────────────────────────────

        private void LoadRecentConnections()
        {
            try
            {
                if (File.Exists(RecentFilePath))
                {
                    var json = File.ReadAllText(RecentFilePath);
                    var list = JsonSerializer.Deserialize<List<RecentConnection>>(json);
                    if (list != null)
                    {
                        _recentConnections = new ObservableCollection<RecentConnection>(
                            list.OrderByDescending(r => r.LastConnected).Take(10));
                    }
                }
            }
            catch { }

            ListRecentConnections.ItemsSource = _recentConnections;
            RefreshRecentVisibility();

            // Run availability checks in the background (LAN broadcast + MQTT signaling)
            _ = CheckRecentAvailabilityAsync();
        }

        private async Task CheckRecentAvailabilityAsync()
        {
            var items = _recentConnections.ToList();
            if (items.Count == 0) return;

            foreach (var item in items)
            {
                try
                {
                    // 1. Fast LAN Discovery probe (UDP broadcast, 500ms)
                    var lanEp = await LanDiscovery.DiscoverHostAsync(item.DeviceId, _myLocalIp, timeoutMs: 500);
                    if (lanEp != null)
                    {
                        Dispatcher.Invoke(() => item.IsOnline = true);
                        continue;
                    }

                    // 2. Global Signaling probe via MQTT if connected
                    if (_signaling != null && _signaling.IsConnected)
                    {
                        var probeClientInfo = new PeerEndpointInfo
                        {
                            DeviceId = _myDeviceId,
                            LocalIp = _myLocalIp,
                            LocalPort = VibeClient.DefaultClientPort
                        };

                        var hostInfo = await _signaling.RequestHostEndpointsAsync(item.DeviceId, probeClientInfo, timeoutMs: 1500);
                        Dispatcher.Invoke(() => item.IsOnline = (hostInfo != null));
                    }
                    else
                    {
                        Dispatcher.Invoke(() => item.IsOnline = false);
                    }
                }
                catch
                {
                    Dispatcher.Invoke(() => item.IsOnline = false);
                }
            }
        }

        private void SaveRecentConnections()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RecentFilePath)!);
                var json = JsonSerializer.Serialize(_recentConnections.ToList(),
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(RecentFilePath, json);
            }
            catch { }
        }

        private void AddRecentConnection(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length != 6) return;

            var existing = _recentConnections.FirstOrDefault(r => r.DeviceId == deviceId);
            if (existing != null)
            {
                existing.LastConnected = DateTime.Now;
                existing.IsOnline = true;
                existing.NotifyThumbnailChanged();
                // Move to top
                _recentConnections.Remove(existing);
                _recentConnections.Insert(0, existing);
            }
            else
            {
                _recentConnections.Insert(0, new RecentConnection
                {
                    DeviceId = deviceId,
                    LastConnected = DateTime.Now,
                    IsOnline = true
                });
                // Keep max 10
                while (_recentConnections.Count > 10)
                    _recentConnections.RemoveAt(_recentConnections.Count - 1);
            }

            SaveRecentConnections();
            RefreshRecentVisibility();
        }

        private void RefreshRecentVisibility()
        {
            bool hasItems = _recentConnections.Count > 0;
            PanelRecentEmpty.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            ListRecentConnections.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ListRecentConnections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListRecentConnections.SelectedItem is RecentConnection rc)
            {
                string deviceId = rc.DeviceId;
                ListRecentConnections.SelectedItem = null;
                TxtRemoteAddress.Text = deviceId;
                await ConnectToTargetAsync(deviceId);
            }
        }

        private async void ListRecentConnections_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && dep is not ListBoxItem)
            {
                dep = VisualTreeHelper.GetParent(dep);
            }

            if (dep is ListBoxItem item && item.DataContext is RecentConnection rc)
            {
                e.Handled = true;
                ListRecentConnections.SelectedItem = null;
                TxtRemoteAddress.Text = rc.DeviceId;
                await ConnectToTargetAsync(rc.DeviceId);
            }
        }

        private async void ListRecentConnections_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListRecentConnections.SelectedItem is RecentConnection rc)
            {
                string deviceId = rc.DeviceId;
                ListRecentConnections.SelectedItem = null;
                TxtRemoteAddress.Text = deviceId;
                await ConnectToTargetAsync(deviceId);
            }
        }

        private async void TxtRemoteAddress_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string target = TxtRemoteAddress.Text.Trim().Replace(" ", "");
                await ConnectToTargetAsync(target);
            }
        }

        private async Task CheckForUpdatesQuietlyAsync()
        {
            try
            {
                var info = await _updateManager.CheckForUpdateAsync();
                if (info.HasUpdate)
                {
                    _latestUpdateInfo = info;
                    Dispatcher.Invoke(() => ShowUpdateBanner(info));
                }
            }
            catch { }
        }

        private void ShowUpdateBanner(UpdateInfo info)
        {
            TxtUpdateNewVersion.Text = info.LatestVersion;
            TxtUpdateSize.Text = info.FileSizeBytes > 0 ? $"({info.FileSizeBytes / 1048576} МБ)" : "";
            TxtUpdateStatus.Text = "Нажмите «Обновить сейчас» для загрузки новой версии и быстрого перезапуска.";
            PbUpdateProgress.Visibility = Visibility.Collapsed;
            BtnApplyUpdate.IsEnabled = true;
            BannerUpdate.Visibility = Visibility.Visible;
            AppendLog($"🔔 [Обновление] Доступна новая версия: {info.LatestVersion} {(info.FileSizeBytes > 0 ? $"({info.FileSizeBytes / 1048576} МБ)" : "")}");
        }

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            BtnCheckUpdate.IsEnabled = false;
            AppendLog("🔍 Проверка обновлений на GitHub (Gera9997/VibeDesk)...");

            try
            {
                var info = await _updateManager.CheckForUpdateAsync();
                _latestUpdateInfo = info;

                if (info.HasUpdate)
                {
                    ShowUpdateBanner(info);
                }
                else if (!string.IsNullOrEmpty(info.ErrorMessage))
                {
                    AppendLog($"⚠️ {info.ErrorMessage}");
                    VibeMessageBox.Show(this, info.ErrorMessage, "Проверка обновлений", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    AppendLog($"✅ У вас установлена актуальная версия ({AppVersion.FullTitle}).");
                    VibeMessageBox.Show(this, $"У вас установлена последняя версия ({AppVersion.FullTitle})!", "Обновления", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"❌ Ошибка проверки обновлений: {ex.Message}");
                VibeMessageBox.Show(this, $"Не удалось проверить обновления: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnCheckUpdate.IsEnabled = true;
            }
        }

        private async void BtnApplyUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_latestUpdateInfo == null || string.IsNullOrEmpty(_latestUpdateInfo.DownloadUrl))
            {
                VibeMessageBox.Show(this, "Ссылка на скачивание файла релиза не найдена.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnApplyUpdate.IsEnabled = false;
            PbUpdateProgress.Visibility = Visibility.Visible;
            PbUpdateProgress.Value = 0;
            TxtUpdateStatus.Text = "Подготовка к загрузке...";

            AppendLog($"⬇️ Загрузка обновления {_latestUpdateInfo.LatestVersion}...");

            var progress = new Progress<(long received, long total, int percent)>(report =>
            {
                PbUpdateProgress.Value = report.percent;
                if (report.total > 0)
                {
                    TxtUpdateStatus.Text = $"Загрузка: {report.percent}% ({report.received / 1048576} МБ / {report.total / 1048576} МБ)...";
                }
                else
                {
                    TxtUpdateStatus.Text = $"Загрузка: {report.received / 1048576} МБ...";
                }
            });

            try
            {
                await _updateManager.DownloadAndApplyUpdateAsync(_latestUpdateInfo.DownloadUrl, progress);
            }
            catch (Exception ex)
            {
                BtnApplyUpdate.IsEnabled = true;
                PbUpdateProgress.Visibility = Visibility.Collapsed;
                TxtUpdateStatus.Text = $"Ошибка загрузки: {ex.Message}";
                AppendLog($"❌ Ошибка загрузки обновления: {ex.Message}");
                VibeMessageBox.Show(this, $"Ошибка загрузки обновления: {ex.Message}\n\nВы также можете скачать файл вручную, нажав кнопку «🌐 Скачать с сайта».", "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnManualDownload_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? targetUrl = _latestUpdateInfo?.DownloadUrl;
                if (string.IsNullOrEmpty(targetUrl))
                {
                    targetUrl = "https://github.com/Gera9997/VibeDesk/releases/latest";
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                VibeMessageBox.Show(this, $"Не удалось открыть браузер: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCloseUpdateBanner_Click(object sender, RoutedEventArgs e)
        {
            BannerUpdate.Visibility = Visibility.Collapsed;
        }
    }
}