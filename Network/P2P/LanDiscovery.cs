using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VibeDesk.Network.P2P
{
    public static class LanDiscovery
    {
        public const int DefaultHostPort = 15890;

        /// <summary>
        /// Sends UDP broadcast discovery packets on the local network to locate the VibeDesk host with targetId.
        /// Returns host IPEndPoint if discovered within timeoutMs, or null.
        /// </summary>
        public static async Task<IPEndPoint?> DiscoverHostAsync(string targetId, string? localIp = null, int timeoutMs = 1500)
        {
            if (string.IsNullOrWhiteSpace(targetId)) return null;
            targetId = targetId.Trim().Replace(" ", "");

            UdpClient udp;
            try
            {
                if (!string.IsNullOrEmpty(localIp) && IPAddress.TryParse(localIp, out var parsedLocalIp) && !IPAddress.IsLoopback(parsedLocalIp))
                {
                    udp = new UdpClient(new IPEndPoint(parsedLocalIp, 0));
                }
                else
                {
                    udp = new UdpClient();
                }
            }
            catch
            {
                udp = new UdpClient();
            }

            using (udp)
            {
                try
                {
                    udp.EnableBroadcast = true;
                    udp.Client.ReceiveTimeout = timeoutMs;

                    byte[] queryData = Encoding.UTF8.GetBytes($"VIBE_DISC:{targetId}");

                    var targets = GetBroadcastEndpoints(DefaultHostPort, localIp);

                    // Send broadcast query multiple times
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        foreach (var target in targets)
                        {
                            try
                            {
                                await udp.SendAsync(queryData, queryData.Length, target);
                            }
                            catch { }
                        }
                        await Task.Delay(40);
                    }

                var cts = new CancellationTokenSource(timeoutMs);
                var receiveTask = udp.ReceiveAsync();
                var completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, cts.Token));

                if (completedTask == receiveTask)
                {
                    var result = receiveTask.Result;
                    string resp = Encoding.UTF8.GetString(result.Buffer);
                    
                    // Format: VIBE_RESP:targetId:port
                    if (resp.StartsWith($"VIBE_RESP:{targetId}"))
                    {
                        var parts = resp.Split(':');
                        int port = DefaultHostPort;
                        if (parts.Length >= 3 && int.TryParse(parts[2], out int p))
                        {
                            port = p;
                        }
                        return new IPEndPoint(result.RemoteEndPoint.Address, port);
                    }
                }
                }
                catch
                {
                    // Discovery timed out or network error
                }
            }

            return null;
        }

        private static List<IPEndPoint> GetBroadcastEndpoints(int port, string? localIp = null)
        {
            var list = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, port) // 255.255.255.255
            };

            // If localIp is specified (e.g. 192.168.0.13), explicitly add standard /24 broadcast (192.168.0.255)
            if (!string.IsNullOrEmpty(localIp) && IPAddress.TryParse(localIp, out var parsedIp) && parsedIp.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = parsedIp.GetAddressBytes();
                bytes[3] = 255;
                list.Add(new IPEndPoint(new IPAddress(bytes), port));
            }

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                        {
                            var ipBytes = u.Address.GetAddressBytes();
                            var maskBytes = u.IPv4Mask.GetAddressBytes();
                            var bcastBytes = new byte[4];
                            for (int i = 0; i < 4; i++)
                            {
                                bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                            }
                            list.Add(new IPEndPoint(new IPAddress(bcastBytes), port));
                        }
                    }
                }
            }
            catch { }

            return list;
        }
    }
}
