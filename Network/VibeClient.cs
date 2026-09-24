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
        public event Action<IPEndPoint>? OnPunchReceived;
        public event Action<IPEndPoint>? OnPunchAckReceived;

        public const int DefaultClientPort = 15891;
        public bool IsConnected => _serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected;
        public int PingMs { get; private set; }
        public int CurrentFps { get; private set; }
        public double IncomingKbps { get; private set; }
        public int RemoteWidth { get; private set; } = 1920;
        public int RemoteHeight { get; private set; } = 1080;

        public NetManager RawNetManager => _netClient;
        public int LocalPort => _netClient.LocalPort;

        public VibeClient()
        {
            _listener = new EventBasedNetListener();
            _netClient = new NetManager(_listener)
            {
                AutoRecycle = true,
                IPv6Enabled = false,
                UnsyncedEvents = true,
                DisconnectTimeout = 20000,
                UnconnectedMessagesEnabled = true,
                UpdateTime = 5
            };

            _listener.NetworkReceiveUnconnectedEvent += (point, reader, messageType) =>
            {
                try
                {
                    if (point.Port == 19302 || point.Port == 3478) return;

                    if (reader.AvailableBytes >= 1)
                    {
                        byte firstByte = reader.PeekByte();
                        if (firstByte == (byte)PacketType.VibePunchAck)
                        {
                            reader.GetByte();
                            int idLen = reader.AvailableBytes > 0 ? reader.GetByte() : 0;
                            string id = idLen > 0 && reader.AvailableBytes >= idLen ? Encoding.UTF8.GetString(reader.GetRemainingBytes()) : "";
                            OnStatusChanged?.Invoke($"✅ Получен Punch-ACK от хоста: {point}");
                            OnPunchAckReceived?.Invoke(point);
                            OnPunchReceived?.Invoke(point);
                            return;
                        }

                        if (firstByte == (byte)PacketType.VibePunch)
                        {
                            reader.GetByte();
                            OnStatusChanged?.Invoke($"🥊 Получен UDP-пакет (Punch) от: {point}");
                            OnPunchReceived?.Invoke(point);
                            return;
                        }
                    }

                    byte[] data = reader.GetRemainingBytes();
                    string msg = Encoding.UTF8.GetString(data);
                    if (msg.StartsWith("VIBE_PUNCH"))
                    {
                        OnStatusChanged?.Invoke($"Получен UDP-пакет (Punch) от: {point}");
                        OnPunchReceived?.Invoke(point);
                    }
                }
                catch { }
            };

            _reassembler = new FrameReassembler();
            _reassembler.OnFrameReadyWithId += (frameId, frameBytes) =>
            {
                _framesThisSecond++;
                if (_serverPeer != null && _serverPeer.ConnectionState == ConnectionState.Connected)
                {
                    _serverPeer.Send(PacketBuilder.CreateFrameAck(frameId), DeliveryMethod.Unreliable);
                }
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
                if (_suppressDisconnectEvent || info.Reason == DisconnectReason.DisconnectPeerCalled)
                {
                    return;
                }
                OnStatusChanged?.Invoke($"Отключено от хоста: {info.Reason}");
                OnDisconnected?.Invoke();
            };

            _listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
            {
                _bytesReceivedThisSecond += reader.AvailableBytes;
                ProcessIncomingPacket(peer, reader);
            };
        }

        public bool Start(int preferredPort = DefaultClientPort, string bindIp = "")
        {
            if (_netClient.IsRunning) return true;

            IPAddress? localAddr = null;
            if (!string.IsNullOrEmpty(bindIp) && IPAddress.TryParse(bindIp, out var parsed) && !IPAddress.IsLoopback(parsed))
            {
                localAddr = parsed;
            }

            // 1. Try dedicated client port on physical adapter if specified
            if (localAddr != null)
            {
                if (preferredPort > 0 && _netClient.Start(localAddr, IPAddress.IPv6None, preferredPort))
                {
                    StartPollThread();
                    return true;
                }
                if (_netClient.Start(localAddr, IPAddress.IPv6None, 0))
                {
                    StartPollThread();
                    return true;
                }
            }

            // 2. Try dedicated client port on all interfaces
            if (preferredPort > 0 && _netClient.Start(preferredPort))
            {
                StartPollThread();
                return true;
            }

            // 3. Fallback to any OS ephemeral port
            if (_netClient.Start())
            {
                StartPollThread();
                return true;
            }

            OnStatusChanged?.Invoke("Не удалось запустить клиентский сетевой модуль.");
            return false;
        }

        private void StartPollThread()
        {
            if (_pollThread != null && _pollThread.IsAlive) return;

            _isRunning = true;
            _pollThread = new Thread(ClientPollLoop)
            {
                IsBackground = true,
                Name = "VibeDesk_ClientPollThread"
            };
            _pollThread.Start();
        }

        private volatile bool _suppressDisconnectEvent = false;

        public void ResetPeer()
        {
            if (_serverPeer != null)
            {
                _suppressDisconnectEvent = true;
                try
                {
                    _serverPeer.Disconnect();
                    _serverPeer = null;
                }
                catch { }
                finally
                {
                    _suppressDisconnectEvent = false;
                }
            }
        }

        public bool Connect(string host, int port)
        {
            if (!_netClient.IsRunning)
            {
                if (!Start())
                {
                    return false;
                }
            }

            ResetPeer();

            StartPollThread();

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
            if (!_netClient.IsRunning)
            {
                if (!Start()) return;
            }

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
                            _serverPeer!.Send(PacketBuilder.CreatePing(_lastPingSendTicks), DeliveryMethod.Unreliable);
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

        public void Punch(IPEndPoint target, string deviceId = "")
        {
            try
            {
                byte[] packet = PacketBuilder.CreatePunch(deviceId);
                _netClient.SendUnconnectedMessage(packet, target);
            }
            catch { }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
