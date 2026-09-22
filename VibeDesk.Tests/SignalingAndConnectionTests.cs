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
        public async Task TestConnectToRemoteHost15()
        {
            using var client = new VibeClient();
            var tcs = new TaskCompletionSource<bool>();
            client.OnStatusChanged += s => Console.WriteLine($"[Client Log] {s}");
            client.OnConnected += () =>
            {
                Console.WriteLine("[Client] CONNECTED EVENT FIRED!");
                tcs.TrySetResult(true);
            };
            client.OnDisconnected += () =>
            {
                Console.WriteLine("[Client] DISCONNECTED EVENT FIRED!");
                tcs.TrySetResult(false);
            };

            bool ok = client.Connect("192.168.0.15", 15890);
            Console.WriteLine($"Initiated connect: {ok}");

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(8000));
            Console.WriteLine($"Completed before timeout: {completed == tcs.Task}, Connected: {client.IsConnected}");
        }
    }
}
