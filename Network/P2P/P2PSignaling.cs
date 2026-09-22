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
        public string LocalIp { get; set; } = string.Empty;
        public int LocalPort { get; set; }
    }

    public class P2PSignaling : IDisposable
    {
        private const string BrokerHost = "broker.emqx.io";
        private const int BrokerPort = 1883;

        private IMqttClient? _mqttClient;
        private readonly MqttClientFactory _factory = new();

        public event Action<string>? OnLog;

        public async Task<bool> ConnectBrokerAsync()
        {
            try
            {
                if (_mqttClient != null && _mqttClient.IsConnected)
                {
                    return true;
                }

                _mqttClient?.Dispose();
                _mqttClient = _factory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(BrokerHost, BrokerPort)
                    .WithClientId($"VibeDesk_{Guid.NewGuid():N}")
                    .WithTimeout(TimeSpan.FromSeconds(5))
                    .Build();

                var res = await _mqttClient.ConnectAsync(options);
                return res.ResultCode == MqttClientConnectResultCode.Success;
            }
            catch (Exception ex)
            {
                OnLog?.Invoke($"Ошибка подключения к брокеру: {ex.Message}");
                return false;
            }
        }

        public async Task RegisterHostAsync(string hostId, Func<PeerEndpointInfo, Task<PeerEndpointInfo>> onClientRequest)
        {
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
            OnLog?.Invoke($"Vibe ID зарегистрирован в глобальной сети: {hostId}");
        }

        public async Task<PeerEndpointInfo?> RequestHostEndpointsAsync(string hostId, PeerEndpointInfo myClientInfo, int timeoutMs = 8000)
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
            _mqttClient?.Dispose();
        }
    }
}
