using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VibeDesk.Network;
using VibeDesk.Network.P2P;

namespace VibeDesk.Tests
{
    [TestClass]
    public class SignalingAndConnectionTests
    {
        [TestMethod]
        public async Task TestP2PSignalingResilientExchange()
        {
            using var hostSignaling = new P2PSignaling();
            using var clientSignaling = new P2PSignaling();

            string testHostId = "999888";
            var hostInfoExpected = new PeerEndpointInfo
            {
                DeviceId = testHostId,
                LocalIp = "192.168.0.15",
                LocalPort = 15890,
                PublicIp = "1.2.3.4",
                PublicPort = 15890
            };

            await hostSignaling.RegisterHostAsync(testHostId, clientReq =>
            {
                return Task.FromResult(hostInfoExpected);
            });

            var clientInfo = new PeerEndpointInfo
            {
                DeviceId = "111222",
                LocalIp = "192.168.0.13",
                LocalPort = 15890,
                PublicIp = "5.6.7.8",
                PublicPort = 15890
            };

            var receivedHostInfo = await clientSignaling.RequestHostEndpointsAsync(testHostId, clientInfo, timeoutMs: 8000);

            Assert.IsNotNull(receivedHostInfo, "Signaling should return host endpoints");
            Assert.AreEqual(testHostId, receivedHostInfo.DeviceId);
            Assert.AreEqual("192.168.0.15", receivedHostInfo.LocalIp);
        }

        [TestMethod]
        public async Task TestVibeHostAndClientDirectConnection()
        {
            int testPort = 15898;
            using var host = new VibeHost(testPort);
            host.DeviceId = "777888";
            bool hostStarted = host.Start(targetFps: 30, jpegQuality: 50, scale: 0.5f);
            Assert.IsTrue(hostStarted, "Host should start on port " + testPort);

            using var client = new VibeClient();
            var tcs = new TaskCompletionSource<bool>();
            client.OnConnected += () => tcs.TrySetResult(true);
            client.OnDisconnected += () => tcs.TrySetResult(false);

            bool initiated = client.Connect("127.0.0.1", testPort);
            Assert.IsTrue(initiated, "Client connect call should initiate");

            var timeoutTask = Task.Delay(5000);
            var completed = await Task.WhenAny(tcs.Task, timeoutTask);

            Assert.AreEqual(tcs.Task, completed, "Connection should complete before timeout");
            Assert.IsTrue(tcs.Task.Result, "Client should be connected");
            Assert.IsTrue(client.IsConnected, "client.IsConnected should be true");
        }

        [TestMethod]
        public async Task TestFrameTransmissionFromHostToClient()
        {
            int testPort = 15897;
            using var host = new VibeHost(testPort);
            host.DeviceId = "777999";
            bool hostStarted = host.Start(targetFps: 30, jpegQuality: 60, scale: 0.5f);
            Assert.IsTrue(hostStarted, "Host should start on port " + testPort);

            using var client = new VibeClient();
            var frameTcs = new TaskCompletionSource<byte[]>();
            client.OnFrameReceived += frame =>
            {
                frameTcs.TrySetResult(frame);
            };

            bool initiated = client.Connect("127.0.0.1", testPort);
            Assert.IsTrue(initiated, "Client connect call should initiate");

            var timeoutTask = Task.Delay(8000);
            var completed = await Task.WhenAny(frameTcs.Task, timeoutTask);

            Assert.AreEqual(frameTcs.Task, completed, "Frame should be received within 8 seconds");
            var frameData = frameTcs.Task.Result;
            Assert.IsNotNull(frameData);
            Assert.IsTrue(frameData.Length > 0, "Frame data should not be empty");
        }

        [TestMethod]
        public async Task TestContinuousStreamingStability()
        {
            int testPort = 15896;
            using var host = new VibeHost(testPort);
            host.DeviceId = "777666";
            host.OnStatusChanged += status =>
            {
                Console.WriteLine($"[HOST STATUS] {status}");
            };
            bool hostStarted = host.Start(targetFps: 60, jpegQuality: 70, scale: 1.0f);
            Assert.IsTrue(hostStarted, "Host should start on port " + testPort);

            using var client = new VibeClient();
            int framesReceived = 0;
            bool disconnected = false;
            string disconnectReason = "";

            client.OnFrameReceived += frame =>
            {
                framesReceived++;
            };

            client.OnDisconnected += () =>
            {
                disconnected = true;
            };

            client.OnStatusChanged += status =>
            {
                if (status.Contains("Отключено")) disconnectReason = status;
            };

            bool initiated = client.Connect("127.0.0.1", testPort);
            Assert.IsTrue(initiated, "Client connect call should initiate");

            // Wait 8 seconds of streaming
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(1000);
                Console.WriteLine($"Sec {i + 1}: Frames={framesReceived}, IncomingKbps={client.IncomingKbps:F0}, Ping={client.PingMs}ms, Disconnected={disconnected}");
            }

            Console.WriteLine($"Frames received in 8s: {framesReceived}, disconnected: {disconnected} ({disconnectReason})");
            Assert.IsFalse(disconnected, $"Client should not disconnect during streaming: {disconnectReason}");
            Assert.IsTrue(framesReceived > 20, $"Expected >20 frames, got {framesReceived}");
        }

        [TestMethod]
        public void TestFrameAckPacketBuilderAndParser()
        {
            uint testFrameId = 12345;
            byte[] packet = VibeDesk.Network.Protocol.PacketBuilder.CreateFrameAck(testFrameId);

            Assert.IsNotNull(packet);
            Assert.AreEqual(5, packet.Length);
            Assert.AreEqual((byte)VibeDesk.Network.Protocol.PacketType.FrameAck, packet[0]);

            uint parsedFrameId = BitConverter.ToUInt32(packet.AsSpan(1, 4));
            Assert.AreEqual(testFrameId, parsedFrameId);
        }

        [TestMethod]
        public void TestFrameReassemblerWithIdAndSlotClearing()
        {
            var reassembler = new VibeDesk.Network.Protocol.FrameReassembler();
            uint? readyFrameId = null;
            byte[]? readyData = null;

            reassembler.OnFrameReadyWithId += (id, data) =>
            {
                readyFrameId = id;
                readyData = data;
            };

            // Create chunk for frame 42 (1 chunk total)
            byte[] payload = new byte[] { 10, 20, 30, 40 };
            byte[] chunkPacket = VibeDesk.Network.Protocol.PacketBuilder.CreateFrameChunk(42, 0, 1, payload, 0, payload.Length);

            reassembler.ProcessChunk(chunkPacket);

            Assert.AreEqual(42u, readyFrameId);
            Assert.IsNotNull(readyData);
            Assert.AreEqual(4, readyData.Length);
            Assert.AreEqual(10, readyData[0]);
            Assert.AreEqual(40, readyData[3]);
        }
    }
}
