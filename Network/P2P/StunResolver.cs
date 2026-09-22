using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace VibeDesk.Network.P2P
{
    public static class StunResolver
    {
        public static async Task<IPEndPoint?> QueryPublicEndPointAsync(int localPort, string stunHost = "stun.l.google.com", int stunPort = 19302)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
                udp.Client.ReceiveTimeout = 3000;

                // STUN Binding Request:
                // 0x0001 (Binding Request), 0x0000 (Length), 0x2112A442 (Magic Cookie), 12-byte Transaction ID
                byte[] request = new byte[20];
                request[0] = 0x00;
                request[1] = 0x01; // Binding Request
                request[2] = 0x00;
                request[3] = 0x00; // Length
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
                var completedTask = await Task.WhenAny(receiveTask, Task.Delay(3000));
                if (completedTask != receiveTask)
                {
                    return null; // Timeout
                }

                var result = receiveTask.Result;
                byte[] response = result.Buffer;

                if (response.Length < 20) return null;

                // Check message type (0x0101 = Binding Response)
                if (response[0] != 0x01 || response[1] != 0x01) return null;

                int offset = 20;
                while (offset + 4 <= response.Length)
                {
                    ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                    ushort attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                    offset += 4;

                    if (offset + attrLen > response.Length) break;

                    // 0x0020 = XOR-MAPPED-ADDRESS
                    if (attrType == 0x0020 && attrLen >= 8)
                    {
                        byte family = response[offset + 1];
                        if (family == 0x01) // IPv4
                        {
                            ushort xorPort = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                            int port = xorPort ^ 0x2112;

                            byte[] ipBytes = new byte[4];
                            ipBytes[0] = (byte)(response[offset + 4] ^ 0x21);
                            ipBytes[1] = (byte)(response[offset + 5] ^ 0x12);
                            ipBytes[2] = (byte)(response[offset + 6] ^ 0xA4);
                            ipBytes[3] = (byte)(response[offset + 7] ^ 0x42);

                            var ip = new IPAddress(ipBytes);
                            return new IPEndPoint(ip, port);
                        }
                    }
                    // 0x0001 = MAPPED-ADDRESS (older STUN)
                    else if (attrType == 0x0001 && attrLen >= 8)
                    {
                        byte family = response[offset + 1];
                        if (family == 0x01)
                        {
                            int port = (response[offset + 2] << 8) | response[offset + 3];
                            byte[] ipBytes = new byte[4];
                            Buffer.BlockCopy(response, offset + 4, ipBytes, 0, 4);
                            return new IPEndPoint(new IPAddress(ipBytes), port);
                        }
                    }

                    offset += attrLen;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
