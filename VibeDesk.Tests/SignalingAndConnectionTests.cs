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
            int testPort = 15897;
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
    }
}
