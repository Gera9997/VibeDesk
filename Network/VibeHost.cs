using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;
using VibeDesk.Capture;
using VibeDesk.Input;
using VibeDesk.Native;
using VibeDesk.Network.Protocol;

namespace VibeDesk.Network
{
    public class VibeHost : IDisposable
    {
        public const int DefaultPort = 15890;
        public const string ConnectionKey = "VibeDesk_v1";

        private readonly EventBasedNetListener _listener;
        private readonly NetManager _netServer;
        private ScreenCaptureManager? _captureManager;
        private NetPeer? _connectedPeer;

        private Thread? _streamingThread;
        private volatile bool _isRunning = false;
        private volatile int _targetFps = 25;
        private volatile int _maxBitrateKbps = 15000;
        private uint _frameCounter = 0;
        private volatile uint _lastClientAckedFrameId = 0;
        private uint _lastSentFrameId = 0;
        private long _lastFrameSentTicks = 0;

        // Hardware cursor state
        private int _lastCursorX = -1;
        private int _lastCursorY = -1;
        private bool _lastCursorVisible = true;

        public event Action<string>? OnStatusChanged;
        public event Action<NetPeer>? OnClientConnected;
        public event Action<NetPeer>? OnClientDisconnected;
        public event Action<string>? OnClipboardReceived;
        public event Action<IPEndPoint>? OnPunchReceived;

        public int Port { get; private set; }
        public int ScreenWidth => _captureManager?.ScreenWidth ?? 1920;
        public int ScreenHeight => _captureManager?.ScreenHeight ?? 1080;
        public string ActiveCaptureEngine => _captureManager?.ActiveEngineName ?? "None";
        public bool HasClient => _connectedPeer != null && _connectedPeer.ConnectionState == ConnectionState.Connected;
        public string DeviceId { get; set; } = string.Empty;

        public int CurrentFps { get; private set; }
        public double OutgoingKbps { get; private set; }
        public int MaxBitrateKbps { get => _maxBitrateKbps; set => _maxBitrateKbps = Math.Clamp(value, 500, 50000); }

        public NetManager RawNetManager => _netServer;
        public IPEndPoint? DiscoveredStunEndPoint { get; private set; }

        public async Task<IPEndPoint?> QueryStunOverActiveSocketAsync(int timeoutMs = 1500)
        {
            DiscoveredStunEndPoint = null;
            await VibeDesk.Network.P2P.StunResolver.SendStunQueriesViaNetManagerAsync(RawNetManager);

            var sw = Stopwatch.StartNew();
            while (DiscoveredStunEndPoint == null && sw.ElapsedMilliseconds < timeoutMs)
            {
                RawNetManager.PollEvents();
                await Task.Delay(15);
            }
            return DiscoveredStunEndPoint;
        }

        public VibeHost(int port = DefaultPort)
        {
            Port = port;
            _listener = new EventBasedNetListener();
            _netServer = new NetManager(_listener)
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
                    if (point.Port == 19302 || point.Port == 3478)
                    {
                        var ep = VibeDesk.Network.P2P.StunResolver.ParseResponse(reader.GetRemainingBytes());
                        if (ep != null)
                        {
                            DiscoveredStunEndPoint = ep;
                        }
                        return;
                    }

                    if (reader.AvailableBytes >= 1)
                    {
                        byte firstByte = reader.PeekByte();
                        if (firstByte == (byte)PacketType.VibePunch)
                        {
                            reader.GetByte();
                            int idLen = reader.AvailableBytes > 0 ? reader.GetByte() : 0;
                            string fromId = idLen > 0 && reader.AvailableBytes >= idLen ? Encoding.UTF8.GetString(reader.GetRemainingBytes()) : "";
                            OnStatusChanged?.Invoke($"🥊 [Punch] Получен от клиента {point} (ID: {fromId})");
                            OnPunchReceived?.Invoke(point);

                            // Send VibePunchAck response immediately
                            byte[] ackPacket = PacketBuilder.CreatePunchAck(DeviceId);
                            _netServer.SendUnconnectedMessage(ackPacket, point);
                            return;
                        }

                        if (firstByte == (byte)PacketType.VibePunchAck)
                        {
                            reader.GetByte();
                            OnPunchReceived?.Invoke(point);
                            return;
                        }
                    }

                    byte[] data = reader.GetRemainingBytes();
                    string msg = Encoding.UTF8.GetString(data);

                    if (msg.StartsWith("VIBE_DISC:"))
                    {
                        var parts = msg.Split(':');
                        if (parts.Length >= 2 && !string.IsNullOrEmpty(DeviceId) && parts[1] == DeviceId)
                        {
                            byte[] resp = Encoding.UTF8.GetBytes($"VIBE_RESP:{DeviceId}:{Port}");
                            _netServer.SendUnconnectedMessage(resp, point);
                            OnStatusChanged?.Invoke($"[LAN Discovery] Ответили на поиск пиру: {point.Address}:{point.Port}");
                        }
                    }

                    if (msg.StartsWith("VIBE_PUNCH"))
                    {
                        OnPunchReceived?.Invoke(point);
                        return;
                    }
                }
                catch { }
            };

            _listener.ConnectionRequestEvent += request =>
            {
                OnStatusChanged?.Invoke($"[Хост] Входящий запрос на сеанс от {request.RemoteEndPoint}...");

                if (_connectedPeer == null ||
                    _connectedPeer.ConnectionState != ConnectionState.Connected ||
                    _connectedPeer.Address.Equals(request.RemoteEndPoint.Address))
                {
                    if (_connectedPeer != null && _connectedPeer.ConnectionState == ConnectionState.Connected)
                    {
                        OnStatusChanged?.Invoke($"[Хост] Сброс предыдущего зависшего сеанса для {request.RemoteEndPoint.Address}");
                        _connectedPeer.Disconnect();
                    }

                    request.AcceptIfKey(ConnectionKey);
                    OnStatusChanged?.Invoke($"[Хост] ✅ Запрос одобрен для {request.RemoteEndPoint}");
                }
                else
                {
                    request.Reject();
                    OnStatusChanged?.Invoke($"[Хост] ⚠️ Запрос отклонен: хост уже занят другим клиентом ({_connectedPeer.Address})");
                }
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _connectedPeer = peer;
                _lastClientAckedFrameId = 0;
                _lastSentFrameId = 0;
                _lastFrameSentTicks = 0;
                OnStatusChanged?.Invoke($"⚡ Клиент подключен: {peer.Address}:{peer.Port}");
                OnClientConnected?.Invoke(peer);

                // Send screen geometry
                peer.Send(PacketBuilder.CreateScreenInfo(ScreenWidth, ScreenHeight), DeliveryMethod.ReliableOrdered);
            };

            _listener.PeerDisconnectedEvent += (peer, info) =>
            {
                if (_connectedPeer == peer)
                {
                    _connectedPeer = null;
                }
                OnStatusChanged?.Invoke($"Клиент отключен: {info.Reason}");
                OnClientDisconnected?.Invoke(peer);
            };

            _listener.NetworkReceiveEvent += (peer, reader, channel, deliveryMethod) =>
            {
                ProcessIncomingPacket(peer, reader);
            };
        }

        public void SetStreamSettings(float scale, int targetFps, int quality)
        {
            _targetFps = Math.Clamp(targetFps, 15, 120);
            _captureManager?.SetScale(scale);
            _captureManager?.SetQuality(quality);
            OnStatusChanged?.Invoke($"[Настройки стрима] Масштаб: {(int)(scale * 100)}%, Цель FPS: {_targetFps}, Качество: {quality}%");
        }

        public bool Start(int targetFps = 25, int jpegQuality = 55, float scale = 0.70f, string bindIp = "", int maxBitrateKbps = 15000)
        {
            if (_isRunning) return true;

            try
            {
                _targetFps = Math.Clamp(targetFps, 15, 120);
                _maxBitrateKbps = Math.Clamp(maxBitrateKbps, 500, 50000);
                _captureManager = new ScreenCaptureManager(jpegQuality, scale);
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Ошибка инициализации захвата: {ex.Message}");
                return false;
            }

            bool bound = false;
            if (!string.IsNullOrEmpty(bindIp) && IPAddress.TryParse(bindIp, out var localAddr) && !IPAddress.IsLoopback(localAddr))
            {
                bound = _netServer.Start(localAddr, IPAddress.IPv6None, Port);
            }

            if (!bound && !_netServer.Start(Port))
            {
                OnStatusChanged?.Invoke($"Не удалось запустить сервер на порту {Port}");
                return false;
            }

            // Enable 1ms multimedia timer resolution on Windows for precise 25-120 FPS
            try { Win32.timeBeginPeriod(1); } catch { }

            _isRunning = true;
            _streamingThread = new Thread(StreamingLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "VibeDesk_StreamingThread"
            };
            _streamingThread.Start();

            OnStatusChanged?.Invoke($"Хост запущен на порту {Port}. Движок: {_captureManager.ActiveEngineName}, FPS: {_targetFps}, Кэп: {_maxBitrateKbps} Кбит/с");
            return true;
        }

        public void Stop()
        {
            _isRunning = false;
            _streamingThread?.Join(500);
            _netServer.Stop();
            _captureManager?.Dispose();
            _captureManager = null;
            _connectedPeer = null;

            try { Win32.timeEndPeriod(1); } catch { }

            OnStatusChanged?.Invoke("Хост остановлен.");
        }

        private void StreamingLoop()
        {
            var stopwatch = new Stopwatch();
            var fpsStopwatch = Stopwatch.StartNew();
            int framesThisSecond = 0;
            long bytesSentThisSecond = 0;
            long lastKeepaliveMs = 0;

            long maxBytesPerSec = (_maxBitrateKbps * 1024L) / 8L;
            double tokenBucket = maxBytesPerSec * 0.15; // burst-окно 150 мс
            long lastTokenTicks = Stopwatch.GetTimestamp();

            while (_isRunning)
            {
                stopwatch.Restart();
                long frameIntervalMs = Math.Max(1, 1000 / _targetFps);

                try
                {
                    _netServer.PollEvents();

                    if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                    {
                        CurrentFps = framesThisSecond;
                        OutgoingKbps = (bytesSentThisSecond * 8.0) / 1024.0;
                        framesThisSecond = 0;
                        bytesSentThisSecond = 0;
                        fpsStopwatch.Restart();
                    }

                    // Обновляем параметры Token Bucket
                    maxBytesPerSec = (_maxBitrateKbps * 1024L) / 8L;
                    double maxBurst = Math.Max(65536, maxBytesPerSec * 0.25);
                    long tokenNow = Stopwatch.GetTimestamp();
                    double elapsedSec = Math.Max(0, (double)(tokenNow - lastTokenTicks) / Stopwatch.Frequency);
                    lastTokenTicks = tokenNow;
                    tokenBucket = Math.Min(maxBurst, tokenBucket + (elapsedSec * maxBytesPerSec));

                    // NAT Keepalive: каждые 15 секунд отправляем реальный STUN-запрос, чтобы роутер держал внешний порт открытым!
                    if (_connectedPeer == null && fpsStopwatch.ElapsedMilliseconds - lastKeepaliveMs >= 15000)
                    {
                        lastKeepaliveMs = fpsStopwatch.ElapsedMilliseconds;
                        _ = VibeDesk.Network.P2P.StunResolver.SendStunQueriesViaNetManagerAsync(_netServer);
                    }

                    var peer = _connectedPeer;
                    if (peer != null && peer.ConnectionState == ConnectionState.Connected && _captureManager != null)
                    {
                        // Hardware Cursor: stream coordinates with 0 latency (11 bytes)
                        try
                        {
                            var ci = new Win32.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.CURSORINFO>() };
                            if (Win32.GetCursorInfo(out ci))
                            {
                                bool visible = (ci.flags & Win32.CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero;
                                int curX = ci.ptScreenPos.X;
                                int curY = ci.ptScreenPos.Y;

                                if (curX != _lastCursorX || curY != _lastCursorY || visible != _lastCursorVisible)
                                {
                                    _lastCursorX = curX;
                                    _lastCursorY = curY;
                                    _lastCursorVisible = visible;

                                    byte[] cursorPacket = PacketBuilder.CreateCursorPosition(visible, curX, curY, ScreenWidth, ScreenHeight);
                                    peer.Send(cursorPacket, DeliveryMethod.Unreliable);
                                }
                            }
                        }
                        catch { }

                        uint inFlight = _lastSentFrameId > _lastClientAckedFrameId ? (_lastSentFrameId - _lastClientAckedFrameId) : 0;
                        long nowTicks = Stopwatch.GetTimestamp();
                        long msSinceLastSend = _lastFrameSentTicks == 0 ? 999 :
                            (long)((nowTicks - _lastFrameSentTicks) * 1000.0 / Stopwatch.Frequency);

                        bool allowSend = true;
                        bool forceKeyframe = (framesThisSecond == 0 && msSinceLastSend > 1000);

                        // Окно inFlight: 8 полос в кадре. Порог 32 = до 4 полных кадров в пути через Интернет.
                        // Если ACK задержался через интернет более чем на 35 мс — плавно двигаем окно, чтобы не тормозить стрим!
                        if (inFlight > 32)
                        {
                            if (msSinceLastSend > 35)
                            {
                                _lastClientAckedFrameId = _lastSentFrameId - 8;
                            }
                            else
                            {
                                allowSend = false;
                            }
                        }

                        // Плавный Token Bucket: исключает заморозку на 800 мс в конце секунды
                        if (tokenBucket < 0 && framesThisSecond > 0 && !forceKeyframe)
                        {
                            allowSend = false;
                        }

                        if (allowSend)
                        {
                            var strips = _captureManager.CaptureDirtyStrips(forceAll: forceKeyframe);
                            if (strips != null && strips.Count > 0)
                            {
                                foreach (var stripData in strips)
                                {
                                    if (peer.ConnectionState != ConnectionState.Connected) break;

                                    uint stripFrameId = unchecked(++_frameCounter);
                                    _lastSentFrameId = stripFrameId;
                                    _lastFrameSentTicks = nowTicks;

                                    int totalLength = stripData.Length;
                                    int chunkSize = PacketBuilder.MaxChunkPayloadSize;
                                    ushort totalChunks = (ushort)Math.Ceiling((double)totalLength / chunkSize);

                                    for (ushort i = 0; i < totalChunks; i++)
                                    {
                                        int offset = i * chunkSize;
                                        int length = Math.Min(chunkSize, totalLength - offset);
                                        byte[] chunkPacket = PacketBuilder.CreateFrameChunk(stripFrameId, i, totalChunks, stripData, offset, length);

                                        peer.Send(chunkPacket, DeliveryMethod.Unreliable);
                                        bytesSentThisSecond += chunkPacket.Length;
                                        tokenBucket -= chunkPacket.Length;
                                    }
                                }

                                _netServer.TriggerUpdate();
                                framesThisSecond++;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnStatusChanged?.Invoke($"[Ошибка захвата/стрима] {ex.Message}");
                }

                long elapsed = stopwatch.ElapsedMilliseconds;
                long sleepTime = frameIntervalMs - elapsed;
                if (sleepTime > 0)
                {
                    Thread.Sleep((int)sleepTime);
                }
            }
        }

        private void ProcessIncomingPacket(NetPeer peer, NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1) return;
            var packetType = (PacketType)reader.GetByte();

            switch (packetType)
            {
                case PacketType.InputEvent:
                    HandleInputEvent(reader);
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

                case PacketType.Ping:
                    if (reader.AvailableBytes >= 8)
                    {
                        long ticks = reader.GetLong();
                        peer.Send(PacketBuilder.CreatePong(ticks), DeliveryMethod.Unreliable);
                    }
                    break;

                case PacketType.FrameAck:
                    if (reader.AvailableBytes >= 4)
                    {
                        uint ackFrameId = reader.GetUInt();
                        if (ackFrameId > _lastClientAckedFrameId)
                        {
                            _lastClientAckedFrameId = ackFrameId;
                        }
                    }
                    break;

                case PacketType.PictureLossIndication:
                    _captureManager?.ResetForceFrame();
                    _lastClientAckedFrameId = _lastSentFrameId;
                    break;

                case PacketType.StreamSettings:
                    if (reader.AvailableBytes >= 12)
                    {
                        float scale = reader.GetFloat();
                        int fps = reader.GetInt();
                        int quality = reader.GetInt();
                        SetStreamSettings(scale, fps, quality);
                    }
                    break;
            }
        }

        private void HandleInputEvent(NetPacketReader reader)
        {
            if (reader.AvailableBytes < 1) return;
            var inputType = (InputEventType)reader.GetByte();

            switch (inputType)
            {
                case InputEventType.MouseMove:
                    if (reader.AvailableBytes >= 4)
                    {
                        ushort rawX = reader.GetUShort();
                        ushort rawY = reader.GetUShort();
                        double normX = rawX / 65535.0;
                        double normY = rawY / 65535.0;
                        InputSimulator.SendMouseMove(normX, normY);
                    }
                    break;

                case InputEventType.MouseDown:
                    if (reader.AvailableBytes >= 1)
                    {
                        var btn = (VibeMouseButton)reader.GetByte();
                        InputSimulator.SendMouseButton(btn, true);
                    }
                    break;

                case InputEventType.MouseUp:
                    if (reader.AvailableBytes >= 1)
                    {
                        var btn = (VibeMouseButton)reader.GetByte();
                        InputSimulator.SendMouseButton(btn, false);
                    }
                    break;

                case InputEventType.MouseWheel:
                    if (reader.AvailableBytes >= 2)
                    {
                        short delta = reader.GetShort();
                        InputSimulator.SendMouseWheel(delta);
                    }
                    break;

                case InputEventType.KeyDown:
                    if (reader.AvailableBytes >= 3)
                    {
                        ushort vk = reader.GetUShort();
                        reader.GetByte(); // isDown flag
                        InputSimulator.SendKey(vk, true);
                    }
                    break;

                case InputEventType.KeyUp:
                    if (reader.AvailableBytes >= 3)
                    {
                        ushort vk = reader.GetUShort();
                        reader.GetByte();
                        InputSimulator.SendKey(vk, false);
                    }
                    break;
            }
        }

        public void PunchNat(IPEndPoint target)
        {
            if (!_netServer.IsRunning) return;

            try
            {
                byte[] punch = PacketBuilder.CreatePunch(DeviceId);
                _netServer.SendUnconnectedMessage(punch, target);

                // Дополнительно пробиваем соседние порты (от -3 до +3) на случай сдвига порта мобильным NAT/CGNAT
                if (target.Port > 1024 && target.Port < 65530)
                {
                    for (int delta = -3; delta <= 3; delta++)
                    {
                        if (delta == 0) continue;
                        int p = target.Port + delta;
                        if (p > 1024 && p < 65535)
                        {
                            _netServer.SendUnconnectedMessage(punch, new IPEndPoint(target.Address, p));
                        }
                    }
                }
            }
            catch { }
        }

        public void SendClipboard(string text)
        {
            if (_connectedPeer != null && _connectedPeer.ConnectionState == ConnectionState.Connected)
            {
                byte[] packet = PacketBuilder.CreateClipboard(text);
                _connectedPeer.Send(packet, DeliveryMethod.ReliableOrdered);
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
