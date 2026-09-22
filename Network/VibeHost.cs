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
        private uint _frameCounter = 0;

        public event Action<string>? OnStatusChanged;
        public event Action<NetPeer>? OnClientConnected;
        public event Action<NetPeer>? OnClientDisconnected;
        public event Action<string>? OnClipboardReceived;

        public int Port { get; private set; }
        public int ScreenWidth => _captureManager?.ScreenWidth ?? 1920;
        public int ScreenHeight => _captureManager?.ScreenHeight ?? 1080;
        public string ActiveCaptureEngine => _captureManager?.ActiveEngineName ?? "None";
        public bool HasClient => _connectedPeer != null && _connectedPeer.ConnectionState == ConnectionState.Connected;

        public int CurrentFps { get; private set; }
        public double OutgoingKbps { get; private set; }

        public NetManager RawNetManager => _netServer;

        public VibeHost(int port = DefaultPort)
        {
            Port = port;
            _listener = new EventBasedNetListener();
            _netServer = new NetManager(_listener)
            {
                AutoRecycle = true,
                IPv6Enabled = false,
                UnsyncedEvents = true,
                DisconnectTimeout = 5000
            };

            _listener.ConnectionRequestEvent += request =>
            {
                if (_connectedPeer == null || _connectedPeer.ConnectionState != ConnectionState.Connected)
                {
                    request.AcceptIfKey(ConnectionKey);
                }
                else
                {
                    request.Reject();
                }
            };

            _listener.PeerConnectedEvent += peer =>
            {
                _connectedPeer = peer;
                OnStatusChanged?.Invoke($"Клиент подключен: {peer.Address}");
                OnClientConnected?.Invoke(peer);

                // Send initial screen dimensions
                if (_captureManager != null)
                {
                    byte[] info = PacketBuilder.CreateScreenInfo(_captureManager.ScreenWidth, _captureManager.ScreenHeight);
                    peer.Send(info, DeliveryMethod.ReliableOrdered);
                }
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

        public bool Start(int targetFps = 60, int jpegQuality = 70)
        {
            if (_isRunning) return true;

            try
            {
                _captureManager = new ScreenCaptureManager(jpegQuality);
            }
            catch (Exception ex)
            {
                OnStatusChanged?.Invoke($"Ошибка инициализации захвата: {ex.Message}");
                return false;
            }

            if (!_netServer.Start(Port))
            {
                OnStatusChanged?.Invoke($"Не удалось запустить сервер на порту {Port}");
                return false;
            }

            _isRunning = true;
            _streamingThread = new Thread(() => StreamingLoop(targetFps))
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "VibeDesk_StreamingThread"
            };
            _streamingThread.Start();

            OnStatusChanged?.Invoke($"Хост запущен на порту {Port}. Движок захвата: {_captureManager.ActiveEngineName}");
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
            OnStatusChanged?.Invoke("Хост остановлен.");
        }

        private void StreamingLoop(int targetFps)
        {
            long frameIntervalMs = 1000 / targetFps;
            var stopwatch = new Stopwatch();
            var fpsStopwatch = Stopwatch.StartNew();
            int framesThisSecond = 0;
            long bytesSentThisSecond = 0;

            while (_isRunning)
            {
                stopwatch.Restart();

                if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    CurrentFps = framesThisSecond;
                    OutgoingKbps = (bytesSentThisSecond * 8.0) / 1024.0;
                    framesThisSecond = 0;
                    bytesSentThisSecond = 0;
                    fpsStopwatch.Restart();
                }

                if (_connectedPeer != null && _connectedPeer.ConnectionState == ConnectionState.Connected && _captureManager != null)
                {
                    byte[]? frameData = _captureManager.CaptureAndEncode(forceFrame: framesThisSecond == 0);
                    if (frameData != null && frameData.Length > 0)
                    {
                        uint frameId = unchecked(++_frameCounter);
                        int totalLength = frameData.Length;
                        int chunkSize = PacketBuilder.MaxChunkPayloadSize;
                        ushort totalChunks = (ushort)Math.Ceiling((double)totalLength / chunkSize);

                        for (ushort i = 0; i < totalChunks; i++)
                        {
                            int offset = i * chunkSize;
                            int length = Math.Min(chunkSize, totalLength - offset);
                            byte[] chunkPacket = PacketBuilder.CreateFrameChunk(frameId, i, totalChunks, frameData, offset, length);

                            _connectedPeer.Send(chunkPacket, DeliveryMethod.Unreliable);
                            bytesSentThisSecond += chunkPacket.Length;
                        }

                        framesThisSecond++;
                    }
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
