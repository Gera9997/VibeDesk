using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace VibeDesk.Network.P2P
{
    public static class StunResolver
    {
        private static readonly (string Host, int Port)[] StunServers = new[]
        {
            ("stun.l.google.com", 19302),
            ("stun1.l.google.com", 19302),
            ("stun2.l.google.com", 19302),
            ("stun.cloudflare.com", 3478)
        };

        public static async Task<(IPEndPoint? EndPoint, string Message)> ResolveAsync(int localPort = 0)
        {
            foreach (var (host, port) in StunServers)
            {
                try
                {
                    var ep = await QueryServerAsync(host, port, localPort);
                    if (ep != null)
                    {
                        return (ep, $"STUN успешен через {host}:{port} -> {ep.Address}:{ep.Port}");
                    }
                }
                catch (Exception)
                {
                    // Try next server
                }
            }

            return (null, "STUN серверы не ответили (возможно, UDP блокируется провайдером или порт занят)");
        }

        private static async Task<IPEndPoint?> QueryServerAsync(string stunHost, int stunPort, int localPort)
        {
            using var udp = new UdpClient();
            try
            {
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if (localPort > 0)
                {
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
                }
                udp.Client.ReceiveTimeout = 2000;

                byte[] request = new byte[20];
                request[0] = 0x00;
                request[1] = 0x01; // Binding Request
                request[2] = 0x00;
                request[3] = 0x00;
                request[4] = 0x21;
                request[5] = 0x12;
                request[6] = 0xA4;
                request[7] = 0x42; // Magic Cookie

                var rng = new Random();
                for (int i = 8; i < 20; i++)
                {
                    request[i] = (byte)rng.Next(0, 256);
                }

                await udp.SendAsync(request, request.Length, stunHost, stunPort);

                var receiveTask = udp.ReceiveAsync();
                var completedTask = await Task.WhenAny(receiveTask, Task.Delay(2000));
                if (completedTask != receiveTask)
                {
                    return null;
                }

                var result = receiveTask.Result;
                byte[] response = result.Buffer;

                if (response.Length < 20 || response[0] != 0x01 || response[1] != 0x01)
                {
                    return null;
                }

                int offset = 20;
                while (offset + 4 <= response.Length)
                {
                    ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                    ushort attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                    offset += 4;

                    if (offset + attrLen > response.Length) break;

                    if (attrType == 0x0020 && attrLen >= 8) // XOR-MAPPED-ADDRESS
                    {
                        byte family = response[offset + 1];
                        if (family == 0x01) // IPv4
                        {
                            ushort xorPort = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                            int mappedPort = xorPort ^ 0x2112;

                            byte[] ipBytes = new byte[4];
                            ipBytes[0] = (byte)(response[offset + 4] ^ 0x21);
                            ipBytes[1] = (byte)(response[offset + 5] ^ 0x12);
                            ipBytes[2] = (byte)(response[offset + 6] ^ 0xA4);
                            ipBytes[3] = (byte)(response[offset + 7] ^ 0x42);

                            return new IPEndPoint(new IPAddress(ipBytes), mappedPort);
                        }
                    }
                    else if (attrType == 0x0001 && attrLen >= 8) // MAPPED-ADDRESS
                    {
                        byte family = response[offset + 1];
                        if (family == 0x01)
                        {
                            int mappedPort = (response[offset + 2] << 8) | response[offset + 3];
                            byte[] ipBytes = new byte[4];
                            Buffer.BlockCopy(response, offset + 4, ipBytes, 0, 4);
                            return new IPEndPoint(new IPAddress(ipBytes), mappedPort);
                        }
                    }

                    offset += attrLen;
                }

                return null;
            }
            finally
            {
                udp.Close();
            }
        }
    }
}
