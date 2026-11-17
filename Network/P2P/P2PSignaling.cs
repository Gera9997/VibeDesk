using System;
using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;

namespace VibeDesk.Network.P2P
{
    public class PeerEndpointInfo
    {
        public string DeviceId { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
        public bool IsProbe { get; set; } = false;
        public string PublicIp { get; set; } = string.Empty;
        public int PublicPort { get; set; }
        public string PhysicalPublicIp { get; set; } = string.Empty;
        public int PhysicalPublicPort { get; set; }
        public string LocalIp { get; set; } = string.Empty;
        public int LocalPort { get; set; }
    }

    public class P2PSignaling : IDisposable
    {
        private static readonly (string Host, int Port)[] Brokers = new[]
        {
            ("broker.emqx.io", 1883),
            ("broker.hivemq.com", 1883)
        };

        private IMqttClient? _mqttClient;
        private readonly MqttClientFactory _factory = new();
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private readonly Timer _watchdogTimer;
        private bool _isDisposed = false;

        private string? _registeredHostId;
        private Func<PeerEndpointInfo, Task<PeerEndpointInfo>>? _registeredHostCallback;
        private string? _activeBrokerHost;

        public event Action<string>? OnLog;
        public bool IsConnected => _mqttClient != null && _mqttClient.IsConnected;

        public P2PSignaling()
        {
            _watchdogTimer = new Timer(async _ =>
            {
                if (_isDisposed) return;
                if (!string.IsNullOrEmpty(_registeredHostId) && (_mqttClient == null || !_mqttClient.IsConnected))
                {
                    await ReRegisterAsync();
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        public async Task<bool> ConnectBrokerAsync()
        {
            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                return true;
            }

            await _connectLock.WaitAsync();
            try
            {
                if (_mqttClient != null && _mqttClient.IsConnected)
                {
                    return true;
                }

                foreach (var (host, port) in Brokers)
                {
                    try
                    {
                        _mqttClient?.Dispose();
                        _mqttClient = _factory.CreateMqttClient();

                        var options = new MqttClientOptionsBuilder()
                            .WithTcpServer(host, port)
                            .WithClientId($"VibeDesk_{Guid.NewGuid():N}")
                            .WithKeepAlivePeriod(TimeSpan.FromSeconds(15))
                            .WithCleanSession(true)
                            .WithTimeout(TimeSpan.FromSeconds(4))
                            .Build();

                        _mqttClient.DisconnectedAsync += async e =>
                        {
                            if (_isDisposed) return;
                            OnLog?.Invoke($"[Signaling] Соединение с брокером {host} разорвано ({e.Reason}). Переподключение...");
                            await Task.Delay(2000);
                            if (!_isDisposed && !string.IsNullOrEmpty(_registeredHostId) && _registeredHostCallback != null)
                            {
                                await ReRegisterAsync();
                            }
                        };

                        var res = await _mqttClient.ConnectAsync(options);
                        if (res.ResultCode == MqttClientConnectResultCode.Success)
                        {
                            _activeBrokerHost = host;
                            OnLog?.Invoke($"Подключено к брокеру сигналов: {host}:{port}");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"Не удалось подключиться к {host}:{port}: {ex.Message}");
                    }
                }

                return false;
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task ReRegisterAsync()
        {
            if (_isDisposed) return;
            try
            {
                if (await ConnectBrokerAsync() && !string.IsNullOrEmpty(_registeredHostId) && _registeredHostCallback != null)
                {
                    await RegisterHostAsync(_registeredHostId, _registeredHostCallback);
                }
            }
            catch { }
        }

        public async Task RegisterHostAsync(string hostId, Func<PeerEndpointInfo, Task<PeerEndpointInfo>> onClientRequest)
        {
            _registeredHostId = hostId;
            _registeredHostCallback = onClientRequest;

            if (!await ConnectBrokerAsync()) return;

            string topicReq = $"vibedesk/session/{hostId}/req";

            _mqttClient!.ApplicationMessageReceivedAsync -= OnHostApplicationMessageReceived;
            _mqttClient!.ApplicationMessageReceivedAsync += OnHostApplicationMessageReceived;

            await _mqttClient.SubscribeAsync(topicReq);
            OnLog?.Invoke($"Vibe ID зарегистрирован в глобальной сети ({_activeBrokerHost}): {hostId}");
        }

        private async Task OnHostApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs e)
        {
            if (string.IsNullOrEmpty(_registeredHostId)) return;
            string topicReq = $"vibedesk/session/{_registeredHostId}/req";
            if (e.ApplicationMessage.Topic == topicReq)
            {
                try
                {
                    string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                    var clientInfo = JsonSerializer.Deserialize<PeerEndpointInfo>(payload);
                    if (clientInfo != null && _registeredHostCallback != null)
                    {
                        var hostInfo = await _registeredHostCallback(clientInfo);
                        if (!string.IsNullOrEmpty(clientInfo.RequestId))
                        {
                            hostInfo.RequestId = clientInfo.RequestId;
                        }
                        string responsePayload = JsonSerializer.Serialize(hostInfo);

                        // 1. Publish to unique request topic for collision-free response
                        if (!string.IsNullOrEmpty(clientInfo.RequestId))
                        {
                            string specificTopicRes = $"vibedesk/session/{_registeredHostId}/res/{clientInfo.RequestId}";
                            var specificMsg = new MqttApplicationMessageBuilder()
                                .WithTopic(specificTopicRes)
                                .WithPayload(responsePayload)
                                .Build();
                            await _mqttClient!.PublishAsync(specificMsg);
                        }

                        // 2. Also publish to legacy topic for backward compatibility
                        string legacyTopicRes = $"vibedesk/session/{_registeredHostId}/res";
                        var legacyMsg = new MqttApplicationMessageBuilder()
                            .WithTopic(legacyTopicRes)
                            .WithPayload(responsePayload)
                            .Build();
                        await _mqttClient!.PublishAsync(legacyMsg);
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"Ошибка обработки запроса пира: {ex.Message}");
                }
            }
        }

        public async Task<PeerEndpointInfo?> RequestHostEndpointsAsync(string hostId, PeerEndpointInfo myClientInfo, int timeoutMs = 7000)
        {
            if (!await ConnectBrokerAsync()) return null;

            string reqId = string.IsNullOrEmpty(myClientInfo.RequestId)
                ? Guid.NewGuid().ToString("N")[..8]
                : myClientInfo.RequestId;
            myClientInfo.RequestId = reqId;

            string topicReq = $"vibedesk/session/{hostId}/req";
            string specificTopicRes = $"vibedesk/session/{hostId}/res/{reqId}";
            string legacyTopicRes = $"vibedesk/session/{hostId}/res";

            var tcs = new TaskCompletionSource<PeerEndpointInfo?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => tcs.TrySetResult(null));

            Task MessageHandler(MqttApplicationMessageReceivedEventArgs e)
            {
                if (e.ApplicationMessage.Topic == specificTopicRes || e.ApplicationMessage.Topic == legacyTopicRes)
                {
                    try
                    {
                        string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                        var hostInfo = JsonSerializer.Deserialize<PeerEndpointInfo>(payload);
                        if (hostInfo != null)
                        {
                            if (e.ApplicationMessage.Topic == specificTopicRes ||
                                string.IsNullOrEmpty(hostInfo.RequestId) ||
                                hostInfo.RequestId == reqId)
                            {
                                tcs.TrySetResult(hostInfo);
                            }
                        }
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                }
                return Task.CompletedTask;
            }

            _mqttClient!.ApplicationMessageReceivedAsync += MessageHandler;

            try
            {
                await _mqttClient.SubscribeAsync(specificTopicRes);
                await _mqttClient.SubscribeAsync(legacyTopicRes);

                string payload = JsonSerializer.Serialize(myClientInfo);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topicReq)
                    .WithPayload(payload)
                    .Build();

                await _mqttClient.PublishAsync(message);
                return await tcs.Task;
            }
            finally
            {
                _mqttClient.ApplicationMessageReceivedAsync -= MessageHandler;
                try
                {
                    await _mqttClient.UnsubscribeAsync(specificTopicRes);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            try
            {
                _watchdogTimer?.Dispose();
                _connectLock.Dispose();
                var client = _mqttClient;
                _mqttClient = null;
                if (client != null)
                {
                    Task.Run(() =>
                    {
                        try { client.Dispose(); } catch { }
                    });
                }
            }
            catch { }
        }
    }
}
