using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeDesk.Input;
using VibeDesk.Network.Protocol;

namespace VibeDesk.Network
{
    public class VibeClient : IDisposable
    {
        private readonly EventBasedNetListener _listener;
        private readonly NetManager _netClient;
        private readonly FrameReassembler _reassembler;
        private NetPeer? _serverPeer;

        private Thread? _pollThread;
        private volatile bool _isRunning = false;

        private long _lastPingSendTicks = 0;
        private readonly Stopwatch _pingStopwatch = new();
        private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
        private int _framesThisSecond = 0;
        private long _bytesReceivedThisSecond = 0;

        public event Action<string>? OnStatusChanged;
        public event Action? OnConnected;
        public event Action? OnDisconnected;
        public event Action<byte[]>? OnFrameReceived;
        public event Action<int, int>? OnScreenInfoReceived;
        public event Action<string>? OnClipboardReceived;

        public bool IsConnected => _serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected;
        public int PingMs { get; private set; }
        public int CurrentFps { get; private set; }
        public double IncomingKbps { get; private set; }
        public int RemoteWidth { get; private set; } = 1920;
        public int RemoteHeight { get; private set; } = 1080;

        public NetManager RawNetManager => _netClient;

        public VibeClient()
        {
            _listener = new EventBasedNetListener();
            _netClient = new NetManager(_listener)
            {
                AutoRecycle = true,
                IPv6Enabled = false,
                UnsyncedEvents = true,
                DisconnectTimeout = 5000,
                UnconnectedMessagesEnabled = true,
                UpdateTime = 5
            };

            _listener.NetworkReceiveUnconnectedEvent += (point, reader, messageType) =>
            {
                OnStatusChanged?.Invoke($"Получен UDP-пакет (Punch) от: {point}");
            };

            _reassembler = new FrameReassembler();
            _reassembler.OnFrameReady += frameBytes =>
            {
                _framesThisSecond++;
                OnFrameReceived?.Invoke(frameBytes);
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _serverPeer = peer;
                OnStatusChanged?.Invoke($"Подключено к хосту: {peer.Address}");
                OnConnected?.Invoke();
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                _serverPeer = null;
                OnStatusChanged?.Invoke($"Отключено от хоста: {info.Reason}");
                OnDisconnected?.Invoke();
            };

            _listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
            {
                _bytesReceivedThisSecond += reader.AvailableBytes;
                ProcessIncomingPacket(peer, reader);
            };
        }

        public bool Connect(string host, int port)
        {
            if (!_netClient.IsRunning)
            {
                if (!_netClient.Start())
                {
                    OnStatusChanged?.Invoke("Не удалось запустить клиентский сетевой модуль.");
                    return false;
                }
            }

            _isRunning = true;
            _pollThread = new Thread(ClientPollLoop)
            {
                IsBackground = true,
                Name = "VibeDesk_ClientPollThread"
            };
            _pollThread.Start();

            OnStatusChanged?.Invoke($"Подключение к {host}:{port}...");
            _serverPeer = _netClient.Connect(host, port, VibeHost.ConnectionKey);
            return _serverPeer != null;
        }

        public bool Connect(IPEndPoint endPoint)
        {
            return Connect(endPoint.Address.ToString(), endPoint.Port);
        }

        public void PunchNat(IPEndPoint target)
        {
            if (!_netClient.IsRunning) return;

            Task.Run(async () =>
            {
                byte[] punch = Encoding.UTF8.GetBytes("VIBE_PUNCH");
                for (int i = 0; i < 4; i++)
                {
                    try
                    {
                        _netClient.SendUnconnectedMessage(punch, target);
                    }
                    catch { }
                    await Task.Delay(25);
                }
            });
        }

        public void Disconnect()
        {
            _isRunning = false;
            _serverPeer?.Disconnect();
            _pollThread?.Join(300);
            _netClient.Stop();
            _serverPeer = null;
            OnStatusChanged?.Invoke("Клиент отключен.");
        }

        private void ClientPollLoop()
        {
            while (_isRunning)
            {
                try
                {
                    _netClient.PollEvents();

                    if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
                    {
                        CurrentFps = _framesThisSecond;
                        IncomingKbps = (_bytesReceivedThisSecond * 8.0) / 1024.0;
                        _framesThisSecond = 0;
                        _bytesReceivedThisSecond = 0;
                        _fpsStopwatch.Restart();

                        // Send periodic ping every 1 second
                        if (IsConnected)
                        {
                            _lastPingSendTicks = Stopwatch.GetTimestamp();
                            _serverPeer!.Send(PacketBuilder.CreatePing(_lastPingSendTicks), DeliveryMethod.ReliableOrdered);
                        }
                    }
                }
                catch { }

                Thread.Sleep(5);
            }
        }

        private void ProcessIncomingPacket(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1) return;
            var packetType = (PacketType)reader.GetByte();

            switch (packetType)
            {
                case PacketType.FrameChunk:
                    // Raw chunk including type byte
                    byte[] rawData = reader.GetRemainingBytes();
                    byte[] withType = new byte[rawData.Length + 1];
                    withType[0] = (byte)PacketType.FrameChunk;
                    Buffer.BlockCopy(rawData, 0, withType, 1, rawData.Length);
                    _reassembler.ProcessChunk(withType);
                    break;

                case PacketType.Pong:
                    if (reader.AvailableBytes >= 8)
                    {
                        long sendTicks = reader.GetLong();
                        long elapsedTicks = Stopwatch.GetTimestamp() - sendTicks;
                        PingMs = (int)((elapsedTicks * 1000.0) / Stopwatch.Frequency);
                    }
                    break;

                case PacketType.ScreenInfo:
                    if (reader.AvailableBytes >= 8)
                    {
                        RemoteWidth = reader.GetInt();
                        RemoteHeight = reader.GetInt();
                        OnScreenInfoReceived?.Invoke(RemoteWidth, RemoteHeight);
                    }
                    break;

                case PacketType.Clipboard:
                    if (reader.AvailableBytes >= 4)
                    {
                        int textLen = reader.GetInt();
                        if (reader.AvailableBytes >= textLen)
                        {
                            byte[] textBytes = new byte[textLen];
                            reader.GetBytes(textBytes, textLen);
                            string text = Encoding.UTF8.GetString(textBytes);
                            OnClipboardReceived?.Invoke(text);
                        }
                    }
                    break;
            }
        }

        public void SendMouseMove(double normX, double normY)
        {
            if (IsConnected)
            {
                byte[] packet = PacketBuilder.CreateMouseMove(normX, normY);
                _serverPeer!.Send(packet, DeliveryMethod.Unreliable);
            }
        }

        public void SendMouseButton(VibeMouseButton button, bool isDown)
        {
            if (IsConnected)
            {
                byte[] packet = PacketBuilder.CreateMouseButton(button, isDown);
                _serverPeer!.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendMouseWheel(int delta)
        {
            if (IsConnected)
            {
                byte[] packet = PacketBuilder.CreateMouseWheel(delta);
                _serverPeer!.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendKey(ushort virtualKey, bool isDown)
        {
            if (IsConnected)
            {
                byte[] packet = PacketBuilder.CreateKey(virtualKey, isDown);
                _serverPeer!.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendClipboard(string text)
        {
            if (IsConnected)
            {
                byte[] packet = PacketBuilder.CreateClipboard(text);
                _serverPeer!.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        public void SendStreamSettings(float scale, int fps, int quality)
        {
            if (IsConnected)
            {
                byte[] packet = PacketBuilder.CreateStreamSettings(scale, fps, quality);
                _serverPeer!.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
