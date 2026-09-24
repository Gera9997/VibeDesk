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
        private bool _isDisposed = false;

        private string? _registeredHostId;
        private Func<PeerEndpointInfo, Task<PeerEndpointInfo>>? _registeredHostCallback;
        private string? _activeBrokerHost;

        public event Action<string>? OnLog;
        public bool IsConnected => _mqttClient != null && _mqttClient.IsConnected;

        public async Task<bool> ConnectBrokerAsync()
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

        private async Task ReRegisterAsync()
        {
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
            string topicRes = $"vibedesk/session/{hostId}/res";

            _mqttClient!.ApplicationMessageReceivedAsync += async e =>
            {
                if (e.ApplicationMessage.Topic == topicReq)
                {
                    try
                    {
                        string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                        var clientInfo = JsonSerializer.Deserialize<PeerEndpointInfo>(payload);
                        if (clientInfo != null)
                        {
                            var hostInfo = await onClientRequest(clientInfo);
                            string responsePayload = JsonSerializer.Serialize(hostInfo);

                            var message = new MqttApplicationMessageBuilder()
                                .WithTopic(topicRes)
                                .WithPayload(responsePayload)
                                .Build();

                            await _mqttClient.PublishAsync(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"Ошибка обработки запроса пира: {ex.Message}");
                    }
                }
            };

            await _mqttClient.SubscribeAsync(topicReq);
            OnLog?.Invoke($"Vibe ID зарегистрирован в глобальной сети ({_activeBrokerHost}): {hostId}");
        }

        public async Task<PeerEndpointInfo?> RequestHostEndpointsAsync(string hostId, PeerEndpointInfo myClientInfo, int timeoutMs = 7000)
        {
            if (!await ConnectBrokerAsync()) return null;

            string topicReq = $"vibedesk/session/{hostId}/req";
            string topicRes = $"vibedesk/session/{hostId}/res";

            var tcs = new TaskCompletionSource<PeerEndpointInfo?>();
            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() => tcs.TrySetResult(null));

            Task MessageHandler(MqttApplicationMessageReceivedEventArgs e)
            {
                if (e.ApplicationMessage.Topic == topicRes)
                {
                    try
                    {
                        string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload.ToArray());
                        var hostInfo = JsonSerializer.Deserialize<PeerEndpointInfo>(payload);
                        tcs.TrySetResult(hostInfo);
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
                await _mqttClient.SubscribeAsync(topicRes);

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
                    await _mqttClient.UnsubscribeAsync(topicRes);
                }
                catch { }
            }
        }

        public void Dispose()
        {
            _isDisposed = true;
            try
            {
                _mqttClient?.Dispose();
            }
            catch { }
        }
    }
}
